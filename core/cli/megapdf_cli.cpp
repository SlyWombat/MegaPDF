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
// The per-page "page N: no text layer" stderr notes need per-page detail megapdf_write_text's
// aggregate return value does not carry, so this file makes its own lightweight
// megapdf_structure_load() pass first (same flags the write derives) purely to find which
// requested pages have no non-PAGE_IMAGE block -- the same test megapdf_write_text itself makes
// internally to decide what a "page with text" is (see megapdf_write_text.cpp's own comment).
#include "megapdf_core.h"
#include "fpdfview.h"   // FPDF_ERR_* constants only: header macros, no pdfium link needed.

#include <algorithm>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <fstream>
#include <iostream>
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

// Bumped by hand; megapdf-cli ships from #356's platform archives/packages, not from this
// project's own .csproj version numbers (a native tool has no .NET assembly to match).
constexpr const char* kVersion = "megapdf-cli 2.2.0-dev";

void PrintUsage(std::FILE* out) {
    std::fprintf(out,
        "usage: megapdf-cli extract <file.pdf> [options]\n"
        "\n"
        "  --format txt               output format (only txt is implemented; md is #357)\n"
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
        "  --heuristic                ignore the document's structure tree even when present\n"
        "  --strict                   a page with no text layer is a failure (exit 6)\n"
        "  --quiet                    no informational notes on stderr (errors always print)\n"
        "  --version                  print the version and exit\n"
        "  --help                     print this and exit\n"
        "\n"
        "exit codes: 0 text written; 1 usage; 2 cannot open; 3 password required or wrong;\n"
        "4 unsupported security handler; 5 no text on any requested page; 6 --strict and some\n"
        "requested page had no text; 7 --out could not be written; 130 interrupted (Ctrl+C).\n");
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

bool ParsePageSpec(const std::string& spec, std::vector<Interval>* out, std::string* err) {
    out->clear();
    std::stringstream ss(spec);
    std::string token;
    while (std::getline(ss, token, ',')) {
        if (token.empty()) { *err = "empty range"; return false; }
        const size_t dash = token.find('-');
        if (dash == std::string::npos) {
            if (!IsAllDigits(token)) { *err = "not a page number: \"" + token + "\""; return false; }
            const long n = std::strtol(token.c_str(), nullptr, 10);
            if (n < 1) { *err = "page numbers start at 1: \"" + token + "\""; return false; }
            out->push_back(Interval{static_cast<int>(n), static_cast<int>(n)});
            continue;
        }
        const std::string a = token.substr(0, dash);
        const std::string b = token.substr(dash + 1);
        if (!IsAllDigits(a)) { *err = "bad range: \"" + token + "\""; return false; }
        const long start = std::strtol(a.c_str(), nullptr, 10);
        if (start < 1) { *err = "page numbers start at 1: \"" + token + "\""; return false; }
        if (b.empty()) {
            out->push_back(Interval{static_cast<int>(start), -1});
            continue;
        }
        if (!IsAllDigits(b)) { *err = "bad range: \"" + token + "\""; return false; }
        const long end = std::strtol(b.c_str(), nullptr, 10);
        if (end < start) { *err = "range end before its start: \"" + token + "\""; return false; }
        out->push_back(Interval{static_cast<int>(start), static_cast<int>(end)});
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

unsigned int StructureFlagsFor(const CliOptions& o) {
    unsigned int f = MEGAPDF_STRUCTURE_DEFAULT;
    if (o.heuristic_only) f |= MEGAPDF_STRUCTURE_HEURISTIC_ONLY;
    if (o.keep_furniture) f |= MEGAPDF_STRUCTURE_KEEP_FURNITURE;
    if (o.fields == MEGAPDF_WRITE_FIELDS_ALL) f |= MEGAPDF_STRUCTURE_ALL_FIELDS;
    return f;
}

// One pass over the requested intervals, purely to learn which pages have no text layer: the
// same PAGE_IMAGE test megapdf_write_text makes internally, exposed here because its own return
// value is only the aggregate count. Returns false only on cancellation.
bool FindTextlessPages(megapdf_document* doc, const std::vector<Interval>& intervals, unsigned int structure_flags,
                       const megapdf_cancel* cancel, std::vector<int>* textless_out, int* total_out) {
    textless_out->clear();
    *total_out = 0;
    for (const Interval& iv : intervals) {
        const int first0 = iv.start1 - 1;
        const int count = iv.end1 - iv.start1 + 1;
        *total_out += count;
        megapdf_structure* s = megapdf_structure_load(doc, first0, count, structure_flags, cancel);
        if (s == nullptr) {
            if (megapdf_last_error() == static_cast<unsigned int>(MEGAPDF_ERR_CANCELLED)) return false;
            // Should not happen once the document opened and the range was validated; err on
            // the side of reporting every page in the range as textless rather than silently
            // skip it.
            for (int p = iv.start1; p <= iv.end1; p++) textless_out->push_back(p);
            continue;
        }
        std::vector<char> has_text(static_cast<size_t>(count), 0);
        const size_t n = megapdf_block_count(s);
        for (size_t i = 0; i < n; i++) {
            megapdf_block b{};
            if (megapdf_block_get(s, i, &b) != MEGAPDF_OK) continue;
            const int rel = b.page - first0;
            if (rel < 0 || rel >= count) continue;
            if (b.kind != MEGAPDF_BLOCK_PAGE_IMAGE) has_text[static_cast<size_t>(rel)] = 1;
        }
        megapdf_structure_free(s);
        for (int rel = 0; rel < count; rel++) {
            if (!has_text[static_cast<size_t>(rel)]) textless_out->push_back(iv.start1 + rel);
        }
    }
    return true;
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

struct Sink {
    std::FILE* f = nullptr;
    bool ok = true;
};

int WriteToSink(void* context, const void* data, size_t size) {
    auto* s = static_cast<Sink*>(context);
    if (!s->ok) return 0;
    if (size == 0) return 1;
    if (std::fwrite(data, 1, size, s->f) != size) {
        s->ok = false;
        return 0;
    }
    return 1;
}

// The separator megapdf_write_text would place between two ordinary consecutive pages within
// one call, placed here between two DISJOINT requested ranges instead (contract 9 only takes
// one contiguous range per load; see this file's header comment).
bool WriteSeparator(Sink* sink, int page_break, int next_page1based) {
    std::string sep;
    switch (page_break) {
        case MEGAPDF_PAGE_BREAK_FORM_FEED: sep = "\f\n"; break;
        case MEGAPDF_PAGE_BREAK_MARKER: sep = "\n--- page " + std::to_string(next_page1based) + " ---\n\n"; break;
        case MEGAPDF_PAGE_BREAK_NONE: sep = "\n"; break;
        default: return true;
    }
    return WriteToSink(sink, sep.data(), sep.size()) != 0;
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
    if (format != "txt") {
        if (format == "md") std::fprintf(stderr, "--format md is not implemented yet (#357)\n");
        else std::fprintf(stderr, "unknown --format: %s\n", format.c_str());
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
    if (!password_file.empty()) {
        std::ifstream pf(password_file, std::ios::binary);
        if (!pf.good()) {
            std::fprintf(stderr, "cannot read --password-file %s\n", password_file.c_str());
            return 1;
        }
        std::getline(pf, password);
        if (!password.empty() && password.back() == '\r') password.pop_back();
        has_password = true;
    } else if (password_stdin) {
        std::getline(std::cin, password);
        if (!password.empty() && password.back() == '\r') password.pop_back();
        has_password = true;
    }

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

    const unsigned int structure_flags = StructureFlagsFor(opt);
    std::vector<int> textless_pages;
    int total_requested = 0;
    if (!FindTextlessPages(doc, intervals, structure_flags, cancel, &textless_pages, &total_requested)) {
        megapdf_close(doc);
        cleanup();
        return 130;
    }

    if (!opt.quiet) {
        for (int p : textless_pages) std::fprintf(stderr, "page %d: no text layer\n", p);
        if (!textless_pages.empty()) {
            std::fprintf(stderr, "no text layer on %zu of %d pages; MegaPDF does not do OCR\n",
                        textless_pages.size(), total_requested);
        }
    }

#if defined(_WIN32)
    _setmode(_fileno(stdout), _O_BINARY);   // LF stays LF; the writer never emits CRLF itself.
#endif
    Sink sink;
    const bool using_out_file = !out_path.empty();
    std::string temp_path;
    if (using_out_file) {
        temp_path = TempPathFor(out_path);
        sink.f = OpenForWrite(temp_path);
        if (sink.f == nullptr) {
            std::fprintf(stderr, "cannot write %s\n", out_path.c_str());
            megapdf_close(doc);
            cleanup();
            return 7;
        }
    } else {
        sink.f = stdout;
    }

    megapdf_write_options wopt{};
    wopt.keep_lines = opt.keep_lines ? 1 : 0;
    wopt.page_break = opt.page_break;
    wopt.keep_furniture = opt.keep_furniture ? 1 : 0;
    wopt.fields = opt.fields;
    wopt.heuristic_only = opt.heuristic_only ? 1 : 0;

    bool cancelled = false;
    bool write_failed = false;
    for (size_t idx = 0; idx < intervals.size(); idx++) {
        const Interval& iv = intervals[idx];
        const int first0 = iv.start1 - 1;
        const int count = iv.end1 - iv.start1 + 1;
        const int rc = megapdf_write_text(doc, first0, count, MEGAPDF_WRITE_TEXT, &wopt, WriteToSink, &sink, cancel);
        if (rc == MEGAPDF_ERR_CANCELLED) { cancelled = true; break; }
        if (rc < 0) { write_failed = true; break; }
        if (idx + 1 < intervals.size() && !WriteSeparator(&sink, opt.page_break, intervals[idx + 1].start1)) {
            write_failed = true;
            break;
        }
    }
    if (!sink.ok) write_failed = true;

    if (using_out_file && sink.f != nullptr) std::fclose(sink.f);
    megapdf_close(doc);
    cleanup();

    if (cancelled) {
        if (using_out_file) RemoveFileQuiet(temp_path);
        return 130;
    }
    if (write_failed) {
        if (using_out_file) RemoveFileQuiet(temp_path);
        std::fprintf(stderr, "cannot write %s\n", using_out_file ? out_path.c_str() : "output");
        return 7;
    }
    if (using_out_file && !RenameOver(temp_path, out_path)) {
        RemoveFileQuiet(temp_path);
        std::fprintf(stderr, "cannot write %s\n", out_path.c_str());
        return 7;
    }

    if (total_requested > 0 && static_cast<int>(textless_pages.size()) == total_requested) return 5;
    if (opt.strict && !textless_pages.empty()) return 6;
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
    if (std::strcmp(argv[1], "extract") != 0) {
        std::fprintf(stderr, "unknown command: %s\n\n", argv[1]);
        PrintUsage(stderr);
        return 1;
    }
    return RunExtract(argc, argv);
}
