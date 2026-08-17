// MegaPDF shared engine core — the C ABI (#33, phase 1).
//
// The problem this exists to solve is #30: the same CropBox bug had to be fixed
// on Windows, then Android, and was missed on iOS, because the policy lives in
// three hand-written copies. This library holds one copy, below the UI and above
// PDFium, and each platform binds to it — P/Invoke on Windows, JNI on Android,
// Swift C interop on iOS. Native UI stays native (SDD §6.1 is unchanged; MAUI/Uno
// remain rejected).
//
// ABI rules, so three toolchains can agree:
//   * C only. No C++ types cross the boundary, no exceptions escape.
//   * Handles are void*: pass the caller's FPDF_PAGE straight through. The header
//     deliberately does not include pdfium's, so binding code needs nothing but
//     this file.
//   * Buffers are caller-owned. A count function pattern (return the number
//     found, fill up to `capacity`) keeps allocation on the caller's side, which
//     is what makes JNI and P/Invoke marshalling simple.
//   * Coordinates are **crop space** — the CropBox origin already subtracted.
//     That is the whole point: getting it wrong is #30, so it happens once here
//     rather than in each binding.

#ifndef MEGAPDF_CORE_H
#define MEGAPDF_CORE_H

#include <stddef.h>

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

/**
 * Drawn-checkbox candidates on `page` (SDD §6.2 contract 2): stroked-not-filled
 * path objects, 6-24 pt on both axes, squareness within 25%.
 *
 * @param page      an FPDF_PAGE, already loaded by the caller.
 * @param out       buffer to fill; may be NULL when `capacity` is 0.
 * @param capacity  how many rects `out` can hold.
 * @return the number of candidates found, which may exceed `capacity` — call
 *         again with a larger buffer to get them all.
 */
size_t megapdf_detect_checkbox_squares(void* page, megapdf_rect* out, size_t capacity);

/**
 * The page's CropBox origin, or (0,0) when it has none. Exposed because bindings
 * still convert coordinates of their own on the way *in* (a tap, a placement).
 */
void megapdf_crop_origin(void* page, double* out_x, double* out_y);

#ifdef __cplusplus
}  /* extern "C" */
#endif

#endif  /* MEGAPDF_CORE_H */
