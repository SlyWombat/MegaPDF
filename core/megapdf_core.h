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
//   * Coordinates are crop space — bottom-left origin, PDF points, the CropBox
//     origin already subtracted. Getting that wrong is #30, so it happens once.
//   * Thread safety: every call takes the core's own mutex, because PDFium is not
//     thread-safe and that is a property of the library, not of any platform.
//     Bindings may keep their own discipline on top; correctness does not need it.
//
// Migration note: until every contract has moved (#112), bindings still
// call PDFium directly for the rest, through the *_raw accessors. Those go away
// with the last migrated contract.
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
    MEGAPDF_ERR_MEMORY = -3       /* the core could not allocate */
};

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
 * Opens a document from memory. The bytes are copied; the caller may free them
 * on return. `password_utf8` may be NULL. Returns NULL on failure, with
 * megapdf_last_error() carrying PDFium's code (FPDF_ERR_PASSWORD when one is
 * required or wrong). The form-fill environment is initialised here, so form
 * fields render and toggle without any binding-side setup.
 */
MEGAPDF_API megapdf_document* megapdf_open(const void* bytes, size_t length, const char* password_utf8);

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

/** Page size in points — the CropBox size, which is what a viewer shows. */
MEGAPDF_API double megapdf_page_width(const megapdf_page* page);
MEGAPDF_API double megapdf_page_height(const megapdf_page* page);

/** The CropBox origin in PDF user space that every returned coordinate has had subtracted. */
MEGAPDF_API void megapdf_page_crop_origin(const megapdf_page* page, double* out_x, double* out_y);

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
 * Detached objects: a page object removed from its page but kept alive so an
 * undo can put it back byte-identical. The core owns it; restoring consumes the
 * handle, discarding frees it, and closing the document frees any still held.
 */
typedef struct megapdf_detached megapdf_detached;
MEGAPDF_API megapdf_detached* megapdf_detach_object(const megapdf_page* page, int object_index);
MEGAPDF_API int megapdf_restore_object(const megapdf_page* page, megapdf_detached* detached, int object_index);
MEGAPDF_API void megapdf_discard_detached(megapdf_detached* detached);

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
 * Raw handles — for the contracts that have not migrated yet. Bindings use these
 * to keep calling PDFium directly for stamps, text, forms, save; each disappears
 * as its contract moves into the core.
 * ----------------------------------------------------------------------- */

MEGAPDF_API void* megapdf_document_raw(const megapdf_document* document);      /* FPDF_DOCUMENT */
MEGAPDF_API void* megapdf_document_form_raw(const megapdf_document* document); /* FPDF_FORMHANDLE */
MEGAPDF_API void* megapdf_page_raw(const megapdf_page* page);                  /* FPDF_PAGE */

#ifdef __cplusplus
}  /* extern "C" */
#endif

#endif /* MEGAPDF_CORE_H */
