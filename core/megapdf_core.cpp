// The one implementation of the shared policy (#33, phase 1).
//
// Phase 1 deliberately moves a *small* operation that already has assertions on
// all three platforms, so the packaging and binding work is proven before the
// text-edit layer — the expensive policy — depends on it.

#include "megapdf_core.h"

#include <vector>

#include "fpdf_edit.h"
#include "fpdfview.h"
#include "fpdf_transformpage.h"  // FPDFPage_GetCropBox

namespace {

struct CropOrigin {
    double x = 0.0;
    double y = 0.0;
};

CropOrigin CropOriginOf(FPDF_PAGE page) {
    float l = 0, b = 0, r = 0, t = 0;
    if (FPDFPage_GetCropBox(page, &l, &b, &r, &t) && r > l && t > b) {
        return CropOrigin{static_cast<double>(l), static_cast<double>(b)};
    }
    return CropOrigin{};
}

}  // namespace

extern "C" {

size_t megapdf_detect_checkbox_squares(void* page_handle, megapdf_rect* out,
                                       size_t capacity) {
    FPDF_PAGE page = static_cast<FPDF_PAGE>(page_handle);
    if (page == nullptr) return 0;

    const CropOrigin crop = CropOriginOf(page);
    size_t found = 0;
    const int count = FPDFPage_CountObjects(page);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, i);
        if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_PATH) continue;

        float l = 0, b = 0, r = 0, t = 0;
        if (!FPDFPageObj_GetBounds(obj, &l, &b, &r, &t)) continue;
        const float w = r - l, h = t - b;
        if (w < 6 || w > 24 || h < 6 || h > 24) continue;
        const float larger = w > h ? w : h;
        const float diff = w > h ? w - h : h - w;
        if (diff > 0.25f * larger) continue;

        int fillmode = 0;
        FPDF_BOOL stroke = 0;
        if (!FPDFPath_GetDrawMode(obj, &fillmode, &stroke)) continue;
        if (!stroke || fillmode != FPDF_FILLMODE_NONE) continue;

        if (found < capacity && out != nullptr) {
            out[found].left = static_cast<double>(l) - crop.x;
            out[found].bottom = static_cast<double>(b) - crop.y;
            out[found].right = static_cast<double>(r) - crop.x;
            out[found].top = static_cast<double>(t) - crop.y;
        }
        found++;
    }
    return found;
}

void megapdf_crop_origin(void* page_handle, double* out_x, double* out_y) {
    const CropOrigin crop = CropOriginOf(static_cast<FPDF_PAGE>(page_handle));
    if (out_x != nullptr) *out_x = crop.x;
    if (out_y != nullptr) *out_y = crop.y;
}

}  // extern "C"
