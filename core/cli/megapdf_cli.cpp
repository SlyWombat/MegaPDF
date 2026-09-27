// megapdf-cli (#142, #355): a native command-line shell over megapdf_write_text(). It opens,
// works out which pages have no text layer, calls the writer, and maps the result onto the
// exit codes and stderr notes the design comment on #142 (2026-09-24, §4-§6) specifies.
//
// Design decision (§4): a standalone C++ binary built with the core, not a .NET program --
// the app's own executables are GUI-subsystem processes with no reliable stdout on Windows,
// the Store packages are not on PATH, and a dotnet tool needs an SDK this audience does not
// have. This file therefore talks to megapdf_core.h only: no MegaPDF.Core, no P/Invoke.
//
// Password handling follows the repo's own rule (tools/gen_security_fixtures.sh:55-57 uses a
// qpdf args file for exactly this reason; core-tests.yml:99-100 skips protected files rather
// than put a password on a command line; MEGAPDF_STRESS_UNLOCK_LIST names a file): there is no
// --password flag. The value comes from the named file's first line or stdin's first line, is
// handed to megapdf_open_file(), and is zeroed (std::fill, the same pattern megapdf_core.cpp's
// own OpenLike() uses on `unlock`) immediately after that call returns, success or failure.
//
// contract 9 (megapdf_structure_load/megapdf_write_text) only loads one CONTIGUOUS page range
// per call; --pages accepts a comma list of possibly-disjoint ranges ("1-3,7,9-"), so this file
// merges the parsed ranges into the smallest set of non-overlapping, non-adjacent-merged
// intervals and calls megapdf_write_text once per interval, inserting one page-break separator
// (matching --page-marker/--no-page-breaks/the default) between intervals itself -- exactly the
// separator megapdf_write_text would put between two ordinary consecutive pages within one call.
//
// The per-page "page N: no text layer" stderr notes and the exit code both need to know which
// requested pages had no text layer, which megapdf_write_text()'s own return value (an
// aggregate count) does not carry by page. An earlier version of this file answered that with
// a second, independent megapdf_structure_load() pass before the real write -- and a real
// Windows CI run of PR #367 found a document (a single all-image page) where that second call's
// answer silently disagreed with the first: the exit code came out right (both calls agreed the
// page had no text) but the actual written output was empty, because the SEPARATE call inside
// megapdf_write_text() that produces the real bytes did not see what the first call saw. Rather
// than chase why two calls over the same range can disagree, this file now makes exactly ONE
// contract-9 pass per interval: the whole output is captured in memory first, textless pages are
// found by scanning that SAME text for the writer's own "[Page N has no text layer]" placeholder
// (megapdf_write_text.cpp's PAGE_IMAGE case), and only then is it written to the real
// destination. The exit code and the stderr notes are now guaranteed to describe exactly what
// was written, because they are read from it.
#include "megapdf_core.h"
#include "fpdfview.h"   // FPDF_ERR_* constants only: header macros, no pdfium link needed.

#include <algorithm>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

#if defined(_WIN32)
#include <fcntl.h>
#include <io.h>
#include <windows.h>
#else
#include <unistd.h>
#endif

namespace {

// Bumped by hand at each release, alongside the five .csproj/manifest/build.gradle.kts/
// project.yml version numbers this repo otherwise keeps in sync (#342) — megapdf-cli ships
// from #356's platform archives/packages, not from this project's own .NET assembly version,
// so nothing bumps this one automatically. It carries the app's version: the CLI is the
// same core as the app on the same commit, and the tags that publish it
// (windows-cli-v*, macos-cli-v*, linux-v*) are cut from the release commit.
constexpr const char* kVersion = "megapdf-cli 2.1.1";

void PrintUsage(std::FILE* out) {
    std::fprintf(out,
        "usage: megapdf-cli extract <file.pdf> [options]\n"
        "\n"
        "  --format txt|md            output format (default txt)\n"
        "  --pages <ranges>           1-based, e.g. \"1-3,7,9-\" (an open end means the last page);\n"
        "                             default: every page\n"
        "  --out <path>               write to <path> instead of stdout (atomic: a sibling temp\n"
        "                             file, then a rename)\n"
        "  --password-file <path>    the file's first line is the password\n"
        "  --password-stdin          the password is the first line of stdin\n"
        "  --keep-lines               keep the PDF's own line breaks inside a block\n"
        "  --page-marker              \"--- page N ---\" lines instead of a form feed between pages\n"
        "  --no-page-breaks           no separator between pages at all\n"
        "  --keep-furniture           keep running headers, footers and page numbers\n"
        "  --all-fields               include empty text fields and unchecked boxes too\n"
        "  --no-fields                no form fields in the output, page text only\n"
        "  --heuristic                ignore the document's structure tree even when present (a\n"
        "                             tagged page is otherwise read from its tree when the tree\n"
        "                             covers at least 90%% of the page's text)\n"
        "  --strict                   a page with no text layer is a failure (exit 6)\n"
        "  --quiet                    no informational notes on stderr (errors always print)\n"
        "  --version                  print the version and exit\n"
        "  --help                     print this and exit\n"
        "\n"
        "exit codes: 0 text written; 1 usage; 2 cannot open; 3 password required or wrong;\n"
        "4 unsupported security handler; 5 no text on any requested page; 6 --strict and some\n"
        "requested page had no text; 7 --out could not be written; 130 interrupted (Ctrl+C).\n"
        "\n"
        "usage: megapdf-cli pages <file.pdf> --out <path> [operations]\n"
        "\n"
        "  Page tools (#174), applied in the order given, each on the document as the one\n"
        "  before left it; pages are 1-based and <ranges> is as --pages above.\n"
        "  --rotate <ranges>:<turns>  quarter turns clockwise (negative for anticlockwise)\n"
        "  --delete <ranges>\n"
        "  --move <from>:<to>         the page at <from> ends up as page <to>\n"
        "  --blank <at>[:<w>x<h>]     an empty page before page <at> (one past the end appends);\n"
        "                             Letter unless a size in points is given\n"
        "  --import <other.pdf>[:<ranges>]@<at>\n"
        "                             the other file's pages (all of them, or <ranges>) before page <at>\n"
        "  --extract <ranges>         write only those pages to --out; the document is untouched\n"
        "  --password-file / --password-stdin, --quiet: as for extract\n"
        "\n"
        "  --out is written whole to a sibling temporary file, read back, and only then renamed\n"
        "  into place. Exit codes: 0 written; 1 usage; 2 cannot open; 3 password required or\n"
        "  wrong; 4 unsupported security handler; 7 --out could not be written; 8 the document's\n"
        "  security does not allow the operation; 9 the engine refused it; 130 interrupted.\n");
}

// The password for <file.pdf>, from --password-file's first line or stdin's (never argv):
// see the header comment. The caller zeroes it the moment megapdf_open_file() returns.
bool ReadPasswordSource(const std::string& password_file, bool password_stdin, std::string* password, bool* has_password) {
    *has_password = false;
    if (!password_file.empty()) {
        std::ifstream pf(password_file, std::ios::binary);
        if (!pf.good()) {
            std::fprintf(stderr, "cannot read --password-file %s\n", password_file.c_str());
            return false;
        }
        std::getline(pf, *password);
        if (!password->empty() && password->back() == '\r') password->pop_back();
        *has_password = true;
    } else if (password_stdin) {
        // stdin's first line through the C stream rather than std::cin, so this file
        // needs no <iostream> (see ParseDigits for the GLIBCXX symbol that header costs).
        // Same result as std::getline: up to and excluding the first '\n' or EOF.
        for (int c = std::getc(stdin); c != EOF && c != '\n'; c = std::getc(stdin)) {
            password->push_back(static_cast<char>(c));
        }
        if (!password->empty() && password->back() == '\r') password->pop_back();
        *has_password = true;
    }
    return true;
}

// #145's cancellation pattern, raised from a signal handler: megapdf_cancel_raise() is
// documented safe to call from any thread at any time, so this is the ordinary way every
// long-running CLI over this core would answer Ctrl+C.
megapdf_cancel* g_cancel = nullptr;

void HandleSigint(int) {
    if (g_cancel != nullptr) megapdf_cancel_raise(g_cancel);
}

// --------------------------------------------------------------------------
// --pages parsing and resolution
// --------------------------------------------------------------------------

struct Interval {
    int start1 = 0;   // 1-based, inclusive
    int end1 = 0;      // 1-based, inclusive; -1 means "open end" until resolved
};

bool IsAllDigits(const std::string& s) {
    if (s.empty()) return false;
    for (char c : s) {
        if (c < '0' || c > '9') return false;
    }
    return true;
}

// The value of an all-digits string, saturating at INT_MAX (a page number that large is
// past the end of any document and is reported as such by ResolveIntervals, which is
// what strtol's LONG_MAX clamp used to produce too). Written out rather than calling
// std::strtol or std::atoi (#395): under glibc 2.38+ headers those resolve to
// __isoc23_strtol, a GLIBC_2.38 symbol, so a binary built on Ubuntu 24.04 -- which is
// where CI builds the Linux package -- refused to load on Debian 12 and Ubuntu 22.04
// with "version GLIBC_2.38 not found" although the .deb's Depends allows libc6 2.35.
// For the same reason this file does not include <iostream>: with GCC 13's libstdc++ it
// references std::ios_base_library_init (GLIBCXX_3.4.32), which Debian 12's libstdc++
// 12 does not have. tools/build-linux-app.sh asserts both ceilings on the built binary.
int ParseDigits(const std::string& digits) {
    long long n = 0;
    for (char c : digits) {
        n = n * 10 + (c - '0');
        if (n > 2147483647LL) return 2147483647;
    }
    return static_cast<int>(n);
}

bool ParsePageSpec(const std::string& spec, std::vector<Interval>* out, std::string* err) {
    out->clear();
    std::stringstream ss(spec);
    std::string token;
    while (std::getline(ss, token, ',')) {
        if (token.empty()) { *err = "empty range"; return false; }
        const size_t dash = token.find('-');
        if (dash == std::string::npos) {
            if (!IsAllDigits(token)) { *err = "not a page number: \"" + token + "\""; return false; }
            const int n = ParseDigits(token);
            if (n < 1) { *err = "page numbers start at 1: \"" + token + "\""; return false; }
            out->push_back(Interval{n, n});
            continue;
        }
        const std::string a = token.substr(0, dash);
        const std::string b = token.substr(dash + 1);
        if (!IsAllDigits(a)) { *err = "bad range: \"" + token + "\""; return false; }
        const int start = ParseDigits(a);
        if (start < 1) { *err = "page numbers start at 1: \"" + token + "\""; return false; }
        if (b.empty()) {
            out->push_back(Interval{start, -1});
            continue;
        }
        if (!IsAllDigits(b)) { *err = "bad range: \"" + token + "\""; return false; }
        const int end = ParseDigits(b);
        if (end < start) { *err = "range end before its start: \"" + token + "\""; return false; }
        out->push_back(Interval{start, end});
    }
    if (out->empty()) { *err = "no ranges given"; return false; }
    return true;
}

// Resolves open ends against the document's real page count, rejects a start past the end of
// the document, clamps an end past it (a range like "1-1000" on a 3-page document is 1-3, not
// an error -- only a START past the end names a page that plainly is not there), then sorts and
// merges overlapping or touching intervals into the smallest set of disjoint ranges
// megapdf_write_text is called over.
bool ResolveIntervals(std::vector<Interval>* v, int page_count, std::string* err) {
    for (Interval& iv : *v) {
        if (iv.start1 > page_count) {
            *err = "page " + std::to_string(iv.start1) + " is past the end of the document (" +
                   std::to_string(page_count) + (page_count == 1 ? " page)" : " pages)");
            return false;
        }
        if (iv.end1 < 0 || iv.end1 > page_count) iv.end1 = page_count;
    }
    std::sort(v->begin(), v->end(), [](const Interval& a, const Interval& b) { return a.start1 < b.start1; });
    std::vector<Interval> merged;
    for (const Interval& iv : *v) {
        if (!merged.empty() && iv.start1 <= merged.back().end1 + 1) {
            merged.back().end1 = merged.back().end1 > iv.end1 ? merged.back().end1 : iv.end1;
        } else {
            merged.push_back(iv);
        }
    }
    *v = merged;
    return true;
}

// --------------------------------------------------------------------------
// Options gathered from argv, translated into megapdf_write_options and contract 9's flags.
// --------------------------------------------------------------------------

struct CliOptions {
    bool keep_lines = false;
    int page_break = MEGAPDF_PAGE_BREAK_FORM_FEED;
    bool keep_furniture = false;
    int fields = MEGAPDF_WRITE_FIELDS_FILLED;
    bool heuristic_only = false;
    bool strict = false;
    bool quiet = false;
};

// Finds every "[Page N has no text layer]" placeholder megapdf_write_text() wrote (its own
// PAGE_IMAGE formatting, megapdf_write_text.cpp) and returns the page numbers, in the order
// they appear (which is reading order, so already ascending). This is how this file learns
// which requested pages had no text layer -- see the header comment for why it reads the
// actual written text rather than asking contract 9 again.
std::vector<int> FindTextlessPagesInOutput(const std::string& text) {
    std::vector<int> out;
    const std::string prefix = "[Page ";
    const std::string suffix = " has no text layer]";
    for (size_t at = text.find(prefix); at != std::string::npos;) {
        const size_t digits_start = at + prefix.size();
        size_t digits_end = digits_start;
        while (digits_end < text.size() && text[digits_end] >= '0' && text[digits_end] <= '9') digits_end++;
        if (digits_end > digits_start && text.compare(digits_end, suffix.size(), suffix) == 0) {
            out.push_back(ParseDigits(text.substr(digits_start, digits_end - digits_start)));
            at = text.find(prefix, digits_end + suffix.size());
        } else {
            at = text.find(prefix, digits_start);
        }
    }
    return out;
}

// --------------------------------------------------------------------------
// Output: stdout or an atomic --out (a sibling temp file, then a rename), matching
// AtomicFileWriter's protocol (src/MegaPDF.Core/Services/AtomicFileWriter.cs). Windows paths
// are UTF-8 on this CLI's own command line, same as megapdf_open_file's; unlike the core, this
// file does its own file I/O, so it widens them itself before any Win32 call.
// --------------------------------------------------------------------------

#if defined(_WIN32)
std::wstring Utf8ToWide(const std::string& s) {
    if (s.empty()) return std::wstring();
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
    if (n <= 0) return std::wstring();
    std::wstring w(static_cast<size_t>(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, &w[0], n);
    return w;
}
#endif

std::FILE* OpenForWrite(const std::string& path) {
#if defined(_WIN32)
    return _wfopen(Utf8ToWide(path).c_str(), L"wb");
#else
    return std::fopen(path.c_str(), "wb");
#endif
}

bool RenameOver(const std::string& from, const std::string& to) {
#if defined(_WIN32)
    return MoveFileExW(Utf8ToWide(from).c_str(), Utf8ToWide(to).c_str(),
                       MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != 0;
#else
    return std::rename(from.c_str(), to.c_str()) == 0;
#endif
}

void RemoveFileQuiet(const std::string& path) {
#if defined(_WIN32)
    _wremove(Utf8ToWide(path).c_str());
#else
    std::remove(path.c_str());
#endif
}

void SplitPath(const std::string& path, std::string* dir, std::string* name) {
    const size_t slash = path.find_last_of("/\\");
    if (slash == std::string::npos) {
        *dir = ".";
        *name = path;
    } else {
        *dir = path.substr(0, slash);
        *name = path.substr(slash + 1);
    }
}

// A hidden sibling in the destination's own directory (the rename below only works within one
// file system), named so it never collides with a concurrent run of this same tool.
std::string TempPathFor(const std::string& out_path) {
    std::string dir, name;
    SplitPath(out_path, &dir, &name);
#if defined(_WIN32)
    const unsigned long pid = GetCurrentProcessId();
#else
    const unsigned long pid = static_cast<unsigned long>(getpid());
#endif
    std::ostringstream tmp;
    tmp << dir << "/." << name << "." << pid << "." << static_cast<long long>(std::time(nullptr))
        << ".megapdf-tmp";
    return tmp.str();
}

// megapdf_write_text()'s own callback, appending into the in-memory buffer this file builds the
// whole answer in before writing it anywhere real (see the header comment). Appending to a
// std::string cannot fail short of std::bad_alloc, which is not modelled as a return-0 abort
// here -- it propagates as an ordinary exception, same as any other allocation failure in this
// process.
int WriteToBuffer(void* context, const void* data, size_t size) {
    static_cast<std::string*>(context)->append(static_cast<const char*>(data), size);
    return 1;
}

// The separator megapdf_write_text would place between two ordinary consecutive pages within
// one call, placed here between two DISJOINT requested ranges instead (contract 9 only takes
// one contiguous range per load; see this file's header comment). Mirrors
// megapdf_write_text.cpp's own AppendPageSeparatorText/AppendPageSeparatorMarkdown, which this
// file cannot call directly (they are private to that translation unit).
void AppendSeparator(std::string* buffer, bool is_markdown, int page_break, int next_page1based) {
    if (is_markdown) {
        if (page_break == MEGAPDF_PAGE_BREAK_MARKER) {
            *buffer += "\n<!-- page " + std::to_string(next_page1based) + " -->\n\n";
        } else {
            *buffer += "\n";
        }
        return;
    }
    switch (page_break) {
        case MEGAPDF_PAGE_BREAK_FORM_FEED: *buffer += "\f\n"; break;
        case MEGAPDF_PAGE_BREAK_MARKER: *buffer += "\n--- page " + std::to_string(next_page1based) + " ---\n\n"; break;
        case MEGAPDF_PAGE_BREAK_NONE: *buffer += "\n"; break;
        default: break;
    }
}

// --------------------------------------------------------------------------
// extract
// --------------------------------------------------------------------------

int RunExtract(int argc, char** argv) {
    std::string pdf_path, pages_spec, out_path, password_file, format = "txt";
    bool password_stdin = false;
    CliOptions opt;

    for (int i = 2; i < argc; i++) {
        const std::string a = argv[i];
        auto value = [&](const char* name) -> const char* {
            if (i + 1 >= argc) {
                std::fprintf(stderr, "%s needs a value\n", name);
                return nullptr;
            }
            return argv[++i];
        };
        if (a == "--format") { const char* v = value("--format"); if (v == nullptr) return 1; format = v; }
        else if (a == "--pages") { const char* v = value("--pages"); if (v == nullptr) return 1; pages_spec = v; }
        else if (a == "--out") { const char* v = value("--out"); if (v == nullptr) return 1; out_path = v; }
        else if (a == "--password-file") { const char* v = value("--password-file"); if (v == nullptr) return 1; password_file = v; }
        else if (a == "--password-stdin") password_stdin = true;
        else if (a == "--keep-lines") opt.keep_lines = true;
        else if (a == "--page-marker") opt.page_break = MEGAPDF_PAGE_BREAK_MARKER;
        else if (a == "--no-page-breaks") opt.page_break = MEGAPDF_PAGE_BREAK_NONE;
        else if (a == "--keep-furniture") opt.keep_furniture = true;
        else if (a == "--all-fields") opt.fields = MEGAPDF_WRITE_FIELDS_ALL;
        else if (a == "--no-fields") opt.fields = MEGAPDF_WRITE_FIELDS_NONE;
        else if (a == "--heuristic") opt.heuristic_only = true;
        else if (a == "--strict") opt.strict = true;
        else if (a == "--quiet") opt.quiet = true;
        else if (a == "--version") { std::printf("%s\n", kVersion); return 0; }
        else if (a == "--help") { PrintUsage(stdout); return 0; }
        else if (a.size() > 1 && a[0] == '-') { std::fprintf(stderr, "unknown option: %s\n", a.c_str()); return 1; }
        else if (pdf_path.empty()) pdf_path = a;
        else { std::fprintf(stderr, "unexpected argument: %s\n", a.c_str()); return 1; }
    }

    if (pdf_path.empty()) { std::fprintf(stderr, "extract needs a PDF path\n\n"); PrintUsage(stderr); return 1; }
    int format_value = MEGAPDF_WRITE_TEXT;
    if (format == "md") {
        format_value = MEGAPDF_WRITE_MARKDOWN;
    } else if (format != "txt") {
        std::fprintf(stderr, "unknown --format: %s\n", format.c_str());
        return 1;
    }
    if (!password_file.empty() && password_stdin) {
        std::fprintf(stderr, "--password-file and --password-stdin are mutually exclusive\n");
        return 1;
    }

    std::vector<Interval> intervals;
    if (!pages_spec.empty()) {
        std::string err;
        if (!ParsePageSpec(pages_spec, &intervals, &err)) {
            std::fprintf(stderr, "--pages: %s\n", err.c_str());
            return 1;
        }
    }

    // The password: read before anything else touches the document, zeroed the moment
    // megapdf_open_file() has consumed it, and never placed on argv.
    std::string password;
    bool has_password = false;
    if (!ReadPasswordSource(password_file, password_stdin, &password, &has_password)) return 1;

    megapdf_cancel* cancel = megapdf_cancel_new();
    g_cancel = cancel;
    std::signal(SIGINT, HandleSigint);

    auto cleanup = [&]() {
        g_cancel = nullptr;
        megapdf_cancel_free(cancel);
    };

    megapdf_document* doc = megapdf_open_file(pdf_path.c_str(), has_password ? password.c_str() : nullptr);
    if (has_password) {
        std::fill(password.begin(), password.end(), '\0');
        password.clear();
    }
    if (doc == nullptr) {
        const unsigned int err = megapdf_last_error();
        std::fprintf(stderr, "cannot open %s: %s\n", pdf_path.c_str(), megapdf_last_error_message());
        cleanup();
        if (err == static_cast<unsigned int>(FPDF_ERR_PASSWORD)) return 3;
        if (err == static_cast<unsigned int>(FPDF_ERR_SECURITY)) return 4;
        return 2;
    }

    const int page_count = megapdf_page_count(doc);
    if (page_count <= 0) {
        std::fprintf(stderr, "cannot open %s: the document has no pages\n", pdf_path.c_str());
        megapdf_close(doc);
        cleanup();
        return 2;
    }
    if (intervals.empty()) intervals.push_back(Interval{1, page_count});
    {
        std::string err;
        if (!ResolveIntervals(&intervals, page_count, &err)) {
            std::fprintf(stderr, "--pages: %s\n", err.c_str());
            megapdf_close(doc);
            cleanup();
            return 1;
        }
    }

    int total_requested = 0;
    for (const Interval& iv : intervals) total_requested += iv.end1 - iv.start1 + 1;

    megapdf_write_options wopt{};
    wopt.keep_lines = opt.keep_lines ? 1 : 0;
    wopt.page_break = opt.page_break;
    wopt.keep_furniture = opt.keep_furniture ? 1 : 0;
    wopt.fields = opt.fields;
    wopt.heuristic_only = opt.heuristic_only ? 1 : 0;

    // The whole answer, built in memory first (see the header comment for why): one
    // megapdf_write_text() call per interval, with this file's own separator appended between
    // disjoint intervals exactly where the writer would put one between two ordinary
    // consecutive pages.
    std::string buffered;
    bool cancelled = false;
    bool write_failed = false;
    for (size_t idx = 0; idx < intervals.size(); idx++) {
        const Interval& iv = intervals[idx];
        const int first0 = iv.start1 - 1;
        const int count = iv.end1 - iv.start1 + 1;
        const int rc =
            megapdf_write_text(doc, first0, count, format_value, &wopt, WriteToBuffer, &buffered, cancel);
        if (rc == MEGAPDF_ERR_CANCELLED) { cancelled = true; break; }
        if (rc < 0) { write_failed = true; break; }
        if (idx + 1 < intervals.size()) {
            AppendSeparator(&buffered, format_value == MEGAPDF_WRITE_MARKDOWN, opt.page_break,
                            intervals[idx + 1].start1);
        }
    }
    megapdf_close(doc);
    cleanup();

    if (cancelled) return 130;
    if (write_failed) {
        std::fprintf(stderr, "internal error extracting %s\n", pdf_path.c_str());
        return 7;
    }

    const std::vector<int> textless_pages = FindTextlessPagesInOutput(buffered);
    if (!opt.quiet) {
        for (int p : textless_pages) std::fprintf(stderr, "page %d: no text layer\n", p);
        if (!textless_pages.empty()) {
            std::fprintf(stderr, "no text layer on %zu of %d pages; MegaPDF does not do OCR\n",
                        textless_pages.size(), total_requested);
        }
    }

    // Only now does anything touch the real destination: a cancelled or failed run above never
    // created an --out temp file at all, let alone one that needs cleaning up.
#if defined(_WIN32)
    _setmode(_fileno(stdout), _O_BINARY);   // LF stays LF; the writer never emits CRLF itself.
#endif
    const bool using_out_file = !out_path.empty();
    if (using_out_file) {
        const std::string temp_path = TempPathFor(out_path);
        std::FILE* f = OpenForWrite(temp_path);
        bool ok = f != nullptr;
        if (ok && !buffered.empty() && std::fwrite(buffered.data(), 1, buffered.size(), f) != buffered.size()) {
            ok = false;
        }
        if (f != nullptr) std::fclose(f);
        if (ok) ok = RenameOver(temp_path, out_path);
        if (!ok) {
            RemoveFileQuiet(temp_path);
            std::fprintf(stderr, "cannot write %s\n", out_path.c_str());
            return 7;
        }
    } else if (!buffered.empty()) {
        // Explicit fflush (Windows CI investigation, 2026-09-26): the `return` from main() a
        // few lines below relies on the C runtime's own exit-time stream flush to get these
        // bytes from stdio's buffer to the OS file object. On windows-latest Debug builds that
        // reliance was observed to silently lose exactly this write -- the single, short,
        // well-under-buffer-size fwrite() below for a page with no text layer (megapdf-cli
        // smoke's scan.pdf fixture): fwrite() itself reported success and the exit code (derived
        // from `buffered`, scanned further down) came out right, but the real destination ended
        // up with 0 bytes, on Windows only, and only sometimes -- interleaving extra stderr
        // writes while diagnosing this made it stop reproducing, which points at a timing-
        // dependent loss of the CRT's own atexit flush rather than a content bug. Flushing
        // explicitly here removes the dependence on that timing (and on process-exit cleanup
        // generally) instead of chasing why the implicit flush is sometimes skipped.
        const size_t wrote = std::fwrite(buffered.data(), 1, buffered.size(), stdout);
        const bool flushed = std::fflush(stdout) == 0;
        if (wrote != buffered.size() || !flushed) {
            std::fprintf(stderr, "cannot write output\n");
            return 7;
        }
    }

    if (total_requested > 0 && static_cast<int>(textless_pages.size()) == total_requested) return 5;
    if (opt.strict && !textless_pages.empty()) return 6;
    return 0;
}

// --------------------------------------------------------------------------
// pages (#174): a thin shell over contract 10. One operation per option, applied in argv
// order; the result saved through the same write-whole, read-back, rename discipline the
// apps' saves use, or written by megapdf_pages_extract() itself, which does the same.
// --------------------------------------------------------------------------

struct PageOp {
    enum Kind { Rotate, Delete, Move, Blank, Import, Extract } kind;
    std::string ranges;     // Rotate, Delete, Extract, Import (may be empty: every page)
    int a = 0, b = 0;       // Rotate: turns; Move: from, to; Blank: at; Import: at
    double w = 612, h = 792;
    std::string path;       // Import
};

// "<ranges>" against a page count, as a 0-based list in ascending order without repeats.
bool ResolvePages(const std::string& spec, int page_count, std::vector<int>* out, std::string* err) {
    std::vector<Interval> intervals;
    if (!ParsePageSpec(spec, &intervals, err) || !ResolveIntervals(&intervals, page_count, err)) return false;
    out->clear();
    for (const Interval& iv : intervals)
        for (int p = iv.start1; p <= iv.end1; p++) out->push_back(p - 1);
    return true;
}

bool ParseIntArg(const std::string& s, int* out) {
    std::string digits = s;
    bool negative = false;
    if (!digits.empty() && digits[0] == '-') { negative = true; digits.erase(0, 1); }
    if (!IsAllDigits(digits)) return false;
    *out = negative ? -ParseDigits(digits) : ParseDigits(digits);
    return true;
}

int WriteToFile(void* context, const void* data, size_t size) {
    return std::fwrite(data, 1, size, static_cast<std::FILE*>(context)) == size ? 1 : 0;
}

int RunPages(int argc, char** argv) {
    std::string pdf_path, out_path, password_file;
    bool password_stdin = false, quiet = false;
    std::vector<PageOp> ops;

    for (int i = 2; i < argc; i++) {
        const std::string a = argv[i];
        auto value = [&](const char* name) -> const char* {
            if (i + 1 >= argc) {
                std::fprintf(stderr, "%s needs a value\n", name);
                return nullptr;
            }
            return argv[++i];
        };
        auto bad = [&](const char* what) { std::fprintf(stderr, "%s: %s\n", a.c_str(), what); return 1; };
        if (a == "--out") { const char* v = value("--out"); if (v == nullptr) return 1; out_path = v; }
        else if (a == "--password-file") { const char* v = value("--password-file"); if (v == nullptr) return 1; password_file = v; }
        else if (a == "--password-stdin") password_stdin = true;
        else if (a == "--quiet") quiet = true;
        else if (a == "--rotate") {
            const char* v = value("--rotate"); if (v == nullptr) return 1;
            const std::string s = v;
            const size_t colon = s.rfind(':');
            PageOp op{PageOp::Rotate};
            if (colon == std::string::npos || !ParseIntArg(s.substr(colon + 1), &op.a)) return bad("expected <ranges>:<turns>");
            op.ranges = s.substr(0, colon);
            ops.push_back(op);
        }
        else if (a == "--delete") { const char* v = value("--delete"); if (v == nullptr) return 1; PageOp op{PageOp::Delete}; op.ranges = v; ops.push_back(op); }
        else if (a == "--extract") { const char* v = value("--extract"); if (v == nullptr) return 1; PageOp op{PageOp::Extract}; op.ranges = v; ops.push_back(op); }
        else if (a == "--move") {
            const char* v = value("--move"); if (v == nullptr) return 1;
            const std::string s = v;
            const size_t colon = s.find(':');
            PageOp op{PageOp::Move};
            if (colon == std::string::npos || !ParseIntArg(s.substr(0, colon), &op.a) || !ParseIntArg(s.substr(colon + 1), &op.b) || op.a < 1 || op.b < 1)
                return bad("expected <from>:<to>");
            ops.push_back(op);
        }
        else if (a == "--blank") {
            const char* v = value("--blank"); if (v == nullptr) return 1;
            const std::string s = v;
            const size_t colon = s.find(':');
            PageOp op{PageOp::Blank};
            if (!ParseIntArg(s.substr(0, colon), &op.a) || op.a < 1) return bad("expected <at>[:<w>x<h>]");
            if (colon != std::string::npos) {
                const std::string size = s.substr(colon + 1);
                const size_t x = size.find('x');
                int w = 0, h = 0;
                if (x == std::string::npos || !ParseIntArg(size.substr(0, x), &w) || !ParseIntArg(size.substr(x + 1), &h) || w < 1 || h < 1)
                    return bad("expected <at>:<w>x<h> in whole points");
                op.w = w;
                op.h = h;
            }
            ops.push_back(op);
        }
        else if (a == "--import") {
            const char* v = value("--import"); if (v == nullptr) return 1;
            const std::string s = v;
            const size_t at = s.rfind('@');
            PageOp op{PageOp::Import};
            if (at == std::string::npos || !ParseIntArg(s.substr(at + 1), &op.a) || op.a < 1) return bad("expected <other.pdf>[:<ranges>]@<at>");
            std::string spec = s.substr(0, at);
            // A colon after the last path separator (and not a Windows drive letter's) splits off the ranges.
            const size_t slash = spec.find_last_of("/\\");
            const size_t colon = spec.rfind(':');
            if (colon != std::string::npos && (slash == std::string::npos || colon > slash) && colon != 1) {
                op.ranges = spec.substr(colon + 1);
                spec = spec.substr(0, colon);
            }
            if (spec.empty()) return bad("expected <other.pdf>[:<ranges>]@<at>");
            op.path = spec;
            ops.push_back(op);
        }
        else if (a == "--version") { std::printf("%s\n", kVersion); return 0; }
        else if (a == "--help") { PrintUsage(stdout); return 0; }
        else if (a.size() > 1 && a[0] == '-') { std::fprintf(stderr, "unknown option: %s\n", a.c_str()); return 1; }
        else if (pdf_path.empty()) pdf_path = a;
        else { std::fprintf(stderr, "unexpected argument: %s\n", a.c_str()); return 1; }
    }
    if (pdf_path.empty()) { std::fprintf(stderr, "pages needs a PDF path\n\n"); PrintUsage(stderr); return 1; }
    if (out_path.empty()) { std::fprintf(stderr, "pages needs --out\n\n"); PrintUsage(stderr); return 1; }
    if (ops.empty()) { std::fprintf(stderr, "pages needs at least one operation\n\n"); PrintUsage(stderr); return 1; }
    if (!password_file.empty() && password_stdin) {
        std::fprintf(stderr, "--password-file and --password-stdin are mutually exclusive\n");
        return 1;
    }
    size_t extracts = 0;
    for (const PageOp& op : ops) if (op.kind == PageOp::Extract) extracts++;
    if (extracts > 1 || (extracts == 1 && ops.back().kind != PageOp::Extract)) {
        std::fprintf(stderr, "--extract writes --out itself, so it must be the last operation and the only extract\n");
        return 1;
    }

    std::string password;
    bool has_password = false;
    if (!ReadPasswordSource(password_file, password_stdin, &password, &has_password)) return 1;

    megapdf_cancel* cancel = megapdf_cancel_new();
    g_cancel = cancel;
    std::signal(SIGINT, HandleSigint);
    auto cleanup = [&]() {
        g_cancel = nullptr;
        megapdf_cancel_free(cancel);
    };

    megapdf_document* doc = megapdf_open_file(pdf_path.c_str(), has_password ? password.c_str() : nullptr);
    if (has_password) {
        std::fill(password.begin(), password.end(), '\0');
        password.clear();
    }
    if (doc == nullptr) {
        const unsigned int err = megapdf_last_error();
        std::fprintf(stderr, "cannot open %s: %s\n", pdf_path.c_str(), megapdf_last_error_message());
        cleanup();
        if (err == static_cast<unsigned int>(FPDF_ERR_PASSWORD)) return 3;
        if (err == static_cast<unsigned int>(FPDF_ERR_SECURITY)) return 4;
        return 2;
    }
    auto fail = [&](int code, const std::string& what) {
        std::fprintf(stderr, "%s: %s\n", what.c_str(), megapdf_last_error_message());
        megapdf_close(doc);
        cleanup();
        return code;
    };
    auto status_code = [](int rc) { return rc == MEGAPDF_ERR_RESTRICTED ? 8 : rc == MEGAPDF_ERR_FILE ? 7 : 9; };

    bool extracted = false;
    int written = 0;   // pages in --out
    for (const PageOp& op : ops) {
        const int page_count = megapdf_page_count(doc);
        std::string err;
        std::vector<int> pages;
        switch (op.kind) {
            case PageOp::Rotate: {
                if (!ResolvePages(op.ranges, page_count, &pages, &err)) return fail(1, "--rotate: " + err);
                for (int p : pages) {
                    const int rc = megapdf_page_rotate(doc, p, op.a);
                    if (rc != MEGAPDF_OK) return fail(status_code(rc), "cannot rotate page " + std::to_string(p + 1));
                }
                break;
            }
            case PageOp::Delete: {
                if (!ResolvePages(op.ranges, page_count, &pages, &err)) return fail(1, "--delete: " + err);
                for (size_t k = pages.size(); k-- > 0;) {   // descending, so each index still names its page
                    const int rc = megapdf_page_delete(doc, pages[k], nullptr);
                    if (rc != MEGAPDF_OK) return fail(status_code(rc), "cannot delete page " + std::to_string(pages[k] + 1));
                }
                break;
            }
            case PageOp::Move: {
                if (op.a > page_count || op.b > page_count) return fail(1, "--move: a page past the end of the document");
                const int rc = megapdf_page_move(doc, op.a - 1, op.b - 1);
                if (rc != MEGAPDF_OK) return fail(status_code(rc), "cannot move page " + std::to_string(op.a));
                break;
            }
            case PageOp::Blank: {
                if (op.a > page_count + 1) return fail(1, "--blank: a place past the end of the document");
                const int rc = megapdf_page_insert_blank(doc, op.a - 1, op.w, op.h);
                if (rc != MEGAPDF_OK) return fail(status_code(rc), "cannot insert a blank page");
                break;
            }
            case PageOp::Import: {
                if (op.a > page_count + 1) return fail(1, "--import: a place past the end of the document");
                if (!op.ranges.empty()) {
                    // The other file's page count is only known to the core; a start past its end
                    // comes back as MEGAPDF_ERR_ARGUMENT below. Open ends are resolved generously
                    // here and clamped by the core's own range check.
                    std::vector<Interval> intervals;
                    if (!ParsePageSpec(op.ranges, &intervals, &err)) return fail(1, "--import: " + err);
                    // An open-ended range needs the other document's page count: import it whole
                    // when the spec is a single "N-" from page 1, otherwise resolve against a
                    // probe open of the other file.
                    megapdf_document* other = megapdf_open_file(op.path.c_str(), nullptr);
                    const int other_count = other != nullptr ? megapdf_page_count(other) : 0;
                    megapdf_close(other);
                    if (other_count <= 0) return fail(7, "cannot open " + op.path);
                    if (!ResolvePages(op.ranges, other_count, &pages, &err)) return fail(1, "--import: " + err);
                }
                int imported = 0;
                const int rc = megapdf_pages_import(doc, op.path.c_str(), nullptr, pages.empty() ? nullptr : pages.data(), pages.size(),
                                                    op.a - 1, &imported);
                if (rc != MEGAPDF_OK) return fail(status_code(rc), "cannot import from " + op.path);
                if (!quiet) std::fprintf(stderr, "imported %d page%s from %s\n", imported, imported == 1 ? "" : "s", op.path.c_str());
                break;
            }
            case PageOp::Extract: {
                if (!ResolvePages(op.ranges, page_count, &pages, &err)) return fail(1, "--extract: " + err);
                const int rc = megapdf_pages_extract(doc, pages.data(), pages.size(), out_path.c_str(), cancel);
                if (rc == MEGAPDF_ERR_CANCELLED) { megapdf_close(doc); cleanup(); return 130; }
                if (rc != MEGAPDF_OK) return fail(status_code(rc), "cannot extract to " + out_path);
                extracted = true;
                written = static_cast<int>(pages.size());
                break;
            }
        }
    }

    if (!extracted) {
        // The apps' save discipline: the whole document to a sibling temporary file, read back
        // through the same open the apps use, and only then the rename into place.
        const std::string temp_path = TempPathFor(out_path);
        std::FILE* f = OpenForWrite(temp_path);
        if (f == nullptr) return fail(7, "cannot write " + out_path);
        written = megapdf_page_count(doc);
        const int rc = megapdf_save(doc, WriteToFile, f);
        const bool closed = std::fclose(f) == 0;
        if (rc != MEGAPDF_OK || !closed) {
            RemoveFileQuiet(temp_path);
            return fail(rc == MEGAPDF_ERR_REDACT ? 9 : 7, "cannot write " + out_path);
        }
        megapdf_document* check = megapdf_open_file(temp_path.c_str(), nullptr);
        const bool reads_back = check != nullptr && megapdf_page_count(check) == megapdf_page_count(doc);
        megapdf_close(check);
        if (!reads_back || !RenameOver(temp_path, out_path)) {
            RemoveFileQuiet(temp_path);
            std::fprintf(stderr, "cannot write %s: %s\n", out_path.c_str(),
                         reads_back ? "the file could not be renamed into place" : "the written file did not read back");
            megapdf_close(doc);
            cleanup();
            return 7;
        }
    }
    if (!quiet) std::fprintf(stderr, "%d page%s written to %s\n", written, written == 1 ? "" : "s", out_path.c_str());
    megapdf_close(doc);
    cleanup();
    return 0;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        PrintUsage(stderr);
        return 1;
    }
    if (std::strcmp(argv[1], "--version") == 0) { std::printf("%s\n", kVersion); return 0; }
    if (std::strcmp(argv[1], "--help") == 0) { PrintUsage(stdout); return 0; }
    if (std::strcmp(argv[1], "pages") == 0) return RunPages(argc, argv);
    if (std::strcmp(argv[1], "extract") != 0) {
        std::fprintf(stderr, "unknown command: %s\n\n", argv[1]);
        PrintUsage(stderr);
        return 1;
    }
    return RunExtract(argc, argv);
}
