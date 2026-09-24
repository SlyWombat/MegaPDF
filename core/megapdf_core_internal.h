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
// user space, less the CropBox origin, times the page's /UserUnit.
double ToCropX(const megapdf_page* page, double x);
double ToCropY(const megapdf_page* page, double y);
megapdf_rect ToCropRect(const megapdf_page* page, double l, double b, double r, double t);

// megapdf_last_error()/megapdf_last_error_message()'s thread-local slot (SetError() in
// megapdf_core.cpp), for a call outside megapdf_core.cpp that needs to report a failure the
// same way megapdf_open() and the rest of the ABI do. `code` is usually a PDFium FPDF_ERR_*
// value, but is not required to be one — MEGAPDF_OPEN_ERR_TOO_LARGE already sets this field
// to a MegaPDF-specific code above PDFium's own range, and megapdf_structure_load() follows
// that precedent for MEGAPDF_ERR_CANCELLED (cast to unsigned).
void SetLastError(unsigned long code, const char* message);

// The #145 cancel-flag pattern: 1 when `cancel` is non-NULL and has been raised.
bool IsCancelled(const megapdf_cancel* cancel);

}  // namespace megapdf_internal

#endif  // MEGAPDF_CORE_INTERNAL_H
