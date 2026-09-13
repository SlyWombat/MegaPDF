// The one implementation of the shared engine policy (ADR-003, #33).
//
// Phase 1 moved a single heuristic here to prove the packaging. From #105 the
// core owns documents: bytes in, opaque handles out, and the form-fill
// environment, page lifecycle and serialising mutex live here rather than in
// three bindings.

#include "megapdf_core.h"

#include <algorithm>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#include "fpdf_annot.h"
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
// Raw handles (transitional)
// --------------------------------------------------------------------------

extern "C" {

MEGAPDF_API void* megapdf_document_raw(const megapdf_document* d) { return d ? d->doc : nullptr; }
MEGAPDF_API void* megapdf_document_form_raw(const megapdf_document* d) { return d ? d->form : nullptr; }
MEGAPDF_API void* megapdf_page_raw(const megapdf_page* p) { return p ? p->page : nullptr; }

}  // extern "C"
