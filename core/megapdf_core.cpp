// The one implementation of the shared engine policy (ADR-003, #33).
//
// Phase 1 moved a single heuristic here to prove the packaging. From #105 the
// core owns documents: bytes in, opaque handles out, and the form-fill
// environment, page lifecycle and serialising mutex live here rather than in
// three bindings.

#include "megapdf_core.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#include "fpdf_annot.h"
#include "fpdf_edit.h"
#include "fpdf_formfill.h"
#include "fpdf_flatten.h"
#include "fpdf_save.h"
#include "fpdf_text.h"
#include "fpdf_transformpage.h"  // FPDFPage_GetCropBox
#include "fpdfview.h"

// --------------------------------------------------------------------------
// Internals
// --------------------------------------------------------------------------

struct megapdf_detached;

struct megapdf_document {
    std::vector<unsigned char> bytes;   // FPDF_LoadMemDocument64 needs the buffer alive for the document's life.
    FPDF_DOCUMENT doc = nullptr;
    FPDF_FORMHANDLE form = nullptr;
    FPDF_FORMFILLINFO ffi{};
    std::vector<megapdf_page*> open_pages;  // closed for the caller if still open at megapdf_close()
    std::vector<megapdf_detached*> detached;  // freed at megapdf_close() if never restored or discarded
};

struct megapdf_detached {
    megapdf_document* owner = nullptr;
    FPDF_PAGEOBJECT object = nullptr;
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
    for (megapdf_detached* x : d->detached) {
        FPDFPageObj_Destroy(x->object);
        delete x;
    }
    d->detached.clear();
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

}  // extern "C"

// --------------------------------------------------------------------------
// Contract 2: text runs and visual lines (#106)
// --------------------------------------------------------------------------

namespace {

using U16 = std::vector<unsigned short>;

struct TextRun {
    megapdf_text_run info{};
    U16 text;
    U16 font;
    U16 box_id;
    U16 box_font;
};

struct TextLine {
    megapdf_rect bounds{};
    std::vector<size_t> runs;
};

// .NET's char.IsWhiteSpace, which is what the desktop engine used to skip
// runs with nothing visible in them.
bool IsWhiteSpace(unsigned short c) {
    return (c >= 0x09 && c <= 0x0D) || c == 0x20 || c == 0x85 || c == 0xA0 || c == 0x1680 ||
           (c >= 0x2000 && c <= 0x200A) || c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000;
}

bool AllWhiteSpace(const U16& s) {
    for (unsigned short c : s) if (!IsWhiteSpace(c)) return false;
    return true;
}

U16 Utf8ToUtf16(const std::vector<unsigned char>& in) {
    U16 out;
    size_t i = 0;
    while (i < in.size()) {
        unsigned int cp;
        unsigned char b = in[i];
        size_t extra;
        if (b < 0x80) { cp = b; extra = 0; }
        else if ((b & 0xE0) == 0xC0) { cp = b & 0x1F; extra = 1; }
        else if ((b & 0xF0) == 0xE0) { cp = b & 0x0F; extra = 2; }
        else if ((b & 0xF8) == 0xF0) { cp = b & 0x07; extra = 3; }
        else { cp = 0xFFFD; extra = 0; }
        for (size_t k = 1; k <= extra; k++) {
            if (i + k >= in.size() || (in[i + k] & 0xC0) != 0x80) { cp = 0xFFFD; extra = k - 1; break; }
            cp = (cp << 6) | (in[i + k] & 0x3F);
        }
        i += extra + 1;
        if (cp >= 0x10000) {
            cp -= 0x10000;
            out.push_back(static_cast<unsigned short>(0xD800 + (cp >> 10)));
            out.push_back(static_cast<unsigned short>(0xDC00 + (cp & 0x3FF)));
        } else {
            out.push_back(static_cast<unsigned short>(cp));
        }
    }
    return out;
}

// FPDFTextObj_GetText's length is in BYTES including the UTF-16 terminator,
// whatever the header says — verified against pdfium 152 on every platform.
U16 ReadObjectText(FPDF_PAGEOBJECT obj, FPDF_TEXTPAGE text_page) {
    const unsigned long bytes = FPDFTextObj_GetText(obj, text_page, nullptr, 0);
    if (bytes <= 2) return {};
    U16 buf(bytes / 2);
    FPDFTextObj_GetText(obj, text_page, buf.data(), bytes);
    buf.resize(bytes / 2 - 1);
    return buf;
}

U16 ReadFontFamily(FPDF_PAGEOBJECT obj) {
    FPDF_FONT font = FPDFTextObj_GetFont(obj);
    if (font == nullptr) return {};
    const size_t bytes = FPDFFont_GetFamilyName(font, nullptr, 0);   // UTF-8, with terminator
    if (bytes <= 1) return {};
    std::vector<unsigned char> buf(bytes);
    FPDFFont_GetFamilyName(font, reinterpret_cast<char*>(buf.data()), static_cast<unsigned long>(bytes));
    buf.resize(bytes - 1);
    return Utf8ToUtf16(buf);
}

bool HasMark(FPDF_PAGEOBJECT obj, const U16& name) {
    const int marks = FPDFPageObj_CountMarks(obj);
    for (int m = 0; m < marks; m++) {
        FPDF_PAGEOBJECTMARK mark = FPDFPageObj_GetMark(obj, static_cast<unsigned long>(m));
        if (mark == nullptr) continue;
        unsigned long bytes = 0;
        FPDFPageObjMark_GetName(mark, nullptr, 0, &bytes);
        if (bytes <= 2) continue;
        U16 buf(bytes / 2);
        FPDFPageObjMark_GetName(mark, buf.data(), bytes, &bytes);
        buf.resize(bytes / 2 - 1);
        if (buf == name) return true;
    }
    return false;
}

U16 ReadMarkParam(FPDF_PAGEOBJECT obj, const char* key) {
    const int marks = FPDFPageObj_CountMarks(obj);
    for (int m = 0; m < marks; m++) {
        FPDF_PAGEOBJECTMARK mark = FPDFPageObj_GetMark(obj, static_cast<unsigned long>(m));
        if (mark == nullptr) continue;
        unsigned long bytes = 0;
        FPDFPageObjMark_GetParamStringValue(mark, key, nullptr, 0, &bytes);
        if (bytes <= 2) continue;
        U16 buf(bytes / 2);
        if (!FPDFPageObjMark_GetParamStringValue(mark, key, buf.data(), bytes, &bytes)) continue;
        buf.resize(bytes / 2 - 1);
        return buf;
    }
    return {};
}

const U16 kTextBoxMark = {'M', 'e', 'g', 'a', 'P', 'D', 'F', 'T', 'e', 'x', 't', 'B', 'o', 'x'};

}  // namespace

struct megapdf_text {
    std::vector<TextRun> runs;
    std::vector<TextLine> lines;
    bool lines_built = false;   // built on first use: a text-box listing never pays for it
};

namespace {

// The desktop line-merging rule, in crop space (bottom-left). Heights, centre
// distances and left-to-right order are the same in either orientation; "top to
// bottom" is descending `top` here.
void BuildLines(megapdf_text* t) {
    if (t->lines_built) return;
    t->lines_built = true;
    const auto& runs = t->runs;
    std::vector<bool> used(runs.size(), false);
    auto height = [](const megapdf_rect& r) { return r.top - r.bottom; };
    auto centre = [](const megapdf_rect& r) { return (r.top + r.bottom) / 2.0; };

    for (size_t i = 0; i < runs.size(); i++) {
        if (used[i]) continue;
        std::vector<size_t> members{i};
        used[i] = true;
        for (size_t j = i + 1; j < runs.size(); j++) {
            if (used[j]) continue;
            const auto& a = runs[i].info.bounds;
            const auto& b = runs[j].info.bounds;
            const double tolerance = (height(a) > height(b) ? height(a) : height(b)) * 0.5;
            const double d = centre(a) - centre(b);
            if ((d < 0 ? -d : d) <= tolerance) {
                members.push_back(j);
                used[j] = true;
            }
        }
        std::stable_sort(members.begin(), members.end(), [&](size_t x, size_t y) {
            return runs[x].info.bounds.left < runs[y].info.bounds.left;
        });
        std::vector<size_t> current{members[0]};
        auto flush = [&]() {
            TextLine line;
            line.runs = current;
            const auto& first = runs[current[0]].info.bounds;
            line.bounds = first;
            for (size_t k = 1; k < current.size(); k++) {
                const auto& r = runs[current[k]].info.bounds;
                if (r.left < line.bounds.left) line.bounds.left = r.left;
                if (r.bottom < line.bounds.bottom) line.bounds.bottom = r.bottom;
                if (r.right > line.bounds.right) line.bounds.right = r.right;
                if (r.top > line.bounds.top) line.bounds.top = r.top;
            }
            t->lines.push_back(std::move(line));
        };
        for (size_t k = 1; k < members.size(); k++) {
            const auto& prev = runs[current.back()];
            const auto& next = runs[members[k]];
            const double gap = next.info.bounds.left - prev.info.bounds.right;
            const double bigger = prev.info.font_size > next.info.font_size ? prev.info.font_size : next.info.font_size;
            if (gap > bigger * 2) {
                flush();
                current.clear();
            }
            current.push_back(members[k]);
        }
        flush();
    }
    // Top to bottom, then left to right, then by first run — a total order, so
    // every platform lists the same page the same way.
    std::stable_sort(t->lines.begin(), t->lines.end(), [](const TextLine& a, const TextLine& b) {
        if (a.bounds.top != b.bounds.top) return a.bounds.top > b.bounds.top;
        if (a.bounds.left != b.bounds.left) return a.bounds.left < b.bounds.left;
        return a.runs[0] < b.runs[0];
    });
}

}  // namespace

extern "C" {

MEGAPDF_API megapdf_text* megapdf_text_load(const megapdf_page* p, unsigned int flags) {
    if (p == nullptr) return nullptr;
    const bool boxes_only = (flags & MEGAPDF_TEXT_BOXES_ONLY) != 0;
    Guard guard(CoreLock());
    auto* t = new (std::nothrow) megapdf_text();
    if (t == nullptr) {
        SetError(FPDF_ERR_UNKNOWN, "out of memory");
        return nullptr;
    }
    try {
        FPDF_TEXTPAGE text_page = FPDFText_LoadPage(p->page);
        const int count = FPDFPage_CountObjects(p->page);
        for (int i = 0; i < count; i++) {
            FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, i);
            if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_TEXT) continue;
            const bool is_box = HasMark(obj, kTextBoxMark);
            if (boxes_only && !is_box) continue;
            float l = 0, b = 0, r = 0, tp = 0;
            if (!FPDFPageObj_GetBounds(obj, &l, &b, &r, &tp)) continue;
            U16 text = ReadObjectText(obj, text_page);
            if (text.empty() || AllWhiteSpace(text)) continue;

            TextRun run;
            run.info.object_index = i;
            run.info.bounds.left = static_cast<double>(l) - p->crop_x;
            run.info.bounds.bottom = static_cast<double>(b) - p->crop_y;
            run.info.bounds.right = static_cast<double>(r) - p->crop_x;
            run.info.bounds.top = static_cast<double>(tp) - p->crop_y;
            float size = 0;
            FPDFTextObj_GetFontSize(obj, &size);
            run.info.font_size = static_cast<double>(size);
            run.info.is_text_box = is_box ? 1 : 0;
            run.text = std::move(text);
            run.font = ReadFontFamily(obj);
            if (run.info.is_text_box) {
                run.box_id = ReadMarkParam(obj, "id");
                run.box_font = ReadMarkParam(obj, "font");
            }
            t->runs.push_back(std::move(run));
        }
        if (text_page != nullptr) FPDFText_ClosePage(text_page);
    } catch (...) {
        delete t;
        SetError(FPDF_ERR_UNKNOWN, "out of memory reading text");
        return nullptr;
    }
    return t;
}

MEGAPDF_API void megapdf_text_free(megapdf_text* t) { delete t; }

MEGAPDF_API size_t megapdf_text_run_count(const megapdf_text* t) { return t ? t->runs.size() : 0; }

MEGAPDF_API int megapdf_text_run_get(const megapdf_text* t, size_t index, megapdf_text_run* out) {
    if (t == nullptr || out == nullptr || index >= t->runs.size()) return MEGAPDF_ERR_ARGUMENT;
    *out = t->runs[index].info;
    return MEGAPDF_OK;
}

MEGAPDF_API size_t megapdf_text_run_string(const megapdf_text* t, size_t index, megapdf_text_field field,
                                           unsigned short* out, size_t capacity) {
    if (t == nullptr || index >= t->runs.size()) return 0;
    const TextRun& run = t->runs[index];
    const U16* s = nullptr;
    switch (field) {
        case MEGAPDF_TEXT_RUN_TEXT: s = &run.text; break;
        case MEGAPDF_TEXT_RUN_FONT: s = &run.font; break;
        case MEGAPDF_TEXT_RUN_BOX_ID: s = &run.box_id; break;
        case MEGAPDF_TEXT_RUN_BOX_FONT: s = &run.box_font; break;
        default: return 0;
    }
    if (out != nullptr) {
        const size_t n = s->size() < capacity ? s->size() : capacity;
        for (size_t i = 0; i < n; i++) out[i] = (*s)[i];
    }
    return s->size();
}

// Lines are built lazily; the handle is logically const to the caller, so the
// build happens through the mutable pointer the core handed out.
static void EnsureLines(const megapdf_text* t) {
    Guard guard(CoreLock());
    BuildLines(const_cast<megapdf_text*>(t));
}

MEGAPDF_API size_t megapdf_text_line_count(const megapdf_text* t) {
    if (t == nullptr) return 0;
    EnsureLines(t);
    return t->lines.size();
}

MEGAPDF_API int megapdf_text_line_get(const megapdf_text* t, size_t index, megapdf_rect* out) {
    if (t == nullptr || out == nullptr) return MEGAPDF_ERR_ARGUMENT;
    EnsureLines(t);
    if (index >= t->lines.size()) return MEGAPDF_ERR_ARGUMENT;
    *out = t->lines[index].bounds;
    return MEGAPDF_OK;
}

MEGAPDF_API size_t megapdf_text_line_runs(const megapdf_text* t, size_t index, size_t* out, size_t capacity) {
    if (t == nullptr) return 0;
    EnsureLines(t);
    if (index >= t->lines.size()) return 0;
    const auto& runs = t->lines[index].runs;
    if (out != nullptr) {
        const size_t n = runs.size() < capacity ? runs.size() : capacity;
        for (size_t i = 0; i < n; i++) out[i] = runs[i];
    }
    return runs.size();
}

}  // extern "C"

// --------------------------------------------------------------------------
// Contract 3: AcroForm fields (#107)
// --------------------------------------------------------------------------

namespace {

struct FormField {
    megapdf_form_field info{};
    U16 name;
    U16 value;
};

// FPDFAnnot_GetFormFieldName/Value: UTF-16, length in bytes including the terminator.
template <typename F>
U16 ReadAnnotWide(F read) {
    const unsigned long bytes = read(nullptr, 0);
    if (bytes <= 2) return {};
    U16 buf(bytes / 2);
    read(buf.data(), bytes);
    buf.resize(bytes / 2 - 1);
    return buf;
}

int KindOf(int pdfium_type) {
    switch (pdfium_type) {
        case FPDF_FORMFIELD_TEXTFIELD: return MEGAPDF_FIELD_TEXT;
        case FPDF_FORMFIELD_CHECKBOX: return MEGAPDF_FIELD_CHECKBOX;
        case FPDF_FORMFIELD_RADIOBUTTON: return MEGAPDF_FIELD_RADIO;
        default: return MEGAPDF_FIELD_OTHER;
    }
}

}  // namespace

struct megapdf_form_fields {
    std::vector<FormField> fields;
};

extern "C" {

MEGAPDF_API megapdf_form_fields* megapdf_form_fields_load(const megapdf_page* p) {
    if (p == nullptr) return nullptr;
    Guard guard(CoreLock());
    auto* f = new (std::nothrow) megapdf_form_fields();
    if (f == nullptr) {
        SetError(FPDF_ERR_UNKNOWN, "out of memory");
        return nullptr;
    }
    FPDF_FORMHANDLE form = p->owner ? p->owner->form : nullptr;
    if (form == nullptr) return f;
    try {
        const int count = FPDFPage_GetAnnotCount(p->page);
        for (int i = 0; i < count; i++) {
            FPDF_ANNOTATION annot = FPDFPage_GetAnnot(p->page, i);
            if (annot == nullptr) continue;
            if (FPDFAnnot_GetSubtype(annot) == FPDF_ANNOT_WIDGET) {
                FS_RECTF r{};
                if (FPDFAnnot_GetRect(annot, &r)) {
                    FormField field;
                    field.info.kind = KindOf(FPDFAnnot_GetFormFieldType(form, annot));
                    field.info.bounds.left = static_cast<double>(r.left) - p->crop_x;
                    field.info.bounds.bottom = static_cast<double>(r.bottom) - p->crop_y;
                    field.info.bounds.right = static_cast<double>(r.right) - p->crop_x;
                    field.info.bounds.top = static_cast<double>(r.top) - p->crop_y;
                    field.info.is_checked =
                        (field.info.kind == MEGAPDF_FIELD_CHECKBOX || field.info.kind == MEGAPDF_FIELD_RADIO) &&
                        FPDFAnnot_IsChecked(form, annot) ? 1 : 0;
                    field.name = ReadAnnotWide([&](FPDF_WCHAR* buf, unsigned long len) {
                        return FPDFAnnot_GetFormFieldName(form, annot, buf, len);
                    });
                    field.value = ReadAnnotWide([&](FPDF_WCHAR* buf, unsigned long len) {
                        return FPDFAnnot_GetFormFieldValue(form, annot, buf, len);
                    });
                    f->fields.push_back(std::move(field));
                }
            }
            FPDFPage_CloseAnnot(annot);
        }
    } catch (...) {
        delete f;
        SetError(FPDF_ERR_UNKNOWN, "out of memory reading form fields");
        return nullptr;
    }
    return f;
}

MEGAPDF_API void megapdf_form_fields_free(megapdf_form_fields* f) { delete f; }

MEGAPDF_API size_t megapdf_form_field_count(const megapdf_form_fields* f) { return f ? f->fields.size() : 0; }

MEGAPDF_API int megapdf_form_field_get(const megapdf_form_fields* f, size_t index, megapdf_form_field* out) {
    if (f == nullptr || out == nullptr || index >= f->fields.size()) return MEGAPDF_ERR_ARGUMENT;
    *out = f->fields[index].info;
    return MEGAPDF_OK;
}

MEGAPDF_API size_t megapdf_form_field_string(const megapdf_form_fields* f, size_t index, megapdf_field_string which,
                                             unsigned short* out, size_t capacity) {
    if (f == nullptr || index >= f->fields.size()) return 0;
    const U16* s = which == MEGAPDF_FIELD_NAME ? &f->fields[index].name
                 : which == MEGAPDF_FIELD_VALUE ? &f->fields[index].value : nullptr;
    if (s == nullptr) return 0;
    if (out != nullptr) {
        const size_t n = s->size() < capacity ? s->size() : capacity;
        for (size_t i = 0; i < n; i++) out[i] = (*s)[i];
    }
    return s->size();
}

MEGAPDF_API int megapdf_form_click(const megapdf_page* p, double x, double y) {
    if (p == nullptr || p->owner == nullptr || p->owner->form == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    FPDF_FORMHANDLE form = p->owner->form;
    FORM_OnLButtonDown(form, p->page, 0, x + p->crop_x, y + p->crop_y);
    FORM_OnLButtonUp(form, p->page, 0, x + p->crop_x, y + p->crop_y);
    FORM_ForceToKillFocus(form);
    return MEGAPDF_OK;
}

MEGAPDF_API int megapdf_form_set_text(const megapdf_page* p, double x, double y, const unsigned short* value_utf16) {
    if (p == nullptr || p->owner == nullptr || p->owner->form == nullptr || value_utf16 == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    FPDF_FORMHANDLE form = p->owner->form;
    FORM_OnLButtonDown(form, p->page, 0, x + p->crop_x, y + p->crop_y);
    FORM_OnLButtonUp(form, p->page, 0, x + p->crop_x, y + p->crop_y);
    FORM_SelectAllText(form, p->page);
    FORM_ReplaceSelection(form, p->page, reinterpret_cast<FPDF_WIDESTRING>(value_utf16));
    FORM_ForceToKillFocus(form);
    return MEGAPDF_OK;
}

MEGAPDF_API void megapdf_form_commit(const megapdf_document* d) {
    if (d == nullptr || d->form == nullptr) return;
    Guard guard(CoreLock());
    FORM_ForceToKillFocus(d->form);
}

}  // extern "C"

// --------------------------------------------------------------------------
// Contract 4: stamps and MegaPDF_Id marks (#108)
// --------------------------------------------------------------------------

namespace {

constexpr const char* kStampIdKey = "MegaPDF_Id";

U16 ReadStampId(FPDF_ANNOTATION annot) {
    return ReadAnnotWide([&](FPDF_WCHAR* buf, unsigned long len) {
        return FPDFAnnot_GetStringValue(annot, kStampIdKey, buf, len);
    });
}

size_t U16Length(const unsigned short* s) {
    size_t n = 0;
    while (s[n] != 0) n++;
    return n;
}

bool SameId(const U16& id, const unsigned short* wanted) {
    const size_t n = U16Length(wanted);
    if (id.size() != n) return false;
    for (size_t i = 0; i < n; i++) if (id[i] != wanted[i]) return false;
    return true;
}

// The annotation index of the stamp carrying `id`, or -1.
int FindStamp(FPDF_PAGE page, const unsigned short* id) {
    const int count = FPDFPage_GetAnnotCount(page);
    for (int i = 0; i < count; i++) {
        FPDF_ANNOTATION annot = FPDFPage_GetAnnot(page, i);
        if (annot == nullptr) continue;
        const bool match = SameId(ReadStampId(annot), id);
        FPDFPage_CloseAnnot(annot);
        if (match) return i;
    }
    return -1;
}

struct Stamp {
    megapdf_stamp info{};
    U16 id;
};

}  // namespace

struct megapdf_stamps {
    std::vector<Stamp> stamps;
};

struct megapdf_image {
    int width = 0;
    int height = 0;
    std::vector<unsigned char> bgra;
};

namespace {

// Renders a page object through `matrix` (temporarily replacing its placement)
// into a fresh BGRA image; NULL when PDFium cannot.
megapdf_image* RenderObjectUnlocked(FPDF_DOCUMENT doc, FPDF_PAGE page, FPDF_PAGEOBJECT obj, const FS_MATRIX& matrix) {
    FS_MATRIX placement{};
    if (!FPDFPageObj_GetMatrix(obj, &placement)) return nullptr;
    FPDFPageObj_SetMatrix(obj, &matrix);
    FPDF_BITMAP bmp = FPDFImageObj_GetRenderedBitmap(doc, page, obj);
    FPDFPageObj_SetMatrix(obj, &placement);
    if (bmp == nullptr) return nullptr;
    const int w = FPDFBitmap_GetWidth(bmp);
    const int h = FPDFBitmap_GetHeight(bmp);
    const int stride = FPDFBitmap_GetStride(bmp);
    const auto* buf = static_cast<const unsigned char*>(FPDFBitmap_GetBuffer(bmp));
    megapdf_image* img = nullptr;
    if (buf != nullptr && w > 0 && h > 0) {
        img = new (std::nothrow) megapdf_image();
        if (img != nullptr) {
            img->width = w;
            img->height = h;
            img->bgra.resize(static_cast<size_t>(w) * h * 4);
            for (int y = 0; y < h; y++) std::memcpy(img->bgra.data() + static_cast<size_t>(y) * w * 4, buf + static_cast<size_t>(y) * stride, static_cast<size_t>(w) * 4);
        }
    }
    FPDFBitmap_Destroy(bmp);
    return img;
}

megapdf_image* LoadStampImageUnlocked(const megapdf_page* p, int annot_index) {
    FPDF_ANNOTATION annot = FPDFPage_GetAnnot(p->page, annot_index);
    if (annot == nullptr) return nullptr;
    megapdf_image* result = nullptr;
    const int objects = FPDFAnnot_GetObjectCount(annot);
    for (int i = 0; i < objects && result == nullptr; i++) {
        FPDF_PAGEOBJECT obj = FPDFAnnot_GetObject(annot, i);
        if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_IMAGE) continue;

        // Render at the image's native pixel size, not its placement size, so
        // repeated remove/re-add cycles never lose resolution.
        FS_MATRIX placement{};
        if (!FPDFPageObj_GetMatrix(obj, &placement)) continue;
        FS_MATRIX native = placement;
        unsigned int pw = 0, ph = 0;
        if (FPDFImageObj_GetImagePixelSize(obj, &pw, &ph) && pw > 0 && ph > 0) {
            native = FS_MATRIX{static_cast<float>(pw), 0, 0, static_cast<float>(ph), 0, 0};
        }
        result = RenderObjectUnlocked(p->owner ? p->owner->doc : nullptr, p->page, obj, native);
    }
    FPDFPage_CloseAnnot(annot);
    return result;
}

int AddImageStampUnlocked(const megapdf_page* p, const unsigned char* bgra, int width, int height,
                          const megapdf_rect* bounds, const unsigned short* id) {
    const float left = static_cast<float>(bounds->left + p->crop_x);
    const float right = static_cast<float>(bounds->right + p->crop_x);
    const float bottom = static_cast<float>(bounds->bottom + p->crop_y);
    const float top = static_cast<float>(bounds->top + p->crop_y);

    FPDF_BITMAP bmp = FPDFBitmap_Create(width, height, /*alpha=*/1);
    if (bmp == nullptr) { SetError(FPDF_ERR_UNKNOWN, "could not create the stamp bitmap"); return MEGAPDF_ERR_PDFIUM; }
    {
        auto* dst = static_cast<unsigned char*>(FPDFBitmap_GetBuffer(bmp));
        const int stride = FPDFBitmap_GetStride(bmp);
        for (int y = 0; y < height; y++) std::memcpy(dst + static_cast<size_t>(y) * stride, bgra + static_cast<size_t>(y) * width * 4, static_cast<size_t>(width) * 4);
    }

    int status = MEGAPDF_OK;
    FPDF_ANNOTATION annot = FPDFPage_CreateAnnot(p->page, FPDF_ANNOT_STAMP);
    if (annot == nullptr) {
        FPDFBitmap_Destroy(bmp);
        SetError(FPDF_ERR_UNKNOWN, "could not create the stamp annotation");
        return MEGAPDF_ERR_PDFIUM;
    }
    FS_RECTF rect{left, top, right, bottom};
    bool ok = FPDFAnnot_SetRect(annot, &rect);
    FPDF_PAGEOBJECT img = ok ? FPDFPageObj_NewImageObj(p->owner ? p->owner->doc : nullptr) : nullptr;
    ok = ok && img != nullptr;
    if (ok) {
        FPDF_PAGE pages[1] = {p->page};
        ok = FPDFImageObj_SetBitmap(pages, 1, img, bmp);
        FS_MATRIX m{right - left, 0, 0, top - bottom, left, bottom};
        ok = ok && FPDFPageObj_SetMatrix(img, &m);
        if (ok) {
            ok = FPDFAnnot_AppendObject(annot, img);   // ownership moves to the annotation on success
            if (!ok) FPDFPageObj_Destroy(img);
        } else {
            FPDFPageObj_Destroy(img);
        }
    }
    ok = ok && FPDFAnnot_SetStringValue(annot, kStampIdKey, reinterpret_cast<FPDF_WIDESTRING>(id));
    if (!ok) { SetError(FPDF_ERR_UNKNOWN, "could not place the image stamp"); status = MEGAPDF_ERR_PDFIUM; }
    FPDFPage_CloseAnnot(annot);
    FPDFBitmap_Destroy(bmp);
    return status;
}

}  // namespace

extern "C" {

MEGAPDF_API int megapdf_add_check_mark(const megapdf_page* p, const megapdf_rect* square, megapdf_mark_style style,
                                       const unsigned short* id) {
    if (p == nullptr || square == nullptr || id == nullptr || id[0] == 0) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    // Mark at ~80% of the square, centred (SDD §3.2), in PDF user space.
    const double width = square->right - square->left;
    const double height = square->top - square->bottom;
    const double inset = (width > height ? width : height) * 0.10;
    const float left = static_cast<float>(square->left + inset + p->crop_x);
    const float right = static_cast<float>(square->right - inset + p->crop_x);
    const float bottom = static_cast<float>(square->bottom + inset + p->crop_y);
    const float top = static_cast<float>(square->top - inset + p->crop_y);

    FPDF_ANNOTATION annot = FPDFPage_CreateAnnot(p->page, FPDF_ANNOT_STAMP);
    if (annot == nullptr) { SetError(FPDF_ERR_UNKNOWN, "could not create the mark annotation"); return MEGAPDF_ERR_PDFIUM; }
    FS_RECTF rect{left, top, right, bottom};
    bool ok = FPDFAnnot_SetRect(annot, &rect);

    FPDF_PAGEOBJECT path = nullptr;
    int fill = FPDF_FILLMODE_NONE;
    FPDF_BOOL stroke = 1;
    switch (style) {
        case MEGAPDF_MARK_CHECK: {
            const float w = right - left, h = top - bottom;
            path = FPDFPageObj_CreateNewPath(left, bottom + h * 0.45f);
            ok = ok && path && FPDFPath_LineTo(path, left + w * 0.38f, bottom) && FPDFPath_LineTo(path, right, top);
            break;
        }
        case MEGAPDF_MARK_FILLED_SQUARE:
            path = FPDFPageObj_CreateNewPath(left, bottom);
            ok = ok && path && FPDFPath_LineTo(path, right, bottom) && FPDFPath_LineTo(path, right, top) &&
                 FPDFPath_LineTo(path, left, top) && FPDFPath_LineTo(path, left, bottom);
            if (path) FPDFPageObj_SetFillColor(path, 0x20, 0x20, 0x20, 0xFF);
            fill = FPDF_FILLMODE_ALTERNATE;
            stroke = 0;
            break;
        default:   // cross
            path = FPDFPageObj_CreateNewPath(left, bottom);
            ok = ok && path && FPDFPath_LineTo(path, right, top) && FPDFPath_MoveTo(path, left, top) && FPDFPath_LineTo(path, right, bottom);
            break;
    }
    if (path != nullptr) {
        FPDFPageObj_SetStrokeColor(path, 0x20, 0x20, 0x20, 0xFF);
        const double stroke_width = width * 0.11 > 1.2 ? width * 0.11 : 1.2;
        FPDFPageObj_SetStrokeWidth(path, static_cast<float>(stroke_width));
        FPDFPath_SetDrawMode(path, fill, stroke);
        if (ok) {
            ok = FPDFAnnot_AppendObject(annot, path);
            if (!ok) FPDFPageObj_Destroy(path);
        } else {
            FPDFPageObj_Destroy(path);
        }
    } else {
        ok = false;
    }
    ok = ok && FPDFAnnot_SetStringValue(annot, kStampIdKey, reinterpret_cast<FPDF_WIDESTRING>(id));
    FPDFPage_CloseAnnot(annot);
    if (!ok) { SetError(FPDF_ERR_UNKNOWN, "could not draw the mark"); return MEGAPDF_ERR_PDFIUM; }
    return MEGAPDF_OK;
}

MEGAPDF_API int megapdf_add_image_stamp(const megapdf_page* p, const unsigned char* bgra, int width, int height,
                                        const megapdf_rect* bounds, const unsigned short* id) {
    if (p == nullptr || bgra == nullptr || width <= 0 || height <= 0 || bounds == nullptr || id == nullptr || id[0] == 0) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    return AddImageStampUnlocked(p, bgra, width, height, bounds, id);
}

MEGAPDF_API megapdf_stamps* megapdf_stamps_load(const megapdf_page* p) {
    if (p == nullptr) return nullptr;
    Guard guard(CoreLock());
    auto* s = new (std::nothrow) megapdf_stamps();
    if (s == nullptr) { SetError(FPDF_ERR_UNKNOWN, "out of memory"); return nullptr; }
    try {
        const int count = FPDFPage_GetAnnotCount(p->page);
        for (int i = 0; i < count; i++) {
            FPDF_ANNOTATION annot = FPDFPage_GetAnnot(p->page, i);
            if (annot == nullptr) continue;
            U16 id = ReadStampId(annot);
            FS_RECTF r{};
            if (!id.empty() && FPDFAnnot_GetRect(annot, &r)) {
                Stamp st;
                st.info.annot_index = i;
                st.info.bounds.left = static_cast<double>(r.left) - p->crop_x;
                st.info.bounds.bottom = static_cast<double>(r.bottom) - p->crop_y;
                st.info.bounds.right = static_cast<double>(r.right) - p->crop_x;
                st.info.bounds.top = static_cast<double>(r.top) - p->crop_y;
                st.id = std::move(id);
                s->stamps.push_back(std::move(st));
            }
            FPDFPage_CloseAnnot(annot);
        }
    } catch (...) {
        delete s;
        SetError(FPDF_ERR_UNKNOWN, "out of memory reading stamps");
        return nullptr;
    }
    return s;
}

MEGAPDF_API void megapdf_stamps_free(megapdf_stamps* s) { delete s; }

MEGAPDF_API size_t megapdf_stamp_count(const megapdf_stamps* s) { return s ? s->stamps.size() : 0; }

MEGAPDF_API int megapdf_stamp_get(const megapdf_stamps* s, size_t index, megapdf_stamp* out) {
    if (s == nullptr || out == nullptr || index >= s->stamps.size()) return MEGAPDF_ERR_ARGUMENT;
    *out = s->stamps[index].info;
    return MEGAPDF_OK;
}

MEGAPDF_API size_t megapdf_stamp_id(const megapdf_stamps* s, size_t index, unsigned short* out, size_t capacity) {
    if (s == nullptr || index >= s->stamps.size()) return 0;
    const U16& id = s->stamps[index].id;
    if (out != nullptr) {
        const size_t n = id.size() < capacity ? id.size() : capacity;
        for (size_t i = 0; i < n; i++) out[i] = id[i];
    }
    return id.size();
}

MEGAPDF_API megapdf_image* megapdf_stamp_image_load(const megapdf_page* p, int annot_index) {
    if (p == nullptr || annot_index < 0) return nullptr;
    Guard guard(CoreLock());
    return LoadStampImageUnlocked(p, annot_index);
}

MEGAPDF_API void megapdf_image_free(megapdf_image* img) { delete img; }
MEGAPDF_API int megapdf_image_width(const megapdf_image* img) { return img ? img->width : 0; }
MEGAPDF_API int megapdf_image_height(const megapdf_image* img) { return img ? img->height : 0; }

MEGAPDF_API size_t megapdf_image_pixels(const megapdf_image* img, unsigned char* out, size_t capacity) {
    if (img == nullptr) return 0;
    if (out != nullptr) {
        const size_t n = img->bgra.size() < capacity ? img->bgra.size() : capacity;
        std::memcpy(out, img->bgra.data(), n);
    }
    return img->bgra.size();
}

MEGAPDF_API int megapdf_remove_annotation(const megapdf_page* p, int annot_index) {
    if (p == nullptr || annot_index < 0) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    if (!FPDFPage_RemoveAnnot(p->page, annot_index)) { SetError(FPDF_ERR_UNKNOWN, "could not remove the annotation"); return MEGAPDF_ERR_PDFIUM; }
    return MEGAPDF_OK;
}

MEGAPDF_API int megapdf_remove_stamp(const megapdf_page* p, const unsigned short* id) {
    if (p == nullptr || id == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    const int index = FindStamp(p->page, id);
    if (index < 0) { SetError(0, "no stamp with that id on the page"); return MEGAPDF_ERR_ARGUMENT; }
    if (!FPDFPage_RemoveAnnot(p->page, index)) { SetError(FPDF_ERR_UNKNOWN, "could not remove the stamp"); return MEGAPDF_ERR_PDFIUM; }
    return MEGAPDF_OK;
}

MEGAPDF_API int megapdf_move_image_stamp(const megapdf_page* p, const unsigned short* id, const megapdf_rect* bounds) {
    if (p == nullptr || id == nullptr || bounds == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    const int index = FindStamp(p->page, id);
    if (index < 0) { SetError(0, "no stamp with that id on the page"); return MEGAPDF_ERR_ARGUMENT; }
    megapdf_image* img = LoadStampImageUnlocked(p, index);
    if (img == nullptr) { SetError(0, "only image stamps can be moved"); return MEGAPDF_ERR_ARGUMENT; }
    int status = MEGAPDF_OK;
    if (!FPDFPage_RemoveAnnot(p->page, index)) {
        SetError(FPDF_ERR_UNKNOWN, "could not remove the stamp before moving it");
        status = MEGAPDF_ERR_PDFIUM;
    } else {
        status = AddImageStampUnlocked(p, img->bgra.data(), img->width, img->height, bounds, id);
    }
    delete img;
    return status;
}

}  // extern "C"

// --------------------------------------------------------------------------
// Contract 5: whiteouts, text boxes and detached objects (#109)
// --------------------------------------------------------------------------

namespace {

constexpr const char* kWhiteoutMark = "MegaPDFWhiteout";
constexpr const char* kTextBoxMarkName = "MegaPDFTextBox";
const U16 kWhiteoutMarkU16 = {'M', 'e', 'g', 'a', 'P', 'D', 'F', 'W', 'h', 'i', 't', 'e', 'o', 'u', 't'};
const char* const kUntaggedPrefix = "text:untagged#";

bool IsStandardTextBoxFont(const char* name) {
    return name != nullptr && (std::strcmp(name, "Helvetica") == 0 || std::strcmp(name, "Times-Roman") == 0 ||
                               std::strcmp(name, "Courier") == 0);
}

bool GenerateContent(const megapdf_page* p) {
    if (!FPDFPage_GenerateContent(p->page)) {
        SetError(FPDF_ERR_UNKNOWN, "PDFium failed to regenerate the page content stream");
        return false;
    }
    return true;
}

// Ascii-only helper for the untagged handle: "text:untagged#<index>".
bool IsUntaggedHandle(const unsigned short* id, int* out_index) {
    size_t i = 0;
    for (; kUntaggedPrefix[i] != '\0'; i++) {
        if (id[i] != static_cast<unsigned short>(kUntaggedPrefix[i])) return false;
    }
    if (id[i] == 0) return false;
    int value = 0;
    for (; id[i] != 0; i++) {
        if (id[i] < '0' || id[i] > '9') return false;
        value = value * 10 + (id[i] - '0');
    }
    *out_index = value;
    return true;
}

// The object index of the box carrying `id`, or -1.
int FindTextBoxUnlocked(FPDF_PAGE page, const unsigned short* id) {
    int untagged = -1;
    const bool wants_untagged = IsUntaggedHandle(id, &untagged);
    const int count = FPDFPage_CountObjects(page);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, i);
        if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_TEXT || !HasMark(obj, kTextBoxMark)) continue;
        const U16 box_id = ReadMarkParam(obj, "id");
        if (box_id.empty()) {
            if (wants_untagged && i == untagged) return i;
        } else if (SameId(box_id, id)) {
            return i;
        }
    }
    return -1;
}

int MoveTextBoxUnlocked(const megapdf_page* p, int object_index, double left, double bottom) {
    FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, object_index);
    if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_TEXT) {
        SetError(0, "the object is no longer a text object");
        return MEGAPDF_ERR_ARGUMENT;
    }
    float l = 0, b = 0, r = 0, t = 0;
    FS_MATRIX m{};
    if (!FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) || !FPDFPageObj_GetMatrix(obj, &m)) {
        SetError(FPDF_ERR_UNKNOWN, "could not read the text box geometry");
        return MEGAPDF_ERR_PDFIUM;
    }
    // Translate in place so the bounds' bottom-left lands on the target; scale and
    // rotation stay as they are.
    m.e += static_cast<float>(left + p->crop_x) - l;
    m.f += static_cast<float>(bottom + p->crop_y) - b;
    if (!FPDFPageObj_SetMatrix(obj, &m)) {
        SetError(FPDF_ERR_UNKNOWN, "could not move the text box");
        return MEGAPDF_ERR_PDFIUM;
    }
    return GenerateContent(p) ? MEGAPDF_OK : MEGAPDF_ERR_PDFIUM;
}

int AddTextBoxUnlocked(const megapdf_page* p, int object_index, const unsigned short* text, const char* font_name,
                       double font_size, double baseline_x, double baseline_y, const unsigned short* id,
                       int* out_object_index) {
    FPDF_DOCUMENT doc = p->owner ? p->owner->doc : nullptr;
    const int count = FPDFPage_CountObjects(p->page);
    if (object_index < 0 || object_index > count) object_index = count;

    FPDF_FONT font = FPDFText_LoadStandardFont(doc, font_name);
    if (font == nullptr) { SetError(FPDF_ERR_UNKNOWN, "the standard font could not be loaded"); return MEGAPDF_ERR_PDFIUM; }
    FPDF_PAGEOBJECT obj = FPDFPageObj_CreateTextObj(doc, font, static_cast<float>(font_size));
    bool ok = obj != nullptr && FPDFText_SetText(obj, reinterpret_cast<FPDF_WIDESTRING>(text));
    if (ok) {
        FS_MATRIX m{1, 0, 0, 1, static_cast<float>(baseline_x + p->crop_x), static_cast<float>(baseline_y + p->crop_y)};
        ok = FPDFPageObj_SetMatrix(obj, &m);
    }
    if (ok) {
        // The id is how the phones address a box, since object indices shift; the
        // face is recorded rather than inferred, because PDFium may normalise a
        // standard font's reported name (#43).
        FPDF_PAGEOBJECTMARK mark = FPDFPageObj_AddMark(obj, kTextBoxMarkName);
        ok = mark != nullptr;
        if (ok) {
            // FPDFPageObjMark_SetStringParam takes UTF-8; the id and face are ASCII by contract.
            std::string id_utf8;
            for (size_t i = 0; id[i] != 0; i++) id_utf8 += static_cast<char>(id[i] < 0x80 ? id[i] : '?');
            ok = FPDFPageObjMark_SetStringParam(doc, obj, mark, "id", id_utf8.c_str()) &&
                 FPDFPageObjMark_SetStringParam(doc, obj, mark, "font", font_name);
        }
    }
    if (ok) {
        // Takes ownership (and frees the object itself on failure).
        ok = FPDFPage_InsertObjectAtIndex(p->page, obj, static_cast<size_t>(object_index));
        obj = nullptr;
    }
    if (obj != nullptr) FPDFPageObj_Destroy(obj);
    FPDFFont_Close(font);
    if (!ok) { SetError(FPDF_ERR_UNKNOWN, "could not place the text box"); return MEGAPDF_ERR_PDFIUM; }
    if (!GenerateContent(p)) return MEGAPDF_ERR_PDFIUM;
    if (out_object_index != nullptr) *out_object_index = object_index;
    return MEGAPDF_OK;
}

}  // namespace

extern "C" {

MEGAPDF_API int megapdf_add_whiteout(const megapdf_page* p, const megapdf_rect* bounds, int* out_object_index) {
    if (p == nullptr || bounds == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    const float left = static_cast<float>(bounds->left + p->crop_x), right = static_cast<float>(bounds->right + p->crop_x);
    const float bottom = static_cast<float>(bounds->bottom + p->crop_y), top = static_cast<float>(bounds->top + p->crop_y);
    FPDF_PAGEOBJECT path = FPDFPageObj_CreateNewPath(left, bottom);
    if (path == nullptr) { SetError(FPDF_ERR_UNKNOWN, "could not create the whiteout"); return MEGAPDF_ERR_PDFIUM; }
    FPDFPath_LineTo(path, right, bottom);
    FPDFPath_LineTo(path, right, top);
    FPDFPath_LineTo(path, left, top);
    FPDFPath_LineTo(path, left, bottom);
    FPDFPageObj_SetFillColor(path, 0xFF, 0xFF, 0xFF, 0xFF);
    FPDFPath_SetDrawMode(path, FPDF_FILLMODE_ALTERNATE, 0);
    FPDFPageObj_AddMark(path, kWhiteoutMark);
    const int index = FPDFPage_CountObjects(p->page);
    if (!FPDFPage_InsertObjectAtIndex(p->page, path, static_cast<size_t>(index))) {
        SetError(FPDF_ERR_UNKNOWN, "could not place the whiteout");
        return MEGAPDF_ERR_PDFIUM;
    }
    if (!GenerateContent(p)) return MEGAPDF_ERR_PDFIUM;
    if (out_object_index != nullptr) *out_object_index = index;
    return MEGAPDF_OK;
}

MEGAPDF_API size_t megapdf_whiteouts(const megapdf_page* p, megapdf_object_rect* out, size_t capacity) {
    if (p == nullptr) return 0;
    Guard guard(CoreLock());
    size_t found = 0;
    const int count = FPDFPage_CountObjects(p->page);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, i);
        if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_PATH || !HasMark(obj, kWhiteoutMarkU16)) continue;
        float l = 0, b = 0, r = 0, t = 0;
        if (!FPDFPageObj_GetBounds(obj, &l, &b, &r, &t)) continue;
        if (found < capacity && out != nullptr) {
            out[found].object_index = i;
            out[found].bounds = megapdf_rect{l - p->crop_x, b - p->crop_y, r - p->crop_x, t - p->crop_y};
        }
        found++;
    }
    return found;
}

MEGAPDF_API int megapdf_add_text_box(const megapdf_page* p, int object_index, const unsigned short* text, const char* font_name,
                                     double font_size, double baseline_x, double baseline_y, const unsigned short* id,
                                     int* out_object_index) {
    if (p == nullptr || text == nullptr || text[0] == 0 || id == nullptr || id[0] == 0 || !IsStandardTextBoxFont(font_name)) {
        SetError(0, "a text box needs text, an id and one of the three standard faces");
        return MEGAPDF_ERR_ARGUMENT;
    }
    Guard guard(CoreLock());
    return AddTextBoxUnlocked(p, object_index, text, font_name, font_size, baseline_x, baseline_y, id, out_object_index);
}

MEGAPDF_API int megapdf_restyle_text_box(const megapdf_page* p, int object_index, const unsigned short* text, const char* font_name,
                                         double font_size, double left, double bottom, const unsigned short* id) {
    if (p == nullptr || text == nullptr || text[0] == 0 || id == nullptr || id[0] == 0 || !IsStandardTextBoxFont(font_name)) {
        SetError(0, "a text box needs text, an id and one of the three standard faces");
        return MEGAPDF_ERR_ARGUMENT;
    }
    Guard guard(CoreLock());
    // Place the baseline at the corner, then normalise onto the bounds anchor:
    // GetTextBoxes and MoveTextBox both speak bounds, and without this a 12 pt →
    // 18 pt restyle drops by the extra descender depth.
    int index = -1;
    const int status = AddTextBoxUnlocked(p, object_index, text, font_name, font_size, left, bottom, id, &index);
    if (status != MEGAPDF_OK) return status;
    return MoveTextBoxUnlocked(p, index, left, bottom);
}

MEGAPDF_API int megapdf_find_text_box(const megapdf_page* p, const unsigned short* id) {
    if (p == nullptr || id == nullptr) return -1;
    Guard guard(CoreLock());
    return FindTextBoxUnlocked(p->page, id);
}

MEGAPDF_API int megapdf_object_type(const megapdf_page* p, int object_index) {
    if (p == nullptr || object_index < 0) return -1;
    Guard guard(CoreLock());
    FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, object_index);
    return obj == nullptr ? -1 : FPDFPageObj_GetType(obj);
}

MEGAPDF_API int megapdf_object_bounds(const megapdf_page* p, int object_index, megapdf_rect* out) {
    if (p == nullptr || out == nullptr || object_index < 0) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, object_index);
    float l = 0, b = 0, r = 0, t = 0;
    if (obj == nullptr || !FPDFPageObj_GetBounds(obj, &l, &b, &r, &t)) return MEGAPDF_ERR_ARGUMENT;
    *out = megapdf_rect{l - p->crop_x, b - p->crop_y, r - p->crop_x, t - p->crop_y};
    return MEGAPDF_OK;
}

MEGAPDF_API int megapdf_move_text_box(const megapdf_page* p, int object_index, double left, double bottom) {
    if (p == nullptr || object_index < 0) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    return MoveTextBoxUnlocked(p, object_index, left, bottom);
}

MEGAPDF_API int megapdf_remove_text_box(const megapdf_page* p, const unsigned short* id) {
    if (p == nullptr || id == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    const int index = FindTextBoxUnlocked(p->page, id);
    if (index < 0) return MEGAPDF_OK;   // already gone: an undo racing a re-render must not fail
    FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, index);
    if (!FPDFPage_RemoveObject(p->page, obj)) { SetError(FPDF_ERR_UNKNOWN, "could not remove the text box"); return MEGAPDF_ERR_PDFIUM; }
    FPDFPageObj_Destroy(obj);
    return GenerateContent(p) ? MEGAPDF_OK : MEGAPDF_ERR_PDFIUM;
}

MEGAPDF_API megapdf_detached* megapdf_detach_object(const megapdf_page* p, int object_index) {
    if (p == nullptr || p->owner == nullptr || object_index < 0) return nullptr;
    Guard guard(CoreLock());
    FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, object_index);
    if (obj == nullptr) { SetError(0, "no page object at that index"); return nullptr; }
    if (!FPDFPage_RemoveObject(p->page, obj)) { SetError(FPDF_ERR_UNKNOWN, "could not remove the object"); return nullptr; }
    GenerateContent(p);
    auto* x = new (std::nothrow) megapdf_detached();
    if (x == nullptr) { FPDFPageObj_Destroy(obj); SetError(FPDF_ERR_UNKNOWN, "out of memory"); return nullptr; }
    x->owner = p->owner;
    x->object = obj;
    p->owner->detached.push_back(x);
    return x;
}

MEGAPDF_API int megapdf_restore_object(const megapdf_page* p, megapdf_detached* x, int object_index) {
    if (p == nullptr || x == nullptr || object_index < 0) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    if (!FPDFPage_InsertObjectAtIndex(p->page, x->object, static_cast<size_t>(object_index))) {
        SetError(FPDF_ERR_UNKNOWN, "could not restore the object");
        return MEGAPDF_ERR_PDFIUM;
    }
    // The page owns it again; the handle is spent.
    if (x->owner != nullptr) {
        auto& list = x->owner->detached;
        for (size_t i = 0; i < list.size(); i++) if (list[i] == x) { list[i] = list.back(); list.pop_back(); break; }
    }
    delete x;
    return GenerateContent(p) ? MEGAPDF_OK : MEGAPDF_ERR_PDFIUM;
}

MEGAPDF_API void megapdf_discard_detached(megapdf_detached* x) {
    if (x == nullptr) return;
    Guard guard(CoreLock());
    if (x->owner != nullptr) {
        auto& list = x->owner->detached;
        for (size_t i = 0; i < list.size(); i++) if (list[i] == x) { list[i] = list.back(); list.pop_back(); break; }
    }
    FPDFPageObj_Destroy(x->object);
    delete x;
}

}  // extern "C"

// --------------------------------------------------------------------------
// Contract 6: save, flatten and images (#110)
// --------------------------------------------------------------------------

namespace {

struct WriteBridge {
    FPDF_FILEWRITE fw;   // first, so PDFium's pointer downcasts
    megapdf_write_fn write;
    void* context;
    bool failed;
};

int WriteBlockThunk(FPDF_FILEWRITE* self, const void* data, unsigned long size) {
    auto* b = reinterpret_cast<WriteBridge*>(self);
    if (b->failed) return 0;
    if (size == 0) return 1;
    if (!b->write(b->context, data, static_cast<size_t>(size))) { b->failed = true; return 0; }
    return 1;
}

struct ReadBridge {
    const unsigned char* data;
    size_t length;
};

int GetBlockThunk(void* param, unsigned long position, unsigned char* buf, unsigned long size) {
    auto* r = static_cast<ReadBridge*>(param);
    if (position + size > r->length) return 0;
    std::memcpy(buf, r->data + position, size);
    return 1;
}

// A page opened through the ABI for the duration of a document-level operation.
struct ScopedPage {
    megapdf_page* page;
    explicit ScopedPage(const megapdf_document* d, int index) : page(megapdf_load_page(const_cast<megapdf_document*>(d), index)) {}
    ~ScopedPage() { megapdf_close_page(page); }
};

FPDF_PAGEOBJECT ImageObjectAt(const megapdf_page* p, int object_index) {
    if (p == nullptr || object_index < 0) return nullptr;
    FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, object_index);
    if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_IMAGE) return nullptr;
    return obj;
}

int ReplaceImageJpegUnlocked(const megapdf_page* p, int object_index, const unsigned char* jpeg, size_t length) {
    FPDF_PAGEOBJECT obj = ImageObjectAt(p, object_index);
    if (obj == nullptr) { SetError(0, "the object is not an image"); return MEGAPDF_ERR_ARGUMENT; }
    ReadBridge bridge{jpeg, length};
    FPDF_FILEACCESS access{};
    access.m_FileLen = static_cast<unsigned long>(length);
    access.m_GetBlock = GetBlockThunk;
    access.m_Param = &bridge;
    FPDF_PAGE pages[1] = {p->page};
    // Inline: PDFium consumes the data during the call, so the bridge may die after.
    if (!FPDFImageObj_LoadJpegFileInline(pages, 1, obj, &access)) {
        SetError(FPDF_ERR_UNKNOWN, "the compressed image could not be applied");
        return MEGAPDF_ERR_PDFIUM;
    }
    return GenerateContent(p) ? MEGAPDF_OK : MEGAPDF_ERR_PDFIUM;
}

}  // namespace

struct megapdf_images {
    std::vector<megapdf_image_info> images;
};

extern "C" {

MEGAPDF_API int megapdf_save(const megapdf_document* d, megapdf_write_fn write, void* context) {
    if (d == nullptr || write == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    if (d->form != nullptr) FORM_ForceToKillFocus(d->form);
    WriteBridge bridge{};
    bridge.fw.version = 1;
    bridge.fw.WriteBlock = WriteBlockThunk;
    bridge.write = write;
    bridge.context = context;
    bridge.failed = false;
    const FPDF_BOOL ok = FPDF_SaveAsCopy(d->doc, &bridge.fw, 0);
    if (bridge.failed) { SetError(0, "the write callback aborted the save"); return MEGAPDF_ERR_PDFIUM; }
    if (!ok) { SetError(FPDF_ERR_UNKNOWN, "PDFium could not serialize the document"); return MEGAPDF_ERR_PDFIUM; }
    return MEGAPDF_OK;
}

MEGAPDF_API int megapdf_flatten_all(const megapdf_document* d) {
    if (d == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    if (d->form != nullptr) FORM_ForceToKillFocus(d->form);
    const int count = FPDF_GetPageCount(d->doc);
    for (int i = 0; i < count; i++) {
        ScopedPage sp(d, i);
        if (sp.page == nullptr) { SetError(FPDF_ERR_UNKNOWN, "a page could not be loaded for flattening"); return MEGAPDF_ERR_PDFIUM; }
        if (FPDFPage_Flatten(sp.page->page, FLAT_NORMALDISPLAY) == FLATTEN_FAIL) {
            SetError(FPDF_ERR_UNKNOWN, "flattening a page failed");
            return MEGAPDF_ERR_PDFIUM;
        }
        if (!GenerateContent(sp.page)) return MEGAPDF_ERR_PDFIUM;
    }
    return MEGAPDF_OK;
}

MEGAPDF_API megapdf_images* megapdf_images_load(const megapdf_document* d) {
    if (d == nullptr) return nullptr;
    Guard guard(CoreLock());
    auto* result = new (std::nothrow) megapdf_images();
    if (result == nullptr) { SetError(FPDF_ERR_UNKNOWN, "out of memory"); return nullptr; }
    try {
        const int pages = FPDF_GetPageCount(d->doc);
        for (int pi = 0; pi < pages; pi++) {
            ScopedPage sp(d, pi);
            if (sp.page == nullptr) continue;
            const int count = FPDFPage_CountObjects(sp.page->page);
            for (int i = 0; i < count; i++) {
                FPDF_PAGEOBJECT obj = FPDFPage_GetObject(sp.page->page, i);
                if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_IMAGE) continue;
                unsigned int pw = 0, ph = 0;
                if (!FPDFImageObj_GetImagePixelSize(obj, &pw, &ph)) continue;
                float l = 0, b = 0, r = 0, t = 0;
                FPDFPageObj_GetBounds(obj, &l, &b, &r, &t);
                megapdf_image_info info{};
                info.page_index = pi;
                info.object_index = i;
                info.pixel_width = static_cast<int>(pw);
                info.pixel_height = static_cast<int>(ph);
                info.display_width = static_cast<double>(r - l);
                info.display_height = static_cast<double>(t - b);
                info.stored_bytes = static_cast<long long>(FPDFImageObj_GetImageDataRaw(obj, nullptr, 0));
                result->images.push_back(info);
            }
        }
    } catch (...) {
        delete result;
        SetError(FPDF_ERR_UNKNOWN, "out of memory listing images");
        return nullptr;
    }
    return result;
}

MEGAPDF_API void megapdf_images_free(megapdf_images* images) { delete images; }
MEGAPDF_API size_t megapdf_image_count(const megapdf_images* images) { return images ? images->images.size() : 0; }

MEGAPDF_API int megapdf_image_get(const megapdf_images* images, size_t index, megapdf_image_info* out) {
    if (images == nullptr || out == nullptr || index >= images->images.size()) return MEGAPDF_ERR_ARGUMENT;
    *out = images->images[index];
    return MEGAPDF_OK;
}

MEGAPDF_API megapdf_image* megapdf_render_image(const megapdf_document* d, int page_index, int object_index, int width, int height) {
    if (d == nullptr || width <= 0 || height <= 0) return nullptr;
    Guard guard(CoreLock());
    ScopedPage sp(d, page_index);
    FPDF_PAGEOBJECT obj = ImageObjectAt(sp.page, object_index);
    if (obj == nullptr) { SetError(0, "the object is not an image"); return nullptr; }
    // Same trick as stamp extraction: render through a temporary matrix sized to
    // the target pixels, then restore the placement.
    const FS_MATRIX target{static_cast<float>(width), 0, 0, static_cast<float>(height), 0, 0};
    megapdf_image* img = RenderObjectUnlocked(d->doc, sp.page->page, obj, target);
    if (img == nullptr) SetError(FPDF_ERR_UNKNOWN, "the image could not be rendered");
    return img;
}

MEGAPDF_API int megapdf_replace_image_jpeg(const megapdf_document* d, int page_index, int object_index,
                                           const unsigned char* jpeg, size_t length) {
    if (d == nullptr || jpeg == nullptr || length == 0) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    ScopedPage sp(d, page_index);
    if (sp.page == nullptr) return MEGAPDF_ERR_ARGUMENT;
    return ReplaceImageJpegUnlocked(sp.page, object_index, jpeg, length);
}

MEGAPDF_API int megapdf_shrink_images(const megapdf_document* d, megapdf_jpeg_encode_fn encode, megapdf_jpeg_release_fn release,
                                      void* context, int* out_replaced) {
    if (d == nullptr || encode == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    if (out_replaced != nullptr) *out_replaced = 0;
    megapdf_images* list = megapdf_images_load(d);
    if (list == nullptr) return MEGAPDF_ERR_MEMORY;
    int replaced = 0;
    int status = MEGAPDF_OK;
    for (const megapdf_image_info& image : list->images) {
        // The desktop rules (ImageShrinker), rounding half to even as .NET does.
        int target_w = static_cast<int>(std::nearbyint(image.display_width / 72.0 * 150.0));
        int target_h = static_cast<int>(std::nearbyint(image.display_height / 72.0 * 150.0));
        const bool oversized = image.pixel_width > target_w * 1.2;
        if ((!oversized && image.stored_bytes < 100000) || image.stored_bytes < 8000) continue;
        if (image.pixel_width < 8 || image.pixel_height < 8) continue;
        target_w = target_w < 8 ? 8 : (target_w > image.pixel_width ? image.pixel_width : target_w);
        target_h = target_h < 8 ? 8 : (target_h > image.pixel_height ? image.pixel_height : target_h);

        megapdf_image* pixels = megapdf_render_image(d, image.page_index, image.object_index, target_w, target_h);
        if (pixels == nullptr) continue;
        unsigned char* jpeg = nullptr;
        size_t length = 0;
        const int encoded = encode(context, pixels->bgra.data(), pixels->width, pixels->height, 0.75, &jpeg, &length);
        megapdf_image_free(pixels);
        if (!encoded || jpeg == nullptr) continue;
        // A re-encode that saves less than 10% is not worth the quality loss.
        if (static_cast<double>(length) < image.stored_bytes * 0.9) {
            ScopedPage sp(d, image.page_index);
            const int one = sp.page ? ReplaceImageJpegUnlocked(sp.page, image.object_index, jpeg, length) : MEGAPDF_ERR_ARGUMENT;
            if (one == MEGAPDF_OK) replaced++; else status = one;
        }
        if (release != nullptr) release(context, jpeg);
    }
    megapdf_images_free(list);
    if (out_replaced != nullptr) *out_replaced = replaced;
    return status;
}

}  // extern "C"

// --------------------------------------------------------------------------
// Contract 7: render policy (#111)
// --------------------------------------------------------------------------

extern "C" {

MEGAPDF_API void megapdf_render_size(double ideal_width, double ideal_height, int* out_width, int* out_height) {
    const double w = ideal_width > 1.0 ? ideal_width : 1.0;
    const double h = ideal_height > 1.0 ? ideal_height : 1.0;
    const double side = MEGAPDF_RENDER_MAX_SIDE;
    const double budget = static_cast<double>(MEGAPDF_RENDER_MAX_PIXELS);
    // Not std::min: <windef.h>, which pdfium's headers pull in on Windows, defines a min macro.
    auto smaller = [](double a, double b) { return a < b ? a : b; };
    double scale = 1.0;
    if (w > side) scale = smaller(scale, side / w);
    if (h > side) scale = smaller(scale, side / h);
    if (w * h * scale * scale > budget) scale = smaller(scale, std::sqrt(budget / (w * h)));
    const int width = static_cast<int>(std::floor(w * scale));
    const int height = static_cast<int>(std::floor(h * scale));
    if (out_width != nullptr) *out_width = width < 1 ? 1 : width;
    if (out_height != nullptr) *out_height = height < 1 ? 1 : height;
}

MEGAPDF_API int megapdf_render_is_capped(double ideal_width, double ideal_height) {
    int w = 0, h = 0;
    megapdf_render_size(ideal_width, ideal_height, &w, &h);
    const int ideal_w = static_cast<int>(std::floor(ideal_width > 1.0 ? ideal_width : 1.0));
    const int ideal_h = static_cast<int>(std::floor(ideal_height > 1.0 ? ideal_height : 1.0));
    return (w < ideal_w || h < ideal_h) ? 1 : 0;
}

MEGAPDF_API int megapdf_render(const megapdf_page* p, void* buffer, int width, int height, int stride, unsigned int flags) {
    if (p == nullptr || buffer == nullptr || width <= 0 || height <= 0 || stride < width * 4) return MEGAPDF_ERR_ARGUMENT;
    if (width > MEGAPDF_RENDER_MAX_SIDE || height > MEGAPDF_RENDER_MAX_SIDE ||
        static_cast<long long>(width) * height > MEGAPDF_RENDER_MAX_PIXELS) {
        SetError(0, "the requested raster is past the render clamp; ask megapdf_render_size first");
        return MEGAPDF_ERR_ARGUMENT;
    }
    Guard guard(CoreLock());
    FPDF_BITMAP bmp = FPDFBitmap_CreateEx(width, height, FPDFBitmap_BGRA, buffer, stride);
    if (bmp == nullptr) {
        SetError(FPDF_ERR_UNKNOWN, "PDFium refused the render bitmap");
        return MEGAPDF_ERR_PDFIUM;
    }
    // The shared recipe: white ground, page content, then live form-field values.
    int render_flags = FPDF_ANNOT | FPDF_LCD_TEXT;
    if (flags & MEGAPDF_RENDER_RGBA) render_flags |= FPDF_REVERSE_BYTE_ORDER;
    FPDFBitmap_FillRect(bmp, 0, 0, width, height, 0xFFFFFFFF);
    FPDF_RenderPageBitmap(bmp, p->page, 0, 0, width, height, 0, render_flags);
    if (p->owner != nullptr && p->owner->form != nullptr) {
        FPDF_FFLDraw(p->owner->form, bmp, p->page, 0, 0, width, height, 0, render_flags);
    }
    FPDFBitmap_Destroy(bmp);
    return MEGAPDF_OK;
}

}  // extern "C"

// --------------------------------------------------------------------------
// Phase 3: body-text editing (#112)
// --------------------------------------------------------------------------

namespace {

// FPDFFont_GetBaseFontName / GetFamilyName: UTF-8, length in bytes including the terminator.
std::string ReadFontNameUtf8(FPDF_FONT font, bool base_name) {
    const size_t bytes = base_name ? FPDFFont_GetBaseFontName(font, nullptr, 0) : FPDFFont_GetFamilyName(font, nullptr, 0);
    if (bytes <= 1) return "";
    std::string buf(bytes, '\0');
    if (base_name) FPDFFont_GetBaseFontName(font, &buf[0], static_cast<unsigned long>(bytes));
    else FPDFFont_GetFamilyName(font, &buf[0], static_cast<unsigned long>(bytes));
    buf.resize(bytes - 1);
    return buf;
}

bool IsSubsetName(const std::string& name) {
    if (name.size() <= 7 || name[6] != '+') return false;
    for (size_t i = 0; i < 6; i++) if (name[i] < 'A' || name[i] > 'Z') return false;
    return true;
}

std::string MapToStandard(const std::string& original) {
    std::string name;
    for (char c : original) name += static_cast<char>((c >= 'A' && c <= 'Z') ? c + 32 : c);
    auto has = [&](const char* needle) { return name.find(needle) != std::string::npos; };
    const bool bold = has("bold");
    const bool italic = has("italic") || has("oblique");
    if (has("courier") || has("mono")) {
        return bold && italic ? "Courier-BoldOblique" : bold ? "Courier-Bold" : italic ? "Courier-Oblique" : "Courier";
    }
    if (has("times") || (has("serif") && !has("sans"))) {
        return bold && italic ? "Times-BoldItalic" : bold ? "Times-Bold" : italic ? "Times-Italic" : "Times-Roman";
    }
    return bold && italic ? "Helvetica-BoldOblique" : bold ? "Helvetica-Bold" : italic ? "Helvetica-Oblique" : "Helvetica";
}

// True when the characters the text page attributes to `obj` are exactly `want`.
// PDFium generates characters of its own while extracting: a separator space before
// the next object on the line, a line break, or spaces where glyphs sit farther
// apart than the font says they should. Generated characters at the ends are
// separators and do not count; one inside the run means the glyphs drew spread
// apart (the font had no width for them), which is a failed edit (#116).
bool AuthoredTextIs(FPDF_TEXTPAGE text_page, FPDF_PAGEOBJECT obj, const unsigned short* want) {
    if (text_page == nullptr) return false;
    struct Char { unsigned short code; bool generated; };
    std::vector<Char> chars;
    const int count = FPDFText_CountChars(text_page);
    for (int i = 0; i < count; i++) {
        if (FPDFText_GetTextObject(text_page, i) != obj) continue;
        const unsigned int u = FPDFText_GetUnicode(text_page, i);
        chars.push_back(Char{static_cast<unsigned short>(u > 0xFFFF ? 0xFFFD : u), FPDFText_IsGenerated(text_page, i) == 1});
    }
    size_t begin = 0, end = chars.size();
    while (begin < end && chars[begin].generated) begin++;
    while (end > begin && chars[end - 1].generated) end--;
    const size_t n = U16Length(want);
    if (end - begin != n) return false;
    for (size_t i = 0; i < n; i++) {
        if (chars[begin + i].generated || chars[begin + i].code != want[i]) return false;
    }
    return true;
}

// Tier 2 test (SDD §3.1): the object's font is a subset and the new text needs a
// glyph the document never used. Coverage is approximated by every character the
// page draws with the same base font.
bool NeedsSubstitution(const megapdf_page* p, FPDF_PAGEOBJECT obj, const unsigned short* text) {
    FPDF_FONT font = FPDFTextObj_GetFont(obj);
    if (font == nullptr) return false;
    const std::string base = ReadFontNameUtf8(font, true);
    if (!IsSubsetName(base)) return false;

    std::vector<bool> covered(65536, false);
    FPDF_TEXTPAGE text_page = FPDFText_LoadPage(p->page);
    const int count = FPDFPage_CountObjects(p->page);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT other = FPDFPage_GetObject(p->page, i);
        if (other == nullptr || FPDFPageObj_GetType(other) != FPDF_PAGEOBJ_TEXT) continue;
        FPDF_FONT other_font = FPDFTextObj_GetFont(other);
        if (other_font == nullptr || ReadFontNameUtf8(other_font, true) != base) continue;
        for (unsigned short c : ReadObjectText(other, text_page)) covered[c] = true;
    }
    if (text_page != nullptr) FPDFText_ClosePage(text_page);
    for (size_t i = 0; text[i] != 0; i++) if (!covered[text[i]]) return true;
    return false;
}

// Replaces the text object with one in a standard face, preserving index,
// matrix, fill colour and the text-box identity (#45).
int SubstituteUnlocked(const megapdf_page* p, FPDF_PAGEOBJECT old_obj, int object_index, const unsigned short* text) {
    FPDF_DOCUMENT doc = p->owner ? p->owner->doc : nullptr;
    float font_size = 0;
    FPDFTextObj_GetFontSize(old_obj, &font_size);
    FPDF_FONT old_font = FPDFTextObj_GetFont(old_obj);
    const std::string original = old_font ? ReadFontNameUtf8(old_font, false) : "";

    FPDF_FONT standard = FPDFText_LoadStandardFont(doc, MapToStandard(original).c_str());
    if (standard == nullptr) { SetError(0, "no substitute font could be loaded"); return MEGAPDF_ERR_NO_FONT; }
    FPDF_PAGEOBJECT new_obj = FPDFPageObj_CreateTextObj(doc, standard, font_size);
    if (new_obj == nullptr) { FPDFFont_Close(standard); SetError(0, "could not create replacement text"); return MEGAPDF_ERR_NO_FONT; }
    if (!FPDFText_SetText(new_obj, reinterpret_cast<FPDF_WIDESTRING>(text))) {
        FPDFPageObj_Destroy(new_obj);
        FPDFFont_Close(standard);
        SetError(0, "the substitute font could not render the new text");
        return MEGAPDF_ERR_NO_FONT;
    }
    FS_MATRIX matrix{};
    if (FPDFPageObj_GetMatrix(old_obj, &matrix)) FPDFPageObj_SetMatrix(new_obj, &matrix);
    unsigned int r = 0, g = 0, b = 0, a = 0;
    if (FPDFPageObj_GetFillColor(old_obj, &r, &g, &b, &a)) FPDFPageObj_SetFillColor(new_obj, r, g, b, a);

    // Read the box's identity off the OLD object while it still exists — it is
    // destroyed below, and the mark is rebuilt on the replacement from these values.
    const bool was_box = HasMark(old_obj, kTextBoxMark);
    const U16 box_id = was_box ? ReadMarkParam(old_obj, "id") : U16{};
    const U16 box_font = was_box ? ReadMarkParam(old_obj, "font") : U16{};

    int status = MEGAPDF_OK;
    if (!FPDFPage_RemoveObject(p->page, old_obj)) {
        FPDFPageObj_Destroy(new_obj);
        SetError(FPDF_ERR_UNKNOWN, "could not remove the original text object");
        status = MEGAPDF_ERR_PDFIUM;
    } else {
        FPDFPageObj_Destroy(old_obj);
        if (!FPDFPage_InsertObjectAtIndex(p->page, new_obj, static_cast<size_t>(object_index))) {
            SetError(FPDF_ERR_UNKNOWN, "could not insert the replacement text object");
            status = MEGAPDF_ERR_PDFIUM;
        } else if (was_box) {
            // Re-tag AFTER insertion, off the object the page now owns — the order the
            // params actually stick in. Re-adding the mark alone was #45: the box read
            // as a text box but carried no id, so both phones refused to select it. A
            // box written before the id param existed stays untagged rather than
            // gaining a fabricated identity no phone recorded.
            FPDF_PAGEOBJECT inserted = FPDFPage_GetObject(p->page, object_index);
            FPDF_PAGEOBJECTMARK mark = inserted ? FPDFPageObj_AddMark(inserted, kTextBoxMarkName) : nullptr;
            if (mark != nullptr) {
                auto ascii = [](const U16& v) { std::string out; for (unsigned short c : v) out += static_cast<char>(c < 0x80 ? c : '?'); return out; };
                if (!box_id.empty()) FPDFPageObjMark_SetStringParam(doc, inserted, mark, "id", ascii(box_id).c_str());
                if (!box_font.empty()) FPDFPageObjMark_SetStringParam(doc, inserted, mark, "font", ascii(box_font).c_str());
            }
        }
    }
    FPDFFont_Close(standard);
    if (status == MEGAPDF_OK && !GenerateContent(p)) status = MEGAPDF_ERR_PDFIUM;
    return status;
}

}  // namespace

extern "C" {

MEGAPDF_API int megapdf_set_text(const megapdf_page* p, int object_index, const unsigned short* text, int force_substitute,
                                 int* out_outcome) {
    if (p == nullptr || object_index < 0 || text == nullptr || text[0] == 0) {
        SetError(0, "PDFium cannot set empty text on a text object");
        return MEGAPDF_ERR_ARGUMENT;
    }
    Guard guard(CoreLock());
    FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, object_index);
    if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_TEXT) {
        SetError(0, "the object is no longer a text object");
        return MEGAPDF_ERR_ARGUMENT;
    }
    if (!force_substitute && !NeedsSubstitution(p, obj, text)) {
        if (FPDFText_SetText(obj, reinterpret_cast<FPDF_WIDESTRING>(text))) {
            if (!GenerateContent(p)) return MEGAPDF_ERR_PDFIUM;
            // Prove the text took (#116). FPDFText_SetText succeeds whatever the font
            // can actually carry: a font with no slot in its encoding drops the
            // character, one with no mapping draws the wrong glyph, one with no width
            // spreads the letters apart — and the subset-name check above only knows
            // about ABCDEF+ fonts. Over the corpus half of all tier-1 edits failed
            // like that. What reads back is what the page now says; anything but the
            // exact requested text falls through to the standard-face substitute.
            FPDF_TEXTPAGE text_page = FPDFText_LoadPage(p->page);
            const bool took = AuthoredTextIs(text_page, obj, text);
            if (text_page != nullptr) FPDFText_ClosePage(text_page);
            if (took) {
                if (out_outcome != nullptr) *out_outcome = MEGAPDF_EDIT_IN_PLACE;
                return MEGAPDF_OK;
            }
        }
        // In-place set failed outright or did not read back — substitute.
    }
    const int status = SubstituteUnlocked(p, obj, object_index, text);
    if (status == MEGAPDF_OK && out_outcome != nullptr) *out_outcome = MEGAPDF_EDIT_SUBSTITUTED;
    return status;
}

MEGAPDF_API int megapdf_insert_text_run(const megapdf_page* p, int object_index, const unsigned short* text, const char* font_name,
                                        double font_size, double left, double baseline) {
    if (p == nullptr || object_index < 0 || text == nullptr || text[0] == 0 || font_name == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    FPDF_DOCUMENT doc = p->owner ? p->owner->doc : nullptr;
    FPDF_FONT font = FPDFText_LoadStandardFont(doc, MapToStandard(font_name).c_str());
    if (font == nullptr) { SetError(0, "no substitute font could be loaded"); return MEGAPDF_ERR_NO_FONT; }
    FPDF_PAGEOBJECT obj = FPDFPageObj_CreateTextObj(doc, font, static_cast<float>(font_size));
    bool ok = obj != nullptr && FPDFText_SetText(obj, reinterpret_cast<FPDF_WIDESTRING>(text));
    if (ok) {
        FS_MATRIX m{1, 0, 0, 1, static_cast<float>(left + p->crop_x), static_cast<float>(baseline + p->crop_y)};
        FPDFPageObj_SetMatrix(obj, &m);
        const int count = FPDFPage_CountObjects(p->page);
        ok = FPDFPage_InsertObjectAtIndex(p->page, obj, static_cast<size_t>(object_index > count ? count : object_index));
        obj = nullptr;
    }
    if (obj != nullptr) FPDFPageObj_Destroy(obj);
    FPDFFont_Close(font);
    if (!ok) { SetError(FPDF_ERR_UNKNOWN, "could not recreate the text"); return MEGAPDF_ERR_PDFIUM; }
    return GenerateContent(p) ? MEGAPDF_OK : MEGAPDF_ERR_PDFIUM;
}

MEGAPDF_API int megapdf_is_subset_font_name(const char* base_name) {
    return base_name != nullptr && IsSubsetName(base_name) ? 1 : 0;
}

MEGAPDF_API size_t megapdf_map_to_standard_font(const char* original_name, char* out, size_t capacity) {
    const std::string face = MapToStandard(original_name ? original_name : "");
    if (out != nullptr && capacity > face.size()) std::memcpy(out, face.c_str(), face.size() + 1);
    return face.size();
}

}  // extern "C"

