// Internal glue between megapdf_core.cpp and megapdf_structure.cpp (#353).
//
// Not part of the public ABI (megapdf_core.h): nothing here crosses a binding boundary, and
// no binding includes this file. It exists so the structure-inference pipeline can read a
// page's raw PDFium text page and take the core's own mutex, without megapdf_page's
// internals being duplicated in a second file or exposed as a public raw-handle accessor —
// the thing megapdf_core.h's own rules forbid bindings from having.
//
// Everything here is a thin read of state megapdf_core.cpp already owns; it adds no policy
// of its own.
#ifndef MEGAPDF_CORE_INTERNAL_H
#define MEGAPDF_CORE_INTERNAL_H

#include "megapdf_core.h"
#include "fpdfview.h"

#include <mutex>
#include <vector>

namespace megapdf_internal {

// The core's own mutex (#105): every ABI entry point, in either .cpp, takes this before
// touching PDFium. Recursive, so megapdf_structure.cpp may call back into the public ABI
// (megapdf_load_page, megapdf_form_fields_load, ...) while already holding it.
std::recursive_mutex& Lock();

FPDF_PAGE PageHandle(const megapdf_page* page);
FPDF_DOCUMENT DocumentHandle(const megapdf_document* document);
FPDF_FORMHANDLE FormHandle(const megapdf_document* document);
double PageUnit(const megapdf_page* page);

// The crop-space transform every coordinate leaving the core goes through (#28/#30/#150):
// user space, less the CropBox origin, times the page's /UserUnit, turned by the page's
// /Rotate (#439). A point takes both coordinates at once, because a quarter turn builds the
// crop-space x from the user-space y.
void ToCropPoint(const megapdf_page* page, double x, double y, double* out_x, double* out_y);
megapdf_rect ToCropRect(const megapdf_page* page, double l, double b, double r, double t);

// The turn alone, in quarter turns clockwise (0-3), read live from /Rotate the same way the
// transform above reads it. Contract 9's layout needs it to undo that turn (#472): a glyph box
// arrives already turned, but the text matrix it has to be measured against does not turn with
// it, so the two must be brought back into one space before any gap or ordering test.
int PageTurns(const megapdf_page* page);

// megapdf_last_error()/megapdf_last_error_message()'s thread-local slot (SetError() in
// megapdf_core.cpp), for a call outside megapdf_core.cpp that needs to report a failure the
// same way megapdf_open() and the rest of the ABI do. `code` is usually a PDFium FPDF_ERR_*
// value, but is not required to be one — MEGAPDF_OPEN_ERR_TOO_LARGE already sets this field
// to a MegaPDF-specific code above PDFium's own range, and megapdf_structure_load() follows
// that precedent for MEGAPDF_ERR_CANCELLED (cast to unsigned).
void SetLastError(unsigned long code, const char* message);

// The #145 cancel-flag pattern: 1 when `cancel` is non-NULL and has been raised.
bool IsCancelled(const megapdf_cancel* cancel);

// The dead-handle contract (#551) for a file outside megapdf_core.cpp: whether `page`'s document
// has closed, and the error a call refusing such a page reports (megapdf_last_error() ==
// MEGAPDF_LAST_ERR_CLOSED). Same one place deciding it as every entry point in that file.
bool PageClosed(const megapdf_page* page);
void ReportClosedPage();

// ---------------------------------------------------------------------------
// Contract 11, text selection (#710): a page's characters laid out by contract 9's own rules,
// built in megapdf_structure.cpp (which owns those rules) and queried by megapdf_select.cpp.
//
// Everything is in the page's LAYOUT FRAME (contract 9's #472 PageFrame): crop space with the
// turn its text reads in taken back out, so the text advances along +x. For almost every page
// that is crop space itself (turns == 0). SelectionToCrop/CropToSelection convert.
// ---------------------------------------------------------------------------

struct SelectionChar {
    unsigned int cp = 0;              // what the copy emits (a PDFium-masked hyphen already U+002D)
    double l = 0, b = 0, r = 0, t = 0;  // the glyph's advance box (FPDFText_GetLooseCharBox); a
                                        // ligature's box is split evenly among its characters
    int word = 0;                     // words are numbered in reading order across the page
    int line = 0;                     // index into SelectionLayout::lines
};

struct SelectionLine {
    double l = 0, b = 0, r = 0, t = 0;  // the union of its characters' boxes
    int first = 0;                      // its characters are chars[first, end)
    int end = 0;
    int region = 0;                     // the XY-cut leaf it belongs to
};

struct SelectionLayout {
    std::vector<SelectionChar> chars;   // reading order
    std::vector<SelectionLine> lines;   // reading order
    int regions = 0;
    int turns = 0;                      // the layout frame: quarter turns undone, 0-3
    double frame_w = 0, frame_h = 0;    // the page's size in that frame
};

// Reads `page` into `out`. The caller holds Lock() and has checked the page is alive. False only
// when the core could not allocate; a page with no text is a true return with no characters.
bool BuildSelectionLayout(const megapdf_page* page, SelectionLayout* out);
megapdf_rect SelectionToCrop(const SelectionLayout& layout, const megapdf_rect& r);
void CropToSelection(const SelectionLayout& layout, double x, double y, double* out_x, double* out_y);

}  // namespace megapdf_internal

#endif  // MEGAPDF_CORE_INTERNAL_H
