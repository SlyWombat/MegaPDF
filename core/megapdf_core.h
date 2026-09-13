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
// Migration note: until every contract has moved (#106–#112), bindings still
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
