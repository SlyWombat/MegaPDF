// MegaPDF shared engine core — the C ABI (ADR-003, #33).
//
// One implementation of the engine policy — everything between a platform's UI
// and PDFium's C API — bound from C# (P/Invoke), Kotlin (JNI) and Swift (C
// interop). Native UI stays native; this is the layer that used to be written
// three times and fixed three times (#30).
//
// ABI rules, so three toolchains can agree:
//   * C only. No C++ types cross the boundary, no exceptions escape.
//   * The core owns documents (#105, ADR-003 decision 1): a binding hands over
//     bytes and gets opaque handles back. The form-fill environment, the page
//     lifecycle and the serialising mutex all live in here.
//   * Every entry point either returns a count or a status (0 ok, negative
//     error); megapdf_last_error_message() explains the most recent failure on
//     the calling thread. Nothing throws, nothing crashes on a PDFium refusal.
//   * Buffers are caller-owned, count-then-fill: a call with capacity 0 returns
//     how much is needed, a call with a buffer fills up to its capacity and
//     returns the total. That keeps JNI and P/Invoke marshalling boring.
//   * Coordinates are crop space — bottom-left origin, the CropBox origin already
//     subtracted, in points: user space units times the page's /UserUnit (#150).
//     Getting that wrong is #30, so it happens once.
//   * Thread safety: every call takes the core's own mutex, because PDFium is not
//     thread-safe and that is a property of the library, not of any platform.
//     Bindings may keep their own discipline on top; correctness does not need it.
//
// Every contract lives here (#105–#112): no binding calls PDFium directly, so
// there are no raw-handle accessors — the opaque handles are the whole surface.
#ifndef MEGAPDF_CORE_H
#define MEGAPDF_CORE_H

#include <stddef.h>

#if defined(_WIN32)
#  if defined(MEGAPDF_CORE_BUILD)
#    define MEGAPDF_API __declspec(dllexport)
#  else
#    define MEGAPDF_API __declspec(dllimport)
#  endif
#else
#  define MEGAPDF_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/** A rectangle in PDF points, bottom-left origin, crop-relative. */
typedef struct megapdf_rect {
    double left;
    double bottom;
    double right;
    double top;
} megapdf_rect;

/** Opaque handles owned by the core. */
typedef struct megapdf_document megapdf_document;
typedef struct megapdf_page megapdf_page;

/** Status codes returned by calls that do not return a count. */
enum {
    MEGAPDF_OK = 0,
    MEGAPDF_ERR_ARGUMENT = -1,    /* a null handle or an out-of-range index */
    MEGAPDF_ERR_PDFIUM = -2,      /* PDFium refused; see megapdf_last_error_message() */
    MEGAPDF_ERR_MEMORY = -3,      /* the core could not allocate */
    MEGAPDF_ERR_NO_FONT = -4,     /* no font could render the text, not even a standard substitute (tier 2 failed) */
    MEGAPDF_ERR_LAYOUT = -5,      /* PDFium would change how the page looks if it rewrote this text (#118) */
    MEGAPDF_ERR_RESTRICTED = -6,  /* the document's security does not allow it; its owner password would (#131) */
    MEGAPDF_ERR_CANCELLED = -7,   /* a page check stopped early: its cancel flag was raised or its document is closing (#145) */
    MEGAPDF_ERR_NOT_JUDGED = -8   /* megapdf_page_regeneration_verdict_cached(): the page has no answer yet (#145) */
};

/**
 * A cancel flag for a page check (#145). megapdf_cancel_raise() may be called from any
 * thread, at any time, also after the check has returned; free the flag once the check has
 * returned. megapdf_cancel_new() returns NULL only when out of memory.
 */
typedef struct megapdf_cancel megapdf_cancel;
MEGAPDF_API megapdf_cancel* megapdf_cancel_new(void);
MEGAPDF_API void megapdf_cancel_raise(megapdf_cancel* cancel);
MEGAPDF_API void megapdf_cancel_free(megapdf_cancel* cancel);

/* --------------------------------------------------------------------------
 * Errors
 * ----------------------------------------------------------------------- */

/**
 * PDFium's FPDF_GetLastError() as of the last failed megapdf_open() on this
 * thread (FPDF_ERR_PASSWORD, FPDF_ERR_FORMAT, ...). 0 when the last open worked.
 * `unsigned int` rather than PDFium's `unsigned long`, which is 4 bytes on Windows
 * and 8 everywhere else — the kind of thing three bindings would each get wrong once.
 */
MEGAPDF_API unsigned int megapdf_last_error(void);

/** A short English message for the most recent failure on this thread; never NULL. */
MEGAPDF_API const char* megapdf_last_error_message(void);

/* --------------------------------------------------------------------------
 * Documents
 * ----------------------------------------------------------------------- */

/**
 * megapdf_last_error() after a failed open. PDFium's own codes are 0–6
 * (FPDF_ERR_*); MegaPDF's start above them so a binding can tell them apart.
 *
 * TOO_LARGE is the file-backed open's only hard size limit, and it bites on
 * Windows alone: FPDF_FILEACCESS describes a file's length and its read offsets
 * as `unsigned long`, which is 32 bits there and 64 bits everywhere else, so a
 * file of 4 GiB or more cannot be addressed through it on Windows (#147).
 */
#define MEGAPDF_OPEN_ERR_TOO_LARGE 100u

/**
 * Opens a document from memory. The bytes are copied; the caller may free them
 * on return. `password_utf8` may be NULL. Returns NULL on failure, with
 * megapdf_last_error() carrying PDFium's code (FPDF_ERR_PASSWORD when one is
 * required or wrong). The form-fill environment is initialised here, so form
 * fields render and toggle without any binding-side setup.
 *
 * This costs the file's size in memory twice over — once in the caller's buffer,
 * once in the copy — so it is for documents that are genuinely in memory (built
 * there, or handed over by a platform that gives no file). A document that has a
 * file should be opened with megapdf_open_file() (#148).
 */
MEGAPDF_API megapdf_document* megapdf_open(const void* bytes, size_t length, const char* password_utf8);

/**
 * Opens a document from its file, read on demand (#147, #148).
 *
 * The core keeps the file open and PDFium reads the parts it needs through it, so
 * opening costs what PDFium's parser caches rather than the size of the file, and
 * a document larger than the address space (or than a .NET byte[]) opens like any
 * other. `path_utf8` is UTF-8 on every platform, including Windows, where the core
 * widens it itself.
 *
 * The file is opened so that it may still be renamed, written or deleted while the
 * document is open, which the atomic-replace save (SDD §3.4) and a cloud sync both
 * need. A save that replaces the file under an open document leaves that document
 * reading the bytes it was opened on, exactly as the in-memory copy did.
 *
 * Returns NULL on failure with megapdf_last_error() set: FPDF_ERR_FILE when the
 * file cannot be opened or read, MEGAPDF_OPEN_ERR_TOO_LARGE past the limit above,
 * and PDFium's own codes otherwise.
 */
MEGAPDF_API megapdf_document* megapdf_open_file(const char* path_utf8, const char* password_utf8);

/**
 * megapdf_open_file() for a file the caller has already opened for reading: the
 * core takes ownership of `fd` and closes it with the document, whether or not the
 * open succeeds. For Android, whose documents arrive as content URIs that have a
 * descriptor but no path. POSIX only — on Windows it fails with FPDF_ERR_FILE.
 */
MEGAPDF_API megapdf_document* megapdf_open_fd(int fd, const char* password_utf8);

/**
 * Opens `bytes` with the credentials `like` was opened with (#132). A save of a
 * protected document writes a copy that is still protected, so reading that copy
 * back needs the same password; every platform's save check opens it this way.
 * The core keeps the password with the document, in memory only, and
 * megapdf_close() wipes it. Returns NULL, with megapdf_last_error() set, when `like`
 * is NULL or the bytes do not open.
 */
MEGAPDF_API megapdf_document* megapdf_open_like(const megapdf_document* like, const void* bytes, size_t length);

/** megapdf_open_file() with the credentials `like` was opened with (#132, #148). */
MEGAPDF_API megapdf_document* megapdf_open_file_like(const megapdf_document* like, const char* path_utf8);

/** megapdf_open_fd() with the credentials `like` was opened with (#132, #148). */
MEGAPDF_API megapdf_document* megapdf_open_fd_like(const megapdf_document* like, int fd);

/** Closes every page still open on it, tears down the form environment, frees it. NULL is fine. */
MEGAPDF_API void megapdf_close(megapdf_document* document);

MEGAPDF_API int megapdf_page_count(const megapdf_document* document);

/* --------------------------------------------------------------------------
 * Pages
 * ----------------------------------------------------------------------- */

/**
 * Loads page `index` (form-fill hooks applied). Returns NULL on failure. A page
 * must be closed with megapdf_close_page(); closing the document closes any
 * page still open, after which the page handle is invalid.
 */
MEGAPDF_API megapdf_page* megapdf_load_page(megapdf_document* document, int index);
MEGAPDF_API void megapdf_close_page(megapdf_page* page);

/** Page size in points — the CropBox size, which is what a viewer shows, times the page's /UserUnit. */
MEGAPDF_API double megapdf_page_width(const megapdf_page* page);
MEGAPDF_API double megapdf_page_height(const megapdf_page* page);

/** The CropBox origin in PDF user space that every returned coordinate has had subtracted. */
MEGAPDF_API void megapdf_page_crop_origin(const megapdf_page* page, double* out_x, double* out_y);
/**
 * The page's /UserUnit (#150): how many points one user space unit is, 1.0 for almost every
 * page. Crop space is already scaled by it: page sizes, bounds, font sizes and every
 * coordinate the core returns or takes are points, so a 10 m banner drawn in 2-point units
 * measures 10 m. 1.0 for a NULL page. Needs MegaPDF's PDFium patch 0023.
 */
MEGAPDF_API double megapdf_page_user_unit(const megapdf_page* page);

/* --------------------------------------------------------------------------
 * Contracts (SDD §6.2)
 * ----------------------------------------------------------------------- */

/**
 * Drawn-checkbox candidates (contract 2): stroked-not-filled path objects, 6–24 pt
 * on both axes, squareness within 25%. Count-then-fill into `out`.
 */
MEGAPDF_API size_t megapdf_detect_checkbox_squares(const megapdf_page* page, megapdf_rect* out, size_t capacity);

/**
 * Text search (#26): case-insensitive literal substring, matches in text order,
 * each match covered by one rect per line it spans. Count-then-fill into `out`
 * as a packed double stream — for each match: rect_count, then rect_count × (left,
 * bottom, right, top) — the layout every binding already decodes. The return value
 * is the total number of doubles; a NULL or empty term yields 0. Matches with no
 * rects are dropped, as on every platform today.
 *
 * `term_utf16` is NUL-terminated UTF-16 (what PDFium's FPDF_WIDESTRING is).
 */
MEGAPDF_API size_t megapdf_search_page(const megapdf_page* page, const unsigned short* term_utf16,
                                       double* out, size_t capacity);

/* --------------------------------------------------------------------------
 * Contract 2: text runs and visual lines (#106) — the inputs to body-text
 * editing. A page's runs and lines are computed once into an opaque result that
 * outlives the page; strings come out count-then-fill as UTF-16 code units.
 * ----------------------------------------------------------------------- */

/** One text object with visible text: a run of body text in one font and size. */
typedef struct megapdf_text_run {
    int object_index;        /* index into the page's object list */
    megapdf_rect bounds;     /* crop space */
    double font_size;
    int is_text_box;         /* 1 when the object carries the MegaPDFTextBox mark (SDD §6.2 contract 4) */
} megapdf_text_run;

/** Which string of a run megapdf_text_run_string() returns. */
typedef enum megapdf_text_field {
    MEGAPDF_TEXT_RUN_TEXT = 0,      /* the glyphs, as FPDFTextObj_GetText reports them */
    MEGAPDF_TEXT_RUN_FONT = 1,      /* the font's family name ("" when the font is unreadable) */
    MEGAPDF_TEXT_RUN_BOX_ID = 2,    /* the `id` mark param, "" when none */
    MEGAPDF_TEXT_RUN_BOX_FONT = 3   /* the `font` mark param, "" when none (the binding applies its default) */
} megapdf_text_field;

typedef struct megapdf_text megapdf_text;

/** Flags for megapdf_text_load(). */
enum {
    MEGAPDF_TEXT_ALL = 0,
    /** Only objects carrying the MegaPDFTextBox mark — what a text-box listing needs,
        without reading every body-text object on the page. Lines are not meaningful
        for such a load. */
    MEGAPDF_TEXT_BOXES_ONLY = 1
};

/**
 * Reads every text object on the page in object order, skipping objects whose
 * text is empty or all whitespace. Visual lines are computed on first use: runs
 * whose vertical centres are within half the taller run's height share a
 * baseline; a baseline is split where the horizontal gap to the next run (left
 * to right) exceeds twice the larger of the two font sizes (columns, page-number
 * gutters). Lines are ordered top to bottom, then left to right. Returns NULL
 * only when the page is NULL or the core cannot allocate.
 */
MEGAPDF_API megapdf_text* megapdf_text_load(const megapdf_page* page, unsigned int flags);
MEGAPDF_API void megapdf_text_free(megapdf_text* text);

MEGAPDF_API size_t megapdf_text_run_count(const megapdf_text* text);
/** MEGAPDF_OK, or MEGAPDF_ERR_ARGUMENT for a bad handle or index. */
MEGAPDF_API int megapdf_text_run_get(const megapdf_text* text, size_t index, megapdf_text_run* out);
/** UTF-16 code units, no terminator, count-then-fill. 0 for a bad handle, index or field. */
MEGAPDF_API size_t megapdf_text_run_string(const megapdf_text* text, size_t index, megapdf_text_field field,
                                           unsigned short* out, size_t capacity);

MEGAPDF_API size_t megapdf_text_line_count(const megapdf_text* text);
/** The line's bounds (the union of its runs) in crop space. */
MEGAPDF_API int megapdf_text_line_get(const megapdf_text* text, size_t index, megapdf_rect* out_bounds);
/** The run indices making up the line, left to right; count-then-fill. */
MEGAPDF_API size_t megapdf_text_line_runs(const megapdf_text* text, size_t index, size_t* out_run_indices,
                                          size_t capacity);

/* --------------------------------------------------------------------------
 * Contract 3: AcroForm fields (#107). The form-fill environment lives in the
 * core, so reading fields and driving them through PDFium's form machinery
 * (a simulated click, which keeps /V, /AS and radio-group siblings consistent,
 * exactly as the Chrome viewer does) happens here for every platform.
 * ----------------------------------------------------------------------- */

typedef enum megapdf_field_kind {
    MEGAPDF_FIELD_OTHER = 0,
    MEGAPDF_FIELD_TEXT = 1,
    MEGAPDF_FIELD_CHECKBOX = 2,
    MEGAPDF_FIELD_RADIO = 3
} megapdf_field_kind;

typedef struct megapdf_form_field {
    int kind;                /* megapdf_field_kind */
    int is_checked;          /* checkbox and radio only; 0 otherwise */
    megapdf_rect bounds;     /* crop space */
} megapdf_form_field;

typedef enum megapdf_field_string {
    MEGAPDF_FIELD_NAME = 0,  /* the fully qualified field name */
    MEGAPDF_FIELD_VALUE = 1  /* the current value (text fields; the export value of a checked box) */
} megapdf_field_string;

typedef struct megapdf_form_fields megapdf_form_fields;

/**
 * Every widget annotation on the page, in annotation order, with its kind, state
 * and bounds; a snapshot that outlives the page. Returns NULL only when the page
 * is NULL or the core cannot allocate (a document without a form environment
 * yields zero fields).
 */
MEGAPDF_API megapdf_form_fields* megapdf_form_fields_load(const megapdf_page* page);
MEGAPDF_API void megapdf_form_fields_free(megapdf_form_fields* fields);
MEGAPDF_API size_t megapdf_form_field_count(const megapdf_form_fields* fields);
MEGAPDF_API int megapdf_form_field_get(const megapdf_form_fields* fields, size_t index, megapdf_form_field* out);
/** UTF-16 code units, no terminator, count-then-fill. */
MEGAPDF_API size_t megapdf_form_field_string(const megapdf_form_fields* fields, size_t index,
                                             megapdf_field_string which, unsigned short* out, size_t capacity);

/**
 * A primary-button click at (x, y) in crop space, then focus released: toggles a
 * checkbox or radio under the point through the form environment. Returns
 * MEGAPDF_OK, or MEGAPDF_ERR_ARGUMENT when the page is NULL or the document has
 * no form environment.
 */
MEGAPDF_API int megapdf_form_click(const megapdf_page* page, double x, double y);

/**
 * Sets the text of the field under (x, y) in crop space: click to focus, select
 * all, replace the selection with `value_utf16` (NUL-terminated), release focus.
 */
MEGAPDF_API int megapdf_form_set_text(const megapdf_page* page, double x, double y,
                                      const unsigned short* value_utf16);

/**
 * Commits any in-progress field edit (FORM_ForceToKillFocus). Call before
 * serialising; the rule every platform had to remember on its own until now.
 */
MEGAPDF_API void megapdf_form_commit(const megapdf_document* document);

/* --------------------------------------------------------------------------
 * Contract 4: stamps and MegaPDF_Id marks (#108, SDD §6.2). A MegaPDF stamp is
 * a STAMP annotation carrying a `MegaPDF_Id` string ("mark:…" for check marks,
 * "sig:…" for signatures). Ids are caller-supplied so they stay stable across
 * undo/redo and across platforms. Coordinates are crop space throughout.
 * ----------------------------------------------------------------------- */

typedef enum megapdf_mark_style {
    MEGAPDF_MARK_CROSS = 0,          /* ✗ — the default (Appendix B #3) */
    MEGAPDF_MARK_CHECK = 1,          /* ✓ */
    MEGAPDF_MARK_FILLED_SQUARE = 2   /* ■ */
} megapdf_mark_style;

/**
 * Draws a check mark over a drawn square: STAMP annot inset 10% of the larger
 * side, 0x202020 ink, stroke width max(1.2, width × 0.11), tagged with `id_utf16`
 * (NUL-terminated). MEGAPDF_OK, MEGAPDF_ERR_ARGUMENT, or MEGAPDF_ERR_PDFIUM.
 */
MEGAPDF_API int megapdf_add_check_mark(const megapdf_page* page, const megapdf_rect* square,
                                       megapdf_mark_style style, const unsigned short* id_utf16);

/**
 * Places an image stamp (a signature): STAMP annot at `bounds`, the image object
 * positioned by the unit-square matrix, alpha respected. `bgra` is width × height
 * × 4 bytes, top row first; the core copies it.
 */
MEGAPDF_API int megapdf_add_image_stamp(const megapdf_page* page, const unsigned char* bgra, int width, int height,
                                        const megapdf_rect* bounds, const unsigned short* id_utf16);

typedef struct megapdf_stamp {
    int annot_index;         /* the annotation's index on the page, valid until the page's annotations change */
    megapdf_rect bounds;     /* crop space */
} megapdf_stamp;

typedef struct megapdf_stamps megapdf_stamps;

/** Every annotation on the page carrying a MegaPDF_Id, in annotation order; a snapshot. */
MEGAPDF_API megapdf_stamps* megapdf_stamps_load(const megapdf_page* page);
MEGAPDF_API void megapdf_stamps_free(megapdf_stamps* stamps);
MEGAPDF_API size_t megapdf_stamp_count(const megapdf_stamps* stamps);
MEGAPDF_API int megapdf_stamp_get(const megapdf_stamps* stamps, size_t index, megapdf_stamp* out);
/** The stamp's MegaPDF_Id: UTF-16 code units, no terminator, count-then-fill. */
MEGAPDF_API size_t megapdf_stamp_id(const megapdf_stamps* stamps, size_t index, unsigned short* out, size_t capacity);

typedef struct megapdf_image megapdf_image;

/**
 * The image of the stamp at `annot_index`, rendered at the image's NATIVE pixel
 * size rather than its placement size (a temporary 1 pt-per-pixel matrix), so
 * repeated move cycles never lose resolution. NULL when the annotation has no
 * image object.
 */
MEGAPDF_API megapdf_image* megapdf_stamp_image_load(const megapdf_page* page, int annot_index);
MEGAPDF_API void megapdf_image_free(megapdf_image* image);
MEGAPDF_API int megapdf_image_width(const megapdf_image* image);
MEGAPDF_API int megapdf_image_height(const megapdf_image* image);
/** BGRA bytes, top row first, width × 4 per row; count-then-fill in bytes. */
MEGAPDF_API size_t megapdf_image_pixels(const megapdf_image* image, unsigned char* out, size_t capacity);

/** Removes the annotation at `annot_index`. MEGAPDF_OK or MEGAPDF_ERR_PDFIUM. */
MEGAPDF_API int megapdf_remove_annotation(const megapdf_page* page, int annot_index);

/** Removes the stamp carrying `id_utf16`. MEGAPDF_ERR_ARGUMENT when no such stamp. */
MEGAPDF_API int megapdf_remove_stamp(const megapdf_page* page, const unsigned short* id_utf16);

/**
 * Moves an image stamp to `bounds` under the same id: extract at native
 * resolution, remove, re-add. (Updating the annotation in place after SetRect
 * wipes its appearance stream.) MEGAPDF_ERR_ARGUMENT when the id names no image stamp.
 */
MEGAPDF_API int megapdf_move_image_stamp(const megapdf_page* page, const unsigned short* id_utf16,
                                         const megapdf_rect* bounds);

/* --------------------------------------------------------------------------
 * Contract 5: whiteouts, text boxes and detached objects (#109). Whiteouts are
 * white filled paths carrying the MegaPDFWhiteout mark; text boxes are text
 * objects in one of three base-14 faces carrying the MegaPDFTextBox mark with
 * `id` and `font` params (SDD §6.2 contract 4, #43). Coordinates are crop space.
 * ----------------------------------------------------------------------- */

typedef struct megapdf_object_rect {
    int object_index;
    megapdf_rect bounds;
} megapdf_object_rect;

/** Appends a whiteout covering `bounds`; its object index comes back in `out_object_index`. */
MEGAPDF_API int megapdf_add_whiteout(const megapdf_page* page, const megapdf_rect* bounds, int* out_object_index);
/** Every whiteout on the page, in object order; count-then-fill. */
MEGAPDF_API size_t megapdf_whiteouts(const megapdf_page* page, megapdf_object_rect* out, size_t capacity);

/**
 * Inserts a text box at `object_index` (-1 appends) with its baseline starting at
 * (baseline_x, baseline_y). `font_name` must be "Helvetica", "Times-Roman" or
 * "Courier" (MEGAPDF_ERR_ARGUMENT otherwise); `text` and `id` are NUL-terminated
 * UTF-16. The mark records the id and the face exactly as chosen.
 */
MEGAPDF_API int megapdf_add_text_box(const megapdf_page* page, int object_index, const unsigned short* text,
                                     const char* font_name, double font_size, double baseline_x, double baseline_y,
                                     const unsigned short* id, int* out_object_index);

/**
 * The id-preserving restyle (#45): inserts a box at `object_index` and then moves
 * it so its bounds' bottom-left corner lands on (left, bottom) — a 12 pt → 18 pt
 * change grows upward from the anchored corner instead of dropping by the extra
 * descender depth. The caller detaches the old object first (megapdf_detach_object).
 */
MEGAPDF_API int megapdf_restyle_text_box(const megapdf_page* page, int object_index, const unsigned short* text,
                                         const char* font_name, double font_size, double left, double bottom,
                                         const unsigned short* id);

/**
 * The object index of the text box carrying `id`, or -1. A marked box with no id
 * (written before the param existed) answers to "text:untagged#<object index>",
 * the derived handle the phones give it.
 */
MEGAPDF_API int megapdf_find_text_box(const megapdf_page* page, const unsigned short* id);

/** PDFium's object type (FPDF_PAGEOBJ_TEXT = 1, PATH = 2, IMAGE = 3, ...), or -1 for a bad index. */
MEGAPDF_API int megapdf_object_type(const megapdf_page* page, int object_index);

/** Bounds of any page object, crop space. MEGAPDF_ERR_ARGUMENT for a bad index. */
MEGAPDF_API int megapdf_object_bounds(const megapdf_page* page, int object_index, megapdf_rect* out);

/** Translates a text object so its bounds' bottom-left corner lands on (left, bottom); scale and rotation untouched. */
MEGAPDF_API int megapdf_move_text_box(const megapdf_page* page, int object_index, double left, double bottom);

/** Removes and frees the box carrying `id`. Already gone counts as success, so an undo cannot fail. */
MEGAPDF_API int megapdf_remove_text_box(const megapdf_page* page, const unsigned short* id);

/**
 * Detached objects: page objects removed from their page but kept alive so an
 * undo can put them back byte-identical. The core owns them; restoring consumes the
 * handle, discarding frees it, and closing the document frees any still held.
 *
 * megapdf_detach_object() takes exactly the one object at `object_index`, whatever it
 * is; megapdf_restore_object() puts a one-object handle back at `object_index`, and
 * refuses a handle holding more (MEGAPDF_ERR_ARGUMENT, the handle stays valid).
 */
typedef struct megapdf_detached megapdf_detached;
MEGAPDF_API megapdf_detached* megapdf_detach_object(const megapdf_page* page, int object_index);
MEGAPDF_API int megapdf_restore_object(const megapdf_page* page, megapdf_detached* detached, int object_index);
MEGAPDF_API void megapdf_discard_detached(megapdf_detached* detached);

/**
 * Removes body text, a line or a single run, with the hidden copies drawn under it
 * (#136). Producers draw a line twice for fake bold, an outline or a shadow; PDFium's
 * text layer reads one copy and the other extracts as empty text, so it is never a run,
 * and removing only the runs leaves the line on the page. `object_indices` are the runs'
 * indices as the page is now, in any order, without repeats. Everything is taken at
 * once into one handle; megapdf_restore_detached() puts every object back where it was.
 *
 * NULL, with the page untouched, for a bad index, an object that is not text, a repeat,
 * or body text megapdf_text_editable() refuses (text boxes are not judged).
 */
MEGAPDF_API megapdf_detached* megapdf_detach_text_runs(const megapdf_page* page, const int* object_indices, size_t count);

/**
 * Undoes whatever produced `detached`, which it consumes: puts every object it holds
 * back at the index it had, and for a megapdf_set_text()/megapdf_set_line_text() handle
 * first takes the edited run off the page. The page must be as that call left it, apart
 * from changes already undone; later edits must be undone first. MEGAPDF_ERR_ARGUMENT,
 * with the page and the handle untouched, when the page is not the handle's or cannot be
 * as the call left it.
 */
MEGAPDF_API int megapdf_restore_detached(const megapdf_page* page, megapdf_detached* detached);

typedef struct megapdf_detached_part {
    int object_index;   /* where the object stood before it was taken, and where restoring puts it */
    int copy_of;        /* the object index of the run it is a hidden copy of; -1 for a run itself */
} megapdf_detached_part;

/** How many objects a handle holds, ascending by object index; for a recovery journal. */
MEGAPDF_API size_t megapdf_detached_count(const megapdf_detached* detached);
MEGAPDF_API int megapdf_detached_get(const megapdf_detached* detached, size_t index, megapdf_detached_part* out);

/* --------------------------------------------------------------------------
 * Contract 6: save, flatten and images (#110). File I/O, atomic replace and
 * verify-before-overwrite stay per platform; the core serialises to a caller
 * write callback exactly as FPDF_FILEWRITE does.
 * ----------------------------------------------------------------------- */

/** Receives one block of the serialised PDF; return 1 to continue, 0 to abort. */
typedef int (*megapdf_write_fn)(void* context, const void* data, size_t size);

/**
 * Commits any in-progress form edit and writes the whole document — always a
 * full rewrite (#97): PDFium's incremental save appends every loaded object,
 * which for a document whose pages were all loaded is the document again.
 * MEGAPDF_ERR_PDFIUM when PDFium refuses or the callback aborts.
 */
MEGAPDF_API int megapdf_save(const megapdf_document* document, megapdf_write_fn write, void* context);

/* --------------------------------------------------------------------------
 * Document security (#131). megapdf_open() takes the password; these report
 * what that open may do and write copies with new security or none.
 * ----------------------------------------------------------------------- */

/** What an open may do: the standard security handler's permission bits (ISO 32000-2, Table 22). */
#define MEGAPDF_PERMIT_PRINT          (1u << 2)
#define MEGAPDF_PERMIT_MODIFY         (1u << 3)
#define MEGAPDF_PERMIT_COPY           (1u << 4)
#define MEGAPDF_PERMIT_ANNOTATE       (1u << 5)
#define MEGAPDF_PERMIT_FILL_FORMS     (1u << 8)
#define MEGAPDF_PERMIT_ACCESSIBILITY  (1u << 9)
#define MEGAPDF_PERMIT_ASSEMBLE       (1u << 10)
#define MEGAPDF_PERMIT_PRINT_HIGH     (1u << 11)
#define MEGAPDF_PERMIT_ALL            (0xF3Cu)

/* `unsigned int`, not `unsigned long`: 32 bits on every platform the apps ship, so the
 * desktop binding marshals one layout on Windows and macOS alike. */
typedef struct megapdf_security {
    int encrypted;              /* 1 when the document has a security handler */
    int revision;               /* the standard handler's revision, 2-6; -1 when not encrypted */
    unsigned int permissions;   /* MEGAPDF_PERMIT_* this open may do; MEGAPDF_PERMIT_ALL unless restricted */
    int full_access;            /* 1 when this open may do everything, including change or remove the security:
                                   an unprotected document, an owner-password open, or one that restricts nothing */
} megapdf_security;

/** MEGAPDF_OK, or MEGAPDF_ERR_ARGUMENT for a NULL document or `out`. */
MEGAPDF_API int megapdf_security_info(const megapdf_document* document, megapdf_security* out);

/**
 * Writes a copy encrypted with AES-256 (the standard handler at revision 6)
 * under new passwords, in place of any security the document had. The user
 * password (UTF-8; NULL or empty for none) opens the copy with `permissions`
 * (MEGAPDF_PERMIT_*); the owner password opens it with all of them, and NULL or
 * empty means the same as the user password. The document itself keeps the
 * security it was opened with, so megapdf_open_like() does not open the copy;
 * open it with the new password. Needs full access: MEGAPDF_ERR_RESTRICTED
 * otherwise. Needs MegaPDF's PDFium patch 0010.
 */
MEGAPDF_API int megapdf_save_with_security(const megapdf_document* document, const char* user_password_utf8,
                                           const char* owner_password_utf8, unsigned int permissions,
                                           megapdf_write_fn write, void* context);

/** Writes a copy with no security at all. Needs full access: MEGAPDF_ERR_RESTRICTED otherwise. */
MEGAPDF_API int megapdf_save_without_security(const megapdf_document* document, megapdf_write_fn write,
                                              void* context);

/** Commits form edits, then bakes every page's annotations and fields into its content. */
MEGAPDF_API int megapdf_flatten_all(const megapdf_document* document);

typedef struct megapdf_image_info {
    int page_index;
    int object_index;
    int pixel_width;         /* stored resolution */
    int pixel_height;
    double display_width;    /* points, as placed */
    double display_height;
    long long stored_bytes;  /* the raw stream length */
} megapdf_image_info;

typedef struct megapdf_images megapdf_images;

/** Every raster image object in the document, page by page; a snapshot. */
MEGAPDF_API megapdf_images* megapdf_images_load(const megapdf_document* document);
MEGAPDF_API void megapdf_images_free(megapdf_images* images);
MEGAPDF_API size_t megapdf_image_count(const megapdf_images* images);
MEGAPDF_API int megapdf_image_get(const megapdf_images* images, size_t index, megapdf_image_info* out);

/** The image object rendered at width × height pixels (BGRA; see megapdf_image_*). NULL on failure. */
MEGAPDF_API megapdf_image* megapdf_render_image(const megapdf_document* document, int page_index, int object_index,
                                                int width, int height);

/** Replaces the image object's data with a JPEG stream (DCTDecode), inline. */
MEGAPDF_API int megapdf_replace_image_jpeg(const megapdf_document* document, int page_index, int object_index,
                                           const unsigned char* jpeg, size_t length);

/**
 * Encodes BGRA pixels as JPEG at `quality` (0..1) into a buffer the callback owns;
 * return 1 with `out_jpeg`/`out_length` set, 0 on failure. The core calls `release`
 * with the buffer when it is done with it.
 */
typedef int (*megapdf_jpeg_encode_fn)(void* context, const unsigned char* bgra, int width, int height, double quality,
                                      unsigned char** out_jpeg, size_t* out_length);
typedef void (*megapdf_jpeg_release_fn)(void* context, unsigned char* jpeg);

/**
 * Shrink-for-email (SDD §3.7), the decision rules in one place: every image is
 * re-encoded at 150 dpi of its placed size, quality 0.75, unless it is already
 * about the right resolution and under 100 KB, under 8 KB, or under 8 px on a
 * side, or unless the re-encode would save less than 10%. `out_replaced` counts
 * the images replaced. Callers work on a copy: this degrades quality by design.
 */
MEGAPDF_API int megapdf_shrink_images(const megapdf_document* document, megapdf_jpeg_encode_fn encode,
                                      megapdf_jpeg_release_fn release, void* context, int* out_replaced);

/* --------------------------------------------------------------------------
 * Contract 7: render policy (#111, #93). Bitmap allocation and presentation stay
 * native; the size clamp, the flags, form-field drawing and "PDFium refused"
 * live here.
 * ----------------------------------------------------------------------- */

/** Longest side any page raster may have (the common GPU texture limit). */
#define MEGAPDF_RENDER_MAX_SIDE 16384
/** Most pixels any page raster may have: 32 MP is 128 MB of BGRA. */
#define MEGAPDF_RENDER_MAX_PIXELS 32000000LL

/**
 * The pixel size to render at for a page that would ideally be ideal_width ×
 * ideal_height pixels: aspect ratio preserved, never below 1 × 1, never past the
 * side or megapixel limits. A poster-sized scan at 300% on a 2× display asks for
 * hundreds of megapixels that nothing on screen needs; the view scales the
 * raster up over the remaining distance.
 */
MEGAPDF_API void megapdf_render_size(double ideal_width, double ideal_height, int* out_width, int* out_height);

/** 1 when megapdf_render_size() would shrink this request. */
MEGAPDF_API int megapdf_render_is_capped(double ideal_width, double ideal_height);

enum {
    MEGAPDF_RENDER_BGRA = 0,   /* PDFium's native byte order (Windows, macOS, iOS) */
    MEGAPDF_RENDER_RGBA = 1    /* byte-reversed, for Android's ARGB_8888 buffers */
};

/**
 * Renders the page into the caller's width × height buffer of `stride` bytes per
 * row (at least width × 4): white ground, page content with annotations and LCD
 * text, then live form-field values. MEGAPDF_ERR_ARGUMENT for a null page or
 * buffer, a non-positive size or a size past the clamp; MEGAPDF_ERR_PDFIUM when
 * PDFium refuses the bitmap. Never crashes on a refusal.
 */
MEGAPDF_API int megapdf_render(const megapdf_page* page, void* buffer, int width, int height, int stride,
                               unsigned int flags);

/* --------------------------------------------------------------------------
 * Phase 3: body-text editing, written once (#112, SDD §3.1). The tiers:
 *   1. the run's own font covers the new text → edit in place;
 *   2. it does not (a subset-embedded font only carries the glyphs the document
 *      already uses, and PDFium would silently draw notdef boxes) → replace the
 *      run with the closest standard face, same index, matrix, colour and marks;
 *   3. rasterised text has no run to edit — the caller reports that; nothing here
 *      pretends otherwise.
 * Undo is detach-and-restore (contract 5); megapdf_insert_text_run replays a
 * journalled restore after a crash.
 * ----------------------------------------------------------------------- */

enum {
    MEGAPDF_EDIT_IN_PLACE = 0,     /* tier 1: the document's own font rendered the new text */
    MEGAPDF_EDIT_SUBSTITUTED = 1   /* tier 2: a similar standard face was used; the UI shows a notice */
};

/** Flags for megapdf_set_text(). */
enum {
    /** Skip tier 1 and substitute — the test hook the desktop suite uses. */
    MEGAPDF_SET_TEXT_FORCE_SUBSTITUTE = 1
};

/**
 * Sets a text run's text. `out_outcome` receives MEGAPDF_EDIT_IN_PLACE (the run's
 * own font drew it) or MEGAPDF_EDIT_SUBSTITUTED (a standard face did).
 *
 * The original object is never modified. Either way the edited run is a new
 * text object at `object_index` — same font size, matrix, colours, render mode
 * and text-box identity — and the original leaves the page detached, together with
 * any hidden copy of the run drawn under it (#136; see megapdf_detach_text_runs). They
 * are handed back through `out_replaced` when non-NULL, so an undo restores them
 * byte-identical with megapdf_restore_detached(), which takes the edited run off
 * first. With `out_replaced` NULL they are freed. (PDFium cannot read a text object's
 * character codes back, so an edit made on the original itself could never be undone
 * exactly, #117.) When the run had no hidden copy the handle holds the one original,
 * and detaching the edited run and megapdf_restore_object() at `object_index` undoes
 * the edit as well.
 *
 * MEGAPDF_ERR_ARGUMENT for a non-text object, empty text or an unknown flag;
 * MEGAPDF_ERR_NO_FONT when not even the substitute can draw the text;
 * MEGAPDF_ERR_LAYOUT when megapdf_text_editable() says no — the document is untouched.
 */
MEGAPDF_API int megapdf_set_text(const megapdf_page* page, int object_index, const unsigned short* text,
                                 unsigned int flags, int* out_outcome, megapdf_detached** out_replaced);

/**
 * Retypes a visual line: megapdf_set_text() on `object_indices[0]`, and the line's other
 * runs removed, all in one call, with the hidden copies of every run (#136). Indices as
 * the page is now, without repeats. One handle holds every original; undo it with
 * megapdf_restore_detached(). The same errors as megapdf_set_text(), with the page
 * untouched; MEGAPDF_ERR_ARGUMENT too for a bad or repeated index among the rest, and
 * MEGAPDF_ERR_LAYOUT when megapdf_text_editable() refuses any of the runs.
 */
MEGAPDF_API int megapdf_set_line_text(const megapdf_page* page, const int* object_indices, size_t count,
                                      const unsigned short* text, unsigned int flags, int* out_outcome,
                                      megapdf_detached** out_replaced);

/**
 * Inserts a text object at `object_index` with its baseline starting at
 * (left, baseline) in crop space, in the standard face closest to `font_name`
 * (any name; see megapdf_map_to_standard_font). Untagged — this recreates body
 * text from the recovery journal, not a text box.
 */
MEGAPDF_API int megapdf_insert_text_run(const megapdf_page* page, int object_index, const unsigned short* text,
                                        const char* font_name, double font_size, double left, double baseline);

/**
 * Whether the text object at `object_index` can be edited or removed without PDFium
 * changing anything else about the page (#118): 1 yes, 0 no, MEGAPDF_ERR_ARGUMENT for
 * a bad page or index.
 *
 * Changing a text object makes PDFium rewrite the content stream that holds it, and
 * its writer drops text state it has no syntax for — character and word spacing,
 * horizontal scaling, rise — and turns colour spaces into device colour. On many
 * real documents that moves or restyles text the user never touched. The answer
 * comes from a dry run on a copy of the page: rewrite the stream there, with the
 * object's hidden copies (#136), save, reopen, and compare the render and every text
 * object's position and text. Cached per page and object until the page next changes
 * (#137). megapdf_set_text(), megapdf_set_line_text(), megapdf_detach_text_runs() and
 * megapdf_detach_object() (for body text) refuse what this refuses, so the apps can ask
 * first and say so when a line is tapped. megapdf_text_editable_reason() says why.
 */
MEGAPDF_API int megapdf_text_editable(const megapdf_page* page, int object_index);

/** Why megapdf_text_editable() answered as it did (#128): megapdf_layout_verdict.cause. */
enum {
    MEGAPDF_LAYOUT_OK = 0,              /* editable: the rewrite changed nothing past the budgets below */
    MEGAPDF_LAYOUT_RENDER = 1,          /* more than 0.05% of the page's pixels would look different */
    MEGAPDF_LAYOUT_TEXT_MOVED = 2,      /* a text object's bounds would move by more than 0.5 pt */
    MEGAPDF_LAYOUT_TEXT_CHANGED = 3,    /* the page would have a different number of text objects, or different text */
    MEGAPDF_LAYOUT_REWRITE_FAILED = 4   /* PDFium could not rewrite, save or reopen the copy of the page */
};

/** Where the changed pixels are: megapdf_layout_verdict.where, a bitmask. */
enum {
    MEGAPDF_LAYOUT_WHERE_OBJECT = 1,    /* on the judged object (with its hidden copies), padded by 2 pt */
    MEGAPDF_LAYOUT_WHERE_TEXT = 2,      /* elsewhere, on another text object's bounds */
    MEGAPDF_LAYOUT_WHERE_OTHER = 4      /* elsewhere, off every text object: images, paths, forms, shadings */
};

/**
 * The dry run's verdict behind megapdf_text_editable(), with the numbers it saw.
 *
 * `cause` names the check that refused. When the text check and the render check both
 * fail, the text cause is given: a moved or changed run explains the pixels. The pixel
 * numbers are always filled in, also for an editable object (a change within the budget).
 * `changed_pixels` counts pixels whose |dR|+|dG|+|dB| exceeds 60 in a render of the page at
 * 72 dpi, halved until it has at most a million pixels; `total_pixels` is that render's size.
 * `max_shift_pt` is the largest move of any text object's bounds, text objects paired in
 * content order; 0 when their number changed. With MEGAPDF_LAYOUT_REWRITE_FAILED every
 * number is 0.
 */
typedef struct megapdf_layout_verdict {
    int editable;          /* 1 or 0, as megapdf_text_editable() */
    int cause;             /* MEGAPDF_LAYOUT_* */
    int where;             /* MEGAPDF_LAYOUT_WHERE_* bits; 0 when no pixel changed */
    int changed_pixels;
    int total_pixels;
    double max_shift_pt;
} megapdf_layout_verdict;

/**
 * megapdf_text_editable() with its reason: the same dry run, the same cache (#137), and
 * the same return value (1, 0 or MEGAPDF_ERR_ARGUMENT, also for a NULL `out`). `out` is
 * filled in when the return is 1 or 0. A line judged together in one dry run (by
 * megapdf_set_line_text() or megapdf_detach_text_runs()) caches the line's verdict for each
 * of its runs when it passes; when it is refused, each run is judged alone and keeps its own.
 */
MEGAPDF_API int megapdf_text_editable_reason(const megapdf_page* page, int object_index, megapdf_layout_verdict* out);

/**
 * The verdict behind this thread's most recent layout refusal (#128). megapdf_set_text(),
 * megapdf_set_line_text(), megapdf_detach_text_runs() and megapdf_detach_object() reset it
 * to editable (1, MEGAPDF_LAYOUT_OK, zeros) when called, and set it when the guard refuses
 * them. So read it straight after one of them, on the same thread: after
 * MEGAPDF_ERR_LAYOUT it holds the refused object's verdict (the first refused run of a
 * line), and after a NULL detach `editable == 0` tells a layout refusal from any other
 * failure. MEGAPDF_OK, or MEGAPDF_ERR_ARGUMENT for a NULL `out`.
 */
MEGAPDF_API int megapdf_last_layout_verdict(megapdf_layout_verdict* out);

/**
 * Whether regenerating the page's content would change how the page looks (#139): 1 no
 * (the page keeps its look), 0 yes, MEGAPDF_ERR_ARGUMENT for a bad page or a NULL `out`.
 * `out` is filled in when the return is 1 or 0.
 *
 * Every change that is not a body-text edit also makes PDFium rewrite the page's content:
 * adding, moving or removing a whiteout or a text box, megapdf_detach_object() on anything
 * but body text, megapdf_restore_object(), megapdf_replace_image_jpeg(), flattening. Those
 * changes are never refused; this lets the apps warn first. The answer is the
 * megapdf_text_editable() dry run with no text object changed: mark one object dirty on a
 * copy of the page, rewrite it, save, reopen, and compare the render and every text object
 * with a reopened copy of the page as it was, by the same budgets. `cause` and `where` are
 * as megapdf_layout_verdict describes; `where` never has MEGAPDF_LAYOUT_WHERE_OBJECT. A page
 * with no objects has nothing to rewrite and keeps its look. Cached per page until the page
 * next changes (#137).
 *
 * The same as megapdf_page_regeneration_verdict_cancellable() with no cancel flag, so it also
 * lets other calls run between its stages and returns MEGAPDF_ERR_CANCELLED when the
 * document is closed while it runs.
 */
MEGAPDF_API int megapdf_page_regeneration_verdict(const megapdf_page* page, megapdf_layout_verdict* out);

/**
 * megapdf_page_regeneration_verdict() for a check started early, in the background (#145).
 *
 * The dry run lets go of the core lock between its stages (copy and render, save, rewrite,
 * save again, compare), so other calls on any document — rendering, an edit, a save — wait at
 * most one stage, not the whole run. Between stages it stops when `cancel` (may be NULL) has
 * been raised or the page's document is being closed, and returns MEGAPDF_ERR_CANCELLED with
 * `out` untouched; megapdf_close() waits for a running check to stop. The page handle may be
 * closed while the check runs; its document must stay open until the call has begun.
 *
 * The answer is cached as megapdf_page_regeneration_verdict()'s is, except when the page
 * changed while the check ran: then it describes the page as it was and is not cached. Two
 * checks of one page at once each do the work; the apps keep one per page.
 */
MEGAPDF_API int megapdf_page_regeneration_verdict_cancellable(const megapdf_page* page, const megapdf_cancel* cancel,
                                                             megapdf_layout_verdict* out);

/**
 * The cached page verdict, without running anything (#145): 1 or 0 as
 * megapdf_page_regeneration_verdict() would return, MEGAPDF_ERR_NOT_JUDGED when the page has
 * not been judged since it last changed, MEGAPDF_ERR_ARGUMENT for a bad page or a NULL `out`.
 */
MEGAPDF_API int megapdf_page_regeneration_verdict_cached(const megapdf_page* page, megapdf_layout_verdict* out);

/** 1 when `base_name` is a subset-embedded font's name: six capitals, a plus sign, the name. */
MEGAPDF_API int megapdf_is_subset_font_name(const char* base_name);

/**
 * The standard-14 face closest to `original_name` (bold/italic from the name;
 * Courier for monospace, Times for serif, Helvetica otherwise). Writes a
 * NUL-terminated name into `out` when it fits; returns its length without the NUL.
 */
MEGAPDF_API size_t megapdf_map_to_standard_font(const char* original_name, char* out, size_t capacity);

#ifdef __cplusplus
}  /* extern "C" */
#endif

#endif /* MEGAPDF_CORE_H */
