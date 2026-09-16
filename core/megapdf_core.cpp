// The one implementation of the shared engine policy (ADR-003, #33).
//
// Phase 1 moved a single heuristic here to prove the packaging. From #105 the
// core owns documents: bytes in, opaque handles out, and the form-fill
// environment, page lifecycle and serialising mutex live here rather than in
// three bindings.

#include "megapdf_core.h"
#include "megapdf_core_testing.h"

#include <algorithm>
#include <atomic>
#include <cerrno>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <functional>
#include <map>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>
#include <utility>
#include <vector>

// Reading a document from its file rather than from a copy of it (#147, #148).
#if defined(_WIN32)
#  include <windows.h>
#else
#  include <fcntl.h>
#  include <sys/stat.h>
#  include <unistd.h>
#  if defined(__APPLE__)
#    include <sys/clonefile.h>   // a copy of the file that shares its blocks (#147)
#  endif
#endif

#include "fpdf_annot.h"
#include "fpdf_edit.h"
#include "fpdf_formfill.h"
#include "fpdf_ppo.h"   // FPDF_ImportPagesByIndex: the #118 dry run works on a copy of the page
#include "fpdf_progressive.h"  // the page check renders in slices it can stop between (#145)
#include "fpdf_flatten.h"
#include "fpdf_save.h"
#include "fpdf_text.h"
#include "fpdf_transformpage.h"  // FPDFPage_GetCropBox
#include "fpdfview.h"

// --------------------------------------------------------------------------
// Internals
// --------------------------------------------------------------------------

struct megapdf_detached;

// A flag the caller raises from any thread to stop a page check at its next stage (#145).
struct megapdf_cancel {
    std::atomic<int> raised{0};
};

// An open file PDFium reads a document out of as it parses (#147, #148).
//
// FPDF_LoadMemDocument64 needs the whole file in memory, so the binding had to read
// it there first: two copies of every document, and on Windows no copy at all past
// the 2 GB a .NET byte[] can hold. FPDF_LoadCustomDocument reads through this
// instead, which costs what PDFium's parser caches and nothing more.
//
// Data only, so megapdf_document can hold one by value; the operations on it are
// free functions in the anonymous namespace below.
struct FileSource {
    FPDF_FILEACCESS access{};      // handed to PDFium; must outlive the document
#if defined(_WIN32)
    void* handle = nullptr;        // a HANDLE; NULL when closed
#else
    int fd = -1;
#endif
    unsigned long long length = 0;
    bool open = false;
};

struct megapdf_document {
    std::vector<unsigned char> bytes;   // FPDF_LoadMemDocument64 needs the buffer alive for the document's life.
    FileSource source;                  // ...or the file it is read from, for a megapdf_open_file() document.
    FPDF_DOCUMENT doc = nullptr;
    FPDF_FORMHANDLE form = nullptr;
    FPDF_FORMFILLINFO ffi{};
    std::vector<megapdf_page*> open_pages;  // closed for the caller if still open at megapdf_close()
    std::vector<megapdf_detached*> detached;  // freed at megapdf_close() if never restored or discarded
    std::map<std::pair<int, int>, megapdf_layout_verdict> rewrite_keeps_page;  // #118/#128 verdicts by (page index, object index)
    // How many times each page's content has been regenerated: a page check that let go of
    // the lock between its stages caches its answer only if the page did not change (#145).
    std::map<int, unsigned long long> page_changes;
    // Page checks running between stages without the lock; megapdf_close() waits for them (#145).
    int active_checks = 0;
    bool moving_source = false;   // megapdf_read_from_copy() is copying without the lock (#147)
    bool closing = false;
    // What the document was opened with, so megapdf_open_like() can read back a saved
    // copy that is still protected (#132). Memory only; megapdf_close() wipes it.
    std::string unlock;
    bool has_unlock = false;
};

// Page objects taken off a page and kept for undo. One object for megapdf_detach_object();
// for a line, its runs and the hidden copies drawn with them (#136), ascending by the index
// each had before it was taken, which is where megapdf_restore_detached() puts it back.
struct megapdf_detached {
    struct Part {
        int index;
        int copy_of;   // the run's object index when this is a hidden copy of it, -1 otherwise
        FPDF_PAGEOBJECT object;
    };
    megapdf_document* owner = nullptr;
    int page_index = -1;
    std::vector<Part> parts;
    // megapdf_set_text() / megapdf_set_line_text(): the edited run stands at this index in
    // place of the part that has it, until megapdf_restore_detached() takes it off. -1 otherwise.
    int edited_index = -1;
    // Its bounds when the edit made it: how the undo knows the object it takes off is the edit.
    // (A page reloaded between the edit and its undo holds new objects, so the pointer cannot.)
    float edited_left = 0, edited_bottom = 0, edited_right = 0, edited_top = 0;
};

struct megapdf_page {
    megapdf_document* owner = nullptr;
    FPDF_PAGE page = nullptr;
    int index = -1;
    double crop_x = 0.0;
    double crop_y = 0.0;
    double unit = 1.0;   // the page's /UserUnit: points per user space unit (#150)
};

namespace {

// PDFium is not thread-safe. Every ABI entry point takes this; recursive so a
// future core routine may call another ABI routine without deadlocking.
std::recursive_mutex& CoreLock() {
    static std::recursive_mutex lock;
    return lock;
}
using Guard = std::lock_guard<std::recursive_mutex>;

// Signalled when a page check that runs between stages without the lock finishes (#145).
std::condition_variable_any& ChecksDone() {
    static std::condition_variable_any done;
    return done;
}

// megapdf_testing_set_page_check_hook(): the core tests park a page check at a stage (#145).
std::atomic<megapdf_page_check_stage_hook> g_page_check_hook{nullptr};
std::atomic<void*> g_page_check_hook_context{nullptr};

// megapdf_testing_set_max_file_bytes(): the core tests lower the file-backed open's size
// limit, so the refusal past it runs on every platform without a 4 GiB file (#147). 0 = none.
std::atomic<unsigned long long> g_testing_max_file_bytes{0};

thread_local unsigned int g_last_error = 0;
thread_local std::string g_last_message;

// The verdict behind this thread's latest layout refusal (#128); see megapdf_last_layout_verdict().
megapdf_layout_verdict EditableVerdict() {
    megapdf_layout_verdict v{};
    v.editable = 1;
    v.cause = MEGAPDF_LAYOUT_OK;
    return v;
}
thread_local megapdf_layout_verdict g_last_layout = EditableVerdict();

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

// Crop space is points: user space, less the CropBox origin, times the page's /UserUnit
// (#150). Most pages have none, and the factor is 1. Every coordinate and length that
// leaves the core goes through Out*, and every one that comes in through In*.
double OutX(const megapdf_page* p, double x) { return (x - p->crop_x) * p->unit; }
double OutY(const megapdf_page* p, double y) { return (y - p->crop_y) * p->unit; }
double InX(const megapdf_page* p, double x) { return x / p->unit + p->crop_x; }
double InY(const megapdf_page* p, double y) { return y / p->unit + p->crop_y; }
megapdf_rect OutRect(const megapdf_page* p, double l, double b, double r, double t) {
    return megapdf_rect{OutX(p, l), OutY(p, b), OutX(p, r), OutY(p, t)};
}

// --------------------------------------------------------------------------
// Reading a document from its file (#147, #148)
// --------------------------------------------------------------------------

// The largest file FPDF_FILEACCESS can describe: it states a length and takes read
// offsets as `unsigned long`, 32 bits on Windows and 64 bits everywhere else.
const unsigned long long kMaxFileSourceBytes = static_cast<unsigned long long>(static_cast<unsigned long>(-1));

void FileSourceClose(FileSource* s) {
#if defined(_WIN32)
    if (s->handle != nullptr) CloseHandle(reinterpret_cast<HANDLE>(s->handle));
    s->handle = nullptr;
#else
    if (s->fd >= 0) ::close(s->fd);
    s->fd = -1;
#endif
    s->open = false;
}

// A positional read that never touches a shared file pointer, so it stays correct
// whatever else is reading the same descriptor.
bool FileSourceRead(FileSource* s, unsigned long long pos, unsigned char* buf, size_t size) {
    size_t done = 0;
    while (done < size) {
#if defined(_WIN32)
        const unsigned long long at = pos + done;
        OVERLAPPED ov{};
        ov.Offset = static_cast<DWORD>(at & 0xFFFFFFFFull);
        ov.OffsetHigh = static_cast<DWORD>(at >> 32);
        DWORD got = 0;
        const size_t want = size - done;
        if (!ReadFile(reinterpret_cast<HANDLE>(s->handle), buf + done,
                      static_cast<DWORD>(want > 0x10000000u ? 0x10000000u : want), &got, &ov) || got == 0) {
            return false;
        }
        done += got;
#else
        const ssize_t got = ::pread(s->fd, buf + done, size - done, static_cast<off_t>(pos + done));
        if (got < 0) {
            if (errno == EINTR) continue;
            return false;
        }
        if (got == 0) return false;   // short of what PDFium asked for: the file shrank
        done += static_cast<size_t>(got);
#endif
    }
    return true;
}

int FileSourceGetBlock(void* param, unsigned long position, unsigned char* buf, unsigned long size) {
    auto* s = static_cast<FileSource*>(param);
    if (s == nullptr || !s->open || buf == nullptr || size == 0) return 0;
    return FileSourceRead(s, position, buf, static_cast<size_t>(size)) ? 1 : 0;
}

// Fills in the length and wires up the callback once the handle is open. False, with
// the error set, when the file is empty or past what FPDF_FILEACCESS can address.
bool FileSourceFinish(FileSource* s, unsigned long long length) {
    if (length == 0) {
        FileSourceClose(s);
        SetError(FPDF_ERR_FILE, "the file is empty");
        return false;
    }
    const unsigned long long testing_max = g_testing_max_file_bytes.load();
    if (length > kMaxFileSourceBytes || (testing_max != 0 && length > testing_max)) {
        FileSourceClose(s);
        SetError(MEGAPDF_OPEN_ERR_TOO_LARGE, "the file is too large to open on this platform");
        return false;
    }
    s->length = length;
    s->open = true;
    s->access.m_FileLen = static_cast<unsigned long>(length);
    s->access.m_GetBlock = FileSourceGetBlock;
    s->access.m_Param = s;
    return true;
}

// Opens `path_utf8` for reading, shared: the file may still be renamed, written or
// deleted while the document is open. That is what the atomic-replace save needs —
// on Windows FILE_SHARE_DELETE lets ReplaceFile swap the file out from under us, and
// on POSIX an unlinked inode stays alive for an open descriptor — and it is why a
// cloud-synced file is never locked by being open in MegaPDF.
bool FileSourceOpenPath(FileSource* s, const char* path_utf8) {
#if defined(_WIN32)
    const int wide_len = MultiByteToWideChar(CP_UTF8, 0, path_utf8, -1, nullptr, 0);
    if (wide_len <= 0) {
        SetError(FPDF_ERR_FILE, "the file name could not be read");
        return false;
    }
    std::wstring wide(static_cast<size_t>(wide_len), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, path_utf8, -1, wide.data(), wide_len);
    HANDLE h = CreateFileW(wide.c_str(), GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_RANDOM_ACCESS, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        SetError(FPDF_ERR_FILE, "the file could not be opened");
        return false;
    }
    s->handle = h;
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(h, &size) || size.QuadPart < 0) {
        FileSourceClose(s);
        SetError(FPDF_ERR_FILE, "the file's size could not be read");
        return false;
    }
    return FileSourceFinish(s, static_cast<unsigned long long>(size.QuadPart));
#else
    const int fd = ::open(path_utf8, O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        SetError(FPDF_ERR_FILE, "the file could not be opened");
        return false;
    }
    s->fd = fd;
    struct stat st{};
    if (::fstat(fd, &st) != 0 || !S_ISREG(st.st_mode)) {
        FileSourceClose(s);
        SetError(FPDF_ERR_FILE, "the file's size could not be read");
        return false;
    }
    return FileSourceFinish(s, static_cast<unsigned long long>(st.st_size));
#endif
}

// Takes ownership of an already-open descriptor (Android's content URIs).
bool FileSourceAdoptFd(FileSource* s, int fd) {
#if defined(_WIN32)
    (void)s;
    (void)fd;
    SetError(FPDF_ERR_FILE, "opening by descriptor is not supported on this platform");
    return false;
#else
    if (fd < 0) {
        SetError(FPDF_ERR_FILE, "not a readable descriptor");
        return false;
    }
    s->fd = fd;
    struct stat st{};
    if (::fstat(fd, &st) != 0 || !S_ISREG(st.st_mode)) {
        FileSourceClose(s);
        SetError(FPDF_ERR_FILE, "the descriptor is not a readable file");
        return false;
    }
    return FileSourceFinish(s, static_cast<unsigned long long>(st.st_size));
#endif
}

#if defined(_WIN32)
// The volume and file index that name a file on Windows, whatever path reached it.
bool FileIdentity(HANDLE h, unsigned long long* volume, unsigned long long* index) {
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(h, &info)) return false;
    *volume = info.dwVolumeSerialNumber;
    *index = (static_cast<unsigned long long>(info.nFileIndexHigh) << 32) | info.nFileIndexLow;
    return true;
}

std::wstring Widen(const char* utf8) {
    const int n = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, nullptr, 0);
    if (n <= 0) return std::wstring();
    std::wstring wide(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8, -1, wide.data(), n);
    wide.resize(static_cast<size_t>(n - 1));
    return wide;
}
#endif

// Whether `s` reads the file at `path_utf8` (#147).
bool FileSourceIsPath(const FileSource& s, const char* path_utf8) {
    if (!s.open || path_utf8 == nullptr || path_utf8[0] == '\0') return false;
#if defined(_WIN32)
    const std::wstring wide = Widen(path_utf8);
    if (wide.empty()) return false;
    // No access asked for: only the identity is read, and nothing is locked.
    HANDLE h = CreateFileW(wide.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    unsigned long long v1 = 0, i1 = 0, v2 = 0, i2 = 0;
    const bool same = FileIdentity(h, &v1, &i1) && FileIdentity(static_cast<HANDLE>(s.handle), &v2, &i2) &&
                      v1 == v2 && i1 == i2;
    CloseHandle(h);
    return same;
#else
    struct stat a{}, b{};
    return ::stat(path_utf8, &a) == 0 && ::fstat(s.fd, &b) == 0 && a.st_dev == b.st_dev && a.st_ino == b.st_ino;
#endif
}

// Makes a private copy of what `s` reads at `path_utf8` and returns a source reading it, with
// the copy's name already gone. Runs without the core lock: FileSourceRead takes none.
bool FileSourceCopy(const FileSource& s, const char* path_utf8, FileSource* out) {
#if defined(_WIN32)
    const std::wstring wide = Widen(path_utf8);
    if (wide.empty()) {
        SetError(FPDF_ERR_FILE, "the copy's file name could not be read");
        return false;
    }
    // Deleted when the handle closes, whether the document closes or the process dies.
    HANDLE h = CreateFileW(wide.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_DELETE, nullptr,
                           CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE | FILE_FLAG_RANDOM_ACCESS,
                           nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        SetError(FPDF_ERR_FILE, "the copy could not be created");
        return false;
    }
    out->handle = h;
    const bool cloned = false;
#else
    int fd = -1;
    bool cloned = false;
#  if defined(__APPLE__)
    // APFS clones in constant time and shares the blocks until either side is written, which
    // is exactly the case here: the original is about to be written, the copy never is.
    if (::fclonefileat(s.fd, AT_FDCWD, path_utf8, 0) == 0) {
        fd = ::open(path_utf8, O_RDONLY | O_CLOEXEC);
        ::unlink(path_utf8);
        struct stat st{};
        if (fd >= 0 && (::fstat(fd, &st) != 0 || static_cast<unsigned long long>(st.st_size) != s.length)) {
            ::close(fd);   // the original changed length since it was opened: copy what the document read
            fd = -1;
        }
        cloned = fd >= 0;
    }
    if (fd < 0)
#  endif
    {
        fd = ::open(path_utf8, O_RDWR | O_CREAT | O_EXCL | O_CLOEXEC, 0600);
        if (fd < 0) {
            SetError(FPDF_ERR_FILE, "the copy could not be created");
            return false;
        }
        ::unlink(path_utf8);   // the descriptor keeps it; nothing is left however the app ends
    }
    out->fd = fd;
#endif
    out->length = s.length;
    out->open = true;
    if (cloned) return true;   // a clone is already complete

    std::vector<unsigned char> buffer;
    try {
        buffer.resize(4u << 20);
    } catch (...) {
        FileSourceClose(out);
        SetError(FPDF_ERR_UNKNOWN, "out of memory copying the file");
        return false;
    }
    for (unsigned long long at = 0; at < s.length;) {
        const size_t n = static_cast<size_t>(std::min<unsigned long long>(buffer.size(), s.length - at));
        if (!FileSourceRead(const_cast<FileSource*>(&s), at, buffer.data(), n)) {
            FileSourceClose(out);
            SetError(FPDF_ERR_FILE, "the file could not be read to copy it");
            return false;
        }
        size_t done = 0;
        while (done < n) {
#if defined(_WIN32)
            const unsigned long long to = at + done;
            OVERLAPPED ov{};
            ov.Offset = static_cast<DWORD>(to & 0xFFFFFFFFull);
            ov.OffsetHigh = static_cast<DWORD>(to >> 32);
            DWORD wrote = 0;
            if (!WriteFile(static_cast<HANDLE>(out->handle), buffer.data() + done, static_cast<DWORD>(n - done), &wrote, &ov) ||
                wrote == 0) {
                FileSourceClose(out);
                SetError(FPDF_ERR_FILE, "the copy could not be written");
                return false;
            }
            done += wrote;
#else
            const ssize_t wrote = ::pwrite(out->fd, buffer.data() + done, n - done, static_cast<off_t>(at + done));
            if (wrote < 0 && errno == EINTR) continue;
            if (wrote <= 0) {
                FileSourceClose(out);
                SetError(FPDF_ERR_FILE, "the copy could not be written");
                return false;
            }
            done += static_cast<size_t>(wrote);
#endif
        }
        at += n;
    }
    return true;
}

// The tail every open shares. PDFium has either produced the document or it has not;
// the failure message, the password kept for megapdf_open_like() (#132) and the
// form-fill environment are the same however the bytes arrived. Takes ownership of
// `d`: on failure it is freed, along with any file it was reading.
megapdf_document* FinishOpenUnlocked(megapdf_document* d, const char* password_utf8) {
    if (d->doc == nullptr) {
        const unsigned long code = FPDF_GetLastError();
        FileSourceClose(&d->source);
        delete d;
        SetError(code, code == FPDF_ERR_PASSWORD ? "the document needs a password, or the password is wrong"
                     : code == FPDF_ERR_FORMAT   ? "the file is not a valid PDF"
                     : code == FPDF_ERR_SECURITY ? "the document's security handler is not supported"
                                                  : "PDFium could not load the document");
        return nullptr;
    }
    if (password_utf8 != nullptr) {
        try {
            d->unlock.assign(password_utf8);
            d->has_unlock = true;
        } catch (...) {
            // Only megapdf_open_like() is affected; the document itself is open.
        }
    }
    InitFormFillInfo(&d->ffi);
    d->form = FPDFDOC_InitFormFillEnvironment(d->doc, &d->ffi);
    // A missing form environment is survivable (no AcroForm interaction); every
    // platform has treated it that way.
    SetError(0, "");
    return d;
}

// Hands PDFium the file `d` has just opened, and finishes the open (#147).
megapdf_document* OpenSourceUnlocked(megapdf_document* d, const char* password_utf8) {
    d->doc = FPDF_LoadCustomDocument(&d->source.access, password_utf8);
    return FinishOpenUnlocked(d, password_utf8);
}

// The "…_like" opens (#132): read the credentials `like` was opened with, then run
// `load` with them. The copy is wiped before returning, so the password does not
// outlive the call on the stack.
template <typename Loader>
megapdf_document* OpenLike(const megapdf_document* like, Loader load) {
    std::string unlock;
    bool has_unlock = false;
    {
        Guard guard(CoreLock());
        if (like == nullptr) {
            SetError(FPDF_ERR_UNKNOWN, "no document to open like");
            return nullptr;
        }
        try {
            unlock = like->unlock;
        } catch (...) {
            SetError(FPDF_ERR_UNKNOWN, "out of memory");
            return nullptr;
        }
        has_unlock = like->has_unlock;
    }
    megapdf_document* d = load(has_unlock ? unlock.c_str() : nullptr);
    std::fill(unlock.begin(), unlock.end(), '\0');
    return d;
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
    return FinishOpenUnlocked(d, password_utf8);
}

MEGAPDF_API megapdf_document* megapdf_open_file(const char* path_utf8, const char* password_utf8) {
    Guard guard(CoreLock());
    EnsureLibrary();
    if (path_utf8 == nullptr || path_utf8[0] == '\0') {
        SetError(FPDF_ERR_FILE, "no file to open");
        return nullptr;
    }
    auto* d = new (std::nothrow) megapdf_document();
    if (d == nullptr) {
        SetError(FPDF_ERR_UNKNOWN, "out of memory");
        return nullptr;
    }
    if (!FileSourceOpenPath(&d->source, path_utf8)) {   // sets the error
        delete d;
        return nullptr;
    }
    return OpenSourceUnlocked(d, password_utf8);
}

MEGAPDF_API megapdf_document* megapdf_open_fd(int fd, const char* password_utf8) {
    Guard guard(CoreLock());
    EnsureLibrary();
    auto* d = new (std::nothrow) megapdf_document();
    if (d == nullptr) {
#if !defined(_WIN32)
        if (fd >= 0) ::close(fd);   // ownership passed to the core with the call
#endif
        SetError(FPDF_ERR_UNKNOWN, "out of memory");
        return nullptr;
    }
    if (!FileSourceAdoptFd(&d->source, fd)) {   // sets the error, and closes `fd` if it took it
        delete d;
        return nullptr;
    }
    return OpenSourceUnlocked(d, password_utf8);
}

MEGAPDF_API megapdf_document* megapdf_open_like(const megapdf_document* like, const void* bytes, size_t length) {
    return OpenLike(like, [&](const char* pw) { return megapdf_open(bytes, length, pw); });
}

MEGAPDF_API megapdf_document* megapdf_open_file_like(const megapdf_document* like, const char* path_utf8) {
    return OpenLike(like, [&](const char* pw) { return megapdf_open_file(path_utf8, pw); });
}

MEGAPDF_API megapdf_document* megapdf_open_fd_like(const megapdf_document* like, int fd) {
    if (like == nullptr) {
        // Checked here as well as in OpenLike, so the descriptor the caller handed
        // over is closed rather than leaked.
#if !defined(_WIN32)
        if (fd >= 0) ::close(fd);
#endif
        SetError(FPDF_ERR_UNKNOWN, "no document to open like");
        return nullptr;
    }
    return OpenLike(like, [&](const char* pw) { return megapdf_open_fd(fd, pw); });
}

MEGAPDF_API int megapdf_reads_file(const megapdf_document* d, const char* path_utf8) {
    if (d == nullptr) return 0;
    Guard guard(CoreLock());
    return !d->moving_source && FileSourceIsPath(d->source, path_utf8) ? 1 : 0;
}

MEGAPDF_API int megapdf_reads_fd(const megapdf_document* d, int fd) {
    if (d == nullptr || fd < 0) return 0;
    Guard guard(CoreLock());
    if (!d->source.open || d->moving_source) return 0;
#if defined(_WIN32)
    return 0;
#else
    struct stat a{}, b{};
    return ::fstat(fd, &a) == 0 && ::fstat(d->source.fd, &b) == 0 && a.st_dev == b.st_dev && a.st_ino == b.st_ino ? 1 : 0;
#endif
}

MEGAPDF_API int megapdf_read_from_copy(megapdf_document* d, const char* copy_path_utf8) {
    if (d == nullptr || copy_path_utf8 == nullptr || copy_path_utf8[0] == '\0') return MEGAPDF_ERR_ARGUMENT;
    std::unique_lock<std::recursive_mutex> lock(CoreLock());
    if (!d->source.open) return MEGAPDF_OK;   // opened from memory: nothing reads a file
    if (d->moving_source || d->closing) {
        SetError(FPDF_ERR_UNKNOWN, "the document is already being moved or closed");
        return MEGAPDF_ERR_ARGUMENT;
    }
    // The copy can take a while for a big file, so it runs without the lock: reading the source
    // takes none, and the source stays open because megapdf_close() waits for this as it waits
    // for a page check.
    d->moving_source = true;
    d->active_checks++;
    const FileSource original = d->source;
    lock.unlock();
    FileSource copy{};
    const bool ok = FileSourceCopy(original, copy_path_utf8, &copy);
    lock.lock();
    d->active_checks--;
    d->moving_source = false;
    ChecksDone().notify_all();
    if (!ok) return MEGAPDF_ERR_FILE;   // the error is set; the document still reads the original

    // Swapped under the lock: every PDFium call, and so every read, holds it. PDFium keeps a
    // pointer to d->source.access, so the handle changes and the structure stays where it is.
#if defined(_WIN32)
    const HANDLE old_handle = static_cast<HANDLE>(d->source.handle);
    d->source.handle = copy.handle;
    if (old_handle != nullptr) CloseHandle(old_handle);
#else
    const int old_fd = d->source.fd;
    d->source.fd = copy.fd;
    if (old_fd >= 0) ::close(old_fd);
#endif
    SetError(0, "");
    return MEGAPDF_OK;
}

MEGAPDF_API void megapdf_close(megapdf_document* d) {
    if (d == nullptr) return;
    std::unique_lock<std::recursive_mutex> guard(CoreLock());
    // A page check between its stages holds the document without the lock (#145): tell it
    // to stop at its next stage, and wait until it has.
    d->closing = true;
    ChecksDone().wait(guard, [d] { return d->active_checks == 0; });
    for (megapdf_page* p : d->open_pages) {
        p->owner = nullptr;   // the document is going; do not call back into its form handle
        if (d->form != nullptr) FORM_OnBeforeClosePage(p->page, d->form);
        FPDF_ClosePage(p->page);
        delete p;
    }
    d->open_pages.clear();
    for (megapdf_detached* x : d->detached) {
        for (const auto& part : x->parts) FPDFPageObj_Destroy(part.object);
        delete x;
    }
    d->detached.clear();
    if (d->form != nullptr) FPDFDOC_ExitFormFillEnvironment(d->form);
    if (d->doc != nullptr) FPDF_CloseDocument(d->doc);
    // After the document: PDFium reads through the file until it is closed (#147).
    FileSourceClose(&d->source);
    std::fill(d->unlock.begin(), d->unlock.end(), '\0');
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
    p->index = index;
    ReadCropOrigin(page, &p->crop_x, &p->crop_y);
    p->unit = static_cast<double>(FPDFPage_GetUserUnit(page));
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
    return static_cast<double>(FPDF_GetPageWidthF(p->page)) * p->unit;
}

MEGAPDF_API double megapdf_page_height(const megapdf_page* p) {
    if (p == nullptr) return 0.0;
    Guard guard(CoreLock());
    return static_cast<double>(FPDF_GetPageHeightF(p->page)) * p->unit;
}

MEGAPDF_API void megapdf_page_crop_origin(const megapdf_page* p, double* out_x, double* out_y) {
    if (out_x != nullptr) *out_x = p ? p->crop_x : 0.0;
    if (out_y != nullptr) *out_y = p ? p->crop_y : 0.0;
}

MEGAPDF_API double megapdf_page_user_unit(const megapdf_page* p) {
    return p ? p->unit : 1.0;
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
        const float w = (r - l) * static_cast<float>(p->unit), h = (t - b) * static_cast<float>(p->unit);
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

        if (found < capacity && out != nullptr) out[found] = OutRect(p, l, b, r, t);
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
                match.push_back(OutX(p, l));
                match.push_back(OutY(p, b));
                match.push_back(OutX(p, r));
                match.push_back(OutY(p, t));
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

// The text of every text object on `text_page`, exactly as FPDFTextObj_GetText reports it,
// from one pass over the page's characters (#149). FPDFTextObj_GetText walks the whole
// character list on every call, so reading each object in turn was quadratic: 20,000 text
// objects took 11 s to list and 24 s to judge an edit. Objects with no characters are absent.
//
// This replays PDFium's CPDF_TextPage::GetTextByPredicate for every object at once. For one
// object, the characters of other objects only matter through the state they leave behind,
// and a stretch of them between two of the object's characters always leaves the same one:
// a separator space when the stretch starts with a space, and a pending line break when it
// holds anything that is not a space. A prefix count of non-space characters answers the
// second in O(1).
using ObjectTexts = std::unordered_map<FPDF_PAGEOBJECT, U16>;

ObjectTexts ReadObjectTexts(FPDF_TEXTPAGE text_page) {
    ObjectTexts texts;
    const int count = text_page != nullptr ? FPDFText_CountChars(text_page) : 0;
    if (count <= 0) return texts;
    std::vector<unsigned int> unicode(static_cast<size_t>(count));
    std::vector<int> non_space_before(static_cast<size_t>(count) + 1, 0);
    for (int i = 0; i < count; i++) {
        unicode[static_cast<size_t>(i)] = FPDFText_GetUnicode(text_page, i);
        non_space_before[static_cast<size_t>(i) + 1] =
            non_space_before[static_cast<size_t>(i)] + (unicode[static_cast<size_t>(i)] != L' ' ? 1 : 0);
    }
    struct State {
        int last = -1;      // the object's previous character
        float posy = 0;
    };
    std::unordered_map<FPDF_PAGEOBJECT, State> states;
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT obj = FPDFText_GetTextObject(text_page, i);
        if (obj == nullptr) continue;
        State& st = states[obj];
        U16& text = texts[obj];
        bool contains_pre_char;   // GetTextByPredicate's IsContainPreChar, before this character
        bool add_line_feed;       // and its IsAddLineFeed
        if (st.last < 0) {
            contains_pre_char = false;
            add_line_feed = non_space_before[static_cast<size_t>(i)] > 0;
        } else if (st.last + 1 == i) {
            contains_pre_char = true;
            add_line_feed = false;
        } else {
            if (unicode[static_cast<size_t>(st.last) + 1] == L' ') text.push_back(L' ');
            contains_pre_char = false;
            add_line_feed = non_space_before[static_cast<size_t>(i)] - non_space_before[static_cast<size_t>(st.last) + 1] > 0;
        }
        double x = 0, y = 0;
        FPDFText_GetCharOrigin(text_page, i, &x, &y);
        const float origin_y = static_cast<float>(y);
        if (std::fabs(st.posy - origin_y) > 0 && !contains_pre_char && add_line_feed) {
            st.posy = origin_y;
            if (!text.empty()) {
                text.push_back(L'\r');
                text.push_back(L'\n');
            }
        }
        const unsigned int u = unicode[static_cast<size_t>(i)];
        if (u > 0xFFFF && u <= 0x10FFFF) {
            text.push_back(static_cast<unsigned short>(0xD800 + ((u - 0x10000) >> 10)));
            text.push_back(static_cast<unsigned short>(0xDC00 + ((u - 0x10000) & 0x3FF)));
        } else if (u != 0) {
            text.push_back(static_cast<unsigned short>(u));
        }
        st.last = i;
    }
    // The stretch after an object's last character leaves only its separator space.
    for (const auto& entry : states) {
        const int last = entry.second.last;
        if (last + 1 < count && unicode[static_cast<size_t>(last) + 1] == L' ') texts[entry.first].push_back(L' ');
    }
    return texts;
}

const U16& TextOf(const ObjectTexts& texts, FPDF_PAGEOBJECT obj) {
    static const U16 kEmpty;
    const auto it = texts.find(obj);
    return it != texts.end() ? it->second : kEmpty;
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
        ObjectTexts texts;
        try {
            texts = ReadObjectTexts(text_page);
        } catch (...) {
            if (text_page != nullptr) FPDFText_ClosePage(text_page);
            throw;
        }
        if (text_page != nullptr) FPDFText_ClosePage(text_page);
        const int count = FPDFPage_CountObjects(p->page);
        for (int i = 0; i < count; i++) {
            FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, i);
            if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_TEXT) continue;
            const bool is_box = HasMark(obj, kTextBoxMark);
            if (boxes_only && !is_box) continue;
            float l = 0, b = 0, r = 0, tp = 0;
            if (!FPDFPageObj_GetBounds(obj, &l, &b, &r, &tp)) continue;
            const U16& text = TextOf(texts, obj);
            if (text.empty() || AllWhiteSpace(text)) continue;

            TextRun run;
            run.info.object_index = i;
            run.info.bounds = OutRect(p, l, b, r, tp);
            float size = 0;
            FPDFTextObj_GetFontSize(obj, &size);
            run.info.font_size = static_cast<double>(size) * p->unit;
            run.info.is_text_box = is_box ? 1 : 0;
            run.text = text;
            run.font = ReadFontFamily(obj);
            if (run.info.is_text_box) {
                run.box_id = ReadMarkParam(obj, "id");
                run.box_font = ReadMarkParam(obj, "font");
            }
            t->runs.push_back(std::move(run));
        }
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
                    field.info.bounds = OutRect(p, r.left, r.bottom, r.right, r.top);
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
    FORM_OnLButtonDown(form, p->page, 0, InX(p, x), InY(p, y));
    FORM_OnLButtonUp(form, p->page, 0, InX(p, x), InY(p, y));
    FORM_ForceToKillFocus(form);
    return MEGAPDF_OK;
}

MEGAPDF_API int megapdf_form_set_text(const megapdf_page* p, double x, double y, const unsigned short* value_utf16) {
    if (p == nullptr || p->owner == nullptr || p->owner->form == nullptr || value_utf16 == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    FPDF_FORMHANDLE form = p->owner->form;
    FORM_OnLButtonDown(form, p->page, 0, InX(p, x), InY(p, y));
    FORM_OnLButtonUp(form, p->page, 0, InX(p, x), InY(p, y));
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
    const float left = static_cast<float>(InX(p, bounds->left));
    const float right = static_cast<float>(InX(p, bounds->right));
    const float bottom = static_cast<float>(InY(p, bounds->bottom));
    const float top = static_cast<float>(InY(p, bounds->top));

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
    const float left = static_cast<float>(InX(p, square->left + inset));
    const float right = static_cast<float>(InX(p, square->right - inset));
    const float bottom = static_cast<float>(InY(p, square->bottom + inset));
    const float top = static_cast<float>(InY(p, square->top - inset));

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
        FPDFPageObj_SetStrokeWidth(path, static_cast<float>(stroke_width / p->unit));
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
                st.info.bounds = OutRect(p, r.left, r.bottom, r.right, r.top);
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
// #118: can PDFium rewrite this text without changing the page?
// --------------------------------------------------------------------------

namespace {

struct ScratchShot {
    int w = 0;
    int h = 0;
    std::vector<unsigned char> px;
};

// Asks PDFium to pause a progressive render once a slice has run this long (#145).
struct ScratchPause {
    std::chrono::steady_clock::time_point slice_started;
};

FPDF_BOOL ScratchNeedToPause(IFSDK_PAUSE* pause) {
    const auto* state = static_cast<const ScratchPause*>(pause->user);
    return std::chrono::steady_clock::now() - state->slice_started >= std::chrono::milliseconds(20) ? 1 : 0;
}

// With `go_on`, the render runs in slices and `go_on` is asked between them (#145): it may let
// go of the core lock, and when it says stop the render ends there and `*stopped` is set. A
// single page with a huge CMYK photo takes over ten seconds to render in PDFium, so a stop
// asked only between whole renders could still wait that long. Between slices no PDFium call
// is in progress, so other threads may use the library. The pixels are the same either way.
ScratchShot RenderScratchPage(FPDF_PAGE page, const std::function<bool()>* go_on = nullptr, bool* stopped = nullptr) {
    // 72 dpi in points, not user space units (#150).
    const double unit = FPDFPage_GetUserUnit(page);
    const double pw = FPDF_GetPageWidthF(page) * unit, ph = FPDF_GetPageHeightF(page) * unit;
    double scale = 1.0;
    while (pw * ph * scale * scale > 1.0e6 && scale > 1e-3) scale /= 2;
    ScratchShot shot;
    shot.w = static_cast<int>(pw * scale) > 0 ? static_cast<int>(pw * scale) : 1;
    shot.h = static_cast<int>(ph * scale) > 0 ? static_cast<int>(ph * scale) : 1;
    shot.px.assign(static_cast<size_t>(shot.w) * shot.h * 4, 0);
    FPDF_BITMAP bmp = FPDFBitmap_CreateEx(shot.w, shot.h, FPDFBitmap_BGRA, shot.px.data(), shot.w * 4);
    if (bmp == nullptr) return ScratchShot{};
    FPDFBitmap_FillRect(bmp, 0, 0, shot.w, shot.h, 0xFFFFFFFF);
    if (go_on == nullptr) {
        FPDF_RenderPageBitmap(bmp, page, 0, 0, shot.w, shot.h, 0, FPDF_ANNOT);
    } else {
        ScratchPause state{std::chrono::steady_clock::now()};
        IFSDK_PAUSE pause{};
        pause.version = 1;
        pause.NeedToPauseNow = ScratchNeedToPause;
        pause.user = &state;
        int status = FPDF_RenderPageBitmap_Start(bmp, page, 0, 0, shot.w, shot.h, 0, FPDF_ANNOT, &pause);
        while (status == FPDF_RENDER_TOBECONTINUED) {
            if (!(*go_on)()) {
                if (stopped != nullptr) *stopped = true;
                break;
            }
            state.slice_started = std::chrono::steady_clock::now();
            status = FPDF_RenderPage_Continue(page, &pause);
        }
        FPDF_RenderPage_Close(page);
    }
    FPDFBitmap_Destroy(bmp);
    return shot;
}

// Takes every image off `obj`'s form and the forms inside it; with `page`, off the page.
void RemoveImages(FPDF_PAGE page, FPDF_PAGEOBJECT form) {
    const int count = page != nullptr ? FPDFPage_CountObjects(page) : FPDFFormObj_CountObjects(form);
    for (int i = count - 1; i >= 0; i--) {
        FPDF_PAGEOBJECT obj = page != nullptr ? FPDFPage_GetObject(page, i) : FPDFFormObj_GetObject(form, static_cast<unsigned long>(i));
        if (obj == nullptr) continue;
        const int type = FPDFPageObj_GetType(obj);
        if (type == FPDF_PAGEOBJ_FORM) {
            RemoveImages(nullptr, obj);
        } else if (type == FPDF_PAGEOBJ_IMAGE &&
                   (page != nullptr ? FPDFPage_RemoveObject(page, obj) : FPDFFormObj_RemoveObject(form, obj))) {
            FPDFPageObj_Destroy(obj);
        }
    }
}

// Resolves the fonts of the document's page `page_index` the way rendering it would (#128),
// without decoding its images (#151). PDFium resolves a non-embedded font against a
// process-wide face cache, so rendering the page's text once, in any document, is what makes
// every later document get the same face. The images play no part in that and are the slow
// part of a render: a 20,000 x 15,000 px Flate image is inflated whole, at any scale, over a
// second each time. So the page is copied into a throwaway document, its images taken off,
// and that copy rendered; nothing of it is ever saved. `go_on` and `stopped` as
// RenderScratchPage's.
void WarmFontsUnlocked(FPDF_DOCUMENT doc, int page_index, const std::function<bool()>* go_on, bool* stopped) {
    FPDF_DOCUMENT warm = FPDF_CreateNewDocument();
    if (warm == nullptr) return;
    const int indices[1] = {page_index};
    FPDF_PAGE page = FPDF_ImportPagesByIndex(warm, doc, indices, 1, 0) ? FPDF_LoadPage(warm, 0) : nullptr;
    if (page != nullptr) {
        RemoveImages(page, nullptr);
        RenderScratchPage(page, go_on, stopped);
        FPDF_ClosePage(page);
    }
    FPDF_CloseDocument(warm);
}

// Every image object on `page` and inside its forms, depth first.
void CollectImages(FPDF_PAGE page, FPDF_PAGEOBJECT form, std::vector<FPDF_PAGEOBJECT>* out) {
    const int count = page != nullptr ? FPDFPage_CountObjects(page) : FPDFFormObj_CountObjects(form);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT obj = page != nullptr ? FPDFPage_GetObject(page, i) : FPDFFormObj_GetObject(form, static_cast<unsigned long>(i));
        if (obj == nullptr) continue;
        const int type = FPDFPageObj_GetType(obj);
        if (type == FPDF_PAGEOBJ_FORM) CollectImages(nullptr, obj, out);
        else if (type == FPDF_PAGEOBJ_IMAGE) out->push_back(obj);
    }
}

// Images of at least this many pixels are drawn as stand-ins in the guard's compare renders.
constexpr double kStandInPixels = 16.0e6;

// Swaps each large image on a reopened scratch page for a small stand-in, so the guard's two
// compare renders do not each decode it (#151). PDFium inflates an image whole at any render
// size and keeps no decoded image over 100 MB: a 20,000 x 15,000 px Flate image costs over a
// second per render, however small the render. The guard compares two reopened copies of the
// page, before and after the rewrite, and the rewrite regenerates content streams, never an
// image's own stream, so what it can get wrong about an image is where and how the content
// draws it: the matrix, the clip, the graphics state around it. A stand-in drawn by the same
// content shows all of that. It is a function of the image's size and raw bytes, so the same
// image gets the same stand-in in both copies and a swapped image a different one. Most of it
// is transparent, so whatever the image covers still shows; its opaque frame, diagonal and
// corner block show where it lands and which way up. Stencil masks and images under 8 bits
// per pixel keep their pixels: their colour comes from the content stream.
void StandInLargeImages(FPDF_PAGE page) {
    std::vector<FPDF_PAGEOBJECT> images;
    CollectImages(page, nullptr, &images);
    struct Swap {
        FPDF_PAGEOBJECT obj;
        uint32_t seed;
    };
    std::vector<Swap> swaps;
    for (FPDF_PAGEOBJECT obj : images) {
        unsigned int w = 0, h = 0;
        if (!FPDFImageObj_GetImagePixelSize(obj, &w, &h) || static_cast<double>(w) * h < kStandInPixels) continue;
        FPDF_IMAGEOBJ_METADATA meta{};
        if (!FPDFImageObj_GetImageMetadata(obj, page, &meta) || meta.bits_per_pixel < 8) continue;
        const unsigned long length = FPDFImageObj_GetImageDataRaw(obj, nullptr, 0);
        std::vector<unsigned char> raw(length);
        if (length > 0 && FPDFImageObj_GetImageDataRaw(obj, raw.data(), length) != length) continue;
        uint32_t seed = 2166136261u;   // FNV-1a
        auto mix = [&seed](unsigned char b) { seed = (seed ^ b) * 16777619u; };
        for (int k = 0; k < 4; k++) { mix(static_cast<unsigned char>(w >> (8 * k))); mix(static_cast<unsigned char>(h >> (8 * k))); }
        for (unsigned char b : raw) mix(b);
        swaps.push_back(Swap{obj, seed});
    }
    // Seeds first: objects sharing one image share its stand-in, which replaces it for all.
    for (const Swap& swap : swaps) {
        const int n = 64;
        FPDF_BITMAP bmp = FPDFBitmap_Create(n, n, 1);
        if (bmp == nullptr) continue;
        const unsigned char r = static_cast<unsigned char>(swap.seed), g = static_cast<unsigned char>(swap.seed >> 8),
                            b = static_cast<unsigned char>(swap.seed >> 16);
        FPDFBitmap_FillRect(bmp, 0, 0, n, n, 0x00000000);
        const unsigned long colour = 0xFF000000ul | (static_cast<unsigned long>(r) << 16) | (static_cast<unsigned long>(g) << 8) | b;
        FPDFBitmap_FillRect(bmp, 0, 0, n, 3, colour);
        FPDFBitmap_FillRect(bmp, 0, n - 3, n, 3, colour);
        FPDFBitmap_FillRect(bmp, 0, 0, 3, n, colour);
        FPDFBitmap_FillRect(bmp, n - 3, 0, 3, n, colour);
        for (int i = 0; i < n; i++) FPDFBitmap_FillRect(bmp, i, i, 2, 1, colour);
        FPDFBitmap_FillRect(bmp, 3, 3, 12, 12, 0xFF000000ul | (~colour & 0xFFFFFFul));   // the image's first row and column
        FPDF_PAGE pages[1] = {page};
        FPDFImageObj_SetBitmap(pages, 1, swap.obj, bmp);
        FPDFBitmap_Destroy(bmp);
    }
}

struct ScratchRun {
    float left, bottom, right, top;
    U16 text;
};

std::vector<ScratchRun> ScratchRuns(FPDF_PAGE page) {
    std::vector<ScratchRun> out;
    FPDF_TEXTPAGE text_page = FPDFText_LoadPage(page);
    ObjectTexts texts;
    try {
        texts = ReadObjectTexts(text_page);
    } catch (...) {
        if (text_page != nullptr) FPDFText_ClosePage(text_page);
        throw;
    }
    if (text_page != nullptr) FPDFText_ClosePage(text_page);
    const int count = FPDFPage_CountObjects(page);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, i);
        if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_TEXT) continue;
        ScratchRun run{};
        FPDFPageObj_GetBounds(obj, &run.left, &run.bottom, &run.right, &run.top);
        run.text = TextOf(texts, obj);
        out.push_back(std::move(run));
    }
    return out;
}

struct ScratchWriter {
    FPDF_FILEWRITE fw;   // first, so PDFium's pointer downcasts
    std::vector<unsigned char> out;
};

int ScratchWriteBlock(FPDF_FILEWRITE* self, const void* data, unsigned long size) {
    auto* w = reinterpret_cast<ScratchWriter*>(self);
    w->out.insert(w->out.end(), static_cast<const unsigned char*>(data), static_cast<const unsigned char*>(data) + size);
    return 1;
}

struct PageBox {
    float l, b, r, t;   // page space
};

// Bounds of the objects at `indices` on `page`, as they are now.
std::vector<PageBox> BoundsOf(FPDF_PAGE page, const std::vector<int>& indices) {
    std::vector<PageBox> out;
    for (int i : indices) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, i);
        PageBox box{};
        if (obj != nullptr && FPDFPageObj_GetBounds(obj, &box.l, &box.b, &box.r, &box.t)) out.push_back(box);
    }
    return out;
}

constexpr int kShotThreshold = 60;      // |dR|+|dG|+|dB| above this is a changed pixel
constexpr float kObjectPadPt = 2.0f;    // MEGAPDF_LAYOUT_WHERE_OBJECT reaches this far past the object
constexpr float kRunShiftPt = 0.5f;     // a text object moving further has moved

// Marks `box` (page space), padded by `pad` points, with `value` in a mask of the page rendered as `shot`.
// (std::min) and (std::max) in parentheses: <windef.h>, which pdfium pulls in on Windows, defines min and max macros.
void PaintBox(FPDF_PAGE page, const ScratchShot& shot, const PageBox& box, float pad, unsigned char value,
              std::vector<unsigned char>* mask) {
    int x0 = shot.w, y0 = shot.h, x1 = -1, y1 = -1;
    pad /= FPDFPage_GetUserUnit(page);   // points to user space units (#150)
    const double xs[2] = {box.l - pad, box.r + pad}, ys[2] = {box.b - pad, box.t + pad};
    for (double x : xs) {
        for (double y : ys) {
            int dx = 0, dy = 0;
            if (!FPDF_PageToDevice(page, 0, 0, shot.w, shot.h, 0, x, y, &dx, &dy)) return;
            x0 = (std::min)(x0, dx); x1 = (std::max)(x1, dx);
            y0 = (std::min)(y0, dy); y1 = (std::max)(y1, dy);
        }
    }
    x0 = (std::max)(x0, 0); y0 = (std::max)(y0, 0);
    x1 = (std::min)(x1, shot.w - 1); y1 = (std::min)(y1, shot.h - 1);
    for (int y = y0; y <= y1; y++) {
        const size_t row = static_cast<size_t>(y) * static_cast<size_t>(shot.w);
        for (int x = x0; x <= x1; x++) {
            unsigned char& m = (*mask)[row + static_cast<size_t>(x)];
            if (value > m) m = value;
        }
    }
}

// The render check (#118) with its numbers (#128): how many pixels differ between the page as
// it was and the rewrite, and where. `edited` are the judged objects' bounds; `runs` the text
// objects of the page as it was. False when a render failed.
bool CompareShots(FPDF_PAGE was_page, const ScratchShot& a, const ScratchShot& b, const std::vector<PageBox>& edited,
                  const std::vector<ScratchRun>& runs, megapdf_layout_verdict* v) {
    if (a.w == 0 || b.w == 0) return false;
    v->total_pixels = a.w * a.h;
    if (a.w != b.w || a.h != b.h) {   // a different page size: all of it changed
        v->changed_pixels = (std::max)(v->total_pixels, b.w * b.h);
        v->where = MEGAPDF_LAYOUT_WHERE_OTHER;
        return true;
    }
    auto differs = [&](size_t i) {
        return std::abs(a.px[i] - b.px[i]) + std::abs(a.px[i + 1] - b.px[i + 1]) + std::abs(a.px[i + 2] - b.px[i + 2]) > kShotThreshold;
    };
    int changed = 0;
    for (size_t i = 0; i < a.px.size(); i += 4) if (differs(i)) changed++;
    v->changed_pixels = changed;
    if (changed == 0) return true;
    // Only a page that changed pays for the map: 0 elsewhere, 1 on a text object, 2 on the judged ones.
    std::vector<unsigned char> mask(static_cast<size_t>(a.w) * static_cast<size_t>(a.h), 0);
    for (const ScratchRun& run : runs) PaintBox(was_page, a, PageBox{run.left, run.bottom, run.right, run.top}, 0.0f, 1, &mask);
    for (const PageBox& box : edited) PaintBox(was_page, a, box, kObjectPadPt, 2, &mask);
    for (size_t p = 0; p < mask.size(); p++) {
        if (!differs(p * 4)) continue;
        v->where |= mask[p] == 2 ? MEGAPDF_LAYOUT_WHERE_OBJECT : mask[p] == 1 ? MEGAPDF_LAYOUT_WHERE_TEXT : MEGAPDF_LAYOUT_WHERE_OTHER;
    }
    return true;
}

// The run check (#118) with its numbers (#128): every text object's text and bounds, in content order.
// `unit` is the page's /UserUnit: the bounds are in user space, the budget in points (#150).
void CompareRuns(const std::vector<ScratchRun>& a, const std::vector<ScratchRun>& b, double unit, bool* text_changed,
                 bool* moved, megapdf_layout_verdict* v) {
    *text_changed = a.size() != b.size();
    *moved = false;
    if (*text_changed) return;
    double worst = 0;
    for (size_t i = 0; i < a.size(); i++) {
        if (a[i].text != b[i].text) *text_changed = true;
        const double shift = (std::max)({std::fabs(a[i].left - b[i].left), std::fabs(a[i].bottom - b[i].bottom),
                                       std::fabs(a[i].right - b[i].right), std::fabs(a[i].top - b[i].top)});
        worst = (std::max)(worst, shift * unit);
    }
    *moved = worst > kRunShiftPt;
    v->max_shift_pt = worst;
}

// Judges a reopened rewrite against the reopened page as it was.
// `go_on`, when given, is asked between the two renders, the heavy half of the compare
// (#145); when it says stop, the verdict returned means nothing.
megapdf_layout_verdict CompareRewrite(FPDF_PAGE was_page, FPDF_PAGE reopened, const std::vector<PageBox>& edited,
                                      const std::function<bool()>* go_on = nullptr) {
    megapdf_layout_verdict v{};
    v.cause = MEGAPDF_LAYOUT_REWRITE_FAILED;
    // Rendered before the text layer is read, as the guard always has.
    StandInLargeImages(was_page);
    StandInLargeImages(reopened);
    bool stopped = false;
    const ScratchShot shot_was = RenderScratchPage(was_page, go_on, &stopped);
    if (stopped || (go_on != nullptr && !(*go_on)())) return v;
    const ScratchShot shot_now = RenderScratchPage(reopened, go_on, &stopped);
    if (stopped) return v;
    const std::vector<ScratchRun> runs_was = ScratchRuns(was_page);
    const std::vector<ScratchRun> runs_now = ScratchRuns(reopened);
    if (!CompareShots(was_page, shot_was, shot_now, edited, runs_was, &v)) {
        return megapdf_layout_verdict{0, MEGAPDF_LAYOUT_REWRITE_FAILED, 0, 0, 0, 0.0};
    }
    bool text_changed = false, moved = false;
    CompareRuns(runs_was, runs_now, FPDFPage_GetUserUnit(was_page), &text_changed, &moved, &v);
    const bool render_kept = static_cast<size_t>(v.changed_pixels) * 2000 <= static_cast<size_t>(v.total_pixels);   // at most 0.05%
    v.editable = render_kept && !text_changed && !moved ? 1 : 0;
    v.cause = text_changed ? MEGAPDF_LAYOUT_TEXT_CHANGED
            : moved        ? MEGAPDF_LAYOUT_TEXT_MOVED
            : !render_kept ? MEGAPDF_LAYOUT_RENDER
                           : MEGAPDF_LAYOUT_OK;
    return v;
}

// --------------------------------------------------------------------------
// #136: the hidden copy of a line drawn twice
// --------------------------------------------------------------------------
//
// Producers draw text twice for fake bold, an outline or a shadow. PDFium's text layer hides
// the second copy in two ways, and the copy then extracts as empty text, so it is never a
// run, and a delete or an edit that touches only the run leaves the copy drawn: the line
// stays, or the old text shows under the new.
//   1. Object level (CPDF_TextPage::IsSameAsPreTextObject): a text object that repeats one
//      of the five text objects before it (same character codes and font size, overlapping,
//      offset by less than a character) is skipped.
//   2. Character level (CPDF_TextPage::ProcessTextObjectItems), #136 reopened: PDFium sorts a
//      line's text objects left to right before reading them, so a copy drawn anywhere on
//      the line, before its run or many objects after it, lands next to it; each character
//      with the same code in the same font object as one of the seven before it, whose
//      origin is within 0.07 of the font size (scaled by the matrix) in x and y, is dropped.
//      Seen in the corpus: whole lines repeated 18 and 22 text objects later, and copies
//      drawn before their run, all offset 0-0.022 em.
//
// The API cannot read character codes, so a copy is recognised by what the same codes in
// the same font imply. A text object is a hidden copy of a run when it is not a text box,
// its extracted text is empty or whitespace (never a run's text), it has the same font size
// and the same matrix scale, skew and rotation, bounds of the same size within 10% of the
// em (one character more or less changes the width by far more; a stroked copy grows by its
// line width, a few tenths of a point), and either:
//   1. it comes after the run in content order, with fewer than five other text objects in
//      between (copies already taken do not count, as matches do not count in PDFium), has
//      the same font (object, or base name), and is placed within a quarter of the em. Fake
//      bold is offset 0.2-0.5 pt and a shadow about 1 pt at body sizes of 8-14 pt, and PDFium
//      itself allows most of a character's width. The em is the font size scaled by the
//      object's matrix, so the tolerance follows the text's drawn size rather than its
//      bounds, which are short for text like "...".
//   2. or, anywhere else on the page, it has the same font object and is placed within
//      PDFium's own character threshold, 0.07 of the font size scaled by the matrix's x
//      axis. Only what PDFium itself would have hidden character by character is taken: an
//      empty object of the same font and size, the same width, drawn where the run is.
// A copy drawn in different pieces from its run (split, or spanning two runs) is not taken.

constexpr int kCopyReach = 5;
constexpr float kCopyOffset = 0.25f;
constexpr float kCopySize = 0.10f;
constexpr float kCharCopyOffset = 0.07f;   // PDFium's kTextCharRatioGapDelta

std::string ReadFontNameUtf8(FPDF_FONT font, bool base_name);

struct TextShape {
    float l = 0, b = 0, r = 0, t = 0;
    float size = 0;
    float em = 0;
    FS_MATRIX m{};
    FPDF_FONT font = nullptr;
};

bool ReadShape(FPDF_PAGEOBJECT obj, TextShape* s) {
    if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_TEXT) return false;
    if (!FPDFPageObj_GetBounds(obj, &s->l, &s->b, &s->r, &s->t) || !FPDFTextObj_GetFontSize(obj, &s->size) ||
        !FPDFPageObj_GetMatrix(obj, &s->m)) {
        return false;
    }
    s->font = FPDFTextObj_GetFont(obj);
    s->em = std::fabs(s->size) * std::sqrt(std::fabs(s->m.a * s->m.d - s->m.b * s->m.c));
    return s->em > 0 && s->r > s->l;
}

bool SameValue(float a, float b) {
    const float scale = std::fabs(a) > 1.0f ? std::fabs(a) : 1.0f;
    return std::fabs(a - b) <= 0.001f * scale;
}

// `offset` is in points.
bool LooksLikeCopy(const TextShape& run, const TextShape& other, float offset) {
    const float size = kCopySize * run.em;
    return SameValue(other.size, run.size) && SameValue(other.m.a, run.m.a) && SameValue(other.m.b, run.m.b) &&
           SameValue(other.m.c, run.m.c) && SameValue(other.m.d, run.m.d) &&
           std::fabs((other.r - other.l) - (run.r - run.l)) <= size && std::fabs((other.t - other.b) - (run.t - run.b)) <= size &&
           std::fabs(other.l - run.l) <= offset && std::fabs(other.b - run.b) <= offset;
}

// The hidden copies of the text objects at `runs` on `page`, as (copy index, run index) pairs
// ascending by copy index. `text_page` is the page's text layer as the page is now.
std::vector<std::pair<int, int>> HiddenCopies(FPDF_PAGE page, FPDF_TEXTPAGE text_page, std::vector<int> runs) {
    std::vector<std::pair<int, int>> copies;
    const int count = FPDFPage_CountObjects(page);
    if (text_page == nullptr || count <= 0) return copies;
    std::vector<char> taken(static_cast<size_t>(count), 0);
    for (int i : runs) if (i >= 0 && i < count) taken[static_cast<size_t>(i)] = 1;
    std::sort(runs.begin(), runs.end());
    // Each object's shape is read once: the character-level pass looks at the whole page.
    std::vector<TextShape> shapes(static_cast<size_t>(count));
    std::vector<signed char> state(static_cast<size_t>(count), 0);   // 0 unread, 1 a body text shape, -1 neither
    auto shape_of = [&](int j) -> const TextShape* {
        signed char& s = state[static_cast<size_t>(j)];
        if (s == 0) {
            FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, j);
            s = ReadShape(obj, &shapes[static_cast<size_t>(j)]) && !HasMark(obj, kTextBoxMark) ? 1 : -1;
        }
        return s == 1 ? &shapes[static_cast<size_t>(j)] : nullptr;
    };
    // Read on first use, all at once: most runs have no copy candidate at all.
    ObjectTexts texts;
    bool texts_read = false;
    auto extracts_empty = [&](int j) {
        if (!texts_read) {
            texts = ReadObjectTexts(text_page);
            texts_read = true;
        }
        const U16& text = TextOf(texts, FPDFPage_GetObject(page, j));
        return text.empty() || AllWhiteSpace(text);
    };
    auto take = [&](int j, int i) {
        taken[static_cast<size_t>(j)] = 1;
        copies.emplace_back(j, i);
    };

    // 1. PDFium's object-level check: shortly after the run.
    for (int i : runs) {
        if (i < 0 || i >= count) continue;
        const TextShape* shape = shape_of(i);
        if (shape == nullptr) continue;
        std::string run_base;
        bool run_base_read = false;
        int passed = 0;
        for (int j = i + 1; j < count && passed < kCopyReach; j++) {
            FPDF_PAGEOBJECT other = FPDFPage_GetObject(page, j);
            if (other == nullptr || FPDFPageObj_GetType(other) != FPDF_PAGEOBJ_TEXT) continue;
            const TextShape* o = taken[static_cast<size_t>(j)] == 0 ? shape_of(j) : nullptr;
            bool copy = o != nullptr && LooksLikeCopy(*shape, *o, kCopyOffset * shape->em);
            if (copy && o->font != shape->font) {
                if (!run_base_read) {
                    run_base = shape->font != nullptr ? ReadFontNameUtf8(shape->font, true) : "";
                    run_base_read = true;
                }
                copy = !run_base.empty() && o->font != nullptr && ReadFontNameUtf8(o->font, true) == run_base;
            }
            if (copy) copy = extracts_empty(j);
            if (copy) take(j, i);
            else passed++;
        }
    }

    // 2. PDFium's character-level check (#136 reopened): anywhere on the page, in the run's
    // own font object, within PDFium's character threshold.
    for (int i : runs) {
        if (i < 0 || i >= count) continue;
        const TextShape* shape = shape_of(i);
        if (shape == nullptr || shape->font == nullptr) continue;
        const float offset = kCharCopyOffset * std::fabs(shape->size) * std::hypot(shape->m.a, shape->m.b);
        for (int j = 0; j < count; j++) {
            if (taken[static_cast<size_t>(j)] != 0) continue;
            const TextShape* o = shape_of(j);
            if (o != nullptr && o->font == shape->font && LooksLikeCopy(*shape, *o, offset) && extracts_empty(j)) take(j, i);
        }
    }
    std::sort(copies.begin(), copies.end());
    return copies;
}

// `runs` and their hidden copies, ascending and without repeats.
std::vector<int> WithHiddenCopies(FPDF_PAGE page, const std::vector<int>& runs) {
    std::vector<int> all = runs;
    FPDF_TEXTPAGE text_page = FPDFText_LoadPage(page);
    for (const auto& copy : HiddenCopies(page, text_page, runs)) all.push_back(copy.first);
    if (text_page != nullptr) FPDFText_ClosePage(text_page);
    std::sort(all.begin(), all.end());
    all.erase(std::unique(all.begin(), all.end()), all.end());
    return all;
}

// Takes the objects at `indices` (ascending) off a scratch page, highest first, and puts
// them straight back: the rewrite of every stream holding them that any change forces.
bool RewriteObjectsUnlocked(FPDF_PAGE page, const std::vector<int>& indices) {
    std::vector<FPDF_PAGEOBJECT> objects;
    for (int i : indices) {
        FPDF_PAGEOBJECT obj = i >= 0 ? FPDFPage_GetObject(page, i) : nullptr;
        if (obj == nullptr) return false;
        objects.push_back(obj);
    }
    size_t removed = 0;
    while (removed < objects.size() && FPDFPage_RemoveObject(page, objects[objects.size() - 1 - removed])) removed++;
    bool ok = removed == objects.size();
    for (size_t k = objects.size() - removed; k < objects.size(); k++) {
        // PDFium frees an object it fails to insert; the copy is thrown away either way.
        if (ok) ok = FPDFPage_InsertObjectAtIndex(page, objects[k], static_cast<size_t>(indices[k]));
        else FPDFPageObj_Destroy(objects[k]);
    }
    return ok && FPDFPage_GenerateContent(page);
}

// A change to the page moves object indices, so every verdict for it is stale (#137).
void ForgetVerdicts(megapdf_document* d, int page_index) {
    d->page_changes[page_index]++;
    auto& verdicts = d->rewrite_keeps_page;
    auto it = verdicts.lower_bound(std::make_pair(page_index, -2147483647 - 1));
    while (it != verdicts.end() && it->first.first == page_index) it = verdicts.erase(it);
}

// Marks one object on a scratch page dirty without changing it and regenerates the page: what
// any change forces, with nothing changed (#139). PDFium regenerates every stream of the page
// (patch 5), so which object does not matter. False when the page has no object to mark or
// PDFium refuses.
bool RewritePageUnlocked(FPDF_PAGE page) {
    const int count = FPDFPage_CountObjects(page);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, i);
        FS_MATRIX m{};
        if (obj != nullptr && FPDFPageObj_GetMatrix(obj, &m) && FPDFPageObj_SetMatrix(obj, &m)) return FPDFPage_GenerateContent(page);
    }
    // No object takes its own matrix back: take the last one off and put it straight back.
    return count > 0 && RewriteObjectsUnlocked(page, std::vector<int>{count - 1});
}

// The #118 dry run on a copy of the page. With `runs`, rewrites the streams holding those
// objects and their hidden copies (#136) — take them off and put them straight back, which is
// what any edit forces; with NULL, regenerates the page with nothing changed (#139). Then saves,
// reopens and compares with a reopened copy of the page before the rewrite.
//
// `between`, when given, is called between the run's stages: after the copy is made and
// rendered, after each save, and after the rewrite. It may let go of the core lock and take it
// back (#145), which is safe because nothing but this run can reach the scratch documents; it
// returns false to stop, and then `*aborted` is set and the verdict means nothing. Only a call
// that holds the core lock once, at the top of an ABI entry point, may let go of it.
megapdf_layout_verdict DryRunUnlocked(megapdf_document* d, int page_index, const std::vector<int>* runs,
                                      const std::function<bool()>* between = nullptr, bool* aborted = nullptr) {
    megapdf_layout_verdict verdict{0, MEGAPDF_LAYOUT_REWRITE_FAILED, 0, 0, 0, 0.0};
    const std::function<bool()> go_on = [&]() {
        if (between == nullptr || (*between)()) return true;
        if (aborted != nullptr) *aborted = true;
        return false;
    };
    FPDF_DOCUMENT scratch = FPDF_CreateNewDocument();
    if (scratch != nullptr) {
        const int indices[1] = {page_index};
        FPDF_PAGE page = FPDF_ImportPagesByIndex(scratch, d->doc, indices, 1, 0) ? FPDF_LoadPage(scratch, 0) : nullptr;
        if (page != nullptr && runs == nullptr && FPDFPage_CountObjects(page) == 0) {
            // Nothing to rewrite: a whiteout on a blank page adds a stream and touches no other.
            FPDF_ClosePage(page);
            FPDF_CloseDocument(scratch);
            return EditableVerdict();
        }
        if (page != nullptr) {
            // Compare like with like (#128). PDFium resolves a non-embedded font once per
            // document, against a process-wide face cache: the first document to ask can get
            // a different face from every later one. Rendering the copy before the rewrite
            // straight from memory therefore compared a cold lookup with the reopened
            // document's warm one, and refused edits that changed nothing. Resolve the
            // page's fonts here first, then judge a reopened copy of the page as it was
            // against a reopened copy of the rewrite. (Without its images, #151.)
            bool stopped = false;
            WarmFontsUnlocked(d->doc, page_index, between != nullptr ? &go_on : nullptr, &stopped);
            if (stopped || !go_on()) {
                FPDF_ClosePage(page);
                FPDF_CloseDocument(scratch);
                return verdict;
            }
            ScratchWriter unchanged{};
            unchanged.fw.version = 1;
            unchanged.fw.WriteBlock = ScratchWriteBlock;
            const bool saved_unchanged = FPDF_SaveAsCopy(scratch, &unchanged.fw, 0);
            std::vector<PageBox> edited;
            bool rewritten = false;
            if (saved_unchanged && go_on()) {
                if (runs != nullptr) {
                    const std::vector<int> judged_objects = WithHiddenCopies(page, *runs);
                    edited = BoundsOf(page, judged_objects);
                    rewritten = RewriteObjectsUnlocked(page, judged_objects);
                } else {
                    rewritten = RewritePageUnlocked(page);
                }
            }
            FPDF_ClosePage(page);
            ScratchWriter writer{};
            writer.fw.version = 1;
            writer.fw.WriteBlock = ScratchWriteBlock;
            if (saved_unchanged && rewritten && go_on() && FPDF_SaveAsCopy(scratch, &writer.fw, 0) && go_on()) {
                FPDF_DOCUMENT was = FPDF_LoadMemDocument64(unchanged.out.data(), unchanged.out.size(), nullptr);
                FPDF_DOCUMENT again = FPDF_LoadMemDocument64(writer.out.data(), writer.out.size(), nullptr);
                FPDF_PAGE was_page = was ? FPDF_LoadPage(was, 0) : nullptr;
                FPDF_PAGE reopened = again ? FPDF_LoadPage(again, 0) : nullptr;
                // Only the page check renders in slices; the text guard renders as it always has (#118).
                if (was_page != nullptr && reopened != nullptr && go_on())
                    verdict = CompareRewrite(was_page, reopened, edited, between != nullptr ? &go_on : nullptr);
                if (reopened != nullptr) FPDF_ClosePage(reopened);
                if (was_page != nullptr) FPDF_ClosePage(was_page);
                if (again != nullptr) FPDF_CloseDocument(again);
                if (was != nullptr) FPDF_CloseDocument(was);
            }
        }
        FPDF_CloseDocument(scratch);
    }
    return verdict;
}

// The page's own verdict (#139) sits in the same cache under this object index, so a change
// to the page clears it with the objects' verdicts (#137).
constexpr int kPageVerdictKey = -1;

megapdf_layout_verdict JudgePageUnlocked(megapdf_document* d, int page_index) {
    const auto key = std::make_pair(page_index, kPageVerdictKey);
    const auto cached = d->rewrite_keeps_page.find(key);
    if (cached != d->rewrite_keeps_page.end()) return cached->second;
    const megapdf_layout_verdict verdict = DryRunUnlocked(d, page_index, nullptr);
    d->rewrite_keeps_page[key] = verdict;
    return verdict;
}

// The dry run behind megapdf_text_editable(). Rewrites the streams holding the objects and
// their hidden copies (#136) on a copy of the page — take them off and put them straight
// back, which is what any edit forces — then saves, reopens and compares with the copy
// before the rewrite. One dry run judges a whole line; the verdict is cached per object.
// The answer is the first refused run's verdict, or an editable one (#128).
megapdf_layout_verdict JudgeRewriteUnlocked(megapdf_document* d, int page_index, const std::vector<int>& runs) {
    bool all_judged = true;
    megapdf_layout_verdict judged = EditableVerdict();
    bool judged_set = false;
    for (int i : runs) {
        const auto cached = d->rewrite_keeps_page.find(std::make_pair(page_index, i));
        if (cached == d->rewrite_keeps_page.end()) all_judged = false;
        else if (!cached->second.editable) return cached->second;
        else if (!judged_set) { judged = cached->second; judged_set = true; }
    }
    if (all_judged) return judged;

    const megapdf_layout_verdict verdict = DryRunUnlocked(d, page_index, &runs);
    if (verdict.editable || runs.size() == 1) {
        for (int i : runs) d->rewrite_keeps_page[std::make_pair(page_index, i)] = verdict;
        return verdict;
    }
    // A refused batch says nothing about which run PDFium cannot rewrite: judge each alone,
    // so every verdict cached is that object's own, and the answer is theirs together.
    megapdf_layout_verdict first_refused{};
    bool refused = false;
    for (int i : runs) {
        const megapdf_layout_verdict one = JudgeRewriteUnlocked(d, page_index, std::vector<int>{i});
        if (!one.editable && !refused) { first_refused = one; refused = true; }
    }
    if (refused) return first_refused;
    return d->rewrite_keeps_page[std::make_pair(page_index, runs.front())];
}

}  // namespace

extern "C" {

MEGAPDF_API int megapdf_text_editable(const megapdf_page* p, int object_index) {
    megapdf_layout_verdict verdict{};
    return megapdf_text_editable_reason(p, object_index, &verdict);
}

MEGAPDF_API int megapdf_text_editable_reason(const megapdf_page* p, int object_index, megapdf_layout_verdict* out) {
    if (p == nullptr || p->owner == nullptr || p->index < 0 || object_index < 0 || out == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, object_index);
    if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_TEXT) return MEGAPDF_ERR_ARGUMENT;
    *out = JudgeRewriteUnlocked(p->owner, p->index, std::vector<int>{object_index});
    return out->editable ? 1 : 0;
}

MEGAPDF_API int megapdf_last_layout_verdict(megapdf_layout_verdict* out) {
    if (out == nullptr) return MEGAPDF_ERR_ARGUMENT;
    *out = g_last_layout;
    return MEGAPDF_OK;
}

MEGAPDF_API int megapdf_page_regeneration_verdict(const megapdf_page* p, megapdf_layout_verdict* out) {
    return megapdf_page_regeneration_verdict_cancellable(p, nullptr, out);
}

MEGAPDF_API int megapdf_page_regeneration_verdict_cancellable(const megapdf_page* p, const megapdf_cancel* cancel,
                                                             megapdf_layout_verdict* out) {
    if (p == nullptr || p->owner == nullptr || p->page == nullptr || p->index < 0 || out == nullptr) return MEGAPDF_ERR_ARGUMENT;
    std::unique_lock<std::recursive_mutex> lock(CoreLock());
    // The page handle may be closed while the run has let go of the lock: from here on only
    // the document (which megapdf_close() keeps alive until the run ends) and the index are used.
    megapdf_document* d = p->owner;
    const int index = p->index;
    const auto key = std::make_pair(index, kPageVerdictKey);
    const auto cached = d->rewrite_keeps_page.find(key);
    if (cached != d->rewrite_keeps_page.end()) {
        *out = cached->second;
        return out->editable ? 1 : 0;
    }
    auto raised = [cancel] { return cancel != nullptr && cancel->raised.load(std::memory_order_relaxed) != 0; };
    if (raised() || d->closing) return MEGAPDF_ERR_CANCELLED;

    const unsigned long long changes = d->page_changes[index];
    d->active_checks++;
    // After a stage that took a while the lock is let go, so rendering, edits and saves on any
    // document go on while a slow page is judged: they wait one stage, not the whole run. A
    // short sleep rather than a yield, because a mutex is not fair and the waiting thread
    // would otherwise rarely win it back. Quick stages keep the lock: a typical page is judged
    // in tens of milliseconds, and a hand-over costs a scheduler tick on some systems. A
    // raised flag or a closing document stops the run at any stage.
    auto stage_started = std::chrono::steady_clock::now();
    const megapdf_page_check_stage_hook hook = g_page_check_hook.load();
    const std::function<bool()> between = [&]() {
        if (hook != nullptr || std::chrono::steady_clock::now() - stage_started >= std::chrono::milliseconds(20)) {
            lock.unlock();
            if (hook != nullptr) hook(g_page_check_hook_context.load());   // core tests only
            else std::this_thread::sleep_for(std::chrono::milliseconds(1));
            lock.lock();
            stage_started = std::chrono::steady_clock::now();
        }
        return !raised() && !d->closing;
    };
    bool aborted = false;
    const megapdf_layout_verdict verdict = DryRunUnlocked(d, index, nullptr, &between, &aborted);
    d->active_checks--;
    // megapdf_close() may be waiting; it cannot run until this call lets go of the lock.
    ChecksDone().notify_all();
    if (aborted) return MEGAPDF_ERR_CANCELLED;
    // A page that changed while the run had let go of the lock was judged as it was: the
    // answer is given, but not kept for the page as it is now.
    if (!d->closing && d->page_changes[index] == changes) d->rewrite_keeps_page[key] = verdict;
    *out = verdict;
    return verdict.editable ? 1 : 0;
}

MEGAPDF_API int megapdf_page_regeneration_verdict_cached(const megapdf_page* p, megapdf_layout_verdict* out) {
    if (p == nullptr || p->owner == nullptr || p->index < 0 || out == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    const auto cached = p->owner->rewrite_keeps_page.find(std::make_pair(p->index, kPageVerdictKey));
    if (cached == p->owner->rewrite_keeps_page.end()) return MEGAPDF_ERR_NOT_JUDGED;
    *out = cached->second;
    return out->editable ? 1 : 0;
}

MEGAPDF_API int megapdf_testing_compare_pages(const megapdf_page* was, const megapdf_page* now, megapdf_layout_verdict* out) {
    if (was == nullptr || now == nullptr || was->page == nullptr || now->page == nullptr || out == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    *out = CompareRewrite(was->page, now->page, std::vector<PageBox>{});
    return out->editable ? 1 : 0;
}

MEGAPDF_API void megapdf_testing_set_page_check_hook(megapdf_page_check_stage_hook hook, void* context) {
    g_page_check_hook_context.store(context);
    g_page_check_hook.store(hook);
}

MEGAPDF_API void megapdf_testing_set_max_file_bytes(unsigned long long bytes) {
    g_testing_max_file_bytes.store(bytes);
}

MEGAPDF_API megapdf_cancel* megapdf_cancel_new(void) {
    return new (std::nothrow) megapdf_cancel();
}

MEGAPDF_API void megapdf_cancel_raise(megapdf_cancel* cancel) {
    if (cancel != nullptr) cancel->raised.store(1, std::memory_order_relaxed);
}

MEGAPDF_API void megapdf_cancel_free(megapdf_cancel* cancel) {
    delete cancel;
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

// Every change to a page's objects ends here: once per call, however many objects it
// moved (a page of thousands of objects takes seconds to regenerate).
bool GenerateContent(const megapdf_page* p) {
    if (p->owner != nullptr) ForgetVerdicts(p->owner, p->index);
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
    m.e += static_cast<float>(InX(p, left)) - l;
    m.f += static_cast<float>(InY(p, bottom)) - b;
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
    FPDF_PAGEOBJECT obj = FPDFPageObj_CreateTextObj(doc, font, static_cast<float>(font_size / p->unit));
    bool ok = obj != nullptr && FPDFText_SetText(obj, reinterpret_cast<FPDF_WIDESTRING>(text));
    if (ok) {
        FS_MATRIX m{1, 0, 0, 1, static_cast<float>(InX(p, baseline_x)), static_cast<float>(InY(p, baseline_y))};
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

void Unlist(megapdf_detached* x) {
    if (x->owner == nullptr) return;
    auto& list = x->owner->detached;
    for (size_t i = 0; i < list.size(); i++) if (list[i] == x) { list[i] = list.back(); list.pop_back(); break; }
}

// A handle for `parts` (ascending by index, off the page), kept by the document until it is
// restored or discarded. NULL, with the objects freed, when out of memory.
megapdf_detached* NewHandle(const megapdf_page* p, std::vector<megapdf_detached::Part> parts, int edited_index) {
    megapdf_detached* x = new (std::nothrow) megapdf_detached();
    if (x != nullptr) {
        x->owner = p->owner;
        x->page_index = p->index;
        x->parts = std::move(parts);   // noexcept
        x->edited_index = edited_index;
        try {
            if (p->owner != nullptr) p->owner->detached.push_back(x);
            return x;
        } catch (...) {
            for (const auto& part : x->parts) FPDFPageObj_Destroy(part.object);
            delete x;
        }
    } else {
        for (const auto& part : parts) FPDFPageObj_Destroy(part.object);
    }
    SetError(FPDF_ERR_UNKNOWN, "out of memory");
    return nullptr;
}

// Takes `parts` (ascending, objects filled) off the page, highest index first, so every
// index is still the object's own when it is taken. On failure puts back what it took.
bool TakeParts(const megapdf_page* p, const std::vector<megapdf_detached::Part>& parts) {
    size_t taken = 0;
    while (taken < parts.size() && FPDFPage_RemoveObject(p->page, parts[parts.size() - 1 - taken].object)) taken++;
    if (taken == parts.size()) return true;
    for (size_t k = parts.size() - taken; k < parts.size(); k++) {
        FPDFPage_InsertObjectAtIndex(p->page, parts[k].object, static_cast<size_t>(parts[k].index));
    }
    SetError(FPDF_ERR_UNKNOWN, "could not remove the text");
    return false;
}

// The text runs at `indices` as parts, with their hidden copies (#136), ascending. False
// for a bad index, an object that is not text, or a repeat. Throws only std::bad_alloc.
bool PlanTextRuns(const megapdf_page* p, const int* indices, size_t count, std::vector<megapdf_detached::Part>* out) {
    const int objects = FPDFPage_CountObjects(p->page);
    std::vector<int> runs(indices, indices + count);
    std::sort(runs.begin(), runs.end());
    for (size_t k = 0; k < runs.size(); k++) {
        FPDF_PAGEOBJECT obj = runs[k] >= 0 && runs[k] < objects ? FPDFPage_GetObject(p->page, runs[k]) : nullptr;
        if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_TEXT || (k > 0 && runs[k] == runs[k - 1])) {
            SetError(0, "every index must be a different text object on the page");
            out->clear();
            return false;
        }
        out->push_back(megapdf_detached::Part{runs[k], -1, obj});
    }
    FPDF_TEXTPAGE text_page = FPDFText_LoadPage(p->page);
    std::vector<std::pair<int, int>> copies;
    try {
        copies = HiddenCopies(p->page, text_page, runs);
    } catch (...) {
        if (text_page != nullptr) FPDFText_ClosePage(text_page);
        throw;
    }
    if (text_page != nullptr) FPDFText_ClosePage(text_page);
    for (const auto& copy : copies) {
        out->push_back(megapdf_detached::Part{copy.first, copy.second, FPDFPage_GetObject(p->page, copy.first)});
    }
    std::sort(out->begin(), out->end(),
              [](const megapdf_detached::Part& a, const megapdf_detached::Part& b) { return a.index < b.index; });
    return true;
}

// #118 for the body text among `indices`; text boxes are MegaPDF's own and are not judged,
// except that megapdf_set_text() has always judged the run it edits. A refusal is recorded
// for megapdf_last_layout_verdict() (#128).
bool BodyTextRewriteKeepsPage(const megapdf_page* p, const int* indices, size_t count, bool judge_first_always) {
    if (p->owner == nullptr || p->index < 0) return true;
    std::vector<int> body;
    for (size_t k = 0; k < count; k++) {
        if ((k == 0 && judge_first_always) || !HasMark(FPDFPage_GetObject(p->page, indices[k]), kTextBoxMark)) {
            body.push_back(indices[k]);
        }
    }
    if (body.empty()) return true;
    const megapdf_layout_verdict verdict = JudgeRewriteUnlocked(p->owner, p->index, body);
    if (verdict.editable) return true;
    g_last_layout = verdict;
    return false;
}

}  // namespace

extern "C" {

MEGAPDF_API int megapdf_add_whiteout(const megapdf_page* p, const megapdf_rect* bounds, int* out_object_index) {
    if (p == nullptr || bounds == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    const float left = static_cast<float>(InX(p, bounds->left)), right = static_cast<float>(InX(p, bounds->right));
    const float bottom = static_cast<float>(InY(p, bounds->bottom)), top = static_cast<float>(InY(p, bounds->top));
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
            out[found].bounds = OutRect(p, l, b, r, t);
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
    *out = OutRect(p, l, b, r, t);
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
    g_last_layout = EditableVerdict();
    if (p == nullptr || p->owner == nullptr || object_index < 0) return nullptr;
    Guard guard(CoreLock());
    FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, object_index);
    if (obj == nullptr) { SetError(0, "no page object at that index"); return nullptr; }
    if (FPDFPageObj_GetType(obj) == FPDF_PAGEOBJ_TEXT && !HasMark(obj, kTextBoxMark) &&
        !BodyTextRewriteKeepsPage(p, &object_index, 1, /*judge_first_always=*/false)) {
        SetError(0, "PDFium would change how this page looks if its text were rewritten");
        return nullptr;
    }
    std::vector<megapdf_detached::Part> parts;
    try {
        parts.push_back(megapdf_detached::Part{object_index, -1, obj});
    } catch (...) {
        SetError(FPDF_ERR_UNKNOWN, "out of memory");
        return nullptr;
    }
    if (!FPDFPage_RemoveObject(p->page, obj)) { SetError(FPDF_ERR_UNKNOWN, "could not remove the object"); return nullptr; }
    GenerateContent(p);
    return NewHandle(p, std::move(parts), -1);
}

MEGAPDF_API int megapdf_restore_object(const megapdf_page* p, megapdf_detached* x, int object_index) {
    if (p == nullptr || x == nullptr || object_index < 0) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    if (x->parts.size() != 1) {
        SetError(0, "this handle holds more than one object; restore it with megapdf_restore_detached");
        return MEGAPDF_ERR_ARGUMENT;
    }
    if (!FPDFPage_InsertObjectAtIndex(p->page, x->parts[0].object, static_cast<size_t>(object_index))) {
        // PDFium frees an object it fails to insert.
        x->parts.clear();
        Unlist(x);
        delete x;
        SetError(FPDF_ERR_UNKNOWN, "could not restore the object");
        return MEGAPDF_ERR_PDFIUM;
    }
    // The page owns it again; the handle is spent.
    Unlist(x);
    delete x;
    return GenerateContent(p) ? MEGAPDF_OK : MEGAPDF_ERR_PDFIUM;
}

MEGAPDF_API void megapdf_discard_detached(megapdf_detached* x) {
    if (x == nullptr) return;
    Guard guard(CoreLock());
    Unlist(x);
    for (const auto& part : x->parts) FPDFPageObj_Destroy(part.object);
    delete x;
}

MEGAPDF_API megapdf_detached* megapdf_detach_text_runs(const megapdf_page* p, const int* indices, size_t count) {
    g_last_layout = EditableVerdict();
    if (p == nullptr || p->owner == nullptr || indices == nullptr || count == 0) {
        SetError(0, "no text runs to remove");
        return nullptr;
    }
    Guard guard(CoreLock());
    std::vector<megapdf_detached::Part> parts;
    try {
        if (!PlanTextRuns(p, indices, count, &parts)) return nullptr;
    } catch (...) {
        SetError(FPDF_ERR_UNKNOWN, "out of memory");
        return nullptr;
    }
    if (!BodyTextRewriteKeepsPage(p, indices, count, /*judge_first_always=*/false)) {
        SetError(0, "PDFium would change how this page looks if its text were rewritten");
        return nullptr;
    }
    if (!TakeParts(p, parts)) return nullptr;
    GenerateContent(p);
    return NewHandle(p, std::move(parts), -1);
}

MEGAPDF_API int megapdf_restore_detached(const megapdf_page* p, megapdf_detached* x) {
    if (p == nullptr || x == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    if ((x->owner != nullptr && p->owner != x->owner) || (x->page_index >= 0 && p->index != x->page_index)) {
        SetError(0, "the detached objects belong to another page");
        return MEGAPDF_ERR_ARGUMENT;
    }
    // Check everything before changing anything. The edited run stands at its original's
    // index less the parts before it that are still off the page.
    const int count = FPDFPage_CountObjects(p->page);
    FPDF_PAGEOBJECT edited = nullptr;
    if (x->edited_index >= 0) {
        int before = 0;
        for (const auto& part : x->parts) if (part.index < x->edited_index) before++;
        edited = FPDFPage_GetObject(p->page, x->edited_index - before);
        float l = 0, b = 0, r = 0, t = 0;
        // Bounds survive a content rewrite and a page reload to well within this.
        const float tolerance = 0.1f;
        if (edited == nullptr || FPDFPageObj_GetType(edited) != FPDF_PAGEOBJ_TEXT || !FPDFPageObj_GetBounds(edited, &l, &b, &r, &t) ||
            std::fabs(l - x->edited_left) > tolerance || std::fabs(b - x->edited_bottom) > tolerance ||
            std::fabs(r - x->edited_right) > tolerance || std::fabs(t - x->edited_top) > tolerance) {
            SetError(0, "the edited text is no longer where the edit left it");
            return MEGAPDF_ERR_ARGUMENT;
        }
    }
    const int base = count - (edited != nullptr ? 1 : 0);
    for (size_t k = 0; k < x->parts.size(); k++) {
        if (x->parts[k].index > base + static_cast<int>(k)) {
            SetError(0, "the page no longer has room for the detached objects where they were");
            return MEGAPDF_ERR_ARGUMENT;
        }
    }
    if (edited != nullptr) {
        if (!FPDFPage_RemoveObject(p->page, edited)) {
            SetError(FPDF_ERR_UNKNOWN, "could not take the edited text off");
            return MEGAPDF_ERR_PDFIUM;
        }
        FPDFPageObj_Destroy(edited);
    }
    // Lowest first: every object before this one is back, so its index is its own again.
    int status = MEGAPDF_OK;
    for (const auto& part : x->parts) {
        if (status != MEGAPDF_OK) {
            FPDFPageObj_Destroy(part.object);
        } else if (!FPDFPage_InsertObjectAtIndex(p->page, part.object, static_cast<size_t>(part.index))) {
            // PDFium frees an object it fails to insert.
            SetError(FPDF_ERR_UNKNOWN, "could not restore the object");
            status = MEGAPDF_ERR_PDFIUM;
        }
    }
    Unlist(x);
    delete x;
    const bool generated = GenerateContent(p);
    return status != MEGAPDF_OK ? status : generated ? MEGAPDF_OK : MEGAPDF_ERR_PDFIUM;
}

MEGAPDF_API size_t megapdf_detached_count(const megapdf_detached* x) {
    if (x == nullptr) return 0;
    Guard guard(CoreLock());
    return x->parts.size();
}

MEGAPDF_API int megapdf_detached_get(const megapdf_detached* x, size_t index, megapdf_detached_part* out) {
    if (x == nullptr || out == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    if (index >= x->parts.size()) return MEGAPDF_ERR_ARGUMENT;
    out->object_index = x->parts[index].index;
    out->copy_of = x->parts[index].copy_of;
    return MEGAPDF_OK;
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

// Serialises through the caller's write callback: with the security the document
// has, with none, or with new security (#131). The caller holds the core lock.
static int SaveDocument(const megapdf_document* d, megapdf_write_fn write, void* context, FPDF_DWORD flags,
                        bool new_security, const char* user_utf8, const char* owner_utf8,
                        unsigned int permissions) {
    if (d->form != nullptr) FORM_ForceToKillFocus(d->form);
    WriteBridge bridge{};
    bridge.fw.version = 1;
    bridge.fw.WriteBlock = WriteBlockThunk;
    bridge.write = write;
    bridge.context = context;
    bridge.failed = false;
    const FPDF_BOOL ok = new_security
        ? FPDF_SaveAsCopyWithSecurity(d->doc, &bridge.fw, flags, user_utf8 != nullptr ? user_utf8 : "",
                                      owner_utf8 != nullptr ? owner_utf8 : "", permissions & MEGAPDF_PERMIT_ALL)
        : FPDF_SaveAsCopy(d->doc, &bridge.fw, flags);
    if (bridge.failed) { SetError(0, "the write callback aborted the save"); return MEGAPDF_ERR_PDFIUM; }
    if (!ok) { SetError(FPDF_ERR_UNKNOWN, "PDFium could not serialize the document"); return MEGAPDF_ERR_PDFIUM; }
    return MEGAPDF_OK;
}

MEGAPDF_API int megapdf_save(const megapdf_document* d, megapdf_write_fn write, void* context) {
    if (d == nullptr || write == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    return SaveDocument(d, write, context, 0, false, nullptr, nullptr, 0);
}

// PDFium reports every permission for an unprotected document and for an owner
// open; MEGAPDF_PERMIT_ALL is the bits that mean something.
static unsigned int OpenPermissions(const megapdf_document* d) {
    return static_cast<unsigned int>(FPDF_GetDocPermissions(d->doc) & MEGAPDF_PERMIT_ALL);
}

MEGAPDF_API int megapdf_security_info(const megapdf_document* d, megapdf_security* out) {
    if (d == nullptr || out == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    const int revision = FPDF_GetSecurityHandlerRevision(d->doc);
    out->encrypted = revision >= 0 ? 1 : 0;
    out->revision = revision >= 0 ? revision : -1;
    out->permissions = OpenPermissions(d);
    out->full_access = out->permissions == MEGAPDF_PERMIT_ALL ? 1 : 0;
    return MEGAPDF_OK;
}

MEGAPDF_API int megapdf_save_with_security(const megapdf_document* d, const char* user_password_utf8,
                                           const char* owner_password_utf8, unsigned int permissions,
                                           megapdf_write_fn write, void* context) {
    if (d == nullptr || write == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    if (OpenPermissions(d) != MEGAPDF_PERMIT_ALL) {
        SetError(FPDF_ERR_SECURITY, "changing this document's security needs its owner password");
        return MEGAPDF_ERR_RESTRICTED;
    }
    return SaveDocument(d, write, context, 0, true, user_password_utf8, owner_password_utf8, permissions);
}

MEGAPDF_API int megapdf_save_without_security(const megapdf_document* d, megapdf_write_fn write, void* context) {
    if (d == nullptr || write == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    if (OpenPermissions(d) != MEGAPDF_PERMIT_ALL) {
        SetError(FPDF_ERR_SECURITY, "removing this document's security needs its owner password");
        return MEGAPDF_ERR_RESTRICTED;
    }
    return SaveDocument(d, write, context, FPDF_REMOVE_SECURITY, false, nullptr, nullptr, 0);
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
                info.display_width = static_cast<double>(r - l) * sp.page->unit;
                info.display_height = static_cast<double>(t - b) * sp.page->unit;
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

// #130: the read-back cannot see a glyph the font program lacks. PDFium keeps the
// Unicode and draws .notdef, so every character that should leave ink must have a
// glyph of its own in `font` (FPDFFont_HasGlyph, MegaPDF's PDFium patch 0011).
// Whitespace has no outline and is skipped.
bool EveryCharacterHasGlyph(FPDF_FONT font, const unsigned short* text) {
    for (size_t i = 0; text[i] != 0; i++) {
        uint32_t c = text[i];
        if (c >= 0xD800 && c <= 0xDBFF && text[i + 1] >= 0xDC00 && text[i + 1] <= 0xDFFF) {
            c = 0x10000 + ((c - 0xD800) << 10) + (static_cast<uint32_t>(text[i + 1]) - 0xDC00);
            i++;
        }
        const bool whitespace = c == 0x20 || c == 0x09 || c == 0xA0 || (c >= 0x2000 && c <= 0x200B) ||
                                c == 0x202F || c == 0x205F || c == 0x3000;
        if (!whitespace && !FPDFFont_HasGlyph(font, c)) return false;
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
    ObjectTexts texts;
    try {
        texts = ReadObjectTexts(text_page);
    } catch (...) {
        if (text_page != nullptr) FPDFText_ClosePage(text_page);
        throw;
    }
    if (text_page != nullptr) FPDFText_ClosePage(text_page);
    const int count = FPDFPage_CountObjects(p->page);
    for (int i = 0; i < count; i++) {
        FPDF_PAGEOBJECT other = FPDFPage_GetObject(p->page, i);
        if (other == nullptr || FPDFPageObj_GetType(other) != FPDF_PAGEOBJ_TEXT) continue;
        FPDF_FONT other_font = FPDFTextObj_GetFont(other);
        if (other_font == nullptr || ReadFontNameUtf8(other_font, true) != base) continue;
        for (unsigned short c : TextOf(texts, other)) covered[c] = true;
    }
    for (size_t i = 0; text[i] != 0; i++) if (!covered[text[i]]) return true;
    return false;
}

// Replaces the text object at `object_index` with a new one drawing `text` in
// `font`, carrying over the original's font size, matrix, fill and stroke colour,
// render mode and text-box identity (#45). The original is never modified —
// PDFium can set a text object's character codes but not read them, so an edit
// tried on the original could not be rolled back or undone exactly (#117). On
// success it is off the page and the caller's, and the content is not yet regenerated.
//
// With `verify` the new object must read back as exactly `text` (#116). If it
// does not, the page is put back as it was and MEGAPDF_ERR_NO_FONT is returned so
// the caller can fall back to a substitute face. Any other failure also leaves the
// page as it was.
int ReplaceTextObjectUnlocked(const megapdf_page* p, FPDF_PAGEOBJECT original, int object_index, FPDF_FONT font,
                              const unsigned short* text, bool verify) {
    FPDF_DOCUMENT doc = p->owner ? p->owner->doc : nullptr;
    float font_size = 0;
    FPDFTextObj_GetFontSize(original, &font_size);
    FPDF_PAGEOBJECT fresh = FPDFPageObj_CreateTextObj(doc, font, font_size);
    if (fresh == nullptr) {
        SetError(0, "could not create the replacement text");
        return MEGAPDF_ERR_NO_FONT;
    }
    if (!FPDFText_SetText(fresh, reinterpret_cast<FPDF_WIDESTRING>(text))) {
        FPDFPageObj_Destroy(fresh);
        SetError(0, "the font could not take the new text");
        return MEGAPDF_ERR_NO_FONT;
    }
    FS_MATRIX matrix{};
    if (FPDFPageObj_GetMatrix(original, &matrix)) FPDFPageObj_SetMatrix(fresh, &matrix);
    unsigned int r = 0, g = 0, b = 0, a = 0;
    if (FPDFPageObj_GetFillColor(original, &r, &g, &b, &a)) FPDFPageObj_SetFillColor(fresh, r, g, b, a);
    if (FPDFPageObj_GetStrokeColor(original, &r, &g, &b, &a)) FPDFPageObj_SetStrokeColor(fresh, r, g, b, a);
    const FPDF_TEXT_RENDERMODE mode = FPDFTextObj_GetTextRenderMode(original);
    if (mode != FPDF_TEXTRENDERMODE_UNKNOWN) FPDFTextObj_SetTextRenderMode(fresh, mode);

    // Read the box's identity off the original while it is still on the page.
    const bool was_box = HasMark(original, kTextBoxMark);
    const U16 box_id = was_box ? ReadMarkParam(original, "id") : U16{};
    const U16 box_font = was_box ? ReadMarkParam(original, "font") : U16{};

    if (!FPDFPage_RemoveObject(p->page, original)) {
        FPDFPageObj_Destroy(fresh);
        SetError(FPDF_ERR_UNKNOWN, "could not remove the original text object");
        return MEGAPDF_ERR_PDFIUM;
    }
    if (!FPDFPage_InsertObjectAtIndex(p->page, fresh, static_cast<size_t>(object_index))) {
        // PDFium frees `fresh` on failure. Put the original back so the page is as it was.
        FPDFPage_InsertObjectAtIndex(p->page, original, static_cast<size_t>(object_index));
        SetError(FPDF_ERR_UNKNOWN, "could not insert the replacement text object");
        return MEGAPDF_ERR_PDFIUM;
    }
    FPDF_PAGEOBJECT inserted = FPDFPage_GetObject(p->page, object_index);
    if (was_box && inserted != nullptr) {
        // Re-tag AFTER insertion, off the object the page now owns — the order the
        // params actually stick in. Re-adding the mark alone was #45: the box read
        // as a text box but carried no id, so both phones refused to select it. A
        // box written before the id param existed stays untagged rather than
        // gaining a fabricated identity no phone recorded.
        FPDF_PAGEOBJECTMARK mark = FPDFPageObj_AddMark(inserted, kTextBoxMarkName);
        if (mark != nullptr) {
            auto ascii = [](const U16& v) { std::string out; for (unsigned short c : v) out += static_cast<char>(c < 0x80 ? c : '?'); return out; };
            if (!box_id.empty()) FPDFPageObjMark_SetStringParam(doc, inserted, mark, "id", ascii(box_id).c_str());
            if (!box_font.empty()) FPDFPageObjMark_SetStringParam(doc, inserted, mark, "font", ascii(box_font).c_str());
        }
    }
    if (verify) {
        // Prove the text took (#116). FPDFText_SetText succeeds whatever the font can
        // carry: a font with no slot in its encoding drops the character, one with no
        // mapping draws the wrong glyph, one with no width spreads the letters apart.
        FPDF_TEXTPAGE text_page = FPDFText_LoadPage(p->page);
        // And every character must have a glyph: PDFium keeps the Unicode of one the
        // font program lacks, so the read-back alone passes .notdef boxes (#130).
        const bool took = AuthoredTextIs(text_page, inserted, text) && EveryCharacterHasGlyph(font, text);
        if (text_page != nullptr) FPDFText_ClosePage(text_page);
        if (!took) {
            FPDFPage_RemoveObject(p->page, inserted);
            FPDFPageObj_Destroy(inserted);
            FPDFPage_InsertObjectAtIndex(p->page, original, static_cast<size_t>(object_index));
            SetError(0, "the run's own font could not carry the new text");
            return MEGAPDF_ERR_NO_FONT;
        }
    }
    return MEGAPDF_OK;
}

}  // namespace

extern "C" {

MEGAPDF_API int megapdf_set_line_text(const megapdf_page* p, const int* indices, size_t count, const unsigned short* text,
                                      unsigned int flags, int* out_outcome, megapdf_detached** out_replaced) {
    g_last_layout = EditableVerdict();
    if (out_replaced != nullptr) *out_replaced = nullptr;
    const bool force_substitute = (flags & MEGAPDF_SET_TEXT_FORCE_SUBSTITUTE) != 0;
    if (p == nullptr || indices == nullptr || count == 0 || indices[0] < 0 || text == nullptr || text[0] == 0 ||
        (flags & ~static_cast<unsigned int>(MEGAPDF_SET_TEXT_FORCE_SUBSTITUTE)) != 0) {
        SetError(0, "PDFium cannot set empty text on a text object");
        return MEGAPDF_ERR_ARGUMENT;
    }
    Guard guard(CoreLock());
    const int object_index = indices[0];
    FPDF_PAGEOBJECT obj = FPDFPage_GetObject(p->page, object_index);
    if (obj == nullptr || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_TEXT) {
        SetError(0, "the object is no longer a text object");
        return MEGAPDF_ERR_ARGUMENT;
    }
    // Every original the edit takes: the edited run, the line's other runs, and the hidden
    // copies of all of them (#136). `rest` is what leaves the page besides the edited run.
    std::vector<megapdf_detached::Part> parts;
    std::vector<megapdf_detached::Part> rest;
    try {
        if (!PlanTextRuns(p, indices, count, &parts)) return MEGAPDF_ERR_ARGUMENT;
        for (const auto& part : parts) if (part.object != obj) rest.push_back(part);
    } catch (...) {
        SetError(FPDF_ERR_UNKNOWN, "out of memory");
        return MEGAPDF_ERR_MEMORY;
    }
    // #118: if PDFium cannot write these streams back without changing the page, the
    // edit would silently alter text the user never touched. Refuse before either
    // tier modifies anything.
    if (!BodyTextRewriteKeepsPage(p, indices, count, /*judge_first_always=*/true)) {
        SetError(0, "PDFium would change how this page looks if its text were rewritten");
        return MEGAPDF_ERR_LAYOUT;
    }

    // Tier 1: the run's own font, if it can carry the text. The line's other runs and the
    // copies are still on the page, so the coverage and the read-back see what they always did.
    int outcome = -1;
    int status = MEGAPDF_ERR_NO_FONT;
    if (!force_substitute && !NeedsSubstitution(p, obj, text)) {
        if (FPDF_FONT own = FPDFTextObj_GetFont(obj)) {
            status = ReplaceTextObjectUnlocked(p, obj, object_index, own, text, /*verify=*/true);
            if (status == MEGAPDF_OK) outcome = MEGAPDF_EDIT_IN_PLACE;
            else if (status != MEGAPDF_ERR_NO_FONT) return status;
            // Otherwise the page is as it was; fall through to the substitute.
        }
    }

    // Tier 2: the closest standard face.
    if (status != MEGAPDF_OK) {
        FPDF_DOCUMENT doc = p->owner ? p->owner->doc : nullptr;
        FPDF_FONT old_font = FPDFTextObj_GetFont(obj);
        const std::string original = old_font ? ReadFontNameUtf8(old_font, false) : "";
        FPDF_FONT standard = FPDFText_LoadStandardFont(doc, MapToStandard(original).c_str());
        if (standard == nullptr) {
            SetError(0, "no substitute font could be loaded");
            return MEGAPDF_ERR_NO_FONT;
        }
        // Verified like tier 1 (#130): PDFium writes a stand-in code for any character the
        // face cannot encode (CJK into Helvetica reads back as U+00FF), so an unverified
        // substitute reported success for text that draws nothing like what was typed.
        status = ReplaceTextObjectUnlocked(p, obj, object_index, standard, text, /*verify=*/true);
        FPDFFont_Close(standard);   // the text object keeps its own reference
        if (status != MEGAPDF_OK) return status;
        outcome = MEGAPDF_EDIT_SUBSTITUTED;
    }

    // The edited run stands at its original's index. Taking the rest, highest first, moves
    // no index still to be taken; if that fails, put the original back too.
    float edited_left = 0, edited_bottom = 0, edited_right = 0, edited_top = 0;
    FPDFPageObj_GetBounds(FPDFPage_GetObject(p->page, object_index), &edited_left, &edited_bottom, &edited_right, &edited_top);
    if (!TakeParts(p, rest)) {
        FPDF_PAGEOBJECT inserted = FPDFPage_GetObject(p->page, object_index);
        if (inserted != nullptr && FPDFPage_RemoveObject(p->page, inserted)) FPDFPageObj_Destroy(inserted);
        FPDFPage_InsertObjectAtIndex(p->page, obj, static_cast<size_t>(object_index));
        return MEGAPDF_ERR_PDFIUM;
    }
    const bool generated = GenerateContent(p);
    if (out_replaced != nullptr) {
        *out_replaced = NewHandle(p, std::move(parts), object_index);
        if (megapdf_detached* x = *out_replaced) {
            x->edited_left = edited_left;
            x->edited_bottom = edited_bottom;
            x->edited_right = edited_right;
            x->edited_top = edited_top;
        }
    } else {
        for (const auto& part : parts) FPDFPageObj_Destroy(part.object);
    }
    if (!generated) return MEGAPDF_ERR_PDFIUM;
    if (out_outcome != nullptr) *out_outcome = outcome;
    return MEGAPDF_OK;
}

MEGAPDF_API int megapdf_set_text(const megapdf_page* p, int object_index, const unsigned short* text, unsigned int flags,
                                 int* out_outcome, megapdf_detached** out_replaced) {
    return megapdf_set_line_text(p, &object_index, 1, text, flags, out_outcome, out_replaced);
}

MEGAPDF_API int megapdf_insert_text_run(const megapdf_page* p, int object_index, const unsigned short* text, const char* font_name,
                                        double font_size, double left, double baseline) {
    if (p == nullptr || object_index < 0 || text == nullptr || text[0] == 0 || font_name == nullptr) return MEGAPDF_ERR_ARGUMENT;
    Guard guard(CoreLock());
    FPDF_DOCUMENT doc = p->owner ? p->owner->doc : nullptr;
    FPDF_FONT font = FPDFText_LoadStandardFont(doc, MapToStandard(font_name).c_str());
    if (font == nullptr) { SetError(0, "no substitute font could be loaded"); return MEGAPDF_ERR_NO_FONT; }
    FPDF_PAGEOBJECT obj = FPDFPageObj_CreateTextObj(doc, font, static_cast<float>(font_size / p->unit));
    bool ok = obj != nullptr && FPDFText_SetText(obj, reinterpret_cast<FPDF_WIDESTRING>(text));
    if (ok) {
        FS_MATRIX m{1, 0, 0, 1, static_cast<float>(InX(p, left)), static_cast<float>(InY(p, baseline))};
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

