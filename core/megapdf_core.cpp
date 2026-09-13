// The one implementation of the shared engine policy (ADR-003, #33).
//
// Phase 1 moved a single heuristic here to prove the packaging. From #105 the
// core owns documents: bytes in, opaque handles out, and the form-fill
// environment, page lifecycle and serialising mutex live here rather than in
// three bindings.

#include "megapdf_core.h"

#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#include "fpdf_edit.h"
#include "fpdf_formfill.h"
#include "fpdf_text.h"
#include "fpdf_transformpage.h"  // FPDFPage_GetCropBox
#include "fpdfview.h"

// --------------------------------------------------------------------------
// Internals
// --------------------------------------------------------------------------

struct megapdf_document {
    std::vector<unsigned char> bytes;   // FPDF_LoadMemDocument64 needs the buffer alive for the document's life.
    FPDF_DOCUMENT doc = nullptr;
    FPDF_FORMHANDLE form = nullptr;
    FPDF_FORMFILLINFO ffi{};
    std::vector<megapdf_page*> open_pages;  // closed for the caller if still open at megapdf_close()
};

struct megapdf_page {
    megapdf_document* owner = nullptr;
    FPDF_PAGE page = nullptr;
    double crop_x = 0.0;
    double crop_y = 0.0;
};

namespace {

// PDFium is not thread-safe. Every ABI entry point takes this; recursive so a
// future core routine may call another ABI routine without deadlocking.
std::recursive_mutex& CoreLock() {
    static std::recursive_mutex lock;
    return lock;
}
using Guard = std::lock_guard<std::recursive_mutex>;

thread_local unsigned int g_last_error = 0;
thread_local std::string g_last_message;

void SetError(unsigned long code, const char* message) {
    // PDFium's code is an unsigned long; the ABI narrows it (values are single digits).
    g_last_error = static_cast<unsigned int>(code);
    g_last_message = message ? message : "";
}

void EnsureLibrary() {
    static bool initialised = false;   // guarded by CoreLock()
    if (!initialised) {
        FPDF_InitLibrary();
        initialised = true;
    }
}

// The same no-op FPDF_FORMFILLINFO every platform has used: no JavaScript, no XFA,
// no timers. Form fields still render and toggle through the form handle.
void FfiInvalidate(FPDF_FORMFILLINFO*, FPDF_PAGE, double, double, double, double) {}
void FfiOutputSelectedRect(FPDF_FORMFILLINFO*, FPDF_PAGE, double, double, double, double) {}
void FfiSetCursor(FPDF_FORMFILLINFO*, int) {}
int FfiSetTimer(FPDF_FORMFILLINFO*, int, TimerCallback) { return 0; }
void FfiKillTimer(FPDF_FORMFILLINFO*, int) {}
FPDF_SYSTEMTIME FfiGetLocalTime(FPDF_FORMFILLINFO*) { return FPDF_SYSTEMTIME{}; }
void FfiOnChange(FPDF_FORMFILLINFO*) {}
FPDF_PAGE FfiGetPage(FPDF_FORMFILLINFO*, FPDF_DOCUMENT, int) { return nullptr; }
FPDF_PAGE FfiGetCurrentPage(FPDF_FORMFILLINFO*, FPDF_DOCUMENT) { return nullptr; }
int FfiGetRotation(FPDF_FORMFILLINFO*, FPDF_PAGE) { return 0; }
void FfiExecuteNamedAction(FPDF_FORMFILLINFO*, FPDF_BYTESTRING) {}
void FfiSetTextFieldFocus(FPDF_FORMFILLINFO*, FPDF_WIDESTRING, FPDF_DWORD, FPDF_BOOL) {}
void FfiDoURIAction(FPDF_FORMFILLINFO*, FPDF_BYTESTRING) {}
void FfiDoGoToAction(FPDF_FORMFILLINFO*, int, int, float*, int) {}

void InitFormFillInfo(FPDF_FORMFILLINFO* ffi) {
    std::memset(ffi, 0, sizeof(*ffi));
    ffi->version = 1;
    ffi->FFI_Invalidate = FfiInvalidate;
    ffi->FFI_OutputSelectedRect = FfiOutputSelectedRect;
    ffi->FFI_SetCursor = FfiSetCursor;
    ffi->FFI_SetTimer = FfiSetTimer;
    ffi->FFI_KillTimer = FfiKillTimer;
    ffi->FFI_GetLocalTime = FfiGetLocalTime;
    ffi->FFI_OnChange = FfiOnChange;
    ffi->FFI_GetPage = FfiGetPage;
    ffi->FFI_GetCurrentPage = FfiGetCurrentPage;
    ffi->FFI_GetRotation = FfiGetRotation;
    ffi->FFI_ExecuteNamedAction = FfiExecuteNamedAction;
    ffi->FFI_SetTextFieldFocus = FfiSetTextFieldFocus;
    ffi->FFI_DoURIAction = FfiDoURIAction;
    ffi->FFI_DoGoToAction = FfiDoGoToAction;
}

// pdfium reports content in user space (MediaBox origin) but renders the CropBox.
// Every coordinate that leaves the core has this subtracted (#28/#30).
void ReadCropOrigin(FPDF_PAGE page, double* x, double* y) {
    float l = 0, b = 0, r = 0, t = 0;
    if (page != nullptr && FPDFPage_GetCropBox(page, &l, &b, &r, &t) && r > l && t > b) {
        *x = static_cast<double>(l);
        *y = static_cast<double>(b);
    } else {
        *x = 0.0;
        *y = 0.0;
    }
}

void ClosePageUnlocked(megapdf_page* p) {
    if (p->owner != nullptr && p->owner->form != nullptr) FORM_OnBeforeClosePage(p->page, p->owner->form);
    FPDF_ClosePage(p->page);
    delete p;
}

}  // namespace

extern "C" {

// --------------------------------------------------------------------------
// Errors
// --------------------------------------------------------------------------

MEGAPDF_API unsigned int megapdf_last_error(void) { return g_last_error; }

MEGAPDF_API const char* megapdf_last_error_message(void) { return g_last_message.c_str(); }

// --------------------------------------------------------------------------
// Documents
// --------------------------------------------------------------------------

MEGAPDF_API megapdf_document* megapdf_open(const void* bytes, size_t length, const char* password_utf8) {
    Guard guard(CoreLock());
    EnsureLibrary();
    if (bytes == nullptr || length == 0) {
        SetError(FPDF_ERR_FILE, "no bytes to open");
        return nullptr;
    }
    auto* d = new (std::nothrow) megapdf_document();
    if (d == nullptr) {
        SetError(FPDF_ERR_UNKNOWN, "out of memory");
        return nullptr;
    }
    try {
        d->bytes.assign(static_cast<const unsigned char*>(bytes), static_cast<const unsigned char*>(bytes) + length);
    } catch (...) {
        delete d;
        SetError(FPDF_ERR_UNKNOWN, "out of memory copying the document");
        return nullptr;
    }
    d->doc = FPDF_LoadMemDocument64(d->bytes.data(), d->bytes.size(), password_utf8);
    if (d->doc == nullptr) {
        const unsigned long code = FPDF_GetLastError();
        delete d;
        SetError(code, code == FPDF_ERR_PASSWORD ? "the document needs a password, or the password is wrong"
                     : code == FPDF_ERR_FORMAT   ? "the file is not a valid PDF"
                     : code == FPDF_ERR_SECURITY ? "the document's security handler is not supported"
                                                  : "PDFium could not load the document");
        return nullptr;
    }
    InitFormFillInfo(&d->ffi);
    d->form = FPDFDOC_InitFormFillEnvironment(d->doc, &d->ffi);
    // A missing form environment is survivable (no AcroForm interaction); every
    // platform has treated it that way.
    SetError(0, "");
    return d;
}

MEGAPDF_API void megapdf_close(megapdf_document* d) {
    if (d == nullptr) return;
    Guard guard(CoreLock());
    for (megapdf_page* p : d->open_pages) {
        p->owner = nullptr;   // the document is going; do not call back into its form handle
        if (d->form != nullptr) FORM_OnBeforeClosePage(p->page, d->form);
        FPDF_ClosePage(p->page);
        delete p;
    }
    d->open_pages.clear();
    if (d->form != nullptr) FPDFDOC_ExitFormFillEnvironment(d->form);
    if (d->doc != nullptr) FPDF_CloseDocument(d->doc);
    delete d;
}

MEGAPDF_API int megapdf_page_count(const megapdf_document* d) {
    if (d == nullptr) return 0;
    Guard guard(CoreLock());
    return FPDF_GetPageCount(d->doc);
}

// --------------------------------------------------------------------------
// Pages
// --------------------------------------------------------------------------

MEGAPDF_API megapdf_page* megapdf_load_page(megapdf_document* d, int index) {
    if (d == nullptr) {
        SetError(0, "null document");
        return nullptr;
    }
    Guard guard(CoreLock());
    FPDF_PAGE page = FPDF_LoadPage(d->doc, index);
    if (page == nullptr) {
        SetError(FPDF_GetLastError(), "the page could not be loaded");
        return nullptr;
    }
    if (d->form != nullptr) FORM_OnAfterLoadPage(page, d->form);
    auto* p = new (std::nothrow) megapdf_page();
    if (p == nullptr) {
        FPDF_ClosePage(page);
        SetError(FPDF_ERR_UNKNOWN, "out of memory");
        return nullptr;
    }
    p->owner = d;
    p->page = page;
    ReadCropOrigin(page, &p->crop_x, &p->crop_y);
    d->open_pages.push_back(p);
    return p;
}

MEGAPDF_API void megapdf_close_page(megapdf_page* p) {
    if (p == nullptr) return;
    Guard guard(CoreLock());
    if (p->owner != nullptr) {
        auto& pages = p->owner->open_pages;
        for (size_t i = 0; i < pages.size(); i++) {
            if (pages[i] == p) {
                pages[i] = pages.back();
                pages.pop_back();
                break;
            }
        }
    }
    ClosePageUnlocked(p);
}

MEGAPDF_API double megapdf_page_width(const megapdf_page* p) {
    if (p == nullptr) return 0.0;
    Guard guard(CoreLock());
    return static_cast<double>(FPDF_GetPageWidthF(p->page));
}

MEGAPDF_API double megapdf_page_height(const megapdf_page* p) {
    if (p == nullptr) return 0.0;
    Guard guard(CoreLock());
    return static_cast<double>(FPDF_GetPageHeightF(p->page));
}

MEGAPDF_API void megapdf_page_crop_origin(const megapdf_page* p, double* out_x, double* out_y) {
    if (out_x != nullptr) *out_x = p ? p->crop_x : 0.0;
    if (out_y != nullptr) *out_y = p ? p->crop_y : 0.0;
}

// --------------------------------------------------------------------------
// Contracts
// --------------------------------------------------------------------------

MEGAPDF_API size_t megapdf_detect_checkbox_squares(const megapdf_page* p, megapdf_rect* out, size_t capacity) {
    if (p == nullptr) return 0;
    Guard guard(CoreLock());
    FPDF_PAGE page = p->page;
    size_t found = 0;
    const int count = FPDFPage_CountObjects(page);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, i);
        if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_PATH) continue;

        float l = 0, b = 0, r = 0, t = 0;
        if (!FPDFPageObj_GetBounds(obj, &l, &b, &r, &t)) continue;
        const float w = r - l, h = t - b;
        // Small, roughly square (bounds include stroke width, so allow slack).
        if (w < 6 || w > 24 || h < 6 || h > 24) continue;
        const float larger = w > h ? w : h;
        const float diff = w > h ? w - h : h - w;
        if (diff > 0.25f * larger) continue;

        // Checkbox outlines are stroked, not filled — filled squares are usually
        // decoration (bullets, table shading), per SDD §3.2.
        int fillmode = 0;
        FPDF_BOOL stroke = 0;
        if (!FPDFPath_GetDrawMode(obj, &fillmode, &stroke)) continue;
        if (!stroke || fillmode != FPDF_FILLMODE_NONE) continue;

        if (found < capacity && out != nullptr) {
            out[found].left = static_cast<double>(l) - p->crop_x;
            out[found].bottom = static_cast<double>(b) - p->crop_y;
            out[found].right = static_cast<double>(r) - p->crop_x;
            out[found].top = static_cast<double>(t) - p->crop_y;
        }
        found++;
    }
    return found;
}

MEGAPDF_API size_t megapdf_search_page(const megapdf_page* p, const unsigned short* term_utf16,
                                       double* out, size_t capacity) {
    if (p == nullptr || term_utf16 == nullptr || term_utf16[0] == 0) return 0;
    Guard guard(CoreLock());

    FPDF_TEXTPAGE text = FPDFText_LoadPage(p->page);
    if (text == nullptr) return 0;

    size_t written = 0;   // doubles that would have been written
    auto emit = [&](double v) {
        if (written < capacity && out != nullptr) out[written] = v;
        written++;
    };

    // Flags 0 = case-insensitive substring — the only mode the product offers (#26).
    FPDF_SCHHANDLE find = FPDFText_FindStart(text, reinterpret_cast<FPDF_WIDESTRING>(term_utf16), 0, 0);
    if (find != nullptr) {
        while (FPDFText_FindNext(find)) {
            const int start = FPDFText_GetSchResultIndex(find);
            const int count = FPDFText_GetSchCount(find);
            const int rects = FPDFText_CountRects(text, start, count);
            if (rects <= 0) continue;

            // Gather first, so a match whose rects all fail to read is dropped whole,
            // exactly as every platform's binding has done.
            std::vector<double> match;
            match.reserve(static_cast<size_t>(rects) * 4);
            for (int i = 0; i < rects; i++) {
                double l = 0, t = 0, r = 0, b = 0;
                if (!FPDFText_GetRect(text, i, &l, &t, &r, &b)) continue;
                match.push_back(l - p->crop_x);
                match.push_back(b - p->crop_y);
                match.push_back(r - p->crop_x);
                match.push_back(t - p->crop_y);
            }
            if (match.empty()) continue;
            emit(static_cast<double>(match.size() / 4));
            for (double v : match) emit(v);
        }
        FPDFText_FindClose(find);
    }
    FPDFText_ClosePage(text);
    return written;
}

// --------------------------------------------------------------------------
// Raw handles (transitional)
// --------------------------------------------------------------------------

MEGAPDF_API void* megapdf_document_raw(const megapdf_document* d) { return d ? d->doc : nullptr; }
MEGAPDF_API void* megapdf_document_form_raw(const megapdf_document* d) { return d ? d->form : nullptr; }
MEGAPDF_API void* megapdf_page_raw(const megapdf_page* p) { return p ? p->page : nullptr; }

}  // extern "C"
