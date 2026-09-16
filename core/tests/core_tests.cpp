// Tests for the shared engine core, run where the code lives (#104, ADR-003).
//
// The per-platform suites remain the parity gate; this target exists so a
// divergence in `core/` fails on the same commit, on every CI OS, with an
// AddressSanitizer build on Linux, instead of three jobs later. Deliberately
// framework-free: a test is a function, a failure is a line on stderr and a
// non-zero exit.
//
// Usage: megapdf_core_tests <fixtures-dir> <schematic.pdf> <text_runs.txt>
//   fixtures-dir   output of tools/gen_test_fixtures.py
//   schematic.pdf  tests/MegaPDF.Core.Tests/Fixtures/microbit-v2-schematic.pdf (#98)
//   text_runs.txt  core/tests/expected/text_runs.txt — the desktop engine's text
//                  runs and lines for every fixture, captured before #106 moved
//                  that contract into the core (`MegaPDF.Stress dump-text`)

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <functional>
#include <mutex>
#include <thread>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iterator>
#include <map>
#include <sstream>
#include <string>
#include <vector>
#include <cstdint>
#include <filesystem>
#include <tuple>

#if defined(_WIN32)
#include <process.h>
#ifndef NOMINMAX
#define NOMINMAX   // the tests call std::min and std::max
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN   // no <rpcndr.h>, whose `small` macro breaks a variable name here
#endif
#include <windows.h>   // ReplaceFileW (#147)
#else
#include <fcntl.h>
#include <unistd.h>
#endif

#include "megapdf_core.h"
#include "megapdf_core_testing.h"
// PDFium itself, as the oracle for what the core reads (#149). On Windows its headers include
// <windows.h>: keep out the min/max macros and the RPC headers' `small`.
#ifdef _WIN32
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#endif
#include "fpdf_edit.h"
#include "fpdf_text.h"
#include "fpdfview.h"

// How many MegaPDF patches the linked PDFium carries (core/CMakeLists.txt reads VERSION).
#ifndef MEGAPDF_PDFIUM_PATCHES
#define MEGAPDF_PDFIUM_PATCHES 0
#endif

namespace {

int failures = 0;

void check(bool ok, const char* what, const std::string& detail = "") {
    if (ok) return;
    failures++;
    std::fprintf(stderr, "FAIL: %s%s%s\n", what, detail.empty() ? "" : " — ", detail.c_str());
}

void check(bool ok, const std::string& what, const std::string& detail = "") {
    check(ok, what.c_str(), detail);
}

// Not "near": that is a legacy macro in <windef.h>, which pdfium's headers pull in on Windows.
bool close_to(double a, double b, double tol = 0.5) { return std::fabs(a - b) <= tol; }

std::vector<unsigned char> read_file(const std::string& path) {
    std::ifstream in(path, std::ios::binary);
    return std::vector<unsigned char>((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
}

// The core owns documents (#105): the test hands over bytes like a binding would.
struct Doc {
    megapdf_document* doc = nullptr;
    explicit Doc(const std::string& path, const char* password = nullptr) {
        auto bytes = read_file(path);
        doc = megapdf_open(bytes.data(), bytes.size(), password);
        // `bytes` dies here — the core must have copied them.
    }
    ~Doc() { megapdf_close(doc); }
};

struct Page {
    megapdf_page* page = nullptr;
    Page(megapdf_document* d, int i) { page = d ? megapdf_load_page(d, i) : nullptr; }
    ~Page() { megapdf_close_page(page); }
};

std::vector<megapdf_rect> squares(const megapdf_page* page) {
    const size_t n = megapdf_detect_checkbox_squares(page, nullptr, 0);
    std::vector<megapdf_rect> out(n);
    if (n > 0) megapdf_detect_checkbox_squares(page, out.data(), n);
    return out;
}

std::vector<unsigned short> utf16(const char* ascii) {
    std::vector<unsigned short> out;
    for (const char* c = ascii; *c; c++) out.push_back(static_cast<unsigned short>(static_cast<unsigned char>(*c)));
    out.push_back(0);
    return out;
}

struct Match {
    std::vector<megapdf_rect> rects;
};

// Decodes the packed stream the way each binding does.
std::vector<Match> search(const megapdf_page* page, const char* term) {
    auto t = utf16(term);
    const size_t n = megapdf_search_page(page, t.data(), nullptr, 0);
    std::vector<double> packed(n);
    if (n > 0) {
        const size_t again = megapdf_search_page(page, t.data(), packed.data(), n);
        check(again == n, "search fill returns the same total as the count pass",
              std::to_string(again) + " vs " + std::to_string(n));
    }
    std::vector<Match> matches;
    size_t i = 0;
    while (i < packed.size()) {
        const size_t rc = static_cast<size_t>(packed[i++]);
        Match m;
        for (size_t r = 0; r < rc && i + 4 <= packed.size(); r++, i += 4) {
            m.rects.push_back(megapdf_rect{packed[i], packed[i + 1], packed[i + 2], packed[i + 3]});
        }
        matches.push_back(m);
    }
    return matches;
}

// --------------------------------------------------------------------------

void test_null_handles() {
    check(megapdf_open(nullptr, 0, nullptr) == nullptr, "open of nothing returns NULL");
    check(megapdf_last_error_message() != nullptr, "error message is never NULL");
    check(megapdf_page_count(nullptr) == 0, "null document has 0 pages");
    check(megapdf_load_page(nullptr, 0) == nullptr, "null document loads no page");
    check(megapdf_page_width(nullptr) == 0.0 && megapdf_page_height(nullptr) == 0.0, "null page has no size");
    check(megapdf_detect_checkbox_squares(nullptr, nullptr, 0) == 0, "null page yields 0 candidates");
    double x = 1, y = 1;
    megapdf_page_crop_origin(nullptr, &x, &y);
    check(x == 0 && y == 0, "null page yields crop origin (0,0)");
    auto t = utf16("x");
    check(megapdf_search_page(nullptr, t.data(), nullptr, 0) == 0, "null page yields no matches");
    megapdf_close(nullptr);
    megapdf_close_page(nullptr);
}

void test_open_failures(const std::string& fixtures) {
    const unsigned char junk[] = "this is not a pdf at all";
    check(megapdf_open(junk, sizeof(junk), nullptr) == nullptr, "junk bytes do not open");
    check(megapdf_last_error() == 3 /* FPDF_ERR_FORMAT */, "junk reports FPDF_ERR_FORMAT", std::to_string(megapdf_last_error()));
    check(std::string(megapdf_last_error_message()).find("valid PDF") != std::string::npos, "junk has a message",
          megapdf_last_error_message());

    Doc d(fixtures + "/fixture.pdf");
    check(d.doc != nullptr, "fixture.pdf opens");
    check(megapdf_last_error() == 0, "a successful open clears the last error");
    if (d.doc) {
        check(megapdf_load_page(d.doc, 99) == nullptr, "out-of-range page index returns NULL");
        check(megapdf_load_page(d.doc, -1) == nullptr, "negative page index returns NULL");
    }
}

void test_document_and_geometry(const std::string& fixtures) {
    Doc d(fixtures + "/fixture.pdf");
    check(d.doc != nullptr, "fixture.pdf opens from bytes");
    if (!d.doc) return;
    check(megapdf_page_count(d.doc) == 2, "fixture.pdf has 2 pages", std::to_string(megapdf_page_count(d.doc)));


    Page p(d.doc, 0);
    check(p.page != nullptr, "page 1 loads");
    if (!p.page) return;
    check(close_to(megapdf_page_width(p.page), 612) && close_to(megapdf_page_height(p.page), 792),
          "fixture.pdf page is Letter", std::to_string(megapdf_page_width(p.page)) + "x" + std::to_string(megapdf_page_height(p.page)));

    // The #30 root cause: content is reported in MediaBox space while the CropBox is
    // what renders. cropped.pdf has CropBox [0 100 612 700]; the core must report the
    // origin it subtracts, and the CropBox size, not the MediaBox size.
    Doc c(fixtures + "/cropped.pdf");
    check(c.doc != nullptr, "cropped.pdf opens");
    if (!c.doc) return;
    Page cp(c.doc, 0);
    if (!cp.page) { check(false, "cropped.pdf page loads"); return; }
    double x = -1, y = -1;
    megapdf_page_crop_origin(cp.page, &x, &y);
    check(close_to(x, 0) && close_to(y, 100), "cropped.pdf crop origin is (0,100)", std::to_string(x) + "," + std::to_string(y));
    check(close_to(megapdf_page_width(cp.page), 612) && close_to(megapdf_page_height(cp.page), 600),
          "cropped.pdf size is the CropBox (612x600)", std::to_string(megapdf_page_height(cp.page)));

    x = y = -1;
    megapdf_page_crop_origin(p.page, &x, &y);
    check(close_to(x, 0) && close_to(y, 0), "fixture.pdf crop origin is (0,0)");
}

// Pages still open when the document closes are closed by the core, and a page
// closed explicitly is removed from the document's list (ASan catches a double free).
void test_lifecycle(const std::string& fixtures) {
    auto bytes = read_file(fixtures + "/fixture.pdf");
    megapdf_document* d = megapdf_open(bytes.data(), bytes.size(), nullptr);
    check(d != nullptr, "lifecycle: opens");
    if (!d) return;
    megapdf_page* a = megapdf_load_page(d, 0);
    megapdf_page* b = megapdf_load_page(d, 1);
    megapdf_page* a2 = megapdf_load_page(d, 0);   // the same index twice is two handles
    check(a && b && a2 && a != a2, "lifecycle: three page handles");
    megapdf_close_page(a);
    megapdf_close(d);                              // closes b and a2
    // Reopen to prove the library survives a close-with-open-pages.
    Doc again(fixtures + "/fixture.pdf");
    check(again.doc != nullptr && megapdf_page_count(again.doc) == 2, "lifecycle: reopen after close works");
}

// SDD §6.2 contract 2 on the shared fixture: page 1 of fixture.pdf draws exactly
// one 12x12 pt stroked square at (72,600). The Android, iOS and desktop suites
// assert this same rect against their bindings; here it is asserted against the
// implementation itself.
void test_fixture_square(const std::string& fixtures) {
    Doc d(fixtures + "/fixture.pdf");
    if (!d.doc) { check(false, "fixture.pdf opens"); return; }
    Page p(d.doc, 0);
    if (!p.page) { check(false, "fixture.pdf page 1 loads"); return; }

    auto s = squares(p.page);
    check(s.size() == 1, "fixture.pdf page 1 has one drawn-checkbox candidate", "got " + std::to_string(s.size()));
    if (s.size() == 1) {
        check(close_to(s[0].left, 72, 1.0) && close_to(s[0].bottom, 600, 1.0) && close_to(s[0].right, 84, 1.0) && close_to(s[0].top, 612, 1.0),
              "square is at (72,600)-(84,612) in crop space, within the 1 pt stroke",
              std::to_string(s[0].left) + "," + std::to_string(s[0].bottom) + "-" + std::to_string(s[0].right) + "," + std::to_string(s[0].top));
    }

    // Count-then-fill semantics: capacity 0 reports, a short buffer fills what fits.
    check(megapdf_detect_checkbox_squares(p.page, nullptr, 0) == 1, "capacity 0 returns the count");
    megapdf_rect one{};
    check(megapdf_detect_checkbox_squares(p.page, &one, 1) == 1 && close_to(one.left, 72, 1.0), "capacity 1 fills one",
          std::to_string(one.left));

    Page p2(d.doc, 1);
    check(p2.page != nullptr && squares(p2.page).empty(), "fixture.pdf page 2 has no candidates");

    Doc c(fixtures + "/cropped.pdf");
    Page cp(c.doc, 0);
    check(cp.page != nullptr && squares(cp.page).empty(), "cropped.pdf has no drawn squares");
}

// Contract 1 search (#26/#28): the same assertions SearchTests, SearchCropBoxTests,
// TextSearchTest and SearchParityTests make through the bindings.
void test_search(const std::string& fixtures) {
    Doc d(fixtures + "/fixture.pdf");
    if (!d.doc) { check(false, "fixture.pdf opens for search"); return; }
    Page p1(d.doc, 0);
    Page p2(d.doc, 1);
    if (!p1.page || !p2.page) { check(false, "fixture pages load for search"); return; }

    // Page 1 says "MegaPDF engine fixture - page 1" (and a caption line), page 2 just "Page 2".
    auto fixture1 = search(p1.page, "fixture");
    check(fixture1.size() == 1, "'fixture' hits once on page 1", std::to_string(fixture1.size()));
    if (fixture1.size() == 1) {
        check(fixture1[0].rects.size() == 1, "single-line match has one rect");
        if (!fixture1[0].rects.empty()) {
            const auto& r = fixture1[0].rects[0];
            check(r.right > r.left && r.top > r.bottom && r.left > 0 && r.bottom > 0 && r.right < 612 && r.top < 792,
                  "match rect is inside the page",
                  std::to_string(r.left) + "," + std::to_string(r.bottom) + "-" + std::to_string(r.right) + "," + std::to_string(r.top));
        }
    }
    check(search(p2.page, "fixture").empty(), "'fixture' does not hit on page 2");
    check(search(p1.page, "FIXTURE").size() == 1, "search is case-insensitive");
    check(search(p1.page, "page 1").size() == 1 && search(p2.page, "page 1").empty(), "search distinguishes pages");
    check(search(p2.page, "page 2").size() == 1, "'page 2' hits once on page 2");
    check(search(p1.page, "the").size() == 1, "'the' hits the caption once on page 1", std::to_string(search(p1.page, "the").size()));
    check(search(p1.page, "zzzz").empty(), "no false hits");
    check(search(p1.page, "").empty(), "empty term yields nothing");

    // Partial buffer: a capacity smaller than the total still returns the total
    // and fills only what fits.
    auto t = utf16("fixture");
    double two[2] = {-1, -1};
    const size_t total = megapdf_search_page(p1.page, t.data(), two, 2);
    check(total == 5, "one single-rect match packs as 5 doubles", std::to_string(total));
    check(two[0] == 1 && two[1] != -1, "short buffer receives the first two doubles");

    // cropped.pdf: CropBox [0 100 612 700]; "Hello MegaPDF" has its baseline at y=650 in
    // MediaBox space, so in crop space it is 550 — 50 pt below the crop top, which is
    // what SearchCropBoxTests asserts through the desktop binding.
    Doc c(fixtures + "/cropped.pdf");
    Page cp(c.doc, 0);
    if (!cp.page) { check(false, "cropped.pdf page loads for search"); return; }
    auto crop = search(cp.page, "megapdf");
    check(crop.size() == 1, "'megapdf' hits once on cropped.pdf", std::to_string(crop.size()));
    if (crop.size() == 1 && !crop[0].rects.empty()) {
        const auto& r = crop[0].rects[0];
        check(r.bottom > 540 && r.bottom < 560 && r.top < 600 && r.left > 100 && r.left < 300,
              "cropped.pdf match is reported in crop space (bottom ≈ 550)",
              std::to_string(r.left) + "," + std::to_string(r.bottom) + "-" + std::to_string(r.right) + "," + std::to_string(r.top));
    }
}

// The #98 canary: the micro:bit schematic once searched differently through the
// desktop engine than through Ghostscript. Every platform asserts 4/6/2 hits for
// "the" and none for "Seaman"; the core is where that number now comes from.
void test_schematic(const std::string& schematic) {
    Doc d(schematic);
    check(d.doc != nullptr, "micro:bit schematic opens");
    if (!d.doc) return;
    check(megapdf_page_count(d.doc) == 3, "schematic has 3 pages");
    const size_t expected[3] = {4, 6, 2};
    for (int i = 0; i < 3; i++) {
        Page p(d.doc, i);
        check(p.page != nullptr, "schematic page loads");
        if (!p.page) continue;
        (void)squares(p.page);   // must not crash or read out of bounds (ASan)
        const auto hits = search(p.page, "the");
        check(hits.size() == expected[i], "schematic 'the' hit count matches the canary",
              "page " + std::to_string(i + 1) + ": " + std::to_string(hits.size()) + " vs " + std::to_string(expected[i]));
        check(search(p.page, "Seaman").empty(), "schematic has no 'Seaman'");
    }
}

// --------------------------------------------------------------------------
// Contract 2 (#106): the core's text runs and visual lines must be what the
// desktop engine produced before the port, for every fixture. The expectation
// file is `MegaPDF.Stress dump-text` output: per page a header, then one `run`
// line per run in object order and one `line` per visual line, coordinates in
// crop space, strings as hex UTF-16 code units.
//
// The policy — which objects are runs, their text, size and marks, which runs
// share a line and in what order — is compared exactly on every OS. The fixtures
// use non-embedded base-14 fonts, so PDFium substitutes a system face: on Windows
// (where the expectation was captured) that is Arial and the family name and
// glyph boxes match exactly; elsewhere the family differs and the boxes move by a
// fraction of a point, so those two compare loosely (1.5 pt — a wrong crop origin
// is off by 100).
#if defined(_WIN32)
constexpr bool kSameFontsAsExpectation = true;
#else
constexpr bool kSameFontsAsExpectation = false;
#endif
constexpr double kBoundsTolerance = kSameFontsAsExpectation ? 0.002 : 1.5;

using U16 = std::vector<unsigned short>;

U16 unhex(const std::string& h) {
    U16 out;
    if (h == "-") return out;   // the dump's spelling of an empty string
    for (size_t i = 0; i + 4 <= h.size(); i += 4) out.push_back(static_cast<unsigned short>(std::strtoul(h.substr(i, 4).c_str(), nullptr, 16)));
    return out;
}

std::string show(const U16& s) {
    std::string out;
    for (unsigned short c : s) out += (c < 0x80 && c >= 0x20) ? static_cast<char>(c) : '?';
    return out;
}

struct ExpectedRun { int object_index; megapdf_rect bounds; double size; U16 font, box_id, box_font, text; };
struct ExpectedLine { megapdf_rect bounds; std::vector<size_t> runs; };
struct ExpectedPage { std::string file; int index; double width, height; std::vector<ExpectedRun> runs; std::vector<ExpectedLine> lines; };

std::vector<ExpectedPage> parse_expected(const std::string& path) {
    std::vector<ExpectedPage> pages;
    std::ifstream in(path);
    std::string line;
    while (std::getline(in, line)) {
        std::istringstream ss(line);
        std::string kind;
        ss >> kind;
        if (kind == "page") {
            ExpectedPage p;
            size_t nr, nl;
            ss >> p.file >> p.index >> p.width >> p.height >> nr >> nl;
            pages.push_back(p);
        } else if (kind == "run" && !pages.empty()) {
            ExpectedRun r;
            size_t i;
            std::string font, id, bf, text;
            ss >> i >> r.object_index >> r.bounds.left >> r.bounds.bottom >> r.bounds.right >> r.bounds.top >> r.size >> font >> id >> bf >> text;
            r.font = unhex(font); r.box_id = unhex(id); r.box_font = unhex(bf); r.text = unhex(text);
            pages.back().runs.push_back(r);
        } else if (kind == "line" && !pages.empty()) {
            ExpectedLine l;
            size_t j;
            std::string runs;
            ss >> j >> l.bounds.left >> l.bounds.bottom >> l.bounds.right >> l.bounds.top >> runs;
            std::istringstream rs(runs);
            std::string tok;
            while (std::getline(rs, tok, ',')) l.runs.push_back(std::strtoul(tok.c_str(), nullptr, 10));
            pages.back().lines.push_back(l);
        }
    }
    return pages;
}

bool rect_close(const megapdf_rect& a, const megapdf_rect& b, double tol = kBoundsTolerance) {
    return close_to(a.left, b.left, tol) && close_to(a.bottom, b.bottom, tol) && close_to(a.right, b.right, tol) && close_to(a.top, b.top, tol);
}

std::string rect_str(const megapdf_rect& r) {
    char buf[128];
    std::snprintf(buf, sizeof buf, "%.3f %.3f %.3f %.3f", r.left, r.bottom, r.right, r.top);
    return buf;
}

U16 run_string(const megapdf_text* t, size_t i, megapdf_text_field f) {
    const size_t n = megapdf_text_run_string(t, i, f, nullptr, 0);
    U16 out(n);
    if (n > 0) megapdf_text_run_string(t, i, f, out.data(), n);
    return out;
}

void test_text_runs(const std::string& fixtures, const std::string& schematic, const std::string& expected_path) {
    auto pages = parse_expected(expected_path);
    check(!pages.empty(), "text expectation file parses", expected_path);
    std::map<std::string, megapdf_document*> docs;
    for (const auto& ep : pages) {
        const std::string tag = ep.file + " page " + std::to_string(ep.index + 1);
        if (docs.find(ep.file) == docs.end()) {
            const std::string path = ep.file == "microbit-v2-schematic.pdf" ? schematic : fixtures + "/" + ep.file;
            auto bytes = read_file(path);
            docs[ep.file] = megapdf_open(bytes.data(), bytes.size(), nullptr);
        }
        megapdf_document* d = docs[ep.file];
        if (!d) { check(false, "text fixture opens", tag); continue; }
        Page p(d, ep.index);
        if (!p.page) { check(false, "text fixture page loads", tag); continue; }
        check(close_to(megapdf_page_width(p.page), ep.width, 0.002) && close_to(megapdf_page_height(p.page), ep.height, 0.002), "page size matches", tag);

        megapdf_text* t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
        if (!t) { check(false, "text loads", tag); continue; }

        const size_t nr = megapdf_text_run_count(t);
        check(nr == ep.runs.size(), "run count matches the desktop engine", tag + ": " + std::to_string(nr) + " vs " + std::to_string(ep.runs.size()));
        int reported = 0;
        for (size_t i = 0; i < nr && i < ep.runs.size(); i++) {
            megapdf_text_run r{};
            check(megapdf_text_run_get(t, i, &r) == MEGAPDF_OK, "run reads", tag);
            const auto& e = ep.runs[i];
            const U16 font = run_string(t, i, MEGAPDF_TEXT_RUN_FONT);
            const bool font_ok = kSameFontsAsExpectation ? font == e.font : font.empty() == e.font.empty();
            // The dump lists a box's face as the desktop engine resolved it — "Helvetica"
            // when the box carries no `font` param — so every box has a non-empty face
            // there, and the core's "" for a missing param maps to that default.
            static const U16 kDefaultFace = {'H', 'e', 'l', 'v', 'e', 't', 'i', 'c', 'a'};
            U16 box_font = run_string(t, i, MEGAPDF_TEXT_RUN_BOX_FONT);
            if (r.is_text_box && box_font.empty()) box_font = kDefaultFace;
            const bool same = r.object_index == e.object_index && rect_close(r.bounds, e.bounds) && close_to(r.font_size, e.size, 0.002) &&
                              run_string(t, i, MEGAPDF_TEXT_RUN_TEXT) == e.text && font_ok &&
                              run_string(t, i, MEGAPDF_TEXT_RUN_BOX_ID) == e.box_id && box_font == e.box_font &&
                              (r.is_text_box == 1) == !e.box_font.empty();
            if (!same && reported++ < 3) {
                check(false, "run matches the desktop engine",
                      tag + " run " + std::to_string(i) + ": got obj " + std::to_string(r.object_index) + " [" + rect_str(r.bounds) + "] " +
                          std::to_string(r.font_size) + " '" + show(run_string(t, i, MEGAPDF_TEXT_RUN_FONT)) + "' '" + show(run_string(t, i, MEGAPDF_TEXT_RUN_TEXT)) +
                          "' box=" + std::to_string(r.is_text_box) + "; expected obj " + std::to_string(e.object_index) + " [" + rect_str(e.bounds) + "] " +
                          std::to_string(e.size) + " '" + show(e.font) + "' '" + show(e.text) + "' id='" + show(e.box_id) + "'");
            } else if (!same) {
                failures++;
            }
        }

        std::vector<ExpectedLine> got;
        const size_t nl = megapdf_text_line_count(t);
        for (size_t j = 0; j < nl; j++) {
            ExpectedLine l;
            megapdf_text_line_get(t, j, &l.bounds);
            const size_t n = megapdf_text_line_runs(t, j, nullptr, 0);
            l.runs.resize(n);
            if (n > 0) megapdf_text_line_runs(t, j, l.runs.data(), n);
            got.push_back(l);
        }
        // Lines match by membership. The desktop engine sorted both its lines and
        // the runs within a baseline group with .NET's introsort, which is unstable
        // above 16 elements, so runs with the same left edge (the schematic draws
        // some labels four times over) came out in arbitrary order there; the core
        // keeps them in object order. Off Windows, substituted fonts can also swap
        // near-equal edges. The core's own left-to-right order is checked below.
        auto key = [](std::vector<size_t> runs) {
            std::sort(runs.begin(), runs.end());
            return runs;
        };
        std::map<std::vector<size_t>, megapdf_rect> got_by_runs;
        for (const auto& l : got) got_by_runs[key(l.runs)] = l.bounds;
        check(got.size() == ep.lines.size(), "line count matches the desktop engine", tag + ": " + std::to_string(got.size()) + " vs " + std::to_string(ep.lines.size()));
        reported = 0;
        for (const auto& w : ep.lines) {
            auto it = got_by_runs.find(key(w.runs));
            const bool same = it != got_by_runs.end() && rect_close(it->second, w.bounds);
            if (!same && reported++ < 3) {
                std::string wr;
                for (size_t k : w.runs) {
                    megapdf_text_run r{};
                    megapdf_text_run_get(t, k, &r);
                    char buf[64];
                    std::snprintf(buf, sizeof buf, "%zu@%.6f,", k, r.bounds.left);
                    wr += buf;
                }
                std::string same_set;
                auto sorted = w.runs;
                std::sort(sorted.begin(), sorted.end());
                for (const auto& l : got) {
                    auto g = l.runs;
                    std::sort(g.begin(), g.end());
                    if (g == sorted) { same_set = " (core has that set in order "; for (size_t k : l.runs) same_set += std::to_string(k) + ","; same_set += ")"; }
                }
                check(false, "line matches the desktop engine",
                      tag + ": expected a line of runs " + wr + " at [" + rect_str(w.bounds) + "]" +
                          (it == got_by_runs.end() ? " — no such line" + same_set : " — got [" + rect_str(it->second) + "]"));
            } else if (!same) {
                failures++;
            }
        }
        // The core's own order: lines top to bottom, runs within a line left to right.
        for (size_t j = 1; j < nl; j++) {
            megapdf_rect a{}, b{};
            megapdf_text_line_get(t, j - 1, &a);
            megapdf_text_line_get(t, j, &b);
            if (b.top > a.top + 1e-9) { check(false, "lines are ordered top to bottom", tag); break; }
        }
        for (const auto& l : got) {
            for (size_t k = 1; k < l.runs.size(); k++) {
                megapdf_text_run a{}, b{};
                megapdf_text_run_get(t, l.runs[k - 1], &a);
                megapdf_text_run_get(t, l.runs[k], &b);
                if (b.bounds.left < a.bounds.left) { check(false, "runs within a line are ordered left to right", tag); break; }
                if (b.bounds.left == a.bounds.left && l.runs[k] < l.runs[k - 1]) { check(false, "tied runs keep object order", tag); break; }
            }
        }
        megapdf_text_free(t);
    }
    // A boxes-only load lists just the marked objects, with the same run data.
    {
        Page tp(docs["textbox.pdf"], 0);
        megapdf_text* all = megapdf_text_load(tp.page, MEGAPDF_TEXT_ALL);
        megapdf_text* boxes = megapdf_text_load(tp.page, MEGAPDF_TEXT_BOXES_ONLY);
        size_t marked = 0;
        for (size_t i = 0; i < megapdf_text_run_count(all); i++) {
            megapdf_text_run r{};
            megapdf_text_run_get(all, i, &r);
            if (r.is_text_box) marked++;
        }
        check(marked == 4 && megapdf_text_run_count(boxes) == 4, "textbox.pdf has four marked boxes either way",
              std::to_string(marked) + " / " + std::to_string(megapdf_text_run_count(boxes)));
        megapdf_text_run first{};
        check(megapdf_text_run_get(boxes, 0, &first) == MEGAPDF_OK && first.is_text_box == 1 && first.object_index == 1, "boxes-only run 0 is object 1");
        check(show(run_string(boxes, 0, MEGAPDF_TEXT_RUN_BOX_ID)) == "text:fixture-1", "boxes-only run 0 carries its id",
              show(run_string(boxes, 0, MEGAPDF_TEXT_RUN_BOX_ID)));
        megapdf_text_free(all);
        megapdf_text_free(boxes);
    }
    for (auto& kv : docs) megapdf_close(kv.second);

    // Bad handles and indices.
    check(megapdf_text_load(nullptr, MEGAPDF_TEXT_ALL) == nullptr, "text of a null page is NULL");
    check(megapdf_text_run_count(nullptr) == 0 && megapdf_text_line_count(nullptr) == 0, "null text has no runs or lines");
    megapdf_text_run r{};
    check(megapdf_text_run_get(nullptr, 0, &r) == MEGAPDF_ERR_ARGUMENT, "run_get rejects a null handle");
    megapdf_text_free(nullptr);
}

// --------------------------------------------------------------------------
// Contract 3 (#107): AcroForm fields, read and driven through the core's form
// environment. forms.pdf has one checkbox widget (agree, [100 600 115 615]);
// formtext.pdf one text field (fullname). AcroFormTests, Android's
// CheckboxTest and iOS's CheckboxTests assert the same through the bindings.

U16 field_string(const megapdf_form_fields* f, size_t i, megapdf_field_string which) {
    const size_t n = megapdf_form_field_string(f, i, which, nullptr, 0);
    U16 out(n);
    if (n > 0) megapdf_form_field_string(f, i, which, out.data(), n);
    return out;
}

void test_form_fields(const std::string& fixtures) {
    // Checkbox: list, click to check, click to uncheck.
    {
        Doc d(fixtures + "/forms.pdf");
        if (!d.doc) { check(false, "forms.pdf opens"); return; }
        Page p(d.doc, 0);
        if (!p.page) { check(false, "forms.pdf page loads"); return; }
        megapdf_form_fields* f = megapdf_form_fields_load(p.page);
        check(f != nullptr && megapdf_form_field_count(f) == 1, "forms.pdf has one field", std::to_string(megapdf_form_field_count(f)));
        megapdf_form_field field{};
        check(megapdf_form_field_get(f, 0, &field) == MEGAPDF_OK, "field reads");
        check(field.kind == MEGAPDF_FIELD_CHECKBOX && field.is_checked == 0, "forms.pdf field is an unchecked checkbox",
              std::to_string(field.kind) + "/" + std::to_string(field.is_checked));
        check(close_to(field.bounds.left, 100) && close_to(field.bounds.bottom, 600) && close_to(field.bounds.top, 615), "checkbox bounds are (100,600)-(115,615) in crop space",
              rect_str(field.bounds));
        check(show(field_string(f, 0, MEGAPDF_FIELD_NAME)) == "agree", "checkbox name is agree", show(field_string(f, 0, MEGAPDF_FIELD_NAME)));
        megapdf_form_fields_free(f);

        const double cx = (field.bounds.left + field.bounds.right) / 2, cy = (field.bounds.bottom + field.bounds.top) / 2;
        check(megapdf_form_click(p.page, cx, cy) == MEGAPDF_OK, "click returns OK");
        f = megapdf_form_fields_load(p.page);
        megapdf_form_field_get(f, 0, &field);
        check(field.is_checked == 1, "a click checks the box");
        megapdf_form_fields_free(f);
        megapdf_form_click(p.page, cx, cy);
        f = megapdf_form_fields_load(p.page);
        megapdf_form_field_get(f, 0, &field);
        check(field.is_checked == 0, "a second click unchecks it");
        megapdf_form_fields_free(f);
        check(megapdf_form_click(p.page, 500, 400) == MEGAPDF_OK, "a click on nothing is harmless");
    }
    // Text field: list, set a value, read it back.
    {
        Doc d(fixtures + "/formtext.pdf");
        if (!d.doc) { check(false, "formtext.pdf opens"); return; }
        Page p(d.doc, 0);
        if (!p.page) { check(false, "formtext.pdf page loads"); return; }
        megapdf_form_fields* f = megapdf_form_fields_load(p.page);
        check(f != nullptr && megapdf_form_field_count(f) == 1, "formtext.pdf has one field", std::to_string(megapdf_form_field_count(f)));
        megapdf_form_field field{};
        megapdf_form_field_get(f, 0, &field);
        check(field.kind == MEGAPDF_FIELD_TEXT, "formtext.pdf field is a text field", std::to_string(field.kind));
        check(show(field_string(f, 0, MEGAPDF_FIELD_NAME)) == "fullname", "text field name is fullname", show(field_string(f, 0, MEGAPDF_FIELD_NAME)));
        check(field_string(f, 0, MEGAPDF_FIELD_VALUE).empty(), "text field starts empty", show(field_string(f, 0, MEGAPDF_FIELD_VALUE)));
        megapdf_form_fields_free(f);

        auto value = utf16("Ada Lovelace");
        const double cx = (field.bounds.left + field.bounds.right) / 2, cy = (field.bounds.bottom + field.bounds.top) / 2;
        check(megapdf_form_set_text(p.page, cx, cy, value.data()) == MEGAPDF_OK, "set_text returns OK");
        megapdf_form_commit(d.doc);
        f = megapdf_form_fields_load(p.page);
        check(show(field_string(f, 0, MEGAPDF_FIELD_VALUE)) == "Ada Lovelace", "the value reads back", show(field_string(f, 0, MEGAPDF_FIELD_VALUE)));
        megapdf_form_fields_free(f);
    }
    // A page without widgets, and bad handles.
    {
        Doc d(fixtures + "/fixture.pdf");
        Page p(d.doc, 0);
        megapdf_form_fields* f = megapdf_form_fields_load(p.page);
        check(f != nullptr && megapdf_form_field_count(f) == 0, "fixture.pdf has no fields");
        megapdf_form_fields_free(f);
    }
    check(megapdf_form_fields_load(nullptr) == nullptr, "fields of a null page is NULL");
    check(megapdf_form_field_count(nullptr) == 0, "null fields count 0");
    megapdf_form_field out{};
    check(megapdf_form_field_get(nullptr, 0, &out) == MEGAPDF_ERR_ARGUMENT, "field_get rejects a null handle");
    check(megapdf_form_click(nullptr, 0, 0) == MEGAPDF_ERR_ARGUMENT, "click rejects a null page");
    check(megapdf_form_set_text(nullptr, 0, 0, nullptr) == MEGAPDF_ERR_ARGUMENT, "set_text rejects a null page");
    megapdf_form_commit(nullptr);
    megapdf_form_fields_free(nullptr);
}

// --------------------------------------------------------------------------
// Contract 4 (#108): stamps and MegaPDF_Id marks. stamped.pdf carries a
// signature stamp (sig:interop-1, [100 500 190 560]) and a mark
// (mark:interop-2, [72 600 84 612]) written by the fixture generator; the
// interop contract is that every platform reads them back the same way.

struct StampList {
    std::vector<megapdf_stamp> stamps;
    std::vector<std::string> ids;
};

StampList stamps_of(const megapdf_page* page) {
    StampList out;
    megapdf_stamps* s = megapdf_stamps_load(page);
    for (size_t i = 0; i < megapdf_stamp_count(s); i++) {
        megapdf_stamp st{};
        megapdf_stamp_get(s, i, &st);
        out.stamps.push_back(st);
        const size_t n = megapdf_stamp_id(s, i, nullptr, 0);
        U16 id(n);
        if (n > 0) megapdf_stamp_id(s, i, id.data(), n);
        out.ids.push_back(show(id));
    }
    megapdf_stamps_free(s);
    return out;
}

void test_stamps(const std::string& fixtures) {
    // Reading stamps another platform wrote.
    {
        Doc d(fixtures + "/stamped.pdf");
        if (!d.doc) { check(false, "stamped.pdf opens"); return; }
        Page p(d.doc, 0);
        if (!p.page) { check(false, "stamped.pdf page loads"); return; }
        auto list = stamps_of(p.page);
        check(list.ids.size() == 2, "stamped.pdf has two MegaPDF stamps", std::to_string(list.ids.size()));
        if (list.ids.size() == 2) {
            check(list.ids[0] == "sig:interop-1" && list.ids[1] == "mark:interop-2", "stamp ids read back", list.ids[0] + ", " + list.ids[1]);
            check(close_to(list.stamps[0].bounds.left, 100) && close_to(list.stamps[0].bounds.top, 560), "signature bounds read back", rect_str(list.stamps[0].bounds));
            check(list.stamps[0].annot_index == 0 && list.stamps[1].annot_index == 1, "annotation indices are reported");
        }
    }
    // Check marks in every style over the fixture's square, then removal by id.
    {
        Doc d(fixtures + "/fixture.pdf");
        Page p(d.doc, 0);
        if (!p.page) { check(false, "fixture.pdf page loads for stamps"); return; }
        const megapdf_rect square{72, 600, 84, 612};
        auto cross = utf16("mark:cross"), tick = utf16("mark:check"), box = utf16("mark:square");
        check(megapdf_add_check_mark(p.page, &square, MEGAPDF_MARK_CROSS, cross.data()) == MEGAPDF_OK, "cross mark added");
        check(megapdf_add_check_mark(p.page, &square, MEGAPDF_MARK_CHECK, tick.data()) == MEGAPDF_OK, "check mark added");
        check(megapdf_add_check_mark(p.page, &square, MEGAPDF_MARK_FILLED_SQUARE, box.data()) == MEGAPDF_OK, "filled-square mark added");
        auto list = stamps_of(p.page);
        check(list.ids.size() == 3, "three marks listed", std::to_string(list.ids.size()));
        if (list.ids.size() == 3) {
            check(list.ids[0] == "mark:cross" && list.ids[2] == "mark:square", "mark ids in order", list.ids[0] + "," + list.ids[1] + "," + list.ids[2]);
            // Inset 10% of the 12 pt square: (73.2, 601.2)-(82.8, 610.8).
            check(close_to(list.stamps[0].bounds.left, 73.2, 0.05) && close_to(list.stamps[0].bounds.bottom, 601.2, 0.05) &&
                      close_to(list.stamps[0].bounds.right, 82.8, 0.05) && close_to(list.stamps[0].bounds.top, 610.8, 0.05),
                  "mark sits 10% inside the square", rect_str(list.stamps[0].bounds));
        }
        check(megapdf_remove_stamp(p.page, tick.data()) == MEGAPDF_OK, "remove by id");
        list = stamps_of(p.page);
        check(list.ids.size() == 2 && list.ids[1] == "mark:square", "the right mark was removed", std::to_string(list.ids.size()));
        check(megapdf_remove_stamp(p.page, tick.data()) == MEGAPDF_ERR_ARGUMENT, "removing a missing id is an argument error");
        check(megapdf_stamp_image_load(p.page, 0) == nullptr, "a mark has no image");
        check(megapdf_remove_annotation(p.page, 0) == MEGAPDF_OK, "remove by index");
        check(stamps_of(p.page).ids.size() == 1, "one mark left");
        check(megapdf_add_check_mark(p.page, &square, MEGAPDF_MARK_CROSS, nullptr) == MEGAPDF_ERR_ARGUMENT, "a mark needs an id");
    }
    // An image stamp: place, read back at native size with alpha, move under the same id.
    {
        Doc d(fixtures + "/fixture.pdf");
        Page p(d.doc, 1);
        if (!p.page) { check(false, "fixture.pdf page 2 loads for stamps"); return; }
        const int w = 8, h = 4;
        std::vector<unsigned char> bgra(static_cast<size_t>(w) * h * 4, 0);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
            unsigned char* px = &bgra[(static_cast<size_t>(y) * w + x) * 4];
            const bool ink = x >= w / 2;          // right half opaque blue, left half transparent
            px[0] = ink ? 0xFF : 0; px[1] = 0; px[2] = 0; px[3] = ink ? 0xFF : 0;
        }
        const megapdf_rect bounds{100, 400, 280, 460};
        auto id = utf16("sig:test-1");
        check(megapdf_add_image_stamp(p.page, bgra.data(), w, h, &bounds, id.data()) == MEGAPDF_OK, "image stamp added");
        auto list = stamps_of(p.page);
        check(list.ids.size() == 1 && list.ids[0] == "sig:test-1", "signature listed");
        if (list.ids.size() == 1) {
            check(close_to(list.stamps[0].bounds.left, 100) && close_to(list.stamps[0].bounds.bottom, 400) &&
                      close_to(list.stamps[0].bounds.right, 280) && close_to(list.stamps[0].bounds.top, 460),
                  "signature placed at its bounds", rect_str(list.stamps[0].bounds));
            megapdf_image* img = megapdf_stamp_image_load(p.page, list.stamps[0].annot_index);
            check(img != nullptr, "signature image reads back");
            if (img) {
                check(megapdf_image_width(img) == w && megapdf_image_height(img) == h, "image comes back at native pixel size",
                      std::to_string(megapdf_image_width(img)) + "x" + std::to_string(megapdf_image_height(img)));
                const size_t bytes = megapdf_image_pixels(img, nullptr, 0);
                std::vector<unsigned char> back(bytes);
                megapdf_image_pixels(img, back.data(), bytes);
                check(bytes == bgra.size(), "pixel buffer size matches", std::to_string(bytes));
                if (bytes == bgra.size()) {
                    check(back[3] == 0, "transparent pixel stays transparent", std::to_string(back[3]));
                    const size_t br = (static_cast<size_t>(h - 1) * w + (w - 1)) * 4;
                    check(back[br] > 0xA0 && back[br + 3] == 0xFF, "opaque blue pixel stays opaque blue",
                          std::to_string(back[br]) + "/" + std::to_string(back[br + 3]));
                }
                megapdf_image_free(img);
            }
        }
        const megapdf_rect moved{300, 100, 420, 140};
        check(megapdf_move_image_stamp(p.page, id.data(), &moved) == MEGAPDF_OK, "move returns OK");
        list = stamps_of(p.page);
        check(list.ids.size() == 1 && list.ids[0] == "sig:test-1", "moved stamp keeps its id", std::to_string(list.ids.size()));
        if (list.ids.size() == 1) {
            check(close_to(list.stamps[0].bounds.left, 300) && close_to(list.stamps[0].bounds.top, 140), "moved stamp is at the new bounds", rect_str(list.stamps[0].bounds));
            megapdf_image* img = megapdf_stamp_image_load(p.page, list.stamps[0].annot_index);
            check(img && megapdf_image_width(img) == w && megapdf_image_height(img) == h, "a move keeps native resolution");
            megapdf_image_free(img);
        }
        auto missing = utf16("sig:nope");
        check(megapdf_move_image_stamp(p.page, missing.data(), &moved) == MEGAPDF_ERR_ARGUMENT, "moving a missing id is an argument error");
        check(megapdf_add_image_stamp(p.page, bgra.data(), 0, h, &bounds, id.data()) == MEGAPDF_ERR_ARGUMENT, "a zero-width image is rejected");
    }
    // Crop space: a stamp on cropped.pdf lands where the UI asked, in crop coordinates.
    {
        Doc d(fixtures + "/cropped.pdf");
        Page p(d.doc, 0);
        if (!p.page) { check(false, "cropped.pdf page loads for stamps"); return; }
        const megapdf_rect square{50, 50, 62, 62};
        auto id = utf16("mark:crop");
        megapdf_add_check_mark(p.page, &square, MEGAPDF_MARK_CROSS, id.data());
        auto list = stamps_of(p.page);
        check(list.ids.size() == 1 && close_to(list.stamps[0].bounds.bottom, 51.2, 0.05), "mark on a cropped page reads back in crop space",
              list.ids.empty() ? "none" : rect_str(list.stamps[0].bounds));
    }
    check(megapdf_stamps_load(nullptr) == nullptr, "stamps of a null page is NULL");
    check(megapdf_stamp_count(nullptr) == 0, "null stamps count 0");
    check(megapdf_stamp_image_load(nullptr, 0) == nullptr, "image of a null page is NULL");
    check(megapdf_image_pixels(nullptr, nullptr, 0) == 0, "null image has no pixels");
    check(megapdf_remove_annotation(nullptr, 0) == MEGAPDF_ERR_ARGUMENT, "remove rejects a null page");
    megapdf_stamps_free(nullptr);
    megapdf_image_free(nullptr);
}

// --------------------------------------------------------------------------
// Contract 5 (#109): whiteouts, text boxes and detached objects.

int FPDFPage_CountObjects_via_bounds_probe(const megapdf_page* page) {
    int n = 0;
    megapdf_rect r{};
    while (megapdf_object_bounds(page, n, &r) == MEGAPDF_OK) n++;
    return n;
}

std::vector<megapdf_object_rect> whiteouts_of(const megapdf_page* page) {
    const size_t n = megapdf_whiteouts(page, nullptr, 0);
    std::vector<megapdf_object_rect> out(n);
    if (n > 0) megapdf_whiteouts(page, out.data(), n);
    return out;
}

struct Box { int object_index; std::string id; std::string font; double size; megapdf_rect bounds; std::string text; };

std::vector<Box> boxes_of(const megapdf_page* page) {
    std::vector<Box> out;
    megapdf_text* t = megapdf_text_load(page, MEGAPDF_TEXT_BOXES_ONLY);
    for (size_t i = 0; i < megapdf_text_run_count(t); i++) {
        megapdf_text_run r{};
        megapdf_text_run_get(t, i, &r);
        out.push_back(Box{r.object_index, show(run_string(t, i, MEGAPDF_TEXT_RUN_BOX_ID)), show(run_string(t, i, MEGAPDF_TEXT_RUN_BOX_FONT)),
                          r.font_size, r.bounds, show(run_string(t, i, MEGAPDF_TEXT_RUN_TEXT))});
    }
    megapdf_text_free(t);
    return out;
}

void test_whiteouts_and_text_boxes(const std::string& fixtures) {
    // Whiteouts, and detach/restore/discard around them.
    {
        Doc d(fixtures + "/fixture.pdf");
        Page p(d.doc, 0);
        if (!p.page) { check(false, "fixture.pdf page loads for whiteouts"); return; }
        check(whiteouts_of(p.page).empty(), "no whiteouts to start");
        const int before = FPDFPage_CountObjects_via_bounds_probe(p.page);
        const megapdf_rect area{100, 500, 200, 540};
        int index = -1;
        check(megapdf_add_whiteout(p.page, &area, &index) == MEGAPDF_OK, "whiteout added");
        check(index == before, "whiteout is appended at the end", std::to_string(index) + " vs " + std::to_string(before));
        auto w = whiteouts_of(p.page);
        check(w.size() == 1 && w[0].object_index == index && rect_close(w[0].bounds, area, 0.01), "whiteout listed with its bounds",
              w.empty() ? "none" : rect_str(w[0].bounds));

        megapdf_detached* held = megapdf_detach_object(p.page, index);
        check(held != nullptr, "whiteout detaches");
        check(whiteouts_of(p.page).empty(), "detached whiteout is off the page");
        check(megapdf_restore_object(p.page, held, index) == MEGAPDF_OK, "whiteout restores");
        w = whiteouts_of(p.page);
        check(w.size() == 1 && rect_close(w[0].bounds, area, 0.01), "restored whiteout is back where it was");

        held = megapdf_detach_object(p.page, index);
        megapdf_discard_detached(held);
        check(whiteouts_of(p.page).empty(), "discarded whiteout stays gone");
        check(megapdf_detach_object(p.page, 9999) == nullptr, "detaching a missing index fails cleanly");
        check(megapdf_detach_object(nullptr, 0) == nullptr && megapdf_restore_object(p.page, nullptr, 0) == MEGAPDF_ERR_ARGUMENT, "null detached handles are rejected");
        megapdf_discard_detached(nullptr);

        // A detached object still held when the document closes is freed by the core (ASan/LSan watch this).
        megapdf_add_whiteout(p.page, &area, &index);
        megapdf_detached* leaked = megapdf_detach_object(p.page, index);
        check(leaked != nullptr, "second whiteout detaches for the close test");
    }
    // Text boxes: add, list, find, move, restyle, remove.
    {
        Doc d(fixtures + "/fixture.pdf");
        Page p(d.doc, 1);
        if (!p.page) { check(false, "fixture.pdf page 2 loads for text boxes"); return; }
        auto hello = utf16("Hello box"), id = utf16("text:core-1");
        int index = -1;
        check(megapdf_add_text_box(p.page, -1, hello.data(), "Helvetica", 12, 100, 300, id.data(), &index) == MEGAPDF_OK, "text box added");
        auto boxes = boxes_of(p.page);
        check(boxes.size() == 1, "one text box listed", std::to_string(boxes.size()));
        if (boxes.size() == 1) {
            check(boxes[0].object_index == index && boxes[0].id == "text:core-1" && boxes[0].font == "Helvetica" && close_to(boxes[0].size, 12) &&
                      boxes[0].text == "Hello box",
                  "box carries id, face, size and text", boxes[0].id + "/" + boxes[0].font + "/" + boxes[0].text);
            check(close_to(boxes[0].bounds.left, 100, 1.0) && boxes[0].bounds.bottom < 300 && boxes[0].bounds.bottom > 295 && boxes[0].bounds.top > 300,
                  "baseline sits on the requested point", rect_str(boxes[0].bounds));
        }
        check(megapdf_find_text_box(p.page, id.data()) == index, "find by id");
        check(megapdf_object_type(p.page, index) == 1 && megapdf_object_type(p.page, 9999) == -1 && megapdf_object_type(nullptr, 0) == -1,
              "object type answers text for the box and -1 for a bad index");
        auto nope = utf16("text:nope");
        check(megapdf_find_text_box(p.page, nope.data()) == -1, "find of a missing id is -1");

        check(megapdf_move_text_box(p.page, index, 150, 400) == MEGAPDF_OK, "move returns OK");
        megapdf_rect b{};
        check(megapdf_object_bounds(p.page, index, &b) == MEGAPDF_OK && close_to(b.left, 150, 0.01) && close_to(b.bottom, 400, 0.01),
              "moved box has its bottom-left on the target", rect_str(b));

        // Restyle (#45): detach the old object, insert the new one at the same index
        // anchored on the old bottom-left, same id — grows upward, keeps the corner.
        megapdf_detached* old = megapdf_detach_object(p.page, index);
        check(old != nullptr, "old box detaches for restyle");
        check(megapdf_restyle_text_box(p.page, index, hello.data(), "Times-Roman", 18, b.left, b.bottom, id.data()) == MEGAPDF_OK, "restyle returns OK");
        boxes = boxes_of(p.page);
        check(boxes.size() == 1 && boxes[0].id == "text:core-1" && boxes[0].font == "Times-Roman" && close_to(boxes[0].size, 18), "restyled box keeps its id and records the new face");
        if (boxes.size() == 1) {
            check(close_to(boxes[0].bounds.left, b.left, 0.05) && close_to(boxes[0].bounds.bottom, b.bottom, 0.05) && boxes[0].bounds.top > b.top,
                  "restyled box keeps its corner and grows upward", rect_str(boxes[0].bounds) + " vs " + rect_str(b));
        }
        // Undo: detach the restyled one, restore the original.
        megapdf_detached* restyled = megapdf_detach_object(p.page, index);
        check(megapdf_restore_object(p.page, old, index) == MEGAPDF_OK, "original restores after restyle");
        boxes = boxes_of(p.page);
        check(boxes.size() == 1 && boxes[0].font == "Helvetica" && close_to(boxes[0].size, 12) && close_to(boxes[0].bounds.bottom, b.bottom, 0.05),
              "restored original is byte-identical in what it reports");
        megapdf_discard_detached(restyled);

        check(megapdf_remove_text_box(p.page, id.data()) == MEGAPDF_OK, "remove by id");
        check(boxes_of(p.page).empty(), "box is gone");
        check(megapdf_remove_text_box(p.page, id.data()) == MEGAPDF_OK, "removing an already-gone box is fine");
        check(megapdf_add_text_box(p.page, -1, hello.data(), "Comic Sans", 12, 100, 300, id.data(), &index) == MEGAPDF_ERR_ARGUMENT, "a face outside the three is rejected");
        auto empty = utf16("");
        check(megapdf_add_text_box(p.page, -1, empty.data(), "Helvetica", 12, 100, 300, id.data(), &index) == MEGAPDF_ERR_ARGUMENT, "empty text is rejected");
    }
    // Legacy boxes with no id answer to the derived handle, uniquely.
    {
        Doc d(fixtures + "/textbox.pdf");
        Page p(d.doc, 0);
        if (!p.page) { check(false, "textbox.pdf page loads"); return; }
        auto boxes = boxes_of(p.page);
        int untagged = -1, untagged2 = -1;
        for (const auto& bx : boxes) if (bx.id.empty()) { if (untagged < 0) untagged = bx.object_index; else untagged2 = bx.object_index; }
        check(untagged >= 0 && untagged2 >= 0, "textbox.pdf has two legacy boxes");
        auto handle = utf16(("text:untagged#" + std::to_string(untagged)).c_str());
        check(megapdf_find_text_box(p.page, handle.data()) == untagged, "the derived handle finds the legacy box");
        check(megapdf_remove_text_box(p.page, handle.data()) == MEGAPDF_OK, "legacy box removed by its handle");
        auto after = boxes_of(p.page);
        check(after.size() == boxes.size() - 1, "exactly one box was removed", std::to_string(after.size()));
        auto tagged = utf16("text:fixture-1");
        check(megapdf_find_text_box(p.page, tagged.data()) >= 0, "the tagged box is still there");
    }
    // Crop space: text lands where asked on the cropped page.
    {
        Doc d(fixtures + "/cropped.pdf");
        Page p(d.doc, 0);
        if (!p.page) { check(false, "cropped.pdf page loads for text boxes"); return; }
        auto text = utf16("Cropped"), id = utf16("text:crop");
        int index = -1;
        megapdf_add_text_box(p.page, -1, text.data(), "Courier", 10, 40, 60, id.data(), &index);
        auto boxes = boxes_of(p.page);
        check(boxes.size() == 1 && close_to(boxes[0].bounds.left, 40, 1.0) && boxes[0].bounds.bottom < 60 && boxes[0].bounds.top > 60,
              "text box on a cropped page reads back in crop space", boxes.empty() ? "none" : rect_str(boxes[0].bounds));
    }
    check(megapdf_add_whiteout(nullptr, nullptr, nullptr) == MEGAPDF_ERR_ARGUMENT, "whiteout rejects null");
    check(megapdf_whiteouts(nullptr, nullptr, 0) == 0, "whiteouts of null is 0");
    check(megapdf_find_text_box(nullptr, nullptr) == -1, "find on null is -1");
}

// --------------------------------------------------------------------------
// Contract 6 (#110): save, flatten and images.

int collect(void* ctx, const void* data, size_t size) {
    auto* out = static_cast<std::vector<unsigned char>*>(ctx);
    out->insert(out->end(), static_cast<const unsigned char*>(data), static_cast<const unsigned char*>(data) + size);
    return 1;
}

int refuse(void*, const void*, size_t) { return 0; }

// #126: with MEGAPDF_SAVED_DIR set, a document a test saves is also written to
// <dir>/<test>-<n>.pdf, so CI can run qpdf --check over what the core writes; reopening
// it in PDFium only proves PDFium can read its own output. Unset, this does nothing.
void keep_saved(const char* test, const std::vector<unsigned char>& bytes) {
    static const char* dir = std::getenv("MEGAPDF_SAVED_DIR");
    if (dir == nullptr || *dir == 0 || bytes.empty()) return;
    static std::map<std::string, int> counts;
    const std::string path = std::string(dir) + "/" + test + "-" + std::to_string(++counts[test]) + ".pdf";
    std::ofstream out(path, std::ios::binary);
    out.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    out.close();
    check(out.good(), "a saved document is kept for the qpdf check", path);
}

// A one-page PDF drawing a raw RGB image of `px` × `px` at `pt` × `pt` points.
std::vector<unsigned char> image_pdf(int px, double pt) {
    std::string pdf = "%PDF-1.4\n";
    std::vector<size_t> offsets;
    auto add = [&](const std::string& body) { offsets.push_back(pdf.size()); pdf += std::to_string(offsets.size()) + " 0 obj\n" + body + "\nendobj\n"; };
    add("<< /Type /Catalog /Pages 2 0 R >>");
    add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
    add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im1 4 0 R >> >> /Contents 5 0 R >>");
    std::string pixels(static_cast<size_t>(px) * px * 3, '\0');
    for (size_t i = 0; i < pixels.size(); i += 3) { pixels[i] = static_cast<char>(40); pixels[i + 1] = static_cast<char>(80); pixels[i + 2] = static_cast<char>(200); }
    add("<< /Type /XObject /Subtype /Image /Width " + std::to_string(px) + " /Height " + std::to_string(px) +
        " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length " + std::to_string(pixels.size()) + " >>\nstream\n" + pixels + "\nendstream");
    char content[128];
    std::snprintf(content, sizeof content, "q %.0f 0 0 %.0f 100 600 cm /Im1 Do Q", pt, pt);
    add("<< /Length " + std::to_string(std::strlen(content)) + " >>\nstream\n" + content + "\nendstream");
    const size_t xref = pdf.size();
    pdf += "xref\n0 " + std::to_string(offsets.size() + 1) + "\n0000000000 65535 f \n";
    for (size_t off : offsets) { char line[32]; std::snprintf(line, sizeof line, "%010zu 00000 n \n", off); pdf += line; }
    pdf += "trailer\n<< /Size " + std::to_string(offsets.size() + 1) + " /Root 1 0 R >>\nstartxref\n" + std::to_string(xref) + "\n%%EOF\n";
    return std::vector<unsigned char>(pdf.begin(), pdf.end());
}

// A real 8×8 JPEG, so the replace path exercises PDFium's DCT loader.
const unsigned char kTinyJpeg[] = {
    0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01,
    0x00, 0x01, 0x00, 0x00, 0xff, 0xdb, 0x00, 0x43, 0x00, 0x10, 0x0b, 0x0c, 0x0e, 0x0c, 0x0a, 0x10,
    0x0e, 0x0d, 0x0e, 0x12, 0x11, 0x10, 0x13, 0x18, 0x28, 0x1a, 0x18, 0x16, 0x16, 0x18, 0x31, 0x23,
    0x25, 0x1d, 0x28, 0x3a, 0x33, 0x3d, 0x3c, 0x39, 0x33, 0x38, 0x37, 0x40, 0x48, 0x5c, 0x4e, 0x40,
    0x44, 0x57, 0x45, 0x37, 0x38, 0x50, 0x6d, 0x51, 0x57, 0x5f, 0x62, 0x67, 0x68, 0x67, 0x3e, 0x4d,
    0x71, 0x79, 0x70, 0x64, 0x78, 0x5c, 0x65, 0x67, 0x63, 0xff, 0xdb, 0x00, 0x43, 0x01, 0x11, 0x12,
    0x12, 0x18, 0x15, 0x18, 0x2f, 0x1a, 0x1a, 0x2f, 0x63, 0x42, 0x38, 0x42, 0x63, 0x63, 0x63, 0x63,
    0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63,
    0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63,
    0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0x63, 0xff, 0xc0,
    0x00, 0x11, 0x08, 0x00, 0x08, 0x00, 0x08, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11,
    0x01, 0xff, 0xc4, 0x00, 0x1f, 0x00, 0x00, 0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09,
    0x0a, 0x0b, 0xff, 0xc4, 0x00, 0xb5, 0x10, 0x00, 0x02, 0x01, 0x03, 0x03, 0x02, 0x04, 0x03, 0x05,
    0x05, 0x04, 0x04, 0x00, 0x00, 0x01, 0x7d, 0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21,
    0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08, 0x23,
    0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17,
    0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a,
    0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a,
    0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a,
    0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99,
    0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7,
    0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5,
    0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1,
    0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa, 0xff, 0xc4, 0x00, 0x1f, 0x01, 0x00, 0x03,
    0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
    0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0xff, 0xc4, 0x00, 0xb5, 0x11, 0x00,
    0x02, 0x01, 0x02, 0x04, 0x04, 0x03, 0x04, 0x07, 0x05, 0x04, 0x04, 0x00, 0x01, 0x02, 0x77, 0x00,
    0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13,
    0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15,
    0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26, 0x27,
    0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
    0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
    0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88,
    0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6,
    0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4,
    0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe2,
    0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9,
    0xfa, 0xff, 0xda, 0x00, 0x0c, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3f, 0x00, 0xc8,
    0xa2, 0x8a, 0x2b, 0xe8, 0x0e, 0x13, 0xff, 0xd9
};

int fake_encode(void* ctx, const unsigned char*, int w, int h, double quality, unsigned char** out, size_t* len) {
    auto* calls = static_cast<std::vector<std::string>*>(ctx);
    calls->push_back(std::to_string(w) + "x" + std::to_string(h) + "@" + std::to_string(quality));
    *out = const_cast<unsigned char*>(kTinyJpeg);
    *len = sizeof kTinyJpeg;
    return 1;
}

void test_save_flatten_images(const std::string& fixtures) {
    // Save: bytes come back through the callback and reopen; an edit survives.
    {
        Doc d(fixtures + "/fixture.pdf");
        if (!d.doc) { check(false, "fixture.pdf opens for save"); return; }
        std::vector<unsigned char> out;
        check(megapdf_save(d.doc, collect, &out) == MEGAPDF_OK, "save returns OK");
        keep_saved("save", out);
        check(out.size() > 4 && std::string(out.begin(), out.begin() + 4) == "%PDF", "saved bytes are a PDF", std::to_string(out.size()));
        megapdf_document* again = megapdf_open(out.data(), out.size(), nullptr);
        check(again != nullptr && megapdf_page_count(again) == 2, "saved document reopens with 2 pages");
        megapdf_close(again);

        Page p(d.doc, 0);
        const megapdf_rect area{100, 500, 200, 540};
        int index = -1;
        megapdf_add_whiteout(p.page, &area, &index);
        out.clear();
        check(megapdf_save(d.doc, collect, &out) == MEGAPDF_OK, "save after an edit returns OK");
        keep_saved("save", out);
        again = megapdf_open(out.data(), out.size(), nullptr);
        if (again) {
            Page rp(again, 0);
            check(rp.page && whiteouts_of(rp.page).size() == 1, "the whiteout survives save and reopen");
        } else {
            check(false, "edited document reopens");
        }
        megapdf_close(again);
        check(megapdf_save(d.doc, refuse, nullptr) == MEGAPDF_ERR_PDFIUM, "a refusing callback fails the save");
        check(megapdf_save(nullptr, collect, &out) == MEGAPDF_ERR_ARGUMENT && megapdf_save(d.doc, nullptr, nullptr) == MEGAPDF_ERR_ARGUMENT, "save rejects nulls");
    }
    // Flatten: stamps and fields bake into content; the text is still there.
    {
        Doc d(fixtures + "/stamped.pdf");
        Page p(d.doc, 0);
        if (!p.page) { check(false, "stamped.pdf page loads for flatten"); return; }
        check(stamps_of(p.page).ids.size() == 2, "two stamps before flatten");
        check(megapdf_flatten_all(d.doc) == MEGAPDF_OK, "flatten returns OK");
        // Flattening replaces the page's object tree; look through a fresh page handle.
        Page after(d.doc, 0);
        check(after.page && stamps_of(after.page).ids.empty(), "no stamp annotations after flatten", std::to_string(stamps_of(after.page).ids.size()));
        check(after.page && search(after.page, "interop").size() == 1, "the page text is still there after flatten");
        std::vector<unsigned char> out;
        check(megapdf_save(d.doc, collect, &out) == MEGAPDF_OK, "flattened document saves");
        keep_saved("flatten", out);
        megapdf_document* again = megapdf_open(out.data(), out.size(), nullptr);
        if (again) { Page rp(again, 0); check(rp.page && stamps_of(rp.page).ids.empty(), "flattened save has no stamps on reopen"); }
        megapdf_close(again);
    }
    {
        Doc d(fixtures + "/forms.pdf");
        Page p(d.doc, 0);
        megapdf_form_fields* f = megapdf_form_fields_load(p.page);
        check(megapdf_form_field_count(f) == 1, "one field before flatten");
        megapdf_form_fields_free(f);
        check(megapdf_flatten_all(d.doc) == MEGAPDF_OK, "forms.pdf flattens");
        Page after(d.doc, 0);
        f = megapdf_form_fields_load(after.page);
        check(megapdf_form_field_count(f) == 0, "no fields after flatten", std::to_string(megapdf_form_field_count(f)));
        megapdf_form_fields_free(f);
    }
    // Images: list, render at a size, replace with a JPEG, and the shrink rules.
    {
        auto bytes = image_pdf(64, 20);   // 64 px drawn at 20 pt: oversized (target 42 px), 12,288 bytes stored
        megapdf_document* d = megapdf_open(bytes.data(), bytes.size(), nullptr);
        check(d != nullptr, "image pdf opens");
        if (!d) return;
        megapdf_images* imgs = megapdf_images_load(d);
        check(megapdf_image_count(imgs) == 1, "one image listed", std::to_string(megapdf_image_count(imgs)));
        megapdf_image_info info{};
        megapdf_image_get(imgs, 0, &info);
        check(info.page_index == 0 && info.pixel_width == 64 && info.pixel_height == 64 && close_to(info.display_width, 20) &&
                  close_to(info.display_height, 20) && info.stored_bytes == 64 * 64 * 3,
              "image info reports pixel, display and stored sizes",
              std::to_string(info.pixel_width) + " " + std::to_string(info.display_width) + " " + std::to_string(info.stored_bytes));
        megapdf_images_free(imgs);

        megapdf_image* rendered = megapdf_render_image(d, 0, info.object_index, 16, 12);
        check(rendered && megapdf_image_width(rendered) == 16 && megapdf_image_height(rendered) == 12, "image renders at the requested size");
        if (rendered) {
            std::vector<unsigned char> px(megapdf_image_pixels(rendered, nullptr, 0));
            megapdf_image_pixels(rendered, px.data(), px.size());
            check(px.size() == 16 * 12 * 4 && px[0] > 150 && px[2] < 100, "rendered pixels are the blue the image holds", std::to_string(px[0]) + "," + std::to_string(px[1]) + "," + std::to_string(px[2]));
        }
        megapdf_image_free(rendered);
        check(megapdf_render_image(d, 0, 9999, 8, 8) == nullptr, "rendering a non-image fails cleanly");

        std::vector<std::string> calls;
        int replaced = -1;
        check(megapdf_shrink_images(d, fake_encode, nullptr, &calls, &replaced) == MEGAPDF_OK, "shrink returns OK");
        check(replaced == 1, "the oversized image was replaced", std::to_string(replaced));
        check(calls.size() == 1 && calls[0] == "42x42@0.750000", "encoder asked for 150 dpi of the placed size at quality 0.75", calls.empty() ? "no calls" : calls[0]);
        imgs = megapdf_images_load(d);
        megapdf_image_get(imgs, 0, &info);
        check(megapdf_image_count(imgs) == 1 && info.pixel_width == 8 && info.pixel_height == 8 && info.stored_bytes == static_cast<long long>(sizeof kTinyJpeg),
              "the image now holds the JPEG", std::to_string(info.pixel_width) + " " + std::to_string(info.stored_bytes));
        megapdf_images_free(imgs);
        // Shrinking again finds nothing worth doing: 632 bytes is under the 8 KB floor.
        calls.clear();
        megapdf_shrink_images(d, fake_encode, nullptr, &calls, &replaced);
        check(replaced == 0 && calls.empty(), "a second shrink leaves the small JPEG alone");
        std::vector<unsigned char> out;
        check(megapdf_save(d, collect, &out) == MEGAPDF_OK && out.size() < bytes.size(), "the shrunk document saves smaller",
              std::to_string(out.size()) + " vs " + std::to_string(bytes.size()));
        keep_saved("shrink-images", out);
        megapdf_close(d);

        // Not worth touching: right-sized and small.
        auto small = image_pdf(32, 216);   // 32 px over 3 inches: not oversized, 3 KB stored
        d = megapdf_open(small.data(), small.size(), nullptr);
        calls.clear();
        megapdf_shrink_images(d, fake_encode, nullptr, &calls, &replaced);
        check(replaced == 0 && calls.empty(), "a small right-sized image is skipped");
        check(megapdf_replace_image_jpeg(d, 0, 9999, kTinyJpeg, sizeof kTinyJpeg) == MEGAPDF_ERR_ARGUMENT, "replacing a non-image is an argument error");
        megapdf_close(d);
    }
    check(megapdf_flatten_all(nullptr) == MEGAPDF_ERR_ARGUMENT, "flatten rejects null");
    check(megapdf_images_load(nullptr) == nullptr && megapdf_image_count(nullptr) == 0, "images of null");
    check(megapdf_shrink_images(nullptr, fake_encode, nullptr, nullptr, nullptr) == MEGAPDF_ERR_ARGUMENT, "shrink rejects null");
}

// --------------------------------------------------------------------------
// Contract 7 (#111): render policy. The size assertions are RenderLimitsTests
// (#93/#94) moved here; the corpus banner (33,408 × 22,408 at 300% on a 2× display)
// and the 66,944 × 9,528 strip are the documents that produced them.

void test_render() {
    int w = 0, h = 0;
    megapdf_render_size(4896, 6336, &w, &h);
    check(w == 4896 && h == 6336 && !megapdf_render_is_capped(4896, 6336), "ordinary pages are not touched", std::to_string(w) + "x" + std::to_string(h));
    megapdf_render_size(0, 0, &w, &h);
    check(w == 1 && h == 1, "a zero request becomes 1x1");

    megapdf_render_size(33408, 22408, &w, &h);
    check(megapdf_render_is_capped(33408, 22408) == 1, "the corpus banner is capped");
    check(static_cast<long long>(w) * h <= MEGAPDF_RENDER_MAX_PIXELS && w <= MEGAPDF_RENDER_MAX_SIDE && h <= MEGAPDF_RENDER_MAX_SIDE,
          "the banner comes down to the megapixel budget", std::to_string(w) + "x" + std::to_string(h));
    check(close_to(static_cast<double>(w) / h, 33408.0 / 22408.0, 0.005), "the banner keeps its aspect ratio");

    megapdf_render_size(66944, 9528, &w, &h);
    check(megapdf_render_is_capped(66944, 9528) == 1 && w <= MEGAPDF_RENDER_MAX_SIDE && h <= MEGAPDF_RENDER_MAX_SIDE &&
              static_cast<long long>(w) * h <= MEGAPDF_RENDER_MAX_PIXELS && w >= 14000,
          "a very wide strip is bound by the side limit", std::to_string(w) + "x" + std::to_string(h));
    check(close_to(static_cast<double>(w) / h, 66944.0 / 9528.0, 0.005), "the strip keeps its aspect ratio");

    int w2 = 0, h2 = 0, w3 = 0, h3 = 0;
    megapdf_render_size(33408 * 2.0 / 3.0, 22408 * 2.0 / 3.0, &w2, &h2);
    megapdf_render_size(33408, 22408, &w3, &h3);
    check(std::abs(w2 - w3) <= 1 && std::abs(h2 - h3) <= 1, "the capped size is the same for every zoom past the cap");
}

void test_render_page(const std::string& fixtures) {
    Doc d(fixtures + "/forms.pdf");
    Page p(d.doc, 0);
    if (!p.page) { check(false, "forms.pdf page loads for render"); return; }
    const int w = 153, h = 198;   // a quarter of Letter
    std::vector<unsigned char> bgra(static_cast<size_t>(w) * h * 4, 0);
    check(megapdf_render(p.page, bgra.data(), w, h, w * 4, MEGAPDF_RENDER_BGRA) == MEGAPDF_OK, "render returns OK");
    size_t white = 0, ink = 0;
    for (size_t i = 0; i < bgra.size(); i += 4) {
        if (bgra[i] == 0xFF && bgra[i + 1] == 0xFF && bgra[i + 2] == 0xFF) white++; else ink++;
        if (bgra[i + 3] != 0xFF) { check(false, "rendered pixels are opaque"); break; }
    }
    check(white > ink && ink > 50, "the page renders as mostly white with some ink", std::to_string(ink) + " ink pixels");

    // The checkbox widget draws through the form environment: click it, and the
    // region around its centre gains ink.
    megapdf_form_fields* f = megapdf_form_fields_load(p.page);
    megapdf_form_field field{};
    megapdf_form_field_get(f, 0, &field);
    megapdf_form_fields_free(f);
    auto ink_in_box = [&](const std::vector<unsigned char>& px) {
        size_t n = 0;
        const int x0 = static_cast<int>(field.bounds.left / 612.0 * w), x1 = static_cast<int>(field.bounds.right / 612.0 * w);
        const int y0 = static_cast<int>((792.0 - field.bounds.top) / 792.0 * h), y1 = static_cast<int>((792.0 - field.bounds.bottom) / 792.0 * h);
        for (int y = y0; y <= y1 && y < h; y++) for (int x = x0; x <= x1 && x < w; x++) {
            const unsigned char* q = &px[(static_cast<size_t>(y) * w + x) * 4];
            if (q[0] < 0x80 && q[1] < 0x80 && q[2] < 0x80) n++;
        }
        return n;
    };
    const size_t before = ink_in_box(bgra);
    megapdf_form_click(p.page, (field.bounds.left + field.bounds.right) / 2, (field.bounds.bottom + field.bounds.top) / 2);
    std::vector<unsigned char> after(bgra.size(), 0);
    megapdf_render(p.page, after.data(), w, h, w * 4, MEGAPDF_RENDER_BGRA);
    check(ink_in_box(after) > before, "a checked box draws its check through the form environment",
          std::to_string(before) + " -> " + std::to_string(ink_in_box(after)));

    // RGBA swaps the channel order; the white ground is white either way, and a
    // blue-ish pixel moves channels.
    std::vector<unsigned char> rgba(bgra.size(), 0);
    check(megapdf_render(p.page, rgba.data(), w, h, w * 4, MEGAPDF_RENDER_RGBA) == MEGAPDF_OK, "RGBA render returns OK");
    bool swapped_ok = true;
    for (size_t i = 0; i < bgra.size(); i += 4) {
        if (after[i] != rgba[i + 2] || after[i + 2] != rgba[i] || after[i + 1] != rgba[i + 1]) { swapped_ok = false; break; }
    }
    check(swapped_ok, "RGBA is BGRA with red and blue exchanged");

    // Refusals are status codes.
    check(megapdf_render(p.page, bgra.data(), 0, 10, 40, 0) == MEGAPDF_ERR_ARGUMENT, "a zero-width render is an argument error");
    check(megapdf_render(p.page, bgra.data(), 20000, 20000, 80000, 0) == MEGAPDF_ERR_ARGUMENT, "a raster past the clamp is refused, not attempted");
    check(megapdf_render(p.page, bgra.data(), w, h, w * 2, 0) == MEGAPDF_ERR_ARGUMENT, "a stride too small is an argument error");
    check(megapdf_render(nullptr, bgra.data(), w, h, w * 4, 0) == MEGAPDF_ERR_ARGUMENT && megapdf_render(p.page, nullptr, w, h, w * 4, 0) == MEGAPDF_ERR_ARGUMENT,
          "render rejects nulls");
}

// --------------------------------------------------------------------------
// Phase 3 (#112): body-text editing. FontSubstitutionTests and TextEditSpikeTests
// make the same assertions through the desktop binding.

std::string mapped(const char* name) {
    char buf[64];
    const size_t n = megapdf_map_to_standard_font(name, buf, sizeof buf);
    return std::string(buf, n);
}

// A one-page PDF whose only text is drawn in the non-embedded Symbol font. Symbol's
// built-in encoding has no Latin letters, so no in-place edit can write Latin
// text in it: FPDFText_SetText still "succeeds", and what reads back is Greek.
std::vector<unsigned char> symbol_font_pdf() {
    std::string pdf = "%PDF-1.4\n";
    std::vector<size_t> offsets;
    auto add = [&](const std::string& body) { offsets.push_back(pdf.size()); pdf += std::to_string(offsets.size()) + " 0 obj\n" + body + "\nendobj\n"; };
    add("<< /Type /Catalog /Pages 2 0 R >>");
    add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
    add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
    add("<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>");
    const std::string content = "BT /F1 24 Tf 72 700 Td (abgd) Tj ET";
    add("<< /Length " + std::to_string(content.size()) + " >>\nstream\n" + content + "\nendstream");
    const size_t xref = pdf.size();
    pdf += "xref\n0 " + std::to_string(offsets.size() + 1) + "\n0000000000 65535 f \n";
    for (size_t off : offsets) { char line[32]; std::snprintf(line, sizeof line, "%010zu 00000 n \n", off); pdf += line; }
    pdf += "trailer\n<< /Size " + std::to_string(offsets.size() + 1) + " /Root 1 0 R >>\nstartxref\n" + std::to_string(xref) + "\n%%EOF\n";
    return std::vector<unsigned char>(pdf.begin(), pdf.end());
}

// Two text objects on one baseline in non-embedded Helvetica. PDFium reads the
// first back with a generated trailing space (the separator before "World"), so a
// read-back check that ignores generation would reject a perfectly good edit.
std::vector<unsigned char> two_run_line_pdf() {
    std::string pdf = "%PDF-1.4\n";
    std::vector<size_t> offsets;
    auto add = [&](const std::string& body) { offsets.push_back(pdf.size()); pdf += std::to_string(offsets.size()) + " 0 obj\n" + body + "\nendobj\n"; };
    add("<< /Type /Catalog /Pages 2 0 R >>");
    add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
    add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
    add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
    const std::string content = "BT /F1 12 Tf 72 700 Td (Hello) Tj ET BT /F1 12 Tf 120 700 Td (World) Tj ET";
    add("<< /Length " + std::to_string(content.size()) + " >>\nstream\n" + content + "\nendstream");
    const size_t xref = pdf.size();
    pdf += "xref\n0 " + std::to_string(offsets.size() + 1) + "\n0000000000 65535 f \n";
    for (size_t off : offsets) { char line[32]; std::snprintf(line, sizeof line, "%010zu 00000 n \n", off); pdf += line; }
    pdf += "trailer\n<< /Size " + std::to_string(offsets.size() + 1) + " /Root 1 0 R >>\nstartxref\n" + std::to_string(xref) + "\n%%EOF\n";
    return std::vector<unsigned char>(pdf.begin(), pdf.end());
}

// Two lines in Helvetica under a character spacing of 2 pt. PDFium's content writer has
// no syntax for Tc, so rewriting this stream would pull every letter together (#118).
std::vector<unsigned char> spaced_text_pdf() {
    std::string pdf = "%PDF-1.4\n";
    std::vector<size_t> offsets;
    auto add = [&](const std::string& body) { offsets.push_back(pdf.size()); pdf += std::to_string(offsets.size()) + " 0 obj\n" + body + "\nendobj\n"; };
    add("<< /Type /Catalog /Pages 2 0 R >>");
    add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
    add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
    add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
    const std::string content = "BT /F1 18 Tf 4 Tc 72 700 Td (Spaced heading) Tj ET BT /F1 18 Tf 72 660 Td (Second spaced line) Tj ET";
    add("<< /Length " + std::to_string(content.size()) + " >>\nstream\n" + content + "\nendstream");
    const size_t xref = pdf.size();
    pdf += "xref\n0 " + std::to_string(offsets.size() + 1) + "\n0000000000 65535 f \n";
    for (size_t off : offsets) { char line[32]; std::snprintf(line, sizeof line, "%010zu 00000 n \n", off); pdf += line; }
    pdf += "trailer\n<< /Size " + std::to_string(offsets.size() + 1) + " /Root 1 0 R >>\nstartxref\n" + std::to_string(xref) + "\n%%EOF\n";
    return std::vector<unsigned char>(pdf.begin(), pdf.end());
}

void test_text_editing(const std::string& fixtures) {
    // #118: a page PDFium cannot rewrite faithfully refuses edits and deletions, and is left untouched.
    // Stock PDFium only: the spacing patch (#121) makes this page editable, and
    // test_rewrite_fidelity holds the refusal cases for every patch level.
    if (MEGAPDF_PDFIUM_PATCHES < 1) {
        auto bytes = spaced_text_pdf();
        megapdf_document* d = megapdf_open(bytes.data(), bytes.size(), nullptr);
        check(d != nullptr, "spaced-text pdf opens");
        if (d) {
            Page p(d, 0);
            megapdf_text* t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
            check(megapdf_text_run_count(t) == 2, "spaced page has two runs", std::to_string(megapdf_text_run_count(t)));
            megapdf_text_run first{};
            megapdf_text_run_get(t, 0, &first);
            const U16 text_before = run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT);
            megapdf_text_free(t);
            check(megapdf_text_editable(p.page, first.object_index) == 0, "a page whose character spacing a rewrite would lose is not editable");
            auto howdy = utf16("Howdy");
            int outcome = -1;
            megapdf_detached* original = reinterpret_cast<megapdf_detached*>(1);
            check(megapdf_set_text(p.page, first.object_index, howdy.data(), 0, &outcome, &original) == MEGAPDF_ERR_LAYOUT && original == nullptr,
                  "the edit is refused with MEGAPDF_ERR_LAYOUT");
            check(megapdf_detach_object(p.page, first.object_index) == nullptr, "deleting that body text is refused too");
            t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
            megapdf_text_run after{};
            megapdf_text_run_get(t, 0, &after);
            check(megapdf_text_run_count(t) == 2 && run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT) == text_before && rect_close(after.bounds, first.bounds, 0.01),
                  "the refused page is untouched");
            megapdf_text_free(t);
            check(megapdf_text_editable(nullptr, 0) == MEGAPDF_ERR_ARGUMENT && megapdf_text_editable(p.page, 9999) == MEGAPDF_ERR_ARGUMENT,
                  "editable rejects a bad page or index");
        }
        megapdf_close(d);
    }
    {
        auto bytes = two_run_line_pdf();
        megapdf_document* d = megapdf_open(bytes.data(), bytes.size(), nullptr);
        if (d) {
            Page p(d, 0);
            megapdf_text* t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
            megapdf_text_run r{};
            megapdf_text_run_get(t, 0, &r);
            megapdf_text_free(t);
            check(megapdf_text_editable(p.page, r.object_index) == 1, "a page PDFium rewrites faithfully is editable");
        }
        megapdf_close(d);
    }

    // Undo of a substituted edit restores the original run byte-identical: the core
    // hands back the replaced object instead of destroying it. Undo of an in-place
    // edit sets the old text on the same object with no substitution.
    {
        auto bytes = symbol_font_pdf();
        megapdf_document* d = megapdf_open(bytes.data(), bytes.size(), nullptr);
        if (d) {
            Page p(d, 0);
            megapdf_text* t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
            megapdf_text_run r{};
            megapdf_text_run_get(t, 0, &r);
            const U16 text_before = run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT);
            const U16 font_before = run_string(t, 0, MEGAPDF_TEXT_RUN_FONT);
            megapdf_text_free(t);

            auto latin = utf16("Hello");
            int outcome = -1;
            megapdf_detached* replaced = nullptr;
            check(megapdf_set_text(p.page, r.object_index, latin.data(), 0, &outcome, &replaced) == MEGAPDF_OK &&
                      outcome == MEGAPDF_EDIT_SUBSTITUTED && replaced != nullptr,
                  "a substituted edit hands back the replaced original");

            // Revert: take the substitute off, put the original back at its index.
            megapdf_detached* substitute = megapdf_detach_object(p.page, r.object_index);
            check(substitute != nullptr, "the substitute detaches");
            megapdf_discard_detached(substitute);
            check(megapdf_restore_object(p.page, replaced, r.object_index) == MEGAPDF_OK, "the original restores");
            t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
            megapdf_text_run after{};
            megapdf_text_run_get(t, 0, &after);
            check(megapdf_text_run_count(t) == 1 && run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT) == text_before &&
                      run_string(t, 0, MEGAPDF_TEXT_RUN_FONT) == font_before && after.object_index == r.object_index,
                  "undo restores the original run's text, font and index",
                  show(run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT)) + " / " + show(run_string(t, 0, MEGAPDF_TEXT_RUN_FONT)));
            megapdf_text_free(t);

            // Without a place to hand it, the replaced original is freed (ASan watches).
            megapdf_set_text(p.page, r.object_index, latin.data(), 0, &outcome, nullptr);
            check(outcome == MEGAPDF_EDIT_SUBSTITUTED, "a second substitution without out_replaced still works");
        }
        megapdf_close(d);
    }
    {
        auto bytes = two_run_line_pdf();
        megapdf_document* d = megapdf_open(bytes.data(), bytes.size(), nullptr);
        if (d) {
            Page p(d, 0);
            megapdf_text* t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
            megapdf_text_run r{};
            megapdf_text_run_get(t, 0, &r);
            megapdf_text_free(t);
            auto howdy = utf16("Howdy"), hello = utf16("Hello");
            int outcome = -1;
            megapdf_detached* replaced = nullptr;
            megapdf_set_text(p.page, r.object_index, howdy.data(), 0, &outcome, &replaced);
            check(outcome == MEGAPDF_EDIT_IN_PLACE && replaced != nullptr, "an in-place edit also hands back the untouched original");
            megapdf_detached* edited = megapdf_detach_object(p.page, r.object_index);
            check(edited != nullptr, "the edited run detaches for undo");
            megapdf_discard_detached(edited);
            check(megapdf_restore_object(p.page, replaced, r.object_index) == MEGAPDF_OK, "the original restores after an in-place edit");
            t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
            std::string back = show(run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT));
            megapdf_text_run restored{};
            megapdf_text_run_get(t, 0, &restored);
            megapdf_text_free(t);
            while (!back.empty() && back.back() == ' ') back.pop_back();
            check(back == "Hello" && restored.object_index == r.object_index, "undoing an in-place edit reads back the original text at its index", back);
            (void)hello;
            check(megapdf_set_text(p.page, r.object_index, howdy.data(), 4, &outcome, nullptr) == MEGAPDF_ERR_ARGUMENT,
                  "an unknown flag is an argument error");
        }
        megapdf_close(d);
    }

    // #116 part 2: a generated separator space after the run is not the edit failing.
    {
        auto bytes = two_run_line_pdf();
        megapdf_document* d = megapdf_open(bytes.data(), bytes.size(), nullptr);
        check(d != nullptr, "two-run pdf opens");
        if (d) {
            Page p(d, 0);
            megapdf_text* t = p.page ? megapdf_text_load(p.page, MEGAPDF_TEXT_ALL) : nullptr;
            check(t && megapdf_text_run_count(t) == 2, "two-run page has two runs");
            megapdf_text_run first{};
            megapdf_text_run_get(t, 0, &first);
            const std::string before = show(run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT));
            megapdf_text_free(t);
            auto howdy = utf16("Howdy");
            int outcome = -1;
            check(megapdf_set_text(p.page, first.object_index, howdy.data(), 0, &outcome, nullptr) == MEGAPDF_OK, "two-run edit returns OK");
            check(outcome == MEGAPDF_EDIT_IN_PLACE, "a standard-font run followed by another run is edited in place",
                  "outcome " + std::to_string(outcome) + ", original read back as '" + before + "'");
            t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
            std::string after = show(run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT));
            megapdf_text_free(t);
            while (!after.empty() && after.back() == ' ') after.pop_back();
            check(after == "Howdy", "the edited run reads back as the new text, separator aside", after);
        }
        megapdf_close(d);
    }

    // The corpus defect: a font that cannot carry the new text must not be edited
    // in place and reported as a success. Tier 1 has to prove the text took, or
    // fall back to tier 2 — never garbled, dropped or letter-spaced text.
    {
        auto bytes = symbol_font_pdf();
        megapdf_document* d = megapdf_open(bytes.data(), bytes.size(), nullptr);
        check(d != nullptr, "symbol-font pdf opens");
        if (d) {
            Page p(d, 0);
            megapdf_text* t = p.page ? megapdf_text_load(p.page, MEGAPDF_TEXT_ALL) : nullptr;
            check(t && megapdf_text_run_count(t) == 1, "symbol-font page has one run");
            megapdf_text_run r{};
            megapdf_text_run_get(t, 0, &r);
            megapdf_text_free(t);
            auto latin = utf16("Hello");
            int outcome = -1;
            check(megapdf_set_text(p.page, r.object_index, latin.data(), 0, &outcome, nullptr) == MEGAPDF_OK, "the edit returns OK");
            check(outcome == MEGAPDF_EDIT_SUBSTITUTED, "a font that cannot encode the text is substituted, not edited in place",
                  std::to_string(outcome));
            t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
            check(megapdf_text_run_count(t) == 1 && show(run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT)) == "Hello",
                  "the new text reads back exactly", t ? show(run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT)) : "no text");
            megapdf_text_free(t);
        }
        megapdf_close(d);
    }

    check(megapdf_is_subset_font_name("ABCDEF+SegoeUI") == 1 && megapdf_is_subset_font_name("BCDFGH+Times-Roman") == 1, "subset prefixes are detected");
    check(megapdf_is_subset_font_name("Helvetica") == 0 && megapdf_is_subset_font_name("Arial-BoldMT") == 0 && megapdf_is_subset_font_name("abcdef+lower") == 0 &&
              megapdf_is_subset_font_name(nullptr) == 0,
          "non-subset names are not");
    const char* cases[][2] = {{"SegoeUI", "Helvetica"}, {"Arial-BoldMT", "Helvetica-Bold"}, {"Calibri-Italic", "Helvetica-Oblique"},
                              {"TimesNewRomanPSMT", "Times-Roman"}, {"Times-BoldItalic", "Times-BoldItalic"}, {"Georgia", "Helvetica"},
                              {"LiberationSerif-Bold", "Times-Bold"}, {"CourierNewPSMT", "Courier"}, {"Consolas-Bold", "Helvetica-Bold"},
                              {"RobotoMono-Italic", "Courier-Oblique"}};
    for (const auto& c : cases) check(mapped(c[0]) == c[1], "closest standard face", std::string(c[0]) + " -> " + mapped(c[0]));
    check(megapdf_map_to_standard_font("Whatever", nullptr, 0) == 9, "map reports the length without a buffer");

    // Tier 1: a standard font covers anything.
    {
        Doc d(fixtures + "/fixture.pdf");
        Page p(d.doc, 0);
        if (!p.page) { check(false, "fixture.pdf page loads for editing"); return; }
        auto text = utf16("Symbols beyond original: XYZQ!?");
        int outcome = -1;
        check(megapdf_set_text(p.page, 0, text.data(), 0, &outcome, nullptr) == MEGAPDF_OK && outcome == MEGAPDF_EDIT_IN_PLACE, "a standard-font edit stays tier 1",
              std::to_string(outcome));
        megapdf_text* t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
        check(show(run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT)) == "Symbols beyond original: XYZQ!?", "the new text reads back", show(run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT)));
        megapdf_text_free(t);

        // Forced substitution keeps the index, the position and the colour.
        megapdf_text_run before{};
        t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
        megapdf_text_run_get(t, 0, &before);
        const size_t runs_before = megapdf_text_run_count(t);
        megapdf_text_free(t);
        auto swapped = utf16("Swapped to a standard face");
        check(megapdf_set_text(p.page, before.object_index, swapped.data(), 1, &outcome, nullptr) == MEGAPDF_OK && outcome == MEGAPDF_EDIT_SUBSTITUTED,
              "forced substitution reports tier 2", std::to_string(outcome));
        t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
        megapdf_text_run after{};
        megapdf_text_run_get(t, 0, &after);
        check(megapdf_text_run_count(t) == runs_before && after.object_index == before.object_index, "substitution preserves the object index");
        check(show(run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT)) == "Swapped to a standard face", "substituted text reads back");
        check(close_to(after.bounds.left, before.bounds.left, 5) && close_to(after.bounds.bottom, before.bounds.bottom, 10), "the replacement keeps the original's position",
              rect_str(after.bounds) + " vs " + rect_str(before.bounds));
        megapdf_text_free(t);

        // Errors are status codes.
        auto empty = utf16("");
        check(megapdf_set_text(p.page, 0, empty.data(), 0, &outcome, nullptr) == MEGAPDF_ERR_ARGUMENT, "empty text is an argument error");
        int path_index = -1;
        for (int i = 0; megapdf_object_type(p.page, i) >= 0; i++) if (megapdf_object_type(p.page, i) == 2) { path_index = i; break; }
        check(path_index >= 0 && megapdf_set_text(p.page, path_index, text.data(), 0, &outcome, nullptr) == MEGAPDF_ERR_ARGUMENT, "editing a path is an argument error");
        check(megapdf_set_text(nullptr, 0, text.data(), 0, &outcome, nullptr) == MEGAPDF_ERR_ARGUMENT, "set_text rejects a null page");

        // Crash-recovery replay: a run inserted at an index in the face closest to the journalled name.
        auto inserted = utf16("Replayed");
        check(megapdf_insert_text_run(p.page, 0, inserted.data(), "Arial-BoldMT", 14, 100, 500) == MEGAPDF_OK, "insert_text_run returns OK");
        t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
        megapdf_text_run r0{};
        megapdf_text_run_get(t, 0, &r0);
        check(r0.object_index == 0 && show(run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT)) == "Replayed" && close_to(r0.font_size, 14) &&
                  close_to(r0.bounds.left, 100, 2) && r0.bounds.bottom < 500 && r0.bounds.top > 500,
              "the replayed run sits at index 0 on its baseline", rect_str(r0.bounds));
        check(!run_string(t, 0, MEGAPDF_TEXT_RUN_FONT).empty() && r0.is_text_box == 0, "the replayed run is body text in a real face");
        megapdf_text_free(t);
        std::vector<unsigned char> edited;
        check(megapdf_save(d.doc, collect, &edited) == MEGAPDF_OK, "the edited fixture saves");
        keep_saved("text-editing", edited);
    }
    // A substituted text box keeps its identity (#45); a legacy untagged box stays untagged.
    {
        Doc d(fixtures + "/textbox.pdf");
        Page p(d.doc, 0);
        if (!p.page) { check(false, "textbox.pdf page loads for editing"); return; }
        auto tagged = utf16("text:fixture-1");
        const int tagged_index = megapdf_find_text_box(p.page, tagged.data());
        check(tagged_index >= 0, "the tagged fixture box is found");
        auto retyped = utf16("Retyped box");
        int outcome = -1;
        std::string face_before;
        for (const auto& b : boxes_of(p.page)) if (b.object_index == tagged_index) face_before = b.font;
        check(megapdf_set_text(p.page, tagged_index, retyped.data(), 1, &outcome, nullptr) == MEGAPDF_OK && outcome == MEGAPDF_EDIT_SUBSTITUTED, "the tagged box substitutes");
        check(megapdf_find_text_box(p.page, tagged.data()) == tagged_index, "the substituted box keeps its id at its index");
        auto boxes = boxes_of(p.page);
        bool found = false;
        for (const auto& b : boxes) if (b.object_index == tagged_index) { found = true; check(b.text == "Retyped box" && b.font == face_before, "the substituted box keeps its face param", b.font + " vs " + face_before); }
        check(found, "the substituted box is still a text box");

        int legacy = -1;
        for (const auto& b : boxes) if (b.id.empty()) { legacy = b.object_index; break; }
        check(legacy >= 0, "a legacy box exists");
        check(megapdf_set_text(p.page, legacy, retyped.data(), 1, &outcome, nullptr) == MEGAPDF_OK, "the legacy box substitutes");
        bool still_untagged = false;
        for (const auto& b : boxes_of(p.page)) if (b.object_index == legacy) still_untagged = b.id.empty();
        check(still_untagged, "a legacy box gains no fabricated id");
        std::vector<unsigned char> edited;
        check(megapdf_save(d.doc, collect, &edited) == MEGAPDF_OK, "the retyped text boxes save");
        keep_saved("text-editing", edited);
    }
}

}  // namespace

// --------------------------------------------------------------------------
// #119: what rewriting a page must keep. Any body-text edit makes PDFium regenerate
// the whole content stream, so each case is one page whose first line is retyped
// longer, then saved and reopened: every other line must keep its text and place.
// `fixed_by` is the MegaPDF PDFium patch (tools/pdfium/patches) that makes the case
// editable; 0 means stock PDFium already keeps it. Below that level the guard must
// refuse the edit and leave the page exactly as it was.

// Objects 1-5 are the catalog, page tree, page, /F1 and the content stream; `extra_fonts`
// adds entries to the page's /Font dictionary, and `extra_objects` become objects 6, 7, ...
std::vector<unsigned char> one_page_pdf(const std::string& content, const std::string& font_dict,
                                        const std::string& extra_resources = "", const std::string& extra_fonts = "",
                                        const std::vector<std::string>& extra_objects = {},
                                        const std::string& page_extra = "", const std::string& contents = "5 0 R") {
    std::string pdf = "%PDF-1.4\n";
    std::vector<size_t> offsets;
    auto add = [&](const std::string& body) { offsets.push_back(pdf.size()); pdf += std::to_string(offsets.size()) + " 0 obj\n" + body + "\nendobj\n"; };
    add("<< /Type /Catalog /Pages 2 0 R >>");
    add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
    add("<< /Type /Page /Parent 2 0 R " + page_extra + " /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R " + extra_fonts + " >> " +
        extra_resources + " >> /Contents " + contents + " >>");
    add(font_dict);
    add("<< /Length " + std::to_string(content.size()) + " >>\nstream\n" + content + "\nendstream");
    for (const std::string& object : extra_objects) add(object);
    const size_t xref = pdf.size();
    pdf += "xref\n0 " + std::to_string(offsets.size() + 1) + "\n0000000000 65535 f \n";
    for (size_t off : offsets) { char line[32]; std::snprintf(line, sizeof line, "%010zu 00000 n \n", off); pdf += line; }
    pdf += "trailer\n<< /Size " + std::to_string(offsets.size() + 1) + " /Root 1 0 R >>\nstartxref\n" + std::to_string(xref) + "\n%%EOF\n";
    return std::vector<unsigned char>(pdf.begin(), pdf.end());
}

// The page the layout-guard tests use for a refusal, on every platform (#118, #128): a form XObject
// whose text sets no font, so it draws in the font, size and colour set before its "Do". PDFium's
// writer writes each object's own state and never the text state a form inherits, so regenerating
// the page loses the form's text. (Text used as a clip, 7 Tr, was this page until patch 0018.)
const char* const kFormInheritingTextPage = "BT /F1 24 Tf 72 700 Td (Spaced report) Tj ET q /F1 72 Tf 0 0 1 rg /Fm1 Do Q";
const char* const kFormInheritingTextResources = "/XObject << /Fm1 6 0 R >>";
std::string form_inheriting_text_object() {
    return "<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Length 24 >>\n"
           "stream\nBT 72 480 Td (FORM) Tj ET\nendstream";
}

struct RunShot {
    int object_index;
    megapdf_rect bounds;
    U16 text;   // authored text, trailing separators trimmed
};

std::vector<RunShot> run_shots(const megapdf_page* page) {
    std::vector<RunShot> out;
    megapdf_text* t = megapdf_text_load(page, MEGAPDF_TEXT_ALL);
    for (size_t i = 0; i < megapdf_text_run_count(t); i++) {
        megapdf_text_run r{};
        megapdf_text_run_get(t, i, &r);
        U16 text = run_string(t, i, MEGAPDF_TEXT_RUN_TEXT);
        while (!text.empty() && (text.back() == 0 || text.back() == ' ' || text.back() == '\r' || text.back() == '\n')) text.pop_back();
        out.push_back(RunShot{r.object_index, r.bounds, text});
    }
    megapdf_text_free(t);
    return out;
}

void test_rewrite_fidelity() {
    const int patches = MEGAPDF_PDFIUM_PATCHES;
    const std::string helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
    struct Case {
        const char* name;
        std::string content;
        int fixed_by;
        std::string resources = "";   // extra entries for the page's /Resources
        std::string fonts = "";       // extra entries for its /Font dictionary
        std::vector<std::string> objects = {};   // objects 6, 7, ...
        std::string contents = "5 0 R";          // the page's /Contents
    };
    auto stream_object = [](const std::string& body) {
        return "<< /Length " + std::to_string(body.size()) + " >>\nstream\n" + body + "\nendstream";
    };
    const std::string checker_image = [] {
        std::string pixels;
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++) pixels += static_cast<char>(((x + y) & 1) ? 255 : 0);
        return "<< /Type /XObject /Subtype /Image /Width 16 /Height 16 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 256 >>\nstream\n" +
               pixels + "\nendstream";
    }();
    const std::string type3_glyph = "<< /Length 38 >>\nstream\n1000 0 0 0 750 750 d1 0 0 750 750 re f\nendstream";
    const std::string monospaced_widths = [] {
        std::string widths;
        for (int i = 32; i <= 126; i++) widths += " 600";
        return widths;
    }();
    const std::vector<Case> cases = {
        {"plain text", "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET BT /F1 12 Tf 72 660 Td (Body line under it) Tj ET", 0},
        {"kerned TJ", "BT /F1 18 Tf 72 700 Td [(Ke) -120 (rned) 250 (heading)] TJ ET BT /F1 12 Tf 72 660 Td [(Body) -300 (kerned)] TJ ET", 0},
        {"character spacing, inherited by the next line",
         "BT /F1 18 Tf 4 Tc 72 700 Td (Spaced heading) Tj ET BT /F1 12 Tf 72 660 Td (Inherits the spacing) Tj ET", 1},
        {"word spacing", "BT /F1 18 Tf 10 Tw 72 700 Td (Word spaced heading) Tj ET BT /F1 12 Tf 72 660 Td (Body with several words) Tj ET", 1},
        {"character and word spacing",
         "BT /F1 18 Tf 2 Tc 6 Tw 72 700 Td (Both kinds of spacing) Tj ET BT /F1 12 Tf 72 660 Td (Body with both kinds) Tj ET", 1},
        {"negative character spacing", "BT /F1 18 Tf -0.8 Tc 72 700 Td (Tight heading) Tj ET BT /F1 12 Tf 72 660 Td (Tight body line) Tj ET", 1},
        {"spacing reset for the next line",
         "BT /F1 18 Tf 3 Tc 72 700 Td (Spaced heading) Tj ET BT /F1 12 Tf 0 Tc 72 660 Td (Unspaced body) Tj ET", 1},
        {"spacing only on the untouched line",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET BT /F1 12 Tf 2 Tc 5 Tw 72 660 Td (Spaced body line below) Tj ET", 1},
        {"kerned TJ under character spacing",
         "BT /F1 18 Tf 1.5 Tc 72 700 Td [(Ke) -120 (rned) 250 (heading)] TJ ET BT /F1 12 Tf 72 660 Td [(Body) -300 (kerned)] TJ ET", 1},
        {"horizontal scaling with character spacing",
         "BT /F1 18 Tf 80 Tz 2 Tc 72 700 Td (Scaled spaced heading) Tj ET BT /F1 12 Tf 72 660 Td (Body inherits both) Tj ET", 1},
        // Colour: the stock writer keeps only DeviceRGB and DeviceGray; every other
        // colour space fell back to black (#122).
        {"gray text colour", "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET 0.4 g BT /F1 12 Tf 72 660 Td (Gray body line) Tj ET", 0},
        {"CMYK text colour", "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET 0.1 0.9 0.2 0 k BT /F1 12 Tf 72 660 Td (Magenta body line) Tj ET", 2},
        {"CMYK outlined text",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET 0 0.8 0.8 0 K 1 w BT /F1 16 Tf 1 Tr 72 660 Td (Outlined body line) Tj ET", 2},
        {"CMYK box beside the text",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET 0.7 0.1 0 0 k 72 600 200 30 re f 0 g BT /F1 12 Tf 72 560 Td (Body under a box) Tj ET", 2},
        {"spot (Separation) colour",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET /CS1 cs 1 scn BT /F1 12 Tf 72 660 Td (Spot coloured body) Tj ET", 2,
         "/ColorSpace << /CS1 [/Separation /Spot /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 0.2 0] /N 1 >>] >>"},
        {"indexed colour",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET /CS1 cs 1 sc BT /F1 12 Tf 72 660 Td (Indexed colour body) Tj ET", 2,
         "/ColorSpace << /CS1 [/Indexed /DeviceRGB 1 <FF000000A040>] >>"},
        {"CalRGB colour",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET /CS1 cs 0.2 0.6 0.9 sc BT /F1 12 Tf 72 660 Td (Calibrated body) Tj ET", 2,
         "/ColorSpace << /CS1 [/CalRGB << /WhitePoint [0.9505 1 1.089] /Gamma [2.2 2.2 2.2] >>] >>"},
        {"Lab colour",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET /CS1 cs 50 60 -40 sc BT /F1 12 Tf 72 660 Td (Lab coloured body) Tj ET", 2,
         "/ColorSpace << /CS1 [/Lab << /WhitePoint [0.9505 1 1.089] /Range [-100 100 -100 100] >>] >>"},
        // Fonts: Type3 text was dropped from a rewritten stream, after an unbalanced
        // q BT (#123); and two different fonts sharing a BaseFont were merged into
        // whichever the writer met first.
        {"Type3 text on the untouched line",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET BT /F2 14 Tf 72 660 Td (aaaa) Tj ET BT /F1 12 Tf 72 620 Td (After the Type3 line) Tj ET", 3,
         "", "/F2 6 0 R",
         {"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 750 750] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /a 7 0 R >> "
          "/Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [1000] /Resources << >> >>",
          type3_glyph}},
        {"two fonts sharing a BaseFont",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET BT /F2 14 Tf 72 660 Td (ABBA) Tj ET", 3,
         "", "/F2 6 0 R",
         {"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding "
          "/Differences [65 /Z 66 /Y] >> >>"}},
        // Objects the stock writer dropped or never wrote: inline images and shading
        // (sh) objects (#124). Forms and clipping are written by stock PDFium.
        {"inline image beside the text",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q 100 0 0 50 72 580 cm BI /W 2 /H 2 /BPC 8 /CS /RGB /F /AHx ID "
         "FF000000FF000000FFFFFF00> EI Q BT /F1 12 Tf 72 540 Td (Body under an image) Tj ET", 4},
        {"shading (sh) under a clip",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q 72 570 330 40 re W n /Sh1 sh Q BT /F1 12 Tf 72 540 Td (Body under a gradient) Tj ET", 4,
         "/Shading << /Sh1 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [72 580 400 580] "
         "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 1 0] /C1 [0 0.5 1] /N 1 >> >> >>"},
        {"form XObject beside the text",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q 1 0 0 1 72 580 cm /Fm1 Do Q BT /F1 12 Tf 72 540 Td (Body under a form) Tj ET", 0,
         "/XObject << /Fm1 6 0 R >>", "",
         {"<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Length 24 >>\nstream\n0 0 1 rg 0 0 100 50 re f\nendstream"}},
        {"clipped body text",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q 72 650 90 30 re W n BT /F1 14 Tf 72 660 Td (Clipped body line runs past its clip) Tj ET Q", 0},
        // Several content streams (#125). PDFium regenerated only the stream holding the
        // edit, inside its own q/Q; a text block or saved state that straddled a stream
        // boundary lost what the untouched neighbour depended on, and objects vanished.
        {"four self-contained content streams",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET", 0, "", "",
         {stream_object("BT /F1 12 Tf 72 660 Td (Second stream line) Tj ET"), stream_object("BT /F1 12 Tf 72 630 Td (Third stream line) Tj ET"),
          stream_object("0 0 1 rg 72 580 200 20 re f")},
         "[5 0 R 6 0 R 7 0 R 8 0 R]"},
        {"text block split across two content streams",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET BT /F1 12 Tf 72 660 Td", 5, "", "",
         {stream_object("(Body split across two streams) Tj ET")},
         "[5 0 R 6 0 R]"},
        {"saved state split across two content streams",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q 1 0 0 1 0 -40 cm", 5, "", "",
         {stream_object("BT /F1 12 Tf 72 660 Td (Body in a shifted state) Tj ET Q BT /F1 12 Tf 72 600 Td (After the restore) Tj ET")},
         "[5 0 R 6 0 R]"},
        // A transform left open across streams that does not commute with the next one (#125).
        // After a regenerated stream PDFium restored the CTM as prev^-1 * ctm rather than
        // ctm * prev^-1, so the streams after it drew in the wrong place; once patch 5
        // regenerated every stream it hit any page whose producer leaves a flip or scale open.
        // The edited heading sits in the middle stream; translations alone commute and hid it.
        {"scale then translation left open across content streams",
         "q 0.5 0 0 0.5 0 0 cm", 7, "", "",
         {stream_object("q 1 0 0 1 100 400 cm BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET"),
          stream_object("BT /F1 12 Tf 72 600 Td (Body after both transforms) Tj ET Q Q")},
         "[5 0 R 6 0 R 7 0 R]"},
        // An open path shaped like a rectangle (#125). The writer wrote any four-cornered path
        // as "re", which is closed, so a stroked three-sided box gained its fourth side.
        {"stroked open three-sided box",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q 4 w 0 0 1 RG 72 500 m 72 560 l 272 560 l 272 500 l S Q", 8},
        // Images drawn inside a q ... cm that one stream opens and the next closes (#125). The
        // writer threaded the CTM between regenerated streams through float inverses of large
        // image matrices, and the drift resampled the images.
        {"images whose saved state straddles content streams",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q 431.52 0 0 -186.24 90 666 cm /Im1 Do", 9, "/XObject << /Im1 6 0 R >>", "",
         {checker_image,
          stream_object("Q q 112.32 0 0 -46.56 409.68 136.56 cm /Im1 Do"),
          stream_object("Q BT /F1 12 Tf 72 420 Td (Body under the images) Tj ET")},
         "[5 0 R 7 0 R 8 0 R]"},
        // Graphics state (#125): the writer knew only alpha and blend mode, so soft masks
        // and the other ExtGState entries were dropped, and the miter limit was never written.
        {"soft mask over a box",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q /GS1 gs 0 0 1 rg 72 560 200 60 re f Q BT /F1 12 Tf 72 520 Td (Body under a masked box) Tj ET", 6,
         "/ExtGState << /GS1 << /SMask << /S /Luminosity /G 6 0 R >> >> >>", "",
         {"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Group << /S /Transparency /CS /DeviceGray >> /Length 22 >>\nstream\n1 g 72 560 100 60 re f\nendstream"}},
        // Soft masks set under a flipped, scaled CTM (#140). PDFium fixes a mask's matrix to the CTM
        // at its "gs"; the writer replayed the ExtGState before the object's "cm", at the identity,
        // so once the page was parsed again the mask covered another part of it. The mask lets
        // through only x < 200 pt; replayed at the identity it let through the whole box or line.
        {"soft mask over a box under a flipped, scaled CTM",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q 0.5 0 0 -0.5 0 792 cm /GS1 gs 0 0 1 rg 144 344 400 120 re f Q "
         "BT /F1 12 Tf 72 520 Td (Body under a masked box) Tj ET", 13,
         "/ExtGState << /GS1 << /SMask << /S /Luminosity /G 6 0 R >> >> >>", "",
         {"<< /Type /XObject /Subtype /Form /BBox [0 0 1224 1584] /Group << /S /Transparency /CS /DeviceGray >> /Length 21 >>\nstream\n1 g 0 0 400 1584 re f\nendstream"}},
        {"soft mask over text under a flipped, scaled CTM",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q 0.5 0 0 -0.5 0 792 cm /GS1 gs "
         "BT /F1 48 Tf 1 0 0 -1 144 464 Tm (Masked body words here) Tj ET Q BT /F1 12 Tf 72 500 Td (Body after it) Tj ET", 13,
         "/ExtGState << /GS1 << /SMask << /S /Luminosity /G 6 0 R >> >> >>", "",
         {"<< /Type /XObject /Subtype /Form /BBox [0 0 1224 1584] /Group << /S /Transparency /CS /DeviceGray >> /Length 21 >>\nstream\n1 g 0 0 400 1584 re f\nendstream"}},
        // A font written straight into the page's resources with its own /Widths (#141). The writer
        // rebuilt a direct font dictionary from its base font and encoding alone, so every run in it
        // came back at the standard font's advances.
        {"font dictionary written directly in the resources, with its own widths",
         "BT /F2 18 Tf 72 700 Td (Plain heading) Tj ET BT /F2 12 Tf 72 660 Td (Body in the same direct font) Tj ET", 14,
         "", "/F2 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths [" +
                 monospaced_widths + " ] >>"},
        // A form XObject with no /Resources of its own draws with the page's (#125). The writer kept
        // only the resources the page's own objects name, so the image or font that only the form's
        // content names was removed, and the form drew nothing, or its text in a substitute font.
        {"form XObject without resources, drawing an image named by the page",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q /Fm1 Do Q BT /F1 12 Tf 72 540 Td (Body under a form) Tj ET", 15,
         "/XObject << /Fm1 6 0 R /Im1 7 0 R >>", "",
         {"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Length 33 >>\nstream\nq 200 0 0 100 72 580 cm /Im1 Do Q\nendstream",
          checker_image}},
        {"form XObject without resources, drawing text in a font named by the page",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q /Fm1 Do Q BT /F1 12 Tf 72 540 Td (Body under a form) Tj ET", 15,
         "/XObject << /Fm1 6 0 R >>", "/F2 7 0 R",
         {"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Length 40 >>\nstream\nBT /F2 20 Tf 72 600 Td (MMMM WWWW) Tj ET\nendstream",
          "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>"}},
        // Text stroked under a scaled CTM (#125). PDFium folds the CTM into the text matrix and keeps
        // it apart for the stroke; the writer wrote the text matrix alone, so the line width applied
        // in page space and the outline came back ten or more times thicker.
        {"outlined text under a scaled CTM",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q 0.1 0 0 0.1 0 0 cm 2 Tr 12 w BT /F1 240 Tf 720 6000 Td (Outlined body) Tj ET Q "
         "BT /F1 12 Tf 72 540 Td (Body after it) Tj ET", 16},
        {"stroked text under a flipped, scaled CTM",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q 0.5 0 0 -0.5 0 792 cm 1 Tr 4 w "
         "BT /F1 48 Tf 1 0 0 -1 144 400 Tm (Stroked flipped body) Tj ET Q BT /F1 12 Tf 72 500 Td (Body after it) Tj ET", 16},
        // A tiling pattern set through a [/Pattern base] colour space (#125). The writer dropped any
        // pattern whose space was an array, so what it filled painted solid black; an uncoloured
        // pattern (PaintType 2) also needs its components written in the base space.
        {"coloured tiling pattern through a [/Pattern /DeviceRGB] space",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q /CS1 cs /P1 scn 72 560 300 60 re f Q "
         "BT /F1 12 Tf 72 520 Td (Body under a patterned box) Tj ET", 17,
         "/ColorSpace << /CS1 [/Pattern /DeviceRGB] >> /Pattern << /P1 6 0 R >>", "",
         {"<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20 /Resources << >> "
          "/Length 23 >>\nstream\n1 0 0 rg 0 0 10 10 re f\nendstream"}},
        {"uncoloured tiling pattern with its colour in the base space",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q /CS1 cs 0 0.4 1 /P1 scn 72 560 300 60 re f Q "
         "BT /F1 12 Tf 72 520 Td (Body under a patterned box) Tj ET", 17,
         "/ColorSpace << /CS1 [/Pattern /DeviceRGB] >> /Pattern << /P1 6 0 R >>", "",
         {"<< /Type /Pattern /PatternType 1 /PaintType 2 /TilingType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20 /Resources << >> "
          "/Length 14 >>\nstream\n0 0 10 10 re f\nendstream"}},
        // Glyphs as a clip (7 Tr) over what is drawn after them (#125). The writer wrote only path
        // clips, and the clipping text as its own object inside q ... Q, so the clip ended there and
        // the box drawn through the word filled its whole rectangle.
        {"box drawn through a glyph clip",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q BT /F1 96 Tf 7 Tr 72 560 Td (MASK) Tj ET 0 0 1 rg 72 540 400 140 re f Q "
         "BT /F1 12 Tf 72 500 Td (Body under a clipped box) Tj ET", 18},
        // A gray colour set before a form that sets its own colour by one component (#125). The writer
        // wrote DeviceGray as RGB, so the colour space the form inherits changed with it, and the
        // form's "0.5 SC" was read in RGB: its gray line came back black.
        {"gray stroke colour before a form that sets a colour by its component alone",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q 0 G /Fm1 Do Q BT /F1 12 Tf 72 520 Td (Body under a form) Tj ET", 19,
         "/XObject << /Fm1 6 0 R >>", "",
         {"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Length 31 >>\nstream\n6 w 0.5 SC 72 560 m 400 560 l S\nendstream"}},
        // The miter limit (patch 6 writes "M") is not a case here: PDFium's rasteriser draws a
        // stroked corner identically whatever the limit or join, so the render-based guard
        // cannot see it lost. Its survival belongs to an operator-level check (#126).
        {"gradient pattern on text",
         "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET /Pattern cs /P1 scn BT /F1 24 Tf 72 640 Td (Gradient body line) Tj ET", 2,
         "/Pattern << /P1 << /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [72 0 400 0] "
         "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >> >> >>"},
    };

    const auto replacement = utf16("A much longer replacement for the heading");
    U16 replacement_text(replacement.begin(), replacement.end() - 1);
    for (const Case& c : cases) {
        const std::string name = c.name;
        auto bytes = one_page_pdf(c.content, helvetica, c.resources, c.fonts, c.objects, "", c.contents);
        megapdf_document* d = megapdf_open(bytes.data(), bytes.size(), nullptr);
        check(d != nullptr, name + ": opens");
        if (!d) continue;
        {
            Page p(d, 0);
            megapdf_text* t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
            size_t first_run = 0;
            const bool has_line = megapdf_text_line_count(t) > 0 && megapdf_text_line_runs(t, 0, &first_run, 1) > 0;
            megapdf_text_run target{};
            if (has_line) megapdf_text_run_get(t, first_run, &target);
            megapdf_text_free(t);
            check(has_line, name + ": has a first line");
            const std::vector<RunShot> before = run_shots(p.page);
            const bool expect_editable = patches >= c.fixed_by;
            const int editable = megapdf_text_editable(p.page, target.object_index);
            check(editable == (expect_editable ? 1 : 0), name + ": editable with " + std::to_string(patches) + " patch(es)",
                  std::to_string(editable));
            megapdf_layout_verdict why{};
            const int reason = megapdf_text_editable_reason(p.page, target.object_index, &why);
            check(reason == editable && why.editable == editable && (why.cause == MEGAPDF_LAYOUT_OK) == (editable == 1),
                  name + ": the layout verdict agrees (#128)", "cause " + std::to_string(why.cause));

            int outcome = -1;
            const int status = megapdf_set_text(p.page, target.object_index, replacement.data(), 0, &outcome, nullptr);
            if (!expect_editable) {
                check(status == MEGAPDF_ERR_LAYOUT, name + ": refused while unpatched", std::to_string(status));
                const std::vector<RunShot> after = run_shots(p.page);
                bool untouched = after.size() == before.size();
                for (size_t i = 0; untouched && i < before.size(); i++)
                    untouched = after[i].text == before[i].text && rect_close(after[i].bounds, before[i].bounds, 0.01);
                check(untouched, name + ": a refused page is untouched");
            } else {
                check(status == MEGAPDF_OK, name + ": the edit goes through", std::to_string(status));
                std::vector<unsigned char> saved;
                check(megapdf_save(d, collect, &saved) == MEGAPDF_OK, name + ": saves");
                keep_saved("rewrite-fidelity", saved);
                megapdf_document* again = megapdf_open(saved.data(), saved.size(), nullptr);
                check(again != nullptr, name + ": the saved file reopens");
                if (again) {
                    Page q(again, 0);
                    const std::vector<RunShot> reopened = run_shots(q.page);
                    bool edited_found = false;
                    for (const RunShot& r : reopened) edited_found = edited_found || r.text == replacement_text;
                    check(edited_found, name + ": the new text reads back after reopening");
                    for (const RunShot& b : before) {
                        if (b.object_index == target.object_index || b.text.empty()) continue;
                        double best = 1e9;
                        for (const RunShot& r : reopened) {
                            if (r.text != b.text) continue;
                            const double moved = std::max({std::fabs(r.bounds.left - b.bounds.left), std::fabs(r.bounds.bottom - b.bounds.bottom),
                                                           std::fabs(r.bounds.right - b.bounds.right), std::fabs(r.bounds.top - b.bounds.top)});
                            best = std::min(best, moved);
                        }
                        check(best <= 0.5, name + ": an untouched line keeps its text and place", best > 1e8 ? "text changed" : std::to_string(best) + " pt");
                    }
                }
                megapdf_close(again);
            }
        }
        megapdf_close(d);
    }
}

// --------------------------------------------------------------------------
// #126: the edits people actually make, beyond fixing a spelling mistake. These are
// plain pages every PDFium level can rewrite; what is under test is the edit itself:
// what it accepts, what reads back, where it lands, what undo restores.

U16 u16(const char* utf8) {
    U16 out;
    const unsigned char* c = reinterpret_cast<const unsigned char*>(utf8);
    while (*c) {
        uint32_t cp;
        if (*c < 0x80) { cp = *c++; }
        else if ((*c >> 5) == 0x6) { cp = ((c[0] & 0x1Fu) << 6) | (c[1] & 0x3Fu); c += 2; }
        else if ((*c >> 4) == 0xE) { cp = ((c[0] & 0x0Fu) << 12) | ((c[1] & 0x3Fu) << 6) | (c[2] & 0x3Fu); c += 3; }
        else { cp = ((c[0] & 0x07u) << 18) | ((c[1] & 0x3Fu) << 12) | ((c[2] & 0x3Fu) << 6) | (c[3] & 0x3Fu); c += 4; }
        if (cp >= 0x10000) {
            cp -= 0x10000;
            out.push_back(static_cast<unsigned short>(0xD800 + (cp >> 10)));
            out.push_back(static_cast<unsigned short>(0xDC00 + (cp & 0x3FF)));
        } else {
            out.push_back(static_cast<unsigned short>(cp));
        }
    }
    out.push_back(0);
    return out;
}

U16 without_nul(const U16& z) { return z.empty() ? z : U16(z.begin(), z.end() - 1); }

struct OpenDoc {
    std::vector<unsigned char> bytes;
    megapdf_document* doc = nullptr;
    explicit OpenDoc(std::vector<unsigned char> b) : bytes(std::move(b)) { doc = megapdf_open(bytes.data(), bytes.size(), nullptr); }
    ~OpenDoc() { megapdf_close(doc); }
    OpenDoc(const OpenDoc&) = delete;
    OpenDoc& operator=(const OpenDoc&) = delete;
};

int first_line_object(const megapdf_page* page, size_t line = 0) {
    megapdf_text* t = megapdf_text_load(page, MEGAPDF_TEXT_ALL);
    int index = -1;
    size_t run = 0;
    if (megapdf_text_line_count(t) > line && megapdf_text_line_runs(t, line, &run, 1) > 0) {
        megapdf_text_run r{};
        megapdf_text_run_get(t, run, &r);
        index = r.object_index;
    }
    megapdf_text_free(t);
    return index;
}

int index_of_text(const megapdf_page* page, const char* text) {
    const U16 want = without_nul(u16(text));
    for (const RunShot& r : run_shots(page)) if (r.text == want) return r.object_index;
    return -1;
}

U16 text_of(const megapdf_page* page, int object_index) {
    for (const RunShot& r : run_shots(page)) if (r.object_index == object_index) return r.text;
    return {};
}

double font_size_of(const megapdf_page* page, int object_index) {
    megapdf_text* t = megapdf_text_load(page, MEGAPDF_TEXT_ALL);
    double size = -1;
    for (size_t i = 0; i < megapdf_text_run_count(t); i++) {
        megapdf_text_run r{};
        megapdf_text_run_get(t, i, &r);
        if (r.object_index == object_index) size = r.font_size;
    }
    megapdf_text_free(t);
    return size;
}

std::vector<unsigned char> save_bytes(megapdf_document* d, const char* test = "edit-scenarios") {
    std::vector<unsigned char> out;
    megapdf_save(d, collect, &out);
    keep_saved(test, out);
    return out;
}

std::vector<unsigned char> render_page(const megapdf_page* page) {
    std::vector<unsigned char> px(612 * 792 * 4, 0);
    megapdf_render(page, px.data(), 612, 792, 612 * 4, MEGAPDF_RENDER_BGRA);
    return px;
}

bool same_runs(const std::vector<RunShot>& a, const std::vector<RunShot>& b, double tol) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); i++)
        if (a[i].text != b[i].text || !rect_close(a[i].bounds, b[i].bounds, tol)) return false;
    return true;
}

void test_edit_scenarios() {
    const std::string helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
    const std::string two_lines = "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET BT /F1 12 Tf 72 660 Td (Body line under it) Tj ET";

    // Accents, French punctuation, symbols, money and dates all exist in WinAnsi: each
    // stays in the run's own font and reads back exactly.
    for (const char* text : {"Reçu : « déjà payé » l’été", "€1 234,56 © 2026 – “reçu”", "$1,234.56 due 2026-09-13",
                             "13 sept. 2026, 14 h 05"}) {
        OpenDoc d(one_page_pdf(two_lines, helvetica));
        Page p(d.doc, 0);
        const int idx = first_line_object(p.page);
        const U16 want = u16(text);
        int outcome = -1;
        check(megapdf_set_text(p.page, idx, want.data(), 0, &outcome, nullptr) == MEGAPDF_OK, std::string("retype: ") + text);
        check(outcome == MEGAPDF_EDIT_IN_PLACE, std::string("stays in the run's own font: ") + text, std::to_string(outcome));
        check(text_of(p.page, idx) == without_nul(want), std::string("reads back exactly: ") + text);
    }

    // Text no available face can draw is refused, and the page is left as it was (#130).
    for (const char* text : {"Invoice 請求書", "Paid ✅", "Thanks 😀"}) {
        OpenDoc d(one_page_pdf(two_lines, helvetica));
        Page p(d.doc, 0);
        const int idx = first_line_object(p.page);
        const auto before = run_shots(p.page);
        const U16 want = u16(text);
        int outcome = -1;
        const int status = megapdf_set_text(p.page, idx, want.data(), 0, &outcome, nullptr);
        check(status == MEGAPDF_ERR_NO_FONT, std::string("refused, not a single glyph missing: ") + text, "status " + std::to_string(status));
        check(same_runs(before, run_shots(p.page), 0.01), std::string("and the page is untouched: ") + text);
    }
    {
        OpenDoc d(one_page_pdf(two_lines, helvetica));
        Page p(d.doc, 0);
        const int idx = first_line_object(p.page);
        const auto before = run_shots(p.page);
        const U16 cjk = u16("日本語の見出し");
        int outcome = -1;
        megapdf_detached* original = nullptr;
        const int status = megapdf_set_text(p.page, idx, cjk.data(), 0, &outcome, &original);
        check(status == MEGAPDF_ERR_NO_FONT, "text no standard face can draw is refused",
              "status " + std::to_string(status) + " outcome " + std::to_string(outcome));
        if (status == MEGAPDF_OK) megapdf_discard_detached(original);
        check(same_runs(before, run_shots(p.page), 0.01), "a refused edit leaves the page untouched");
    }

    // Empty text is an argument error, not a deletion.
    {
        OpenDoc d(one_page_pdf(two_lines, helvetica));
        Page p(d.doc, 0);
        const U16 empty = {0};
        int outcome = -1;
        check(megapdf_set_text(p.page, first_line_object(p.page), empty.data(), 0, &outcome, nullptr) == MEGAPDF_ERR_ARGUMENT,
              "empty text is refused as an argument error");
    }

    // Much longer, then down to one word: each edit replaces the run at its own index.
    {
        OpenDoc d(one_page_pdf(two_lines, helvetica));
        Page p(d.doc, 0);
        const int idx = first_line_object(p.page);
        const auto before = run_shots(p.page);
        const U16 longer = u16("A heading that has grown to several times the length it had before the edit");
        const U16 shorter = u16("Short");
        int outcome = -1;
        check(megapdf_set_text(p.page, idx, longer.data(), 0, &outcome, nullptr) == MEGAPDF_OK, "lengthen a line a lot");
        megapdf_rect grown{};
        for (const RunShot& r : run_shots(p.page)) if (r.object_index == idx) grown = r.bounds;
        // Bounds are glyph boxes, so the left edge moves by the difference in the first
        // letters' side bearings ("P" to "A"), not by where the line starts.
        check(grown.right > before[0].bounds.right + 100 && std::fabs(grown.left - before[0].bounds.left) <= 2.0,
              "the longer line grows to the right from the same start",
              "left " + std::to_string(before[0].bounds.left) + " -> " + std::to_string(grown.left) + ", right " +
                  std::to_string(before[0].bounds.right) + " -> " + std::to_string(grown.right));
        check(megapdf_set_text(p.page, idx, shorter.data(), 0, &outcome, nullptr) == MEGAPDF_OK, "then shorten it to one word");
        check(text_of(p.page, idx) == without_nul(shorter) && run_shots(p.page).size() == before.size(),
              "the last edit reads back and no run was added or lost");
    }

    // Two edits to one line, undone in reverse order, end exactly where it started.
    {
        OpenDoc d(one_page_pdf(two_lines, helvetica));
        Page p(d.doc, 0);
        const int idx = first_line_object(p.page);
        const auto before = run_shots(p.page);
        const U16 first = u16("First revision"), second = u16("Second revision");
        megapdf_detached* r1 = nullptr;
        megapdf_detached* r2 = nullptr;
        int outcome = -1;
        check(megapdf_set_text(p.page, idx, first.data(), 0, &outcome, &r1) == MEGAPDF_OK && r1, "first edit hands back the original");
        check(megapdf_set_text(p.page, idx, second.data(), 0, &outcome, &r2) == MEGAPDF_OK && r2, "second edit hands back the first revision");
        megapdf_discard_detached(megapdf_detach_object(p.page, idx));
        check(megapdf_restore_object(p.page, r2, idx) == MEGAPDF_OK && text_of(p.page, idx) == without_nul(first), "undo the second edit");
        megapdf_discard_detached(megapdf_detach_object(p.page, idx));
        check(megapdf_restore_object(p.page, r1, idx) == MEGAPDF_OK, "undo the first edit");
        check(same_runs(before, run_shots(p.page), 0.01), "two undos put the page back exactly");
    }

    // Two lines edited, only the heading undone: the body keeps its edit.
    {
        OpenDoc d(one_page_pdf(two_lines, helvetica));
        Page p(d.doc, 0);
        const int head = first_line_object(p.page, 0), body = first_line_object(p.page, 1);
        const U16 head_text = text_of(p.page, head);
        const U16 new_head = u16("New heading"), new_body = u16("New body line");
        megapdf_detached* r_head = nullptr;
        megapdf_detached* r_body = nullptr;
        int outcome = -1;
        check(megapdf_set_text(p.page, head, new_head.data(), 0, &outcome, &r_head) == MEGAPDF_OK, "edit the heading");
        check(megapdf_set_text(p.page, body, new_body.data(), 0, &outcome, &r_body) == MEGAPDF_OK, "edit the body");
        megapdf_discard_detached(megapdf_detach_object(p.page, head));
        check(megapdf_restore_object(p.page, r_head, head) == MEGAPDF_OK, "undo only the heading");
        check(text_of(p.page, head) == head_text && text_of(p.page, body) == without_nul(new_body),
              "the heading is back and the body keeps its edit");
        megapdf_discard_detached(r_body);
    }

    // Deleting one word of a two-run line, then undoing it.
    {
        OpenDoc d(two_run_line_pdf());
        Page p(d.doc, 0);
        const auto before = run_shots(p.page);
        check(before.size() == 2, "two-run line has two runs", std::to_string(before.size()));
        if (before.size() == 2) {
            megapdf_detached* word = megapdf_detach_object(p.page, before[1].object_index);
            check(word != nullptr && run_shots(p.page).size() == 1, "the second word comes off");
            check(word && megapdf_restore_object(p.page, word, before[1].object_index) == MEGAPDF_OK, "and goes back");
            check(same_runs(before, run_shots(p.page), 0.01), "the line is exactly as it was");
        }
    }

    // Edit, save, reopen, edit again: every generation reads back and the body survives.
    {
        std::vector<unsigned char> bytes = one_page_pdf(two_lines, helvetica);
        for (const char* text : {"Second generation heading", "Third generation heading"}) {
            {
                OpenDoc d(bytes);
                Page p(d.doc, 0);
                const U16 want = u16(text);
                int outcome = -1;
                check(megapdf_set_text(p.page, first_line_object(p.page), want.data(), 0, &outcome, nullptr) == MEGAPDF_OK,
                      std::string("edit generation: ") + text);
                bytes = save_bytes(d.doc);
            }
            OpenDoc again(bytes);
            Page q(again.doc, 0);
            check(index_of_text(q.page, text) >= 0 && index_of_text(q.page, "Body line under it") >= 0,
                  std::string("after reopening, the edit and the body are both there: ") + text);
        }
    }

    // A rotated page, rotated text and skewed (faux-italic) text: the edit lands where the
    // line was, reads back exactly, and the rest stays put.
    const std::string rotated_text =
        "BT /F1 18 Tf 0.7071 0.7071 -0.7071 0.7071 150 450 Tm (Rotated heading) Tj ET BT /F1 12 Tf 72 700 Td (Body line under it) Tj ET";
    const std::string skewed_text = "BT /F1 18 Tf 1 0 0.3 1 72 600 Tm (Skewed heading) Tj ET BT /F1 12 Tf 72 700 Td (Body line under it) Tj ET";
    for (const auto& [name, content, page_extra, target] : {
             std::tuple<const char*, std::string, std::string, const char*>{"rotated page", two_lines, "/Rotate 90", "Plain heading"},
             std::tuple<const char*, std::string, std::string, const char*>{"rotated text", rotated_text, "", "Rotated heading"},
             std::tuple<const char*, std::string, std::string, const char*>{"skewed text", skewed_text, "", "Skewed heading"}}) {
        OpenDoc d(one_page_pdf(content, helvetica, "", "", {}, page_extra));
        Page p(d.doc, 0);
        const int idx = index_of_text(p.page, target);
        check(idx >= 0, std::string(name) + ": the line is found");
        if (idx < 0) continue;
        megapdf_rect was{};
        megapdf_rect body_was{};
        for (const RunShot& r : run_shots(p.page)) {
            if (r.object_index == idx) was = r.bounds;
            else body_was = r.bounds;
        }
        const U16 want = u16("Edited line");
        int outcome = -1;
        check(megapdf_set_text(p.page, idx, want.data(), 0, &outcome, nullptr) == MEGAPDF_OK, std::string(name) + ": edits");
        auto bytes = save_bytes(d.doc);
        OpenDoc again(bytes);
        Page q(again.doc, 0);
        bool read_back = false, landed = false, body_kept = false;
        for (const RunShot& r : run_shots(q.page)) {
            if (r.text == without_nul(want)) {
                read_back = true;
                landed = r.bounds.left < was.right && was.left < r.bounds.right && r.bounds.bottom < was.top && was.bottom < r.bounds.top;
            } else if (rect_close(r.bounds, body_was, 0.5)) {
                body_kept = true;
            }
        }
        check(read_back, std::string(name) + ": the edit reads back exactly after reopening");
        check(landed, std::string(name) + ": the edit overlaps where the line was");
        check(body_kept, std::string(name) + ": the other line keeps its place");
    }

    // Invisible OCR text over a scan (render mode 3): correcting it must not paint it.
    {
        OpenDoc d(one_page_pdf("0.85 g 72 500 468 250 re f 0 g BT 3 Tr /F1 14 Tf 90 700 Td (Scanned invoice words) Tj ET", helvetica));
        Page p(d.doc, 0);
        const auto before_px = render_page(p.page);
        const int idx = index_of_text(p.page, "Scanned invoice words");
        const U16 want = u16("Corrected invoice words");
        int outcome = -1;
        check(idx >= 0 && megapdf_set_text(p.page, idx, want.data(), 0, &outcome, nullptr) == MEGAPDF_OK, "OCR text layer: edits");
        const auto after_px = render_page(p.page);
        size_t differing = 0;
        for (size_t i = 0; i < before_px.size(); i += 4)
            if (std::abs(before_px[i] - after_px[i]) + std::abs(before_px[i + 1] - after_px[i + 1]) + std::abs(before_px[i + 2] - after_px[i + 2]) > 60)
                differing++;
        check(differing == 0, "OCR text layer: the corrected text stays invisible", std::to_string(differing) + " pixels");
        check(index_of_text(p.page, "Corrected invoice words") >= 0, "OCR text layer: and is still there to search and copy");
    }

    // A substituted font keeps the line's size and starting point.
    {
        OpenDoc d(symbol_font_pdf());
        Page p(d.doc, 0);
        const auto before = run_shots(p.page);
        const int idx = before.empty() ? -1 : before[0].object_index;
        const double size = font_size_of(p.page, idx);
        const U16 hello = u16("Hello");
        int outcome = -1;
        check(idx >= 0 && megapdf_set_text(p.page, idx, hello.data(), 0, &outcome, nullptr) == MEGAPDF_OK && outcome == MEGAPDF_EDIT_SUBSTITUTED,
              "a Symbol-font line takes Latin text through a substitute");
        megapdf_rect now{};
        for (const RunShot& r : run_shots(p.page)) if (r.object_index == idx) now = r.bounds;
        check(std::fabs(font_size_of(p.page, idx) - size) < 0.01, "the substitute keeps the font size");
        // Glyph boxes again: a Symbol alpha and a Helvetica H have different side bearings.
        check(!before.empty() && std::fabs(now.left - before[0].bounds.left) <= 2.0, "the substitute starts where the line started",
              before.empty() ? "" : "left " + std::to_string(before[0].bounds.left) + " -> " + std::to_string(now.left));
    }
}

// The first text run on page 1, ASCII only (enough to see the document decrypted).
std::string first_run_text(megapdf_document* d) {
    Page p(d, 0);
    if (!p.page) return "";
    megapdf_text* t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
    const size_t n = megapdf_text_run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT, nullptr, 0);
    std::vector<unsigned short> s(n);
    if (n) megapdf_text_run_string(t, 0, MEGAPDF_TEXT_RUN_TEXT, s.data(), n);
    megapdf_text_free(t);
    std::string out;
    for (unsigned short c : s)
        if (c) out += c < 128 ? static_cast<char>(c) : '?';
    return out;
}

// #131: every standard security handler the apps open, what an open may do, and copies
// written with new security or none. Fixtures: tools/gen_security_fixtures.sh.
void test_security(const std::string& fixtures) {
    const std::string dir = MEGAPDF_SECURITY_FIXTURES;
    struct Encrypted { const char* file; int revision; const char* user; const char* owner; };
    const Encrypted matrix[] = {
        {"rc4-40.pdf", 2, "u-rc4-40", "o-rc4-40"},
        {"rc4-128.pdf", 3, "u-rc4-128", "o-rc4-128"},
        {"aes-128.pdf", 4, "u-aes-128", "o-aes-128"},
        {"aes-256.pdf", 6, "u-aes-256", "o-aes-256"},
        {"nonascii-aes-256.pdf", 6, "cl\xC3\xA9-\xC3\xA9t\xC3\xA9", "o-nonascii"},
        {"nonascii-rc4-128.pdf", 3, "cl\xC3\xA9", "o-nonascii-rc4"},
        {"metadata-clear.pdf", 6, "u-meta", "o-meta"},
    };
    for (const Encrypted& f : matrix) {
        const std::string name = f.file;
        const auto bytes = read_file(dir + "/" + f.file);
        megapdf_document* bare = megapdf_open(bytes.data(), bytes.size(), nullptr);
        check(bare == nullptr && megapdf_last_error() == 4 /* FPDF_ERR_PASSWORD */, name + ": needs a password");
        megapdf_close(bare);
        megapdf_document* wrong = megapdf_open(bytes.data(), bytes.size(), "not-it");
        check(wrong == nullptr, name + ": a wrong password is refused");
        megapdf_close(wrong);

        megapdf_document* as_user = megapdf_open(bytes.data(), bytes.size(), f.user);
        check(as_user != nullptr, name + ": the user password opens it", megapdf_last_error_message());
        if (as_user) {
            megapdf_security s{};
            check(megapdf_security_info(as_user, &s) == MEGAPDF_OK && s.encrypted == 1 && s.revision == f.revision,
                  name + ": reports its revision", std::to_string(s.revision));
            check(first_run_text(as_user).find("MegaPDF") != std::string::npos, name + ": its text reads",
                  first_run_text(as_user));
            megapdf_close(as_user);
        }
        megapdf_document* as_owner = megapdf_open(bytes.data(), bytes.size(), f.owner);
        megapdf_security s{};
        check(as_owner != nullptr && megapdf_security_info(as_owner, &s) == MEGAPDF_OK && s.full_access == 1 &&
                  s.permissions == MEGAPDF_PERMIT_ALL,
              name + ": the owner password opens it with full access", std::to_string(s.permissions));
        megapdf_close(as_owner);
    }

    // Restricted, with no user password: opens freely, may do nothing, and only the owner
    // may change or remove its security.
    {
        const auto bytes = read_file(dir + "/owner-only.pdf");
        megapdf_document* d = megapdf_open(bytes.data(), bytes.size(), nullptr);
        check(d != nullptr, "owner-only.pdf opens without a password");
        if (d) {
            megapdf_security s{};
            check(megapdf_security_info(d, &s) == MEGAPDF_OK && s.encrypted == 1 && s.full_access == 0,
                  "owner-only.pdf: a plain open is restricted");
            const unsigned int forbidden = MEGAPDF_PERMIT_PRINT | MEGAPDF_PERMIT_MODIFY | MEGAPDF_PERMIT_COPY |
                                            MEGAPDF_PERMIT_ANNOTATE | MEGAPDF_PERMIT_FILL_FORMS;
            check((s.permissions & forbidden) == 0,
                  "owner-only.pdf: no printing, modifying, copying, annotating or form filling",
                  std::to_string(s.permissions));
            std::vector<unsigned char> out;
            check(megapdf_save_without_security(d, collect, &out) == MEGAPDF_ERR_RESTRICTED,
                  "owner-only.pdf: removing its security needs the owner password");
            check(megapdf_save_with_security(d, "x", "y", MEGAPDF_PERMIT_ALL, collect, &out) == MEGAPDF_ERR_RESTRICTED,
                  "owner-only.pdf: so does changing it");
            megapdf_close(d);
        }
        megapdf_document* owner = megapdf_open(bytes.data(), bytes.size(), "o-restricted");
        check(owner != nullptr, "owner-only.pdf: the owner password opens it");
        if (owner) {
            std::vector<unsigned char> plain;
            check(megapdf_save_without_security(owner, collect, &plain) == MEGAPDF_OK,
                  "owner-only.pdf: the owner removes its security");
            keep_saved("security", plain);
            megapdf_document* open = megapdf_open(plain.data(), plain.size(), nullptr);
            megapdf_security s{};
            check(open != nullptr && megapdf_security_info(open, &s) == MEGAPDF_OK && s.encrypted == 0 &&
                      s.full_access == 1,
                  "the copy without security opens with full access");
            megapdf_close(open);
            megapdf_close(owner);
        }
    }

    // New security on an unprotected document, then changed by its owner.
    {
        const auto bytes = read_file(fixtures + "/fixture.pdf");
        megapdf_document* d = megapdf_open(bytes.data(), bytes.size(), nullptr);
        megapdf_security s{};
        check(d != nullptr && megapdf_security_info(d, &s) == MEGAPDF_OK && s.encrypted == 0 && s.revision == -1 &&
                  s.full_access == 1,
              "fixture.pdf has no security and full access");
        std::vector<unsigned char> locked;
        check(d != nullptr && megapdf_save_with_security(d, "new-user", "new-owner", MEGAPDF_PERMIT_PRINT, collect,
                                                         &locked) == MEGAPDF_OK,
              "a copy saves with new security");
        keep_saved("security", locked);
        megapdf_close(d);

        megapdf_document* bare = megapdf_open(locked.data(), locked.size(), nullptr);
        check(bare == nullptr, "the copy needs a password");
        megapdf_close(bare);
        megapdf_document* u = megapdf_open(locked.data(), locked.size(), "new-user");
        check(u != nullptr && megapdf_security_info(u, &s) == MEGAPDF_OK && s.revision == 6 && s.full_access == 0 &&
                  s.permissions == MEGAPDF_PERMIT_PRINT,
              "the new user password opens it, permitted only to print", std::to_string(s.permissions));
        if (u) {
            check(first_run_text(u).find("MegaPDF") != std::string::npos, "the copy's text reads");
            std::vector<unsigned char> x;
            check(megapdf_save_without_security(u, collect, &x) == MEGAPDF_ERR_RESTRICTED,
                  "a user open cannot remove the new security");
            megapdf_close(u);
        }
        megapdf_document* o = megapdf_open(locked.data(), locked.size(), "new-owner");
        check(o != nullptr && megapdf_security_info(o, &s) == MEGAPDF_OK && s.full_access == 1,
              "the new owner password opens it with full access");
        if (o) {
            std::vector<unsigned char> changed;
            check(megapdf_save_with_security(o, "changed", nullptr, MEGAPDF_PERMIT_ALL, collect, &changed) ==
                      MEGAPDF_OK,
                  "the owner changes the password");
            keep_saved("security", changed);
            megapdf_document* stale = megapdf_open(changed.data(), changed.size(), "new-user");
            check(stale == nullptr, "the old password no longer opens the changed copy");
            megapdf_close(stale);
            megapdf_document* c = megapdf_open(changed.data(), changed.size(), "changed");
            check(c != nullptr && megapdf_security_info(c, &s) == MEGAPDF_OK && s.full_access == 1,
                  "the changed password opens it, as owner when no owner password was given");
            megapdf_close(c);
            megapdf_close(o);
        }
    }
    megapdf_security none{};
    check(megapdf_security_info(nullptr, &none) == MEGAPDF_ERR_ARGUMENT, "security info needs a document");
}

// #130: a heading in an embedded TrueType subset holding only the glyphs of "Hello World"
// (tools/gen_subset_font_fixture.py). Text the subset can draw stays in its own font. A
// letter it lacks reads back as typed but would draw .notdef, so the edit must go to the
// standard substitute. PDFium reports the font's BaseFont without its subset tag, so the
// subset-name test never caught this; only the glyph check does.
void test_subset_font_glyphs() {
    const auto bytes = read_file(std::string(MEGAPDF_REPO_FIXTURES) + "/subset-font.pdf");
    struct Case { const char* text; int outcome; const char* why; };
    const Case cases[] = {
        {"Hello Word", MEGAPDF_EDIT_IN_PLACE, "letters the subset holds stay in its font"},
        {"World Hello", MEGAPDF_EDIT_IN_PLACE, "reordered letters the subset holds stay in its font"},
        {"Hex World", MEGAPDF_EDIT_SUBSTITUTED, "an x the subset lacks goes to the substitute"},
        {"Hello Old", MEGAPDF_EDIT_SUBSTITUTED, "a capital O the subset lacks goes to the substitute"},
    };
    for (const Case& c : cases) {
        megapdf_document* d = megapdf_open(bytes.data(), bytes.size(), nullptr);
        check(d != nullptr, "subset-font.pdf opens");
        if (!d) return;
        {
            Page p(d, 0);
            megapdf_text* t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
            size_t run = 0;
            const bool has_line = megapdf_text_line_count(t) > 0 && megapdf_text_line_runs(t, 0, &run, 1) > 0;
            megapdf_text_run r{};
            if (has_line) megapdf_text_run_get(t, run, &r);
            megapdf_text_free(t);
            check(has_line, "subset-font.pdf has its heading");
            std::vector<unsigned short> text;
            for (const char* s = c.text; *s; s++) text.push_back(static_cast<unsigned char>(*s));
            text.push_back(0);
            int outcome = -1;
            const int status = has_line ? megapdf_set_text(p.page, r.object_index, text.data(), 0, &outcome, nullptr) : -99;
            check(status == MEGAPDF_OK && outcome == c.outcome, std::string("subset font, \"") + c.text + "\": " + c.why,
                  "status " + std::to_string(status) + ", outcome " + std::to_string(outcome));
            std::vector<unsigned char> saved;
            check(megapdf_save(d, collect, &saved) == MEGAPDF_OK, std::string("subset font, \"") + c.text + "\": saves");
            keep_saved("subset-font", saved);
        }
        megapdf_close(d);
    }
}

// #132: a protected document saves still protected, and megapdf_open_like() reads the
// copy back with the credentials the document was opened with. Every platform's save
// check reopened the copy without them, so every protected save failed.
// #147/#148: a document opened from its file is read on demand through the handle the core
// keeps, so the file must stay usable (renamed over, deleted) while it is open, the error
// paths must say what went wrong, and a descriptor handed over is always closed.
void test_open_from_file(const std::string& fixtures) {
    namespace fs = std::filesystem;
    std::error_code ec;
#if defined(_WIN32)
    const long long pid = static_cast<long long>(_getpid());
#else
    const long long pid = static_cast<long long>(getpid());
#endif
    const fs::path dir = fs::temp_directory_path(ec) / ("megapdf-core-open-file-" + std::to_string(pid));
    fs::remove_all(dir, ec);
    fs::create_directories(dir, ec);
    check(!ec, "open from file: a scratch folder", ec.message());
    auto write = [](const fs::path& path, const std::vector<unsigned char>& bytes) {
        std::ofstream out(path, std::ios::binary | std::ios::trunc);
        out.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    };
    auto utf8 = [](const fs::path& path) { return path.u8string(); };
    const auto plain = read_file(fixtures + "/fixture.pdf");
    const auto other = read_file(fixtures + "/cropped.pdf");
    // A non-ASCII name: the core takes UTF-8 on every platform and widens it itself on Windows.
    const fs::path doc = dir / fs::u8path("document \xC3\xA9.pdf");

    write(doc, plain);
    megapdf_document* d = megapdf_open_file(utf8(doc).c_str(), nullptr);
    check(d != nullptr, "open from file: fixture.pdf opens from its file", megapdf_last_error_message());
    check(megapdf_last_error() == 0, "open from file: a successful open clears the last error");
    if (d) {
        check(megapdf_page_count(d) == 2, "open from file: 2 pages");
        {
            Page p(d, 0);
            check(p.page != nullptr && search(p.page, "fixture").size() == 1, "open from file: page 1 loads and searches");
        }

        // The atomic-replace save swaps another file in under the open document, and a sync
        // client may delete it: the open document keeps reading the bytes it was opened on.
        const fs::path staged = dir / "staged.pdf";
        write(staged, other);
#if defined(_WIN32)
        // What .NET's File.Replace does for the desktop save: MoveFileEx cannot replace a file
        // another handle has open, even one shared for delete, but ReplaceFile can.
        const bool replaced_ok = ReplaceFileW(doc.wstring().c_str(), staged.wstring().c_str(), nullptr,
                                              REPLACEFILE_IGNORE_MERGE_ERRORS, nullptr, nullptr) != 0;
        check(replaced_ok, "open from file: the file can be replaced while it is open", std::to_string(GetLastError()));
#else
        fs::rename(staged, doc, ec);
        check(!ec, "open from file: the file can be replaced while it is open", ec.message());
#endif
        {
            Page p2(d, 1);
            check(p2.page != nullptr && !search(p2.page, "Page").empty(),
                  "open from file: after the replace, the document still reads what it was opened on");
        }
        std::vector<unsigned char> saved;
        check(megapdf_save(d, collect, &saved) == MEGAPDF_OK, "open from file: saves after the replace");
        megapdf_document* back = megapdf_open(saved.data(), saved.size(), nullptr);
        check(back != nullptr && megapdf_page_count(back) == 2, "open from file: the save is the original document");
        megapdf_close(back);

        megapdf_document* replaced = megapdf_open_file(utf8(doc).c_str(), nullptr);
        check(replaced != nullptr && megapdf_page_count(replaced) == 1, "open from file: reopening reads the replacement");
        megapdf_close(replaced);

        fs::remove(doc, ec);
        check(!ec, "open from file: the file can be deleted while it is open", ec.message());
        {
            Page p1(d, 0);
            check(p1.page != nullptr, "open from file: pages still load after the file is deleted");
        }
        megapdf_close(d);
    }

    check(megapdf_open_file(nullptr, nullptr) == nullptr && megapdf_last_error() == 2 /* FPDF_ERR_FILE */,
          "open from file: no path reports FPDF_ERR_FILE");
    check(megapdf_open_file("", nullptr) == nullptr && megapdf_last_error() == 2, "open from file: an empty path reports FPDF_ERR_FILE");
    check(megapdf_open_file(utf8(dir / "missing.pdf").c_str(), nullptr) == nullptr && megapdf_last_error() == 2,
          "open from file: a missing file reports FPDF_ERR_FILE", std::to_string(megapdf_last_error()));
    check(megapdf_open_file(utf8(dir).c_str(), nullptr) == nullptr && megapdf_last_error() == 2,
          "open from file: a folder reports FPDF_ERR_FILE", std::to_string(megapdf_last_error()));
    const fs::path empty = dir / "empty.pdf";
    write(empty, {});
    check(megapdf_open_file(utf8(empty).c_str(), nullptr) == nullptr && megapdf_last_error() == 2,
          "open from file: an empty file reports FPDF_ERR_FILE", std::to_string(megapdf_last_error()));
    const fs::path junk = dir / "junk.pdf";
    const std::string junk_text = "this is not a pdf at all";
    write(junk, std::vector<unsigned char>(junk_text.begin(), junk_text.end()));
    check(megapdf_open_file(utf8(junk).c_str(), nullptr) == nullptr && megapdf_last_error() == 3 /* FPDF_ERR_FORMAT */,
          "open from file: junk reports FPDF_ERR_FORMAT", std::to_string(megapdf_last_error()));

    // Past the size limit (4 GiB, and Windows only, in real life) the open is refused with its own code.
    const fs::path limited = dir / "limited.pdf";
    write(limited, plain);
    megapdf_testing_set_max_file_bytes(plain.size() - 1);
    check(megapdf_open_file(utf8(limited).c_str(), nullptr) == nullptr && megapdf_last_error() == MEGAPDF_OPEN_ERR_TOO_LARGE,
          "open from file: past the size limit reports MEGAPDF_OPEN_ERR_TOO_LARGE", std::to_string(megapdf_last_error()));
    megapdf_testing_set_max_file_bytes(plain.size());
    megapdf_document* at_limit = megapdf_open_file(utf8(limited).c_str(), nullptr);
    check(at_limit != nullptr, "open from file: a file exactly at the limit opens");
    megapdf_close(at_limit);
    megapdf_testing_set_max_file_bytes(0);

    // Credentials carry over to a file-backed reopen (#132): the saved copy is still protected.
    const auto locked_bytes = read_file(fixtures + "/encrypted.pdf");
    const char* unlock = "u123";   // tools/gen_test_fixtures.py
    const fs::path locked = dir / "locked.pdf";
    write(locked, locked_bytes);
    check(megapdf_open_file(utf8(locked).c_str(), nullptr) == nullptr && megapdf_last_error() == 4 /* FPDF_ERR_PASSWORD */,
          "open from file: encrypted.pdf asks to be unlocked");
    megapdf_document* e = megapdf_open_file(utf8(locked).c_str(), unlock);
    check(e != nullptr, "open from file: encrypted.pdf opens once unlocked");
    if (e) {
        std::vector<unsigned char> saved;
        check(megapdf_save(e, collect, &saved) == MEGAPDF_OK, "open from file: the unlocked document saves");
        const fs::path copy = dir / "locked-copy.pdf";
        write(copy, saved);
        check(megapdf_open_file(utf8(copy).c_str(), nullptr) == nullptr, "open from file: the saved copy is still protected");
        megapdf_document* again = megapdf_open_file_like(e, utf8(copy).c_str());
        check(again != nullptr && megapdf_page_count(again) == megapdf_page_count(e),
              "open from file: megapdf_open_file_like reads the saved copy back", std::to_string(megapdf_last_error()));
        megapdf_close(again);
        megapdf_close(e);
    }
    check(megapdf_open_file_like(nullptr, utf8(limited).c_str()) == nullptr, "open from file: no document to open like returns NULL");

#if defined(_WIN32)
    check(megapdf_open_fd(0, nullptr) == nullptr && megapdf_last_error() == 2, "open from fd: not supported on Windows");
#else
    int fd = ::open(utf8(limited).c_str(), O_RDONLY);
    megapdf_document* f = megapdf_open_fd(fd, nullptr);
    check(f != nullptr && megapdf_page_count(f) == 2, "open from fd: fixture.pdf opens from a descriptor", megapdf_last_error_message());
    if (f) {
        Page p(f, 0);
        check(p.page != nullptr && search(p.page, "fixture").size() == 1, "open from fd: page 1 loads and searches");
    }
    megapdf_close(f);
    check(::fcntl(fd, F_GETFD) == -1, "open from fd: closing the document closes the descriptor");

    fd = ::open(utf8(junk).c_str(), O_RDONLY);
    check(megapdf_open_fd(fd, nullptr) == nullptr && megapdf_last_error() == 3, "open from fd: junk reports FPDF_ERR_FORMAT");
    check(::fcntl(fd, F_GETFD) == -1, "open from fd: a failed open still closes the descriptor");

    fd = ::open(utf8(empty).c_str(), O_RDONLY);
    check(megapdf_open_fd(fd, nullptr) == nullptr && megapdf_last_error() == 2, "open from fd: an empty file reports FPDF_ERR_FILE");
    check(::fcntl(fd, F_GETFD) == -1, "open from fd: an empty file's descriptor is closed");

    fd = ::open(utf8(dir).c_str(), O_RDONLY);
    check(megapdf_open_fd(fd, nullptr) == nullptr && megapdf_last_error() == 2, "open from fd: a folder reports FPDF_ERR_FILE");
    check(::fcntl(fd, F_GETFD) == -1, "open from fd: a folder's descriptor is closed");

    check(megapdf_open_fd(-1, nullptr) == nullptr && megapdf_last_error() == 2, "open from fd: -1 reports FPDF_ERR_FILE");

    fd = ::open(utf8(limited).c_str(), O_RDONLY);
    check(megapdf_open_fd_like(nullptr, fd) == nullptr, "open from fd: no document to open like returns NULL");
    check(::fcntl(fd, F_GETFD) == -1, "open from fd: open_fd_like without a document still closes the descriptor");

    megapdf_document* e2 = megapdf_open_file(utf8(locked).c_str(), unlock);
    fd = ::open(utf8(locked).c_str(), O_RDONLY);
    megapdf_document* e3 = e2 ? megapdf_open_fd_like(e2, fd) : nullptr;
    check(e3 != nullptr, "open from fd: megapdf_open_fd_like unlocks with the document's credentials", std::to_string(megapdf_last_error()));
    if (e2 == nullptr) ::close(fd);
    megapdf_close(e3);
    megapdf_close(e2);
#endif

    fs::remove_all(dir, ec);
}

// #147: a platform that must write the document's own file in place (the macOS sandbox,
// Android's content URIs) first moves the document onto a private copy of it, and the
// document then keeps working however the original is written.
void test_read_from_copy(const std::string& fixtures) {
    namespace fs = std::filesystem;
    std::error_code ec;
#if defined(_WIN32)
    const long long pid = static_cast<long long>(_getpid());
#else
    const long long pid = static_cast<long long>(getpid());
#endif
    const fs::path dir = fs::temp_directory_path(ec) / ("megapdf-core-read-from-copy-" + std::to_string(pid));
    fs::remove_all(dir, ec);
    fs::create_directories(dir, ec);
    auto write = [](const fs::path& path, const std::vector<unsigned char>& bytes) {
        std::ofstream out(path, std::ios::binary | std::ios::trunc);
        out.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    };
    auto utf8 = [](const fs::path& path) { return path.u8string(); };
    const auto plain = read_file(fixtures + "/fixture.pdf");
    const auto other = read_file(fixtures + "/cropped.pdf");
    const fs::path original = dir / "original.pdf";
    const fs::path link = dir / "link.pdf";
    const fs::path copy = dir / "copy.pdf";
    write(original, plain);

    megapdf_document* d = megapdf_open_file(utf8(original).c_str(), nullptr);
    check(d != nullptr, "read from copy: opens");
    if (!d) return;
    check(megapdf_reads_file(d, utf8(original).c_str()) == 1, "read from copy: the document reads its file");
    fs::create_hard_link(original, link, ec);
    if (!ec) check(megapdf_reads_file(d, utf8(link).c_str()) == 1, "read from copy: a second name for the file is the same file");
    check(megapdf_reads_file(d, utf8(dir / "missing.pdf").c_str()) == 0, "read from copy: a missing path is not the file");
    write(dir / "twin.pdf", plain);
    check(megapdf_reads_file(d, utf8(dir / "twin.pdf").c_str()) == 0, "read from copy: identical bytes elsewhere are not the file");
    check(megapdf_reads_file(nullptr, utf8(original).c_str()) == 0 && megapdf_reads_file(d, nullptr) == 0,
          "read from copy: NULLs are not the file");
#if !defined(_WIN32)
    int fd = ::open(utf8(original).c_str(), O_RDONLY);
    check(megapdf_reads_fd(d, fd) == 1, "read from copy: a descriptor on the file is the file");
    ::close(fd);
    fd = ::open(utf8(dir / "twin.pdf").c_str(), O_RDONLY);
    check(megapdf_reads_fd(d, fd) == 0, "read from copy: a descriptor on another file is not the file");
    ::close(fd);
#endif
    {
        Page p(d, 0);   // something parsed before the move, something (page 2) after
        check(p.page != nullptr, "read from copy: page 1 loads before the move");
    }

    // A copy that cannot be made leaves the document reading the original.
    write(copy, other);
    check(megapdf_read_from_copy(d, utf8(copy).c_str()) == MEGAPDF_ERR_FILE, "read from copy: an existing copy path is refused");
    check(megapdf_reads_file(d, utf8(original).c_str()) == 1, "read from copy: after a refusal the document still reads its file");
    fs::remove(copy, ec);
    check(megapdf_read_from_copy(d, nullptr) == MEGAPDF_ERR_ARGUMENT && megapdf_read_from_copy(nullptr, "x") == MEGAPDF_ERR_ARGUMENT,
          "read from copy: NULLs are refused");

    check(megapdf_read_from_copy(d, utf8(copy).c_str()) == MEGAPDF_OK, "read from copy: moves", megapdf_last_error_message());
    check(!fs::exists(copy), "read from copy: the copy's name is gone at once");
    check(megapdf_reads_file(d, utf8(original).c_str()) == 0, "read from copy: the document no longer reads its file");

    // Now the original is written over where it is, shorter, as a sandboxed save would.
    {
        std::fstream in_place(original, std::ios::binary | std::ios::in | std::ios::out);
        in_place.seekp(0);
        in_place.write(reinterpret_cast<const char*>(other.data()), static_cast<std::streamsize>(other.size()));
    }
    fs::resize_file(original, other.size(), ec);
    {
        Page p2(d, 1);
        check(p2.page != nullptr && !search(p2.page, "Page").empty(), "read from copy: page 2 still reads the original bytes");
    }
    std::vector<unsigned char> saved;
    check(megapdf_save(d, collect, &saved) == MEGAPDF_OK, "read from copy: saves after the original was written over");
    megapdf_document* back = megapdf_open(saved.data(), saved.size(), nullptr);
    check(back != nullptr && megapdf_page_count(back) == 2, "read from copy: the save is the document, not what was written");
    megapdf_close(back);
    const fs::path second = dir / "second-copy.pdf";
    check(megapdf_read_from_copy(d, utf8(second).c_str()) == MEGAPDF_OK && !fs::exists(second), "read from copy: moving again works");
    megapdf_close(d);

    megapdf_document* m = megapdf_open(plain.data(), plain.size(), nullptr);
    check(m != nullptr && megapdf_reads_file(m, utf8(original).c_str()) == 0, "read from copy: a document from memory reads no file");
    check(megapdf_read_from_copy(m, utf8(copy).c_str()) == MEGAPDF_OK && !fs::exists(copy),
          "read from copy: a document from memory has nothing to move");
    megapdf_close(m);

    fs::remove_all(dir, ec);
}

void test_protected_save(const std::string& fixtures) {
    const auto bytes = read_file(fixtures + "/encrypted.pdf");
    check(megapdf_open(bytes.data(), bytes.size(), nullptr) == nullptr, "encrypted.pdf does not open without unlocking");
    check(megapdf_last_error() == 4 /* FPDF_ERR_PASSWORD */, "encrypted.pdf reports FPDF_ERR_PASSWORD",
          std::to_string(megapdf_last_error()));
    const char* unlock = "u123";   // tools/gen_test_fixtures.py
    megapdf_document* d = megapdf_open(bytes.data(), bytes.size(), unlock);
    check(d != nullptr, "encrypted.pdf opens once unlocked");
    if (!d) return;

    std::vector<unsigned char> saved;
    check(megapdf_save(d, collect, &saved) == MEGAPDF_OK, "the unlocked document saves");
    keep_saved("protected-save", saved);
    check(megapdf_open(saved.data(), saved.size(), nullptr) == nullptr, "the saved copy is still protected");
    megapdf_document* again = megapdf_open_like(d, saved.data(), saved.size());
    check(again != nullptr, "megapdf_open_like reads the saved copy back", std::to_string(megapdf_last_error()));
    if (again) {
        check(megapdf_page_count(again) == megapdf_page_count(d), "the copy has the same pages");
        megapdf_close(again);
    }
    megapdf_close(d);

    const auto plain = read_file(fixtures + "/fixture.pdf");
    megapdf_document* p = megapdf_open(plain.data(), plain.size(), nullptr);
    megapdf_document* like_plain = p ? megapdf_open_like(p, plain.data(), plain.size()) : nullptr;
    check(like_plain != nullptr, "an unprotected document opens like itself");
    megapdf_close(like_plain);
    megapdf_close(p);
    check(megapdf_open_like(nullptr, plain.data(), plain.size()) == nullptr, "no document to open like returns NULL");
}

U16 font_of(const megapdf_page* page, int object_index) {
    megapdf_text* t = megapdf_text_load(page, MEGAPDF_TEXT_ALL);
    U16 font;
    for (size_t i = 0; i < megapdf_text_run_count(t); i++) {
        megapdf_text_run r{};
        megapdf_text_run_get(t, i, &r);
        if (r.object_index == object_index) font = run_string(t, i, MEGAPDF_TEXT_RUN_FONT);
    }
    megapdf_text_free(t);
    return font;
}

// #126: a heading in an embedded CID font: a Type0 font, Identity-H, over a CIDFontType2
// subset holding only the glyphs of "Hello World" (tools/gen_cid_font_fixture.py), the way
// Word, Chrome, LibreOffice and every CJK producer write text. PDFium can edit it in place:
// it finds a character's CID by reverse lookup in the ToUnicode map. A character the map
// does not name has no CID to find (Identity-H offers no other route from Unicode to a CID),
// so PDFium falls back to CID 0, .notdef, and the edit still "succeeds"; the read-back and
// glyph checks (#116, #130) refuse that, and the edit goes to the standard substitute.
void test_cid_font_glyphs() {
    const auto bytes = read_file(std::string(MEGAPDF_REPO_FIXTURES) + "/cid-font.pdf");
    struct Case { const char* text; int outcome; const char* why; };
    const Case cases[] = {
        {"Hello Word", MEGAPDF_EDIT_IN_PLACE, "letters the subset holds stay in the CID font"},
        {"World Hello", MEGAPDF_EDIT_IN_PLACE, "reordered letters the subset holds stay in the CID font"},
        {"Hex World", MEGAPDF_EDIT_SUBSTITUTED, "an x the subset lacks goes to the substitute"},
        {"Hello Old", MEGAPDF_EDIT_SUBSTITUTED, "a capital O the subset lacks goes to the substitute"},
    };
    const U16 body_text = without_nul(u16("Body line under it"));
    for (const Case& c : cases) {
        const std::string name = std::string("CID font, \"") + c.text + "\": ";
        OpenDoc d(bytes);
        check(d.doc != nullptr, "cid-font.pdf opens");
        if (!d.doc) return;
        std::vector<unsigned char> saved;
        megapdf_rect body_was{};
        {
            Page p(d.doc, 0);
            const int idx = first_line_object(p.page);
            for (const RunShot& r : run_shots(p.page)) if (r.text == body_text) body_was = r.bounds;
            check(idx >= 0 && text_of(p.page, idx) == without_nul(u16("Hello World")), "cid-font.pdf: the heading reads through its ToUnicode map",
                  show(text_of(p.page, idx)));
            const U16 cid_font = font_of(p.page, idx);
            const U16 want = u16(c.text);
            int outcome = -1;
            const int status = idx >= 0 ? megapdf_set_text(p.page, idx, want.data(), 0, &outcome, nullptr) : -99;
            check(status == MEGAPDF_OK && outcome == c.outcome, name + c.why, "status " + std::to_string(status) + ", outcome " + std::to_string(outcome));
            check(text_of(p.page, idx) == without_nul(want), name + "reads back exactly", show(text_of(p.page, idx)));
            const bool in_place = c.outcome == MEGAPDF_EDIT_IN_PLACE;
            check((font_of(p.page, idx) == cid_font) == in_place, name + (in_place ? "the run keeps the CID font" : "the run leaves the CID font"),
                  "'" + show(font_of(p.page, idx)) + "' vs '" + show(cid_font) + "'");
            check(megapdf_save(d.doc, collect, &saved) == MEGAPDF_OK, name + "saves");
            keep_saved("cid-font", saved);
        }
        OpenDoc again(saved);
        Page q(again.doc, 0);
        check(index_of_text(q.page, c.text) >= 0, name + "the edit reads back after reopening");
        bool body_kept = false;
        for (const RunShot& r : run_shots(q.page)) if (r.text == body_text) body_kept = rect_close(r.bounds, body_was, 0.5);
        check(body_kept, name + "the body line keeps its place");
    }
}

// #126: body text beside AcroForm fields and markup annotations: a filled text field and a
// checked checkbox with appearance streams, a square and a sticky note. The two markup
// annotations carry a MegaPDF_Id only so megapdf_stamps_load() reports their rects; to
// PDFium that is one more dictionary entry.
std::vector<unsigned char> fields_and_annotations_pdf() {
    std::string pdf = "%PDF-1.4\n";
    std::vector<size_t> offsets;
    auto add = [&](const std::string& body) { offsets.push_back(pdf.size()); pdf += std::to_string(offsets.size()) + " 0 obj\n" + body + "\nendobj\n"; };
    auto stream = [](const std::string& dict, const std::string& body) {
        return "<< " + dict + " /Length " + std::to_string(body.size()) + " >>\nstream\n" + body + "\nendstream";
    };
    add("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R 7 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 4 0 R >> >> >> >>");
    add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
    add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R "
        "/Annots [6 0 R 7 0 R 10 0 R 11 0 R] >>");
    add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
    add(stream("", "BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET BT /F1 12 Tf 72 660 Td (Body line under it) Tj ET"));
    add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (fullname) /V (Ada Lovelace) /DA (/Helv 12 Tf 0 g) /Rect [300 655 500 675] /F 4 "
        "/P 3 0 R /AP << /N 8 0 R >> >>");
    add("<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /V /Yes /AS /Yes /Rect [300 700 315 715] /F 4 /P 3 0 R "
        "/AP << /N << /Yes 9 0 R /Off 12 0 R >> >> >>");
    add(stream("/Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 4 0 R >> >>",
               "0.13 G 1 w 0.5 0.5 199 19 re S BT /Helv 12 Tf 0 g 2 5 Td (Ada Lovelace) Tj ET"));
    add(stream("/Type /XObject /Subtype /Form /BBox [0 0 15 15]", "0.13 G 1 w 0.5 0.5 14 14 re S 1.6 w 3 3 m 12 12 l S 3 12 m 12 3 l S"));
    add("<< /Type /Annot /Subtype /Square /Rect [72 580 172 630] /C [1 0 0] /BS << /W 3 >> /F 4 /P 3 0 R /MegaPDF_Id (note:square) >>");
    add("<< /Type /Annot /Subtype /Text /Rect [520 695 540 715] /Contents (A sticky note) /Name /Comment /F 4 /P 3 0 R /MegaPDF_Id (note:text) >>");
    add(stream("/Type /XObject /Subtype /Form /BBox [0 0 15 15]", "0.13 G 1 w 0.5 0.5 14 14 re S"));
    const size_t xref = pdf.size();
    pdf += "xref\n0 " + std::to_string(offsets.size() + 1) + "\n0000000000 65535 f \n";
    for (size_t off : offsets) { char line[32]; std::snprintf(line, sizeof line, "%010zu 00000 n \n", off); pdf += line; }
    pdf += "trailer\n<< /Size " + std::to_string(offsets.size() + 1) + " /Root 1 0 R >>\nstartxref\n" + std::to_string(xref) + "\n%%EOF\n";
    return std::vector<unsigned char>(pdf.begin(), pdf.end());
}

struct FieldShot { int kind; int checked; megapdf_rect bounds; U16 name, value; };

std::vector<FieldShot> field_shots(const megapdf_page* page) {
    std::vector<FieldShot> out;
    megapdf_form_fields* f = megapdf_form_fields_load(page);
    for (size_t i = 0; i < megapdf_form_field_count(f); i++) {
        megapdf_form_field field{};
        megapdf_form_field_get(f, i, &field);
        out.push_back(FieldShot{field.kind, field.is_checked, field.bounds, field_string(f, i, MEGAPDF_FIELD_NAME), field_string(f, i, MEGAPDF_FIELD_VALUE)});
    }
    megapdf_form_fields_free(f);
    return out;
}

bool same_fields(const std::vector<FieldShot>& a, const std::vector<FieldShot>& b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); i++)
        if (a[i].kind != b[i].kind || a[i].checked != b[i].checked || a[i].name != b[i].name || a[i].value != b[i].value ||
            !rect_close(a[i].bounds, b[i].bounds, 0.01))
            return false;
    return true;
}

bool same_stamps(const StampList& a, const StampList& b) {
    if (a.ids != b.ids || a.stamps.size() != b.stamps.size()) return false;
    for (size_t i = 0; i < a.stamps.size(); i++)
        if (!rect_close(a.stamps[i].bounds, b.stamps[i].bounds, 0.01)) return false;
    return true;
}

// Strongly red pixels of a 612 x 792 BGRA render inside `r` (PDF points, bottom-left origin).
size_t red_pixels(const std::vector<unsigned char>& px, const megapdf_rect& r) {
    size_t n = 0;
    for (int y = static_cast<int>(792 - r.top); y < static_cast<int>(792 - r.bottom); y++)
        for (int x = static_cast<int>(r.left); x < static_cast<int>(r.right); x++) {
            const unsigned char* q = &px[(static_cast<size_t>(y) * 612 + x) * 4];
            if (q[2] > 180 && q[1] < 90 && q[0] < 90) n++;
        }
    return n;
}

void test_edit_beside_fields_and_annotations() {
    OpenDoc d(fields_and_annotations_pdf());
    check(d.doc != nullptr, "beside fields: the page opens");
    if (!d.doc) return;
    const megapdf_rect square{72, 580, 172, 630};
    const U16 heading = without_nul(u16("Plain heading"));
    const U16 want = u16("A retyped body line beside the fields");
    std::vector<FieldShot> fields_before;
    StampList notes_before;
    size_t red_before = 0;
    megapdf_rect heading_was{};
    std::vector<unsigned char> saved;
    {
        Page p(d.doc, 0);
        fields_before = field_shots(p.page);
        notes_before = stamps_of(p.page);
        check(fields_before.size() == 2 && fields_before[0].kind == MEGAPDF_FIELD_TEXT && show(fields_before[0].value) == "Ada Lovelace" &&
                  fields_before[1].kind == MEGAPDF_FIELD_CHECKBOX && fields_before[1].checked == 1,
              "beside fields: a filled text field and a checked checkbox", std::to_string(fields_before.size()) + " fields");
        check(notes_before.ids.size() == 2, "beside fields: the square and the sticky note are listed", std::to_string(notes_before.ids.size()));
        red_before = red_pixels(render_page(p.page), square);
        check(red_before > 0, "beside fields: the square annotation draws");
        for (const RunShot& r : run_shots(p.page)) if (r.text == heading) heading_was = r.bounds;

        const int body = index_of_text(p.page, "Body line under it");
        int outcome = -1;
        check(body >= 0 && megapdf_set_text(p.page, body, want.data(), 0, &outcome, nullptr) == MEGAPDF_OK && outcome == MEGAPDF_EDIT_IN_PLACE,
              "beside fields: the body line is retyped in place");
        check(same_fields(fields_before, field_shots(p.page)) && same_stamps(notes_before, stamps_of(p.page)),
              "beside fields: the edit leaves every field and annotation as it was");
        check(megapdf_save(d.doc, collect, &saved) == MEGAPDF_OK, "beside fields: saves");
        keep_saved("fields-and-annotations", saved);
    }
    OpenDoc again(saved);
    Page q(again.doc, 0);
    check(index_of_text(q.page, "A retyped body line beside the fields") >= 0, "beside fields: the edit reads back after reopening");
    bool heading_kept = false;
    for (const RunShot& r : run_shots(q.page)) if (r.text == heading) heading_kept = rect_close(r.bounds, heading_was, 0.5);
    check(heading_kept, "beside fields: the heading keeps its place");
    const auto fields_after = field_shots(q.page);
    check(same_fields(fields_before, fields_after), "beside fields: after reopening, every field keeps its name, value, state and rect",
          std::to_string(fields_after.size()) + " fields");
    const StampList notes_after = stamps_of(q.page);
    check(same_stamps(notes_before, notes_after), "beside fields: after reopening, every annotation keeps its rect",
          std::to_string(notes_after.ids.size()) + " annotations");
    const size_t red_after = red_pixels(render_page(q.page), square);
    check(red_after == red_before, "beside fields: the square still draws the same", std::to_string(red_before) + " -> " + std::to_string(red_after));
}

// #126: text drawn inside a form XObject. The core's runs are the page's own text objects,
// so text inside a form is not offered as a run, and megapdf_set_text() on the form object
// is an argument error rather than an edit of anything. Search still finds the text, because
// PDFium's text page reads into forms: the apps could tell a tap on it from a tap on nothing.
void test_form_xobject_text() {
    const std::string helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
    const std::string form_body = "BT /F1 14 Tf 0 40 Td (Text inside a form) Tj ET";
    const std::string form = "<< /Type /XObject /Subtype /Form /BBox [0 0 300 100] /Resources << /Font << /F1 4 0 R >> >> /Length " +
                             std::to_string(form_body.size()) + " >>\nstream\n" + form_body + "\nendstream";
    OpenDoc d(one_page_pdf("BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET q 1 0 0 1 72 560 cm /Fm1 Do Q BT /F1 12 Tf 72 520 Td (Body under a form) Tj ET",
                           helvetica, "/XObject << /Fm1 6 0 R >>", "", {form}));
    check(d.doc != nullptr, "form XObject: the page opens");
    if (!d.doc) return;
    Page p(d.doc, 0);
    const auto before = run_shots(p.page);
    const auto px_before = render_page(p.page);
    check(before.size() == 2 && index_of_text(p.page, "Text inside a form") < 0, "form XObject: its text is not offered as a run",
          std::to_string(before.size()) + " runs");
    check(search(p.page, "inside a form").size() == 1, "form XObject: search still finds its text");
    int form_index = -1;
    for (int i = 0; megapdf_object_type(p.page, i) >= 0; i++)
        if (megapdf_object_type(p.page, i) == 5 /* FPDF_PAGEOBJ_FORM */) form_index = i;
    check(form_index >= 0, "form XObject: the page lists the form object");
    if (form_index < 0) return;

    const U16 edit = u16("Edited form text");
    int outcome = -1;
    megapdf_detached* original = reinterpret_cast<megapdf_detached*>(1);
    check(megapdf_set_text(p.page, form_index, edit.data(), 0, &outcome, &original) == MEGAPDF_ERR_ARGUMENT && original == nullptr && outcome == -1,
          "form XObject: setting text on the form object is refused as an argument error");
    check(megapdf_text_editable(p.page, form_index) == MEGAPDF_ERR_ARGUMENT, "form XObject: it is not a text object to ask about either");
    check(same_runs(before, run_shots(p.page), 0.01) && render_page(p.page) == px_before, "form XObject: the refused page is untouched");

    // The heading beside the form still edits, and the form's text survives the rewrite.
    const U16 retyped = u16("A retyped heading above the form");
    const int head = index_of_text(p.page, "Plain heading");
    check(head >= 0 && megapdf_set_text(p.page, head, retyped.data(), 0, &outcome, nullptr) == MEGAPDF_OK,
          "form XObject: the heading beside it still edits");
    OpenDoc again(save_bytes(d.doc, "form-xobject"));
    Page q(again.doc, 0);
    check(search(q.page, "inside a form").size() == 1 && index_of_text(q.page, "Body under a form") >= 0 &&
              index_of_text(q.page, "A retyped heading above the form") >= 0,
          "form XObject: after reopening, the edit, the form's text and the body are all there");
    const auto px_after = render_page(q.page);
    size_t differing = 0;
    for (int y = 792 - 660; y < 792 - 540; y++)
        for (int x = 72; x < 372; x++) {
            const size_t i = (static_cast<size_t>(y) * 612 + x) * 4;
            if (std::abs(px_before[i] - px_after[i]) + std::abs(px_before[i + 1] - px_after[i + 1]) + std::abs(px_before[i + 2] - px_after[i + 2]) > 60)
                differing++;
        }
    check(differing == 0, "form XObject: the form and the body under it render as before", std::to_string(differing) + " pixels");
}

// --------------------------------------------------------------------------
// #136: a line drawn twice (fake bold, fill then stroke, a shadow). PDFium's text layer reads
// one copy; the other extracts as empty text, is never a run, and a delete or an edit that
// took only the runs left it drawn. doubled.pdf, in content order: 0 a plain line; 1 fake bold
// and 2 its copy; 3 filled and 4 its stroked copy; 5 a grey shadow and 6 the black text;
// 7 and 8 a two-run line with 9 and 10 their copies; 11 a closing line.

int objects_on(const megapdf_page* page) {
    int n = 0;
    while (megapdf_object_type(page, n) >= 0) n++;
    return n;
}

// Text objects overlapping `box`, runs or not: whatever is still drawn where a line was.
int text_objects_over(const megapdf_page* page, const megapdf_rect& box) {
    int n = 0;
    for (int i = 0; megapdf_object_type(page, i) >= 0; i++) {
        megapdf_rect r{};
        if (megapdf_object_type(page, i) != 1 || megapdf_object_bounds(page, i, &r) != MEGAPDF_OK) continue;
        if (r.left < box.right && r.right > box.left && r.bottom < box.top && r.top > box.bottom) n++;
    }
    return n;
}

std::string parts_of(const megapdf_detached* x) {
    std::string s;
    for (size_t i = 0; i < megapdf_detached_count(x); i++) {
        megapdf_detached_part part{};
        megapdf_detached_get(x, i, &part);
        s += "[" + std::to_string(part.object_index) + " of " + std::to_string(part.copy_of) + "]";
    }
    return s;
}

void test_hidden_copies(const std::string& fixtures) {
    const auto bytes = read_file(fixtures + "/doubled.pdf");
    struct Line {
        const char* name;
        std::vector<int> runs;   // as a caller passes them, in any order
        const char* taken;       // what the handle holds: [object index of the run it copies, or -1]
        megapdf_rect box;        // where the line is drawn
    };
    const std::vector<Line> lines = {
        {"fake bold", {1}, "[1 of -1][2 of 1]", {70, 674, 240, 694}},
        {"fill then stroke", {3}, "[3 of -1][4 of 3]", {70, 636, 240, 654}},
        {"shadow", {5}, "[5 of -1][6 of 5]", {70, 596, 240, 614}},
        {"two runs drawn twice", {8, 7}, "[7 of -1][8 of -1][9 of 7][10 of 8]", {70, 556, 240, 574}},
    };
    const megapdf_rect plain_box{70, 714, 240, 734}, closing_box{70, 512, 240, 534};
    const U16 retyped = u16("Retyped");
    {
        OpenDoc d(bytes);
        Page p(d.doc, 0);
        check(p.page != nullptr, "doubled.pdf: opens");
        if (p.page == nullptr) return;
        check(objects_on(p.page) == 12 && run_shots(p.page).size() == 7, "doubled.pdf: PDFium reads one copy of each doubled line",
              std::to_string(objects_on(p.page)) + " objects, " + std::to_string(run_shots(p.page).size()) + " runs");
        for (const Line& line : lines) check(megapdf_text_editable(p.page, line.runs[0]) == 1, std::string("doubled.pdf: editable: ") + line.name);
    }

    for (const Line& line : lines) {
        const std::string name = std::string("hidden copies: ") + line.name;
        const auto count = line.runs.size();
        const int taken = static_cast<int>(std::count(line.taken, line.taken + std::strlen(line.taken), '['));
        // Delete, then undo.
        {
            OpenDoc d(bytes);
            Page p(d.doc, 0);
            const auto px = render_page(p.page);
            const auto shots = run_shots(p.page);
            megapdf_detached* x = megapdf_detach_text_runs(p.page, line.runs.data(), count);
            check(x != nullptr && parts_of(x) == line.taken, name + ": the delete takes the runs and their hidden copies", parts_of(x));
            check(objects_on(p.page) == 12 - taken && text_objects_over(p.page, line.box) == 0,
                  name + ": nothing of the line is left drawn", std::to_string(text_objects_over(p.page, line.box)));
            check(text_objects_over(p.page, plain_box) == 1 && text_objects_over(p.page, closing_box) == 1, name + ": the other lines stay");
            check(megapdf_restore_object(p.page, x, line.runs[0]) == MEGAPDF_ERR_ARGUMENT && objects_on(p.page) == 12 - taken,
                  name + ": a handle of several objects does not go back as one");
            check(megapdf_restore_detached(p.page, x) == MEGAPDF_OK, name + ": the delete undoes");
            check(objects_on(p.page) == 12 && same_runs(shots, run_shots(p.page), 0.01) && render_page(p.page) == px,
                  name + ": undoing the delete puts both copies back where they were");
        }
        // Delete, save, reopen: the copy must not surface as the line.
        {
            OpenDoc d(bytes);
            {
                Page p(d.doc, 0);
                megapdf_discard_detached(megapdf_detach_text_runs(p.page, line.runs.data(), count));
            }
            OpenDoc again(save_bytes(d.doc, "hidden-copies"));
            Page q(again.doc, 0);
            check(q.page != nullptr && text_objects_over(q.page, line.box) == 0 && run_shots(q.page).size() == 7 - count &&
                      text_objects_over(q.page, plain_box) == 1 && text_objects_over(q.page, closing_box) == 1,
                  name + ": after saving and reopening, the line is gone and nothing surfaces in its place");
        }
        // Edit, undo, edit again, save, reopen.
        {
            OpenDoc d(bytes);
            {
                Page p(d.doc, 0);
                const auto px = render_page(p.page);
                const auto shots = run_shots(p.page);
                int outcome = -1;
                megapdf_detached* x = nullptr;
                check(megapdf_set_line_text(p.page, line.runs.data(), count, retyped.data(), 0, &outcome, &x) == MEGAPDF_OK &&
                          outcome == MEGAPDF_EDIT_IN_PLACE,
                      name + ": the edit lands in the run's own font");
                check(parts_of(x) == line.taken, name + ": the edit hands back the runs and their hidden copies", parts_of(x));
                check(text_objects_over(p.page, line.box) == 1 && objects_on(p.page) == 12 - taken + 1,
                      name + ": only the new text is drawn where the line was", std::to_string(text_objects_over(p.page, line.box)));
                check(megapdf_restore_detached(p.page, x) == MEGAPDF_OK && objects_on(p.page) == 12 &&
                          same_runs(shots, run_shots(p.page), 0.01) && render_page(p.page) == px,
                      name + ": undoing the edit puts both copies back where they were");
                x = nullptr;
                check(megapdf_set_line_text(p.page, line.runs.data(), count, retyped.data(), 0, &outcome, &x) == MEGAPDF_OK,
                      name + ": and it edits again");
                megapdf_discard_detached(x);
            }
            OpenDoc again(save_bytes(d.doc, "hidden-copies"));
            Page q(again.doc, 0);
            int over = 0;
            bool reads = false;
            for (const RunShot& r : run_shots(q.page)) {
                if (r.bounds.left < line.box.right && r.bounds.right > line.box.left && r.bounds.bottom < line.box.top && r.bounds.top > line.box.bottom) {
                    over++;
                    reads = r.text == without_nul(retyped);
                }
            }
            check(over == 1 && reads && text_objects_over(q.page, line.box) == 1,
                  name + ": after reopening, one run where the line was, reading as the edit, and nothing under it", std::to_string(over));
        }
    }

    // Several changes, undone last first: each handle's indices are the page's as it stood then.
    // Undone out of order, the page cannot be as that change left it, and nothing is touched.
    {
        OpenDoc d(bytes);
        Page p(d.doc, 0);
        const auto px = render_page(p.page);
        const int closing = 11, bold = 1, plain = 0;
        const int two[] = {7, 8};
        megapdf_detached* last_line = megapdf_detach_text_runs(p.page, &closing, 1);
        megapdf_detached* two_runs = megapdf_detach_text_runs(p.page, two, 2);
        int outcome = -1;
        megapdf_detached* edited = nullptr;
        check(last_line != nullptr && two_runs != nullptr &&
                  megapdf_set_line_text(p.page, &bold, 1, retyped.data(), 0, &outcome, &edited) == MEGAPDF_OK && edited != nullptr,
              "stacked: delete two lines, then edit a doubled one");
        check(objects_on(p.page) == 6, "stacked: six objects are left", std::to_string(objects_on(p.page)));
        megapdf_detached* first_line = megapdf_detach_text_runs(p.page, &plain, 1);
        check(megapdf_restore_detached(p.page, last_line) == MEGAPDF_ERR_ARGUMENT, "stacked: a delete undone before the later changes is refused");
        check(megapdf_restore_detached(p.page, edited) == MEGAPDF_ERR_ARGUMENT, "stacked: an edit undone before a later delete is refused");
        check(objects_on(p.page) == 5, "stacked: and the refusals changed nothing");
        check(megapdf_restore_detached(p.page, first_line) == MEGAPDF_OK && megapdf_restore_detached(p.page, edited) == MEGAPDF_OK &&
                  megapdf_restore_detached(p.page, two_runs) == MEGAPDF_OK && megapdf_restore_detached(p.page, last_line) == MEGAPDF_OK,
              "stacked: undone last first, every undo lands");
        check(objects_on(p.page) == 12 && render_page(p.page) == px, "stacked: the page is as it was");
    }

    // A line drawn once takes only itself, and undoing its edit the pre-#136 way still works.
    // megapdf_set_text on a doubled run takes the copy too.
    {
        OpenDoc d(bytes);
        Page p(d.doc, 0);
        const auto px = render_page(p.page);
        const int plain = 0, bold = 1;
        megapdf_detached* x = megapdf_detach_text_runs(p.page, &plain, 1);
        check(parts_of(x) == "[0 of -1]", "plain line: only its run is taken", parts_of(x));
        check(megapdf_restore_object(p.page, x, 0) == MEGAPDF_OK && render_page(p.page) == px, "plain line: a one-object handle goes back with megapdf_restore_object");
        const U16 plain_text = u16("Retyped plain line");
        int outcome = -1;
        megapdf_detached* original = nullptr;
        check(megapdf_set_text(p.page, plain, plain_text.data(), 0, &outcome, &original) == MEGAPDF_OK && parts_of(original) == "[0 of -1]",
              "plain line: its edit hands back only the original", parts_of(original));
        megapdf_discard_detached(megapdf_detach_object(p.page, plain));
        check(megapdf_restore_object(p.page, original, plain) == MEGAPDF_OK && render_page(p.page) == px,
              "plain line: detaching the edit and restoring the original undoes it, as before");
        original = nullptr;
        check(megapdf_set_text(p.page, bold, retyped.data(), 0, &outcome, &original) == MEGAPDF_OK && parts_of(original) == "[1 of -1][2 of 1]",
              "megapdf_set_text takes a doubled run's hidden copy", parts_of(original));
        check(megapdf_restore_detached(p.page, original) == MEGAPDF_OK && render_page(p.page) == px, "megapdf_restore_detached undoes it");
    }

    // The same text on another line is a run of its own, never a copy.
    {
        OpenDoc d(one_page_pdf("BT /F1 14 Tf 72 700 Td (Repeated line) Tj ET BT /F1 14 Tf 72 680 Td (Repeated line) Tj ET",
                               "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        Page p(d.doc, 0);
        const int first = 0;
        megapdf_detached* x = megapdf_detach_text_runs(p.page, &first, 1);
        check(parts_of(x) == "[0 of -1]" && run_shots(p.page).size() == 1, "a repeated line elsewhere is not taken as a copy", parts_of(x));
        megapdf_discard_detached(x);
    }

    // Refusals leave the page alone.
    {
        OpenDoc d(bytes);
        Page p(d.doc, 0);
        const int repeated[] = {1, 1};
        const int missing[] = {1, 99};
        check(megapdf_detach_text_runs(p.page, repeated, 2) == nullptr && megapdf_detach_text_runs(p.page, missing, 2) == nullptr &&
                  megapdf_detach_text_runs(p.page, repeated, 0) == nullptr && megapdf_detach_text_runs(nullptr, repeated, 1) == nullptr &&
                  objects_on(p.page) == 12,
              "hidden copies: a repeated or missing index, no runs or no page is refused, and nothing is taken");
        int outcome = -1;
        megapdf_detached* x = reinterpret_cast<megapdf_detached*>(1);
        check(megapdf_set_line_text(p.page, missing, 2, retyped.data(), 0, &outcome, &x) == MEGAPDF_ERR_ARGUMENT && x == nullptr &&
                  objects_on(p.page) == 12 && render_page(p.page) == [&] { OpenDoc fresh(bytes); Page f(fresh.doc, 0); return render_page(f.page); }(),
              "hidden copies: a line edit naming a missing object is refused before anything changes");
        check(megapdf_restore_detached(p.page, nullptr) == MEGAPDF_ERR_ARGUMENT && megapdf_restore_detached(nullptr, nullptr) == MEGAPDF_ERR_ARGUMENT &&
                  megapdf_detached_count(nullptr) == 0,
              "hidden copies: null handles are refused");
    }
}

// #136 reopened: copies PDFium hides character by character, out of reach of its five-object
// check. doubled-far.pdf, in content order: 0-6 a copy of each word of a seven-word line drawn
// before it (0.7 pt right and up); 7-13 the line's runs; 14-20 and 21-27 copies drawn 7 and 14
// objects after their runs (0.7 pt right; 0.7 pt up); 28 a closing line.
void test_far_hidden_copies(const std::string& fixtures) {
    const auto bytes = read_file(fixtures + "/doubled-far.pdf");
    const std::vector<int> runs = {13, 7, 10, 8, 9, 12, 11};   // as a caller passes them, in any order
    std::string taken;
    for (int i = 0; i < 28; i++) {
        const int copy_of = i < 7 ? i + 7 : i < 14 ? -1 : 7 + (i - 14) % 7;
        taken += "[" + std::to_string(i) + " of " + std::to_string(copy_of) + "]";
    }
    const megapdf_rect line_box{60, 690, 340, 720}, closing_box{60, 650, 240, 675};
    const U16 retyped = u16("Retyped");
    {
        OpenDoc d(bytes);
        Page p(d.doc, 0);
        check(p.page != nullptr && objects_on(p.page) == 29 && run_shots(p.page).size() == 8,
              "doubled-far.pdf: PDFium reads one copy of the line", p.page ? std::to_string(run_shots(p.page).size()) + " runs" : "no page");
        if (p.page == nullptr) return;
    }
    // Delete, then undo.
    {
        OpenDoc d(bytes);
        Page p(d.doc, 0);
        const auto px = render_page(p.page);
        const auto shots = run_shots(p.page);
        megapdf_detached* x = megapdf_detach_text_runs(p.page, runs.data(), runs.size());
        check(x != nullptr && parts_of(x) == taken, "far copies: the delete takes the runs and the copies drawn before and far after them", parts_of(x));
        check(objects_on(p.page) == 1 && text_objects_over(p.page, line_box) == 0 && text_objects_over(p.page, closing_box) == 1,
              "far copies: nothing of the line is left drawn, and the closing line stays", std::to_string(text_objects_over(p.page, line_box)));
        check(megapdf_restore_detached(p.page, x) == MEGAPDF_OK && objects_on(p.page) == 29 && same_runs(shots, run_shots(p.page), 0.01) &&
                  render_page(p.page) == px,
              "far copies: undoing the delete puts every copy back where it was");
    }
    // Delete, save, reopen.
    {
        OpenDoc d(bytes);
        {
            Page p(d.doc, 0);
            megapdf_discard_detached(megapdf_detach_text_runs(p.page, runs.data(), runs.size()));
        }
        OpenDoc again(save_bytes(d.doc, "far-copies"));
        Page q(again.doc, 0);
        check(q.page != nullptr && text_objects_over(q.page, line_box) == 0 && run_shots(q.page).size() == 1 &&
                  text_objects_over(q.page, closing_box) == 1,
              "far copies: after saving and reopening, the line is gone and nothing surfaces in its place");
    }
    // Edit, undo, edit again, save, reopen.
    {
        OpenDoc d(bytes);
        {
            Page p(d.doc, 0);
            const auto px = render_page(p.page);
            const auto shots = run_shots(p.page);
            int outcome = -1;
            megapdf_detached* x = nullptr;
            check(megapdf_set_line_text(p.page, runs.data(), runs.size(), retyped.data(), 0, &outcome, &x) == MEGAPDF_OK &&
                      outcome == MEGAPDF_EDIT_IN_PLACE,
                  "far copies: the edit lands in the run's own font");
            check(parts_of(x) == taken, "far copies: the edit hands back the runs and every copy", parts_of(x));
            check(text_objects_over(p.page, line_box) == 1 && objects_on(p.page) == 2,
                  "far copies: only the new text is drawn where the line was", std::to_string(text_objects_over(p.page, line_box)));
            check(megapdf_restore_detached(p.page, x) == MEGAPDF_OK && objects_on(p.page) == 29 && same_runs(shots, run_shots(p.page), 0.01) &&
                      render_page(p.page) == px,
                  "far copies: undoing the edit puts every copy back where it was");
            x = nullptr;
            check(megapdf_set_line_text(p.page, runs.data(), runs.size(), retyped.data(), 0, &outcome, &x) == MEGAPDF_OK,
                  "far copies: and it edits again");
            megapdf_discard_detached(x);
        }
        OpenDoc again(save_bytes(d.doc, "far-copies"));
        Page q(again.doc, 0);
        int over = 0;
        bool reads = false;
        for (const RunShot& r : run_shots(q.page)) {
            if (r.bounds.left < line_box.right && r.bounds.right > line_box.left && r.bounds.bottom < line_box.top && r.bounds.top > line_box.bottom) {
                over++;
                reads = r.text == without_nul(retyped);
            }
        }
        check(over == 1 && reads && text_objects_over(q.page, line_box) == 1,
              "far copies: after reopening, one run where the line was, reading as the edit, and nothing under it", std::to_string(over));
    }
    // One word retyped: megapdf_set_text takes its three copies, and undo puts them back.
    {
        OpenDoc d(bytes);
        Page p(d.doc, 0);
        const auto px = render_page(p.page);
        int outcome = -1;
        megapdf_detached* x = nullptr;
        check(megapdf_set_text(p.page, 10, retyped.data(), 0, &outcome, &x) == MEGAPDF_OK &&
                  parts_of(x) == "[3 of 10][10 of -1][17 of 10][24 of 10]" && objects_on(p.page) == 26,
              "far copies: megapdf_set_text on one word takes its copies before and after it", parts_of(x));
        check(megapdf_restore_detached(p.page, x) == MEGAPDF_OK && objects_on(p.page) == 29 && render_page(p.page) == px,
              "far copies: undoing the one-word edit restores the page");
    }
}

// #152: a render much smaller than a JPEG decodes it at a reduced size (PDFium patch 0021; PDFium
// sizes the decode against the whole render). A JPEG libjpeg will not scale (a lossless one) must still
// draw, at full size (0022): at patch 21 it failed to load and its page stayed blank. Each page is
// filled by one 661 x 855 image of mid gray with a dark bar, rendered at 80 x 103 px (a 1/4 decode).
void test_scaled_jpeg_render() {
    const auto bytes = read_file(std::string(MEGAPDF_REPO_FIXTURES) + "/scaled-jpeg.pdf");
    megapdf_document* d = megapdf_open(bytes.data(), bytes.size(), nullptr);
    check(d != nullptr, "scaled-jpeg.pdf opens");
    if (!d) return;
    const int patches = MEGAPDF_PDFIUM_PATCHES;
    const char* const names[3] = {"the progressive CMYK JPEG of odd size", "the baseline RGB JPEG of odd size", "the lossless JPEG"};
    for (int page_index = 0; page_index < 3; page_index++) {
        Page p(d, page_index);
        const int w = 80, h = 103;
        std::vector<unsigned char> px(static_cast<size_t>(w) * h * 4, 0);
        check(megapdf_render(p.page, px.data(), w, h, w * 4, MEGAPDF_RENDER_BGRA) == MEGAPDF_OK,
              std::string("scaled-jpeg: page ") + std::to_string(page_index + 1) + " renders small");
        int drawn = 0;
        for (size_t i = 0; i < px.size(); i += 4) {
            const int sum = px[i] + px[i + 1] + px[i + 2];
            if (sum < 700 && sum > 30) drawn++;
        }
        const double share = static_cast<double>(drawn) / (w * h);
        if (page_index == 2 && patches == 21) {
            check(share < 0.05, "scaled-jpeg: at patch 21 the lossless JPEG fails to load (fixed by 0022)", std::to_string(share));
        } else {
            check(share > 0.5, std::string("scaled-jpeg: ") + names[page_index] + " draws at a reduced size", std::to_string(share));
        }
    }
    megapdf_close(d);
}

// #149: the core reads every text object's text in one pass over the page's characters instead
// of calling FPDFTextObj_GetText per object, which walks the whole page each time. PDFium's own
// call, on a second copy of the same document, is the oracle: every run's text must be exactly
// what it returns, and every object left out must extract as nothing or whitespace.
bool is_white_space(unsigned short c) {
    return (c >= 0x09 && c <= 0x0D) || c == 0x20 || c == 0x85 || c == 0xA0 || c == 0x1680 ||
           (c >= 0x2000 && c <= 0x200A) || c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000;
}

void check_texts_match_pdfium(const std::vector<unsigned char>& bytes, const std::string& tag, int max_pages = 8) {
    megapdf_document* d = megapdf_open(bytes.data(), bytes.size(), nullptr);
    FPDF_DOCUMENT raw = FPDF_LoadMemDocument(bytes.data(), static_cast<int>(bytes.size()), nullptr);
    check(d != nullptr && raw != nullptr, "text oracle: " + tag + " opens twice");
    if (d == nullptr || raw == nullptr) {
        megapdf_close(d);
        if (raw != nullptr) FPDF_CloseDocument(raw);
        return;
    }
    const int pages = std::min(megapdf_page_count(d), max_pages);
    for (int pi = 0; pi < pages; pi++) {
        const std::string where = tag + " page " + std::to_string(pi + 1);
        std::map<int, U16> oracle;
        FPDF_PAGE page = FPDF_LoadPage(raw, pi);
        FPDF_TEXTPAGE text_page = page != nullptr ? FPDFText_LoadPage(page) : nullptr;
        const int objects = page != nullptr ? FPDFPage_CountObjects(page) : 0;
        for (int j = 0; j < objects && text_page != nullptr; j++) {
            FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, j);
            if (FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_TEXT) continue;
            const unsigned long n = FPDFTextObj_GetText(obj, text_page, nullptr, 0);   // bytes, with the terminator
            U16 text(n / 2);
            if (n > 2) FPDFTextObj_GetText(obj, text_page, text.data(), n);
            if (!text.empty()) text.pop_back();
            oracle[j] = text;
        }
        if (text_page != nullptr) FPDFText_ClosePage(text_page);
        if (page != nullptr) FPDF_ClosePage(page);

        Page p(d, pi);
        megapdf_text* t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
        std::map<int, U16> runs;
        for (size_t i = 0; i < megapdf_text_run_count(t); i++) {
            megapdf_text_run r{};
            megapdf_text_run_get(t, i, &r);
            runs[r.object_index] = run_string(t, i, MEGAPDF_TEXT_RUN_TEXT);
        }
        megapdf_text_free(t);
        int wrong = 0;
        std::string first;
        for (const auto& [index, text] : oracle) {
            const auto it = runs.find(index);
            const bool blank = std::all_of(text.begin(), text.end(), is_white_space);
            const bool ok = it == runs.end() ? blank : !blank && it->second == text;
            if (!ok && wrong++ == 0) first = "object " + std::to_string(index);
        }
        for (const auto& run : runs)
            if (oracle.find(run.first) == oracle.end() && wrong++ == 0) first = "run " + std::to_string(run.first) + " is no text object";
        check(wrong == 0, "text oracle: every run reads as FPDFTextObj_GetText does", where + ": " + std::to_string(wrong) + " wrong, first " + first);
    }
    megapdf_close(d);
    FPDF_CloseDocument(raw);
}

void test_text_in_one_pass(const std::string& fixtures, const std::string& schematic) {
    const std::string helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
    // What decides the text PDFium gives one object: separator spaces it generates between
    // objects on a line, spaces it generates inside a wide TJ, line breaks when an object's
    // characters resume on another line, duplicates it drops, and text drawn in another order.
    const std::string tricky =
        "BT /F1 12 Tf 72 700 Td (Hello) Tj ET BT /F1 12 Tf 110 700 Td (world) Tj ET "
        "BT /F1 12 Tf 72 680 Td [(Wide) -3000 (gap) -200 (kern) 400 (tight)] TJ ET "
        "BT /F1 12 Tf 72 660 Td ( leading and  double  spaces ) Tj ET "
        "BT /F1 12 Tf 300 640 Td (right first) Tj ET BT /F1 12 Tf 72 640 Td (then left) Tj ET "
        "BT /F1 12 Tf 72 620 Td (Twice) Tj ET BT /F1 12 Tf 72 620 Td (Twice) Tj ET "
        "BT /F1 12 Tf 72 600 Td (d\351j\340 vu) Tj 0 -14 Td (next line, same object) Tj ET "
        "BT /F1 12 Tf 0 1 -1 0 500 300 Tm (Rotated text) Tj ET "
        "BT /F1 12 Tf 72 560 Td (   ) Tj ET "
        "BT /F1 12 Tf 72 540 Td (up) Tj 0 40 Td (and back) Tj ET "
        "BT /F1 12 Tf 3 Tr 72 520 Td (invisible) Tj ET "
        "BT /F1 6 Tf 72 500 Td (small) Tj /F1 30 Tf 30 0 Td (LARGE) Tj ET";
    check_texts_match_pdfium(one_page_pdf(tricky, helvetica), "tricky page");
    for (const char* name : {"fixture.pdf", "forms.pdf", "formtext.pdf", "textbox.pdf", "doubled.pdf", "doubled-far.pdf",
                             "demo.pdf", "demo-fr.pdf", "cropped.pdf", "stamped.pdf", "softmask.pdf"})
        check_texts_match_pdfium(read_file(fixtures + "/" + name), name);
    check_texts_match_pdfium(read_file(schematic), "microbit-v2-schematic.pdf");
    for (const char* name : {"cid-font.pdf", "subset-font.pdf"})
        check_texts_match_pdfium(read_file(std::string(MEGAPDF_REPO_FIXTURES) + "/" + name), name);

    // And the time it takes grows with the page, not with its square. 30,000 one-word objects
    // took about 25 s to list read one by one (11 s at 20,000); read in one pass, well under 0.1 s
    // in a Release build. The budget sits far from both, so neither a slow runner nor a sanitizer
    // makes it flaky, and the old way cannot pass it.
    std::string many;
    for (int i = 0; i < 30000; i++) {
        char obj[96];
        std::snprintf(obj, sizeof obj, "BT /F1 1 Tf %d %d Td (w%d) Tj ET\n", 20 + (i % 60) * 9, 770 - (i / 60) * 3 / 2, i);
        many += obj;
    }
    OpenDoc d(one_page_pdf(many, helvetica));
    Page p(d.doc, 0);
    const auto started = std::chrono::steady_clock::now();
    megapdf_text* t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
    const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started).count();
    const size_t runs = megapdf_text_run_count(t);
    U16 last = runs > 0 ? run_string(t, runs - 1, MEGAPDF_TEXT_RUN_TEXT) : U16{};
    last.push_back(0);   // u16() ends with a terminator
    megapdf_text_free(t);
    check(runs == 30000 && last == u16("w29999"), "text in one pass: 30,000 text objects are 30,000 runs", std::to_string(runs) + " runs");
    check(ms < 5000, "text in one pass: 30,000 text objects list in under 5 s", std::to_string(static_cast<int>(ms)) + " ms");
}

// #151: the guard draws images of 16 megapixels or more as stand-ins in its compare renders, so
// a huge scan is not decoded twice per edit. What a rewrite can get wrong about an image must
// still show: where it lands, which way up, its clip, and which image it is; and a stencil
// mask, whose colour is the content stream's, keeps its own pixels. Each case compares a page
// with a copy that differs in one way, as the guard compares a page before and after a rewrite.
std::vector<unsigned char> big_image_pdf(const std::string& content, unsigned char shade, bool stencil) {
    const int w = 4000, h = 4000;   // 16 megapixels: the stand-in threshold
    // RunLengthDecode keeps the fixture small without zlib: grey bands, in runs of up to 128 bytes.
    std::string data;
    auto run = [&](unsigned char value, int count) {
        for (; count > 0; count -= 128) {
            const int n = count > 128 ? 128 : count;
            data += static_cast<char>(n == 1 ? 0 : 257 - n);
            data += static_cast<char>(value);
        }
    };
    const int row_bytes = stencil ? (w + 7) / 8 : w * 3;
    for (int y = 0; y < h; y++) {
        for (int band = 0; band < 8; band++) {
            const unsigned char v = stencil ? ((band + y / 500) % 2 ? 0xFF : 0x00)
                                            : static_cast<unsigned char>((band * 30 + y / 16 + shade) & 0xFF);
            run(v, band < 7 ? row_bytes / 8 : row_bytes - 7 * (row_bytes / 8));
        }
    }
    data += static_cast<char>(128);   // EOD
    const std::string dict = std::string("<< /Type /XObject /Subtype /Image /Width 4000 /Height 4000 ") +
                             (stencil ? "/ImageMask true /BitsPerComponent 1" : "/ColorSpace /DeviceRGB /BitsPerComponent 8") +
                             " /Filter /RunLengthDecode /Length " + std::to_string(data.size()) + " >>";
    return one_page_pdf(content, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
                        "/XObject << /Im1 6 0 R >>", "", {dict + "\nstream\n" + data + "\nendstream"});
}

void test_large_image_stand_ins() {
    struct Case {
        const char* what;
        std::string was, now;
        unsigned char shade_now;
        bool stencil, same;
    };
    const std::string text = "BT /F1 12 Tf 72 740 Td (Caption above the image) Tj ET ";
    const std::string placed = text + "q 400 0 0 300 100 300 cm /Im1 Do Q";
    const std::vector<Case> cases = {
        {"the same page", placed, placed, 0, false, true},
        {"the image moved by 12 pt", placed, text + "q 400 0 0 300 112 300 cm /Im1 Do Q", 0, false, false},
        {"the image flipped", placed, text + "q 400 0 0 -300 100 600 cm /Im1 Do Q", 0, false, false},
        {"the image clipped", placed, text + "q 100 300 200 150 re W n 400 0 0 300 100 300 cm /Im1 Do Q", 0, false, false},
        {"another image of the same size", placed, placed, 90, false, false},
        {"a path under the image changed", "0 0 1 rg 150 350 60 60 re f " + placed, "1 0 0 rg 150 350 60 60 re f " + placed, 0, false, false},
        {"the same stencil mask", text + "q 0 0 1 rg 400 0 0 300 100 300 cm /Im1 Do Q", text + "q 0 0 1 rg 400 0 0 300 100 300 cm /Im1 Do Q", 0, true, true},
        {"a stencil mask in another colour", text + "q 0 0 1 rg 400 0 0 300 100 300 cm /Im1 Do Q", text + "q 1 0 0 rg 400 0 0 300 100 300 cm /Im1 Do Q", 0, true, false},
    };
    for (const Case& c : cases) {
        OpenDoc a(big_image_pdf(c.was, 0, c.stencil));
        OpenDoc b(big_image_pdf(c.now, c.shade_now, c.stencil));
        const std::string what = std::string("stand-ins: ") + c.what;
        check(a.doc != nullptr && b.doc != nullptr, what + ": both pages open");
        if (a.doc == nullptr || b.doc == nullptr) continue;
        Page pa(a.doc, 0), pb(b.doc, 0);
        megapdf_layout_verdict v{};
        const int editable = megapdf_testing_compare_pages(pa.page, pb.page, &v);
        check(editable == (c.same ? 1 : 0), what + (c.same ? " compares as unchanged" : " is seen"),
              "cause " + std::to_string(v.cause) + ", " + std::to_string(v.changed_pixels) + " of " + std::to_string(v.total_pixels) + " px");
    }
}

// #137: megapdf_text_editable() caches its verdict per object, and a change to the page moves
// object indices and rewrites the page's streams. After a change every answer must be what a
// fresh open of the saved page gives. PDFium regenerates every stream of a page (patch 5), so one
// page's verdicts agree with each other and the stale answer shows when a change alters whether
// the page can be rewritten at all: here, a form whose text inherits its font, which the writer
// cannot keep. Removing the path before it, which the guard does not judge, moves both texts down.
void test_verdicts_follow_changes() {
    const std::string content = "0 0 1 rg 72 500 50 50 re f BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET "
                                "BT /F1 12 Tf 72 660 Td (Body line) Tj ET q /F1 30 Tf 1 0 0 rg /Fm1 Do Q";
    OpenDoc d(one_page_pdf(content, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
                           kFormInheritingTextResources, "", {form_inheriting_text_object()}));
    std::vector<int> after;
    {
        Page p(d.doc, 0);
        check(megapdf_text_editable(p.page, 1) >= 0 && megapdf_text_editable(p.page, 2) >= 0, "verdicts: both texts are judged before the change");
        megapdf_discard_detached(megapdf_detach_object(p.page, 0));
        check(megapdf_object_type(p.page, 0) == 1 && megapdf_object_type(p.page, 1) == 1, "verdicts: the change moved both texts down one");
        after = {megapdf_text_editable(p.page, 0), megapdf_text_editable(p.page, 1)};
    }
    OpenDoc fresh(save_bytes(d.doc, "verdicts"));
    Page q(fresh.doc, 0);
    const std::vector<int> expected = {megapdf_text_editable(q.page, 0), megapdf_text_editable(q.page, 1)};
    check(after == expected, "verdicts: after a change they are what a fresh open of the saved page says",
          std::to_string(after[0]) + std::to_string(after[1]) + " vs " + std::to_string(expected[0]) + std::to_string(expected[1]));
}

// #128: megapdf_text_editable_reason() names the check that refused, with the numbers the dry run
// saw, and megapdf_last_layout_verdict() hands the same verdict back after a refused edit.
void test_layout_verdicts() {
    const std::string helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
    auto same = [](const megapdf_layout_verdict& a, const megapdf_layout_verdict& b) {
        return a.editable == b.editable && a.cause == b.cause && a.where == b.where && a.changed_pixels == b.changed_pixels &&
               a.total_pixels == b.total_pixels && a.max_shift_pt == b.max_shift_pt;
    };
    auto describe = [](const megapdf_layout_verdict& v) {
        return "editable " + std::to_string(v.editable) + ", cause " + std::to_string(v.cause) + ", where " + std::to_string(v.where) + ", " +
               std::to_string(v.changed_pixels) + "/" + std::to_string(v.total_pixels) + " px, shift " + std::to_string(v.max_shift_pt) + " pt";
    };
    const auto retyped = utf16("Annual report");

    // An editable line: nothing changed at all.
    {
        OpenDoc d(one_page_pdf("BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET BT /F1 12 Tf 72 660 Td (Body line under it) Tj ET", helvetica));
        Page p(d.doc, 0);
        megapdf_layout_verdict v{};
        const int r = megapdf_text_editable_reason(p.page, 0, &v);
        check(r == 1 && v.editable == 1 && v.cause == MEGAPDF_LAYOUT_OK && v.changed_pixels == 0 && v.total_pixels == 612 * 792 &&
                  v.where == 0 && v.max_shift_pt <= 0.01,
              "layout verdict: an editable line is OK, with nothing changed", describe(v));
        check(megapdf_text_editable(p.page, 0) == 1, "layout verdict: megapdf_text_editable gives the same answer");
        megapdf_layout_verdict unused{};
        check(megapdf_text_editable_reason(nullptr, 0, &unused) == MEGAPDF_ERR_ARGUMENT &&
                  megapdf_text_editable_reason(p.page, 0, nullptr) == MEGAPDF_ERR_ARGUMENT &&
                  megapdf_text_editable_reason(p.page, 9999, &unused) == MEGAPDF_ERR_ARGUMENT && megapdf_last_layout_verdict(nullptr) == MEGAPDF_ERR_ARGUMENT,
              "layout verdict: a bad page, index or out pointer is an argument error");
        megapdf_detached* original = nullptr;
        int outcome = -1;
        megapdf_layout_verdict last{};
        check(megapdf_set_text(p.page, 0, retyped.data(), 0, &outcome, &original) == MEGAPDF_OK && megapdf_last_layout_verdict(&last) == MEGAPDF_OK &&
                  last.editable == 1 && last.cause == MEGAPDF_LAYOUT_OK,
              "layout verdict: an edit that goes through leaves no refusal behind", describe(last));
        megapdf_discard_detached(original);
    }

    // Refused for the render: a form whose text inherits the font set before it, which the writer
    // cannot keep, the page every platform's layout-guard test uses. The heading itself is
    // untouched; the form's text is what changes.
    {
        OpenDoc d(one_page_pdf(kFormInheritingTextPage, helvetica, kFormInheritingTextResources, "", {form_inheriting_text_object()}));
        Page p(d.doc, 0);
        megapdf_layout_verdict v{};
        check(megapdf_text_editable_reason(p.page, 0, &v) == 0 && v.editable == 0 && v.cause == MEGAPDF_LAYOUT_RENDER,
              "layout verdict: a form's inherited text is refused for the render", describe(v));
        check(v.total_pixels == 612 * 792 && v.changed_pixels * 2000 > v.total_pixels && v.changed_pixels <= 400 * 100 && v.max_shift_pt <= 0.5,
              "layout verdict: the render numbers are the form text's: over the budget, within a 400 x 100 box, no text moved", describe(v));
        check((v.where & MEGAPDF_LAYOUT_WHERE_OTHER) != 0 && (v.where & MEGAPDF_LAYOUT_WHERE_OBJECT) == 0,
              "layout verdict: the change is off text and away from the heading", describe(v));
        megapdf_layout_verdict form{};
        check(megapdf_text_editable_reason(p.page, 1, &form) == MEGAPDF_ERR_ARGUMENT,
              "layout verdict: the form itself is no text run, so it is not judged as one");

        megapdf_layout_verdict again{};
        check(megapdf_text_editable_reason(p.page, 0, &again) == 0 && same(again, v), "layout verdict: asked again, the cached verdict is the same", describe(again));

        megapdf_layout_verdict last{};
        int outcome = -1;
        megapdf_detached* original = nullptr;
        check(megapdf_set_text(p.page, 0, retyped.data(), 0, &outcome, &original) == MEGAPDF_ERR_LAYOUT && megapdf_last_layout_verdict(&last) == MEGAPDF_OK &&
                  same(last, v),
              "layout verdict: megapdf_set_text's refusal hands back the verdict", describe(last));
        const int line[1] = {0};
        check(megapdf_set_line_text(p.page, line, 1, retyped.data(), 0, &outcome, &original) == MEGAPDF_ERR_LAYOUT &&
                  megapdf_last_layout_verdict(&last) == MEGAPDF_OK && same(last, v),
              "layout verdict: megapdf_set_line_text's refusal hands back the first refused run's verdict", describe(last));
        const int bad[1] = {9999};
        check(megapdf_detach_text_runs(p.page, bad, 1) == nullptr && megapdf_last_layout_verdict(&last) == MEGAPDF_OK && last.editable == 1 &&
                  last.cause == MEGAPDF_LAYOUT_OK,
              "layout verdict: a detach that fails for another reason resets it to editable", describe(last));
        const int heading[1] = {0};
        check(megapdf_detach_text_runs(p.page, heading, 1) == nullptr && megapdf_last_layout_verdict(&last) == MEGAPDF_OK && same(last, v),
              "layout verdict: megapdf_detach_text_runs's refusal hands back the verdict", describe(last));
        check(megapdf_detach_object(p.page, 0) == nullptr && megapdf_last_layout_verdict(&last) == MEGAPDF_OK && same(last, v),
              "layout verdict: megapdf_detach_object's refusal hands back the verdict", describe(last));
    }

    // Refused because text would move: a font written straight into the page's resources with its
    // own /Widths. Below patch 14 the writer re-creates a direct font dictionary from its base font,
    // without the widths, so every run in it comes back at Helvetica's own advance (#141). No other
    // way of moving text on a rewrite is known (the corpus battery has never refused an edit as
    // TEXT_MOVED), so from patch 14 this page pins the fix instead: the same verdict is editable.
    if (MEGAPDF_PDFIUM_PATCHES >= 14) {
        std::string widths;
        for (int i = 32; i <= 126; i++) widths += " 1000";
        const std::string direct_font =
            "/F2 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths [" + widths + " ] >>";
        OpenDoc d(one_page_pdf("BT /F2 18 Tf 72 700 Td (Heading) Tj ET BT /F2 12 Tf 72 660 Td (Body line here) Tj ET", helvetica, "", direct_font));
        Page p(d.doc, 0);
        megapdf_layout_verdict v{};
        check(megapdf_text_editable_reason(p.page, 0, &v) == 1 && v.editable == 1 && v.cause == MEGAPDF_LAYOUT_OK,
              "layout verdict: a direct font keeps its widths from patch 14, so its page is editable (#141)", describe(v));
    } else {
        std::string widths;
        for (int i = 32; i <= 126; i++) widths += " 1000";
        const std::string direct_font =
            "/F2 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths [" + widths + " ] >>";
        OpenDoc d(one_page_pdf("BT /F2 18 Tf 72 700 Td (Heading) Tj ET BT /F2 12 Tf 72 660 Td (Body line here) Tj ET", helvetica, "", direct_font));
        Page p(d.doc, 0);
        megapdf_layout_verdict v{};
        check(megapdf_text_editable_reason(p.page, 0, &v) == 0 && v.editable == 0 && v.cause == MEGAPDF_LAYOUT_TEXT_MOVED,
              "layout verdict: a direct font's lost widths are refused because text moves", describe(v));
        check(v.max_shift_pt > 0.5 && v.changed_pixels > 0 && (v.where & (MEGAPDF_LAYOUT_WHERE_OBJECT | MEGAPDF_LAYOUT_WHERE_TEXT)) != 0,
              "layout verdict: the moved runs' numbers: a shift past 0.5 pt, pixels on the text", describe(v));
        megapdf_layout_verdict last{};
        int outcome = -1;
        check(megapdf_set_text(p.page, 1, retyped.data(), 0, &outcome, nullptr) == MEGAPDF_ERR_LAYOUT && megapdf_last_layout_verdict(&last) == MEGAPDF_OK &&
                  last.cause == MEGAPDF_LAYOUT_TEXT_MOVED,
              "layout verdict: the edit's refusal names the text move", describe(last));
    }

    // The cache follows changes (#137): removing the path before the texts moves both down one,
    // and each index must then give what a fresh open of the saved page gives, in full.
    {
        OpenDoc d(one_page_pdf("0 0 1 rg 72 300 50 50 re f BT /F1 24 Tf 72 700 Td (Spaced report) Tj ET "
                               "BT /F1 12 Tf 72 660 Td (Body line) Tj ET q /F1 72 Tf 0 0 1 rg /Fm1 Do Q",
                               helvetica, kFormInheritingTextResources, "", {form_inheriting_text_object()}));
        std::vector<megapdf_layout_verdict> after(2);
        {
            Page p(d.doc, 0);
            megapdf_layout_verdict before{};
            check(megapdf_text_editable_reason(p.page, 1, &before) >= 0 && megapdf_text_editable_reason(p.page, 2, &before) >= 0,
                  "layout verdict: both texts are judged before the change");
            megapdf_discard_detached(megapdf_detach_object(p.page, 0));
            check(megapdf_object_type(p.page, 0) == 1 && megapdf_object_type(p.page, 1) == 1, "layout verdict: the change moved both texts down one");
            megapdf_text_editable_reason(p.page, 0, &after[0]);
            megapdf_text_editable_reason(p.page, 1, &after[1]);
        }
        OpenDoc fresh(save_bytes(d.doc, "layout-verdicts"));
        Page q(fresh.doc, 0);
        for (int i = 0; i < 2; i++) {
            megapdf_layout_verdict expected{};
            megapdf_text_editable_reason(q.page, i, &expected);
            check(same(after[static_cast<size_t>(i)], expected), "layout verdict: after a change, object " + std::to_string(i) + " is judged afresh",
                  describe(after[static_cast<size_t>(i)]) + " vs " + describe(expected));
        }
    }
}

// #139: changes the text guard never judges (whiteouts, text boxes, removing a path) regenerate
// the page as well. megapdf_page_regeneration_verdict() says whether that alone changes the page,
// so the apps can warn; the change itself is never refused.
void test_page_regeneration_verdict() {
    const std::string helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
    auto describe = [](const megapdf_layout_verdict& v) {
        return "editable " + std::to_string(v.editable) + ", cause " + std::to_string(v.cause) + ", where " + std::to_string(v.where) + ", " +
               std::to_string(v.changed_pixels) + "/" + std::to_string(v.total_pixels) + " px, shift " + std::to_string(v.max_shift_pt) + " pt";
    };
    // Pixels that differ past the guard's threshold, outside a box in page space (612 x 792 at 1 px per pt).
    auto changed_outside = [](const std::vector<unsigned char>& a, const std::vector<unsigned char>& b, const std::vector<megapdf_rect>& boxes) {
        int changed = 0;
        for (int y = 0; y < 792; y++) {
            for (int x = 0; x < 612; x++) {
                const double px = x + 0.5, py = 792 - (y + 0.5);
                bool inside = false;
                for (const megapdf_rect& box : boxes) {
                    if (px >= box.left - 2 && px <= box.right + 2 && py >= box.bottom - 2 && py <= box.top + 2) inside = true;
                }
                if (inside) continue;
                const size_t i = (static_cast<size_t>(y) * 612 + static_cast<size_t>(x)) * 4;
                if (std::abs(a[i] - b[i]) + std::abs(a[i + 1] - b[i + 1]) + std::abs(a[i + 2] - b[i + 2]) > 60) changed++;
            }
        }
        return changed;
    };
    const megapdf_rect corner{500, 20, 560, 60};

    // A plain page keeps its look: no warning, and a whiteout changes nothing but its own box.
    {
        const auto bytes = one_page_pdf("BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET 0 0 1 rg 72 500 200 40 re f", helvetica);
        OpenDoc d(bytes);
        {
            Page p(d.doc, 0);
            megapdf_layout_verdict v{};
            check(megapdf_page_regeneration_verdict(p.page, &v) == 1 && v.editable == 1 && v.cause == MEGAPDF_LAYOUT_OK && v.changed_pixels == 0 &&
                      v.where == 0 && v.total_pixels == 612 * 792,
                  "page verdict: a plain page keeps its look", describe(v));
            megapdf_layout_verdict unused{};
            check(megapdf_page_regeneration_verdict(nullptr, &unused) == MEGAPDF_ERR_ARGUMENT &&
                      megapdf_page_regeneration_verdict(p.page, nullptr) == MEGAPDF_ERR_ARGUMENT,
                  "page verdict: a NULL page or out pointer is an argument error");
            int index = -1;
            check(megapdf_add_whiteout(p.page, &corner, &index) == MEGAPDF_OK, "page verdict: a whiteout on a plain page applies");
        }
        OpenDoc was(bytes);
        OpenDoc saved(save_bytes(d.doc, "page-verdict"));
        Page a(was.doc, 0), b(saved.doc, 0);
        const int changed = changed_outside(render_page(a.page), render_page(b.page), {corner});
        check(changed == 0, "page verdict: on a plain page the whiteout changes nothing outside itself", std::to_string(changed) + " px");
    }

    // A page with no objects has nothing to rewrite.
    {
        OpenDoc d(one_page_pdf("", helvetica));
        Page p(d.doc, 0);
        megapdf_layout_verdict v{};
        check(megapdf_page_regeneration_verdict(p.page, &v) == 1 && v.cause == MEGAPDF_LAYOUT_OK, "page verdict: an empty page keeps its look", describe(v));
    }

    // The inherited-text form page every platform's layout-guard test uses: the text guard refuses
    // it, the page verdict says a regeneration changes it, and a whiteout and a text box still apply.
    {
        const auto bytes = one_page_pdf(kFormInheritingTextPage, helvetica, kFormInheritingTextResources, "", {form_inheriting_text_object()});
        OpenDoc d(bytes);
        megapdf_rect box_bounds{};
        {
            Page p(d.doc, 0);
            megapdf_layout_verdict v{};
            check(megapdf_page_regeneration_verdict(p.page, &v) == 0 && v.editable == 0 && v.cause == MEGAPDF_LAYOUT_RENDER,
                  "page verdict: the inherited-text page changes when regenerated", describe(v));
            check(v.changed_pixels * 2000 > v.total_pixels && (v.where & MEGAPDF_LAYOUT_WHERE_OTHER) != 0 && (v.where & MEGAPDF_LAYOUT_WHERE_OBJECT) == 0,
                  "page verdict: over the budget, off text, and never on a judged object", describe(v));
            megapdf_layout_verdict again{};
            check(megapdf_page_regeneration_verdict(p.page, &again) == 0 && again.changed_pixels == v.changed_pixels && again.where == v.where,
                  "page verdict: asked again, the cached verdict is the same", describe(again));
            check(megapdf_text_editable(p.page, 0) == 0, "page verdict: the text guard refuses the same page");

            int index = -1;
            check(megapdf_add_whiteout(p.page, &corner, &index) == MEGAPDF_OK && whiteouts_of(p.page).size() == 1,
                  "page verdict: a whiteout still applies on a page that changes");
            // Judged afresh, not from the cache: the whiteout already had PDFium write the page, so its
            // streams are PDFium's own now and writing them again changes nothing. The first change
            // is the one that alters the page, which is why the apps ask before it.
            megapdf_layout_verdict after{};
            const int after_result = megapdf_page_regeneration_verdict(p.page, &after);
            check(after_result == 1 && after.cause == MEGAPDF_LAYOUT_OK,
                  "page verdict: judged afresh after the whiteout, the rewritten page keeps its new look", std::to_string(after_result) + ": " + describe(after));
            const auto text = utf16("Note");
            const auto id = utf16("text:139");
            check(megapdf_add_text_box(p.page, -1, text.data(), "Helvetica", 12, 300, 300, id.data(), &index) == MEGAPDF_OK &&
                      megapdf_find_text_box(p.page, id.data()) == index,
                  "page verdict: a text box still applies on a page that changes");
            megapdf_object_bounds(p.page, index, &box_bounds);
        }
        // Why the apps warn: the regeneration the whiteout forced changed the page outside it.
        OpenDoc was(bytes);
        OpenDoc saved(save_bytes(d.doc, "page-verdict"));
        Page a(was.doc, 0), b(saved.doc, 0);
        const int changed = changed_outside(render_page(a.page), render_page(b.page), {corner, box_bounds});
        check(box_bounds.right > box_bounds.left && changed > 0,
              "page verdict: on the inherited-text page the change reaches outside the whiteout and the text box", std::to_string(changed) + " px");
    }
}

// #145: the page check started early, in the background. It can be cancelled, lets other calls
// run between its stages, survives its document being closed, and does not cache an answer for
// a page that changed while it ran.
void test_page_check_cancel_and_concurrency() {
    const std::string helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
    // Heavy enough that each stage of the dry run takes a while: thousands of small filled squares.
    std::string heavy = "BT /F1 18 Tf 72 740 Td (Heavy page) Tj ET 0 0 1 rg ";
    for (int i = 0; i < 20000; i++) {
        heavy += std::to_string(20 + (i % 560)) + " " + std::to_string(20 + (i / 560) % 700) + " 0.8 0.8 re f ";
    }
    const auto bytes = one_page_pdf(heavy, helvetica);
    using clock = std::chrono::steady_clock;
    auto ms_since = [](clock::time_point t) {
        return std::to_string(std::chrono::duration_cast<std::chrono::milliseconds>(clock::now() - t).count()) + " ms";
    };

    // Raised before it starts: stops at once, leaves `out` alone, and caches nothing.
    {
        OpenDoc d(bytes);
        Page p(d.doc, 0);
        megapdf_cancel* cancel = megapdf_cancel_new();
        check(cancel != nullptr, "page check: a cancel flag is created");
        megapdf_cancel_raise(cancel);
        megapdf_layout_verdict v{};
        v.cause = 12345;
        check(megapdf_page_regeneration_verdict_cancellable(p.page, cancel, &v) == MEGAPDF_ERR_CANCELLED && v.cause == 12345,
              "page check: a flag raised before the check stops it, out untouched");
        megapdf_layout_verdict cached{};
        check(megapdf_page_regeneration_verdict_cached(p.page, &cached) == MEGAPDF_ERR_NOT_JUDGED, "page check: a cancelled check caches nothing");
        megapdf_cancel_free(cancel);

        check(megapdf_page_regeneration_verdict_cached(nullptr, &cached) == MEGAPDF_ERR_ARGUMENT &&
                  megapdf_page_regeneration_verdict_cached(p.page, nullptr) == MEGAPDF_ERR_ARGUMENT &&
                  megapdf_page_regeneration_verdict_cancellable(nullptr, nullptr, &cached) == MEGAPDF_ERR_ARGUMENT &&
                  megapdf_page_regeneration_verdict_cancellable(p.page, nullptr, nullptr) == MEGAPDF_ERR_ARGUMENT,
              "page check: NULL arguments are argument errors");
        megapdf_cancel_raise(nullptr);
        megapdf_cancel_free(nullptr);

        const auto started = clock::now();
        megapdf_layout_verdict answer{};
        const int result = megapdf_page_regeneration_verdict_cancellable(p.page, nullptr, &answer);
        const std::string took = ms_since(started);
        std::printf("page check: the heavy page is judged in %s\n", took.c_str());
        megapdf_layout_verdict kept{};
        check((result == 1 || result == 0) && megapdf_page_regeneration_verdict_cached(p.page, &kept) == result &&
                  kept.changed_pixels == answer.changed_pixels && kept.cause == answer.cause,
              "page check: without a flag it answers, and the cached answer is the same", took);
        megapdf_cancel* late = megapdf_cancel_new();
        megapdf_cancel_raise(late);
        check(megapdf_page_regeneration_verdict_cancellable(p.page, late, &kept) == result,
              "page check: a page already judged answers from the cache, whatever the flag");
        megapdf_cancel_free(late);
    }

    // From here the check is parked at a stage by megapdf_testing_set_page_check_hook(), so what
    // happens "while it runs" is decided by the test, not raced against the machine (#145). A
    // fast macOS runner judged the heavy page in stages under the 20 ms hand-over threshold, and
    // the timing version of these checks failed there.
    struct Park {
        std::mutex m;
        std::condition_variable cv;
        int calls = 0;
        bool parked = false;
        bool go = false;
        std::function<void()> on_first;   // runs on the check's thread, lock let go, before parking
        bool park = true;
    };
    auto hook = [](void* context) {
        auto* park = static_cast<Park*>(context);
        std::unique_lock<std::mutex> lock(park->m);
        if (park->calls++ > 0) return;
        if (park->on_first) {
            lock.unlock();
            park->on_first();
            lock.lock();
        }
        if (!park->park) return;
        park->parked = true;
        park->cv.notify_all();
        park->cv.wait(lock, [park] { return park->go; });
    };
    auto wait_parked = [](Park& park) {
        std::unique_lock<std::mutex> lock(park.m);
        return park.cv.wait_for(lock, std::chrono::seconds(120), [&park] { return park.parked; });
    };
    auto release = [](Park& park) {
        std::lock_guard<std::mutex> lock(park.m);
        park.go = true;
        park.cv.notify_all();
    };
    const auto light = one_page_pdf("BT /F1 18 Tf 72 700 Td (Parked page) Tj ET 0 0 1 rg 72 500 200 40 re f", helvetica);

    // Raised while it runs: it stops at the stage and caches nothing.
    {
        OpenDoc d(light);
        Page p(d.doc, 0);
        megapdf_cancel* cancel = megapdf_cancel_new();
        Park park;
        park.park = false;
        park.on_first = [cancel] { megapdf_cancel_raise(cancel); };
        megapdf_testing_set_page_check_hook(hook, &park);
        megapdf_layout_verdict v{};
        const int result = megapdf_page_regeneration_verdict_cancellable(p.page, cancel, &v);
        megapdf_testing_set_page_check_hook(nullptr, nullptr);
        megapdf_layout_verdict cached{};
        check(park.calls > 0 && result == MEGAPDF_ERR_CANCELLED &&
                  megapdf_page_regeneration_verdict_cached(p.page, &cached) == MEGAPDF_ERR_NOT_JUDGED,
              "page check: a flag raised mid-run stops it and caches nothing", std::to_string(result));
        megapdf_cancel_free(cancel);
    }

    // Other calls run between its stages: parked with the lock let go, a call on another document
    // completes. With the lock held for the whole run it could not return until the check did.
    {
        OpenDoc d(light);
        OpenDoc other(one_page_pdf("BT /F1 12 Tf 72 700 Td (Other) Tj ET", helvetica));
        Page p(d.doc, 0);
        Page q(other.doc, 0);
        Park park;
        megapdf_testing_set_page_check_hook(hook, &park);
        std::atomic<int> result{999};
        std::thread check_thread([&] {
            megapdf_layout_verdict v{};
            result = megapdf_page_regeneration_verdict_cancellable(p.page, nullptr, &v);
        });
        const bool parked = wait_parked(park);
        std::atomic<bool> call_done{false};
        std::thread call_thread([&] {
            megapdf_page_width(q.page);
            call_done = true;
        });
        bool completed = false;
        for (int i = 0; i < 1200 && parked && !(completed = call_done.load()); i++) {
            std::this_thread::sleep_for(std::chrono::milliseconds(100));
        }
        {
            std::lock_guard<std::mutex> lock(park.m);
            check(!park.go, "page check: it stays parked until released");
        }
        release(park);
        call_thread.join();
        check_thread.join();
        megapdf_testing_set_page_check_hook(nullptr, nullptr);
        check(parked && completed, "page check: another call completes while it is parked between stages");
        check(result == 1, "page check: released, it finishes with its answer", std::to_string(result.load()));
    }

    // Its document closed while it runs: close waits for the parked check, which then stops,
    // and nothing is used after it is freed (ASan on Linux).
    {
        std::vector<unsigned char> copy = light;
        megapdf_document* doc = megapdf_open(copy.data(), copy.size(), nullptr);
        megapdf_page* page = megapdf_load_page(doc, 0);
        Park park;
        megapdf_testing_set_page_check_hook(hook, &park);
        std::atomic<int> result{999};
        std::thread check_thread([&] {
            megapdf_layout_verdict v{};
            result = megapdf_page_regeneration_verdict_cancellable(page, nullptr, &v);
        });
        const bool parked = wait_parked(park);
        std::atomic<bool> closed{false};
        std::thread close_thread([&] {
            megapdf_close(doc);   // closes the page handle too
            closed = true;
        });
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
        check(parked && !closed, "page check: closing its document waits while the check is parked");
        release(park);
        close_thread.join();
        check_thread.join();
        megapdf_testing_set_page_check_hook(nullptr, nullptr);
        check(closed && (result == MEGAPDF_ERR_CANCELLED || result == 1), "page check: closing its document mid-run stops it cleanly",
              std::to_string(result.load()));
    }

    // The page changes while it runs: the answer describes the page as it was and is not kept.
    {
        OpenDoc d(light);
        Page p(d.doc, 0);
        Page same(d.doc, 0);
        Park park;
        park.park = false;
        int edit = MEGAPDF_ERR_ARGUMENT;
        park.on_first = [&] {
            const megapdf_rect box{500, 20, 560, 60};
            int index = -1;
            edit = megapdf_add_whiteout(same.page, &box, &index);
        };
        megapdf_testing_set_page_check_hook(hook, &park);
        megapdf_layout_verdict v{};
        const int result = megapdf_page_regeneration_verdict_cancellable(p.page, nullptr, &v);
        megapdf_testing_set_page_check_hook(nullptr, nullptr);
        megapdf_layout_verdict cached{};
        check(edit == MEGAPDF_OK && (result == 1 || result == 0), "page check: an edit applies while the check runs, and the check still answers");
        check(megapdf_page_regeneration_verdict_cached(p.page, &cached) == MEGAPDF_ERR_NOT_JUDGED,
              "page check: a page edited while it was judged keeps no answer");
    }
}

// #150: /UserUnit. userunit.pdf is cropped.pdf drawn in 2-point units (MediaBox [0 0 306 396],
// CropBox [0 50 306 350], /UserUnit 2), so every coordinate and size crossing the ABI is in
// points: (user - crop origin) x 2 out, the inverse in. Needs PDFium patch 0023.
void test_user_unit(const std::string& fixtures) {
    check(megapdf_page_user_unit(nullptr) == 1.0, "a null page has a user unit of 1");
    {
        Doc c(fixtures + "/cropped.pdf");
        Page cp(c.doc, 0);
        check(cp.page != nullptr && megapdf_page_user_unit(cp.page) == 1.0, "a page without /UserUnit has 1");
    }
    Doc d(fixtures + "/userunit.pdf");
    if (!d.doc) { check(false, "userunit.pdf opens"); return; }
    Page p(d.doc, 0);
    if (!p.page) { check(false, "userunit.pdf page loads"); return; }
    check(close_to(megapdf_page_user_unit(p.page), 2.0, 1e-9), "userunit.pdf has /UserUnit 2",
          std::to_string(megapdf_page_user_unit(p.page)));
    check(close_to(megapdf_page_width(p.page), 612) && close_to(megapdf_page_height(p.page), 600),
          "userunit.pdf measures 612 x 600 pt, not its 306 x 300 units",
          std::to_string(megapdf_page_width(p.page)) + "x" + std::to_string(megapdf_page_height(p.page)));
    double ox = -1, oy = -1;
    megapdf_page_crop_origin(p.page, &ox, &oy);
    check(close_to(ox, 0) && close_to(oy, 50), "the crop origin stays in user space (0,50)",
          std::to_string(ox) + "," + std::to_string(oy));

    // Drawn squares: 5.5 units with the stroke is 11 pt, a checkbox only in points.
    auto sq = squares(p.page);
    check(sq.size() == 1 && rect_close(sq[0], megapdf_rect{100, 400, 110, 410}, 1.0),
          "the 10 pt square is found at (100,400)-(110,410)", sq.empty() ? "none" : rect_str(sq[0]));

    // Search and text runs.
    auto hits = search(p.page, "megapdf");
    check(hits.size() == 1 && !hits[0].rects.empty() && hits[0].rects[0].bottom > 530 && hits[0].rects[0].bottom < 560 &&
              hits[0].rects[0].left > 150 && hits[0].rects[0].right < 340,
          "a search hit is in points (bottom near the 550 pt baseline)",
          hits.empty() || hits[0].rects.empty() ? "none" : rect_str(hits[0].rects[0]));
    megapdf_text* t = megapdf_text_load(p.page, MEGAPDF_TEXT_ALL);
    megapdf_text_run run{};
    check(megapdf_text_run_count(t) == 1 && megapdf_text_run_get(t, 0, &run) == MEGAPDF_OK && close_to(run.font_size, 36, 0.01) &&
              run.bounds.bottom < 550 && run.bounds.top > 570 && close_to(run.bounds.left, 72, 4.0),
          "the text run is 36 pt at x = 72 pt", std::to_string(run.font_size) + " " + rect_str(run.bounds));
    megapdf_text_free(t);

    // Form fields: bounds out, a tap in.
    megapdf_form_fields* f = megapdf_form_fields_load(p.page);
    megapdf_form_field field{};
    check(megapdf_form_field_count(f) == 2 && megapdf_form_field_get(f, 0, &field) == MEGAPDF_OK &&
              rect_close(field.bounds, megapdf_rect{100, 300, 300, 320}, 0.01),
          "the text field is at (100,300)-(300,320) pt", rect_str(field.bounds));
    megapdf_form_field box{};
    check(megapdf_form_field_get(f, 1, &box) == MEGAPDF_OK && box.kind == MEGAPDF_FIELD_CHECKBOX && !box.is_checked &&
              rect_close(box.bounds, megapdf_rect{100, 260, 115, 275}, 0.01),
          "the checkbox is at (100,260)-(115,275) pt", rect_str(box.bounds));
    megapdf_form_fields_free(f);
    megapdf_form_click(p.page, 107.5, 267.5);
    f = megapdf_form_fields_load(p.page);
    check(megapdf_form_field_get(f, 1, &box) == MEGAPDF_OK && box.is_checked, "a tap at the checkbox's centre in points ticks it");
    megapdf_form_fields_free(f);
    auto value = utf16("Ada Lovelace");
    megapdf_form_set_text(p.page, 200, 310, value.data());
    f = megapdf_form_fields_load(p.page);
    check(show(field_string(f, 0, MEGAPDF_FIELD_VALUE)) == "Ada Lovelace", "a tap at the field's centre in points fills it",
          show(field_string(f, 0, MEGAPDF_FIELD_VALUE)));
    megapdf_form_fields_free(f);

    // Stamps land where the UI asked, with the mark's stroke in points.
    const megapdf_rect square{200, 200, 220, 220};
    auto mark_id = utf16("mark:uu");
    check(megapdf_add_check_mark(p.page, &square, MEGAPDF_MARK_CROSS, mark_id.data()) == MEGAPDF_OK, "a mark goes on");
    const megapdf_rect sig{300, 100, 400, 150};
    std::vector<unsigned char> bgra(4 * 4 * 4, 0x80);
    auto sig_id = utf16("sig:uu");
    check(megapdf_add_image_stamp(p.page, bgra.data(), 4, 4, &sig, sig_id.data()) == MEGAPDF_OK, "a signature goes on");
    auto list = stamps_of(p.page);
    check(list.ids.size() == 2 && rect_close(list.stamps[0].bounds, megapdf_rect{202, 202, 218, 218}, 0.05) &&
              rect_close(list.stamps[1].bounds, sig, 0.05),
          "the mark and the signature read back where they were placed, in points",
          list.ids.size() == 2 ? rect_str(list.stamps[0].bounds) + " / " + rect_str(list.stamps[1].bounds) : "count");
    const megapdf_rect moved{350, 200, 450, 250};
    check(megapdf_move_image_stamp(p.page, sig_id.data(), &moved) == MEGAPDF_OK, "the signature moves");
    list = stamps_of(p.page);
    check(list.ids.size() == 2 && rect_close(list.stamps[1].bounds, moved, 0.05), "the moved signature is where it was dropped",
          list.ids.size() == 2 ? rect_str(list.stamps[1].bounds) : "count");

    // Whiteouts, text boxes and a recreated run: positions and font sizes in points.
    const megapdf_rect cover{10, 10, 50, 30};
    int wo = -1;
    check(megapdf_add_whiteout(p.page, &cover, &wo) == MEGAPDF_OK, "a whiteout goes on");
    auto covers = whiteouts_of(p.page);
    check(covers.size() == 1 && rect_close(covers[0].bounds, cover, 0.05), "the whiteout reads back in points",
          covers.empty() ? "none" : rect_str(covers[0].bounds));
    megapdf_rect wb{};
    check(megapdf_object_bounds(p.page, wo, &wb) == MEGAPDF_OK && rect_close(wb, cover, 0.05), "object bounds are in points", rect_str(wb));

    auto text = utf16("Unit"), box_id = utf16("text:uu");
    int index = -1;
    check(megapdf_add_text_box(p.page, -1, text.data(), "Courier", 10, 40, 60, box_id.data(), &index) == MEGAPDF_OK, "a text box goes on");
    auto boxes = boxes_of(p.page);
    check(boxes.size() == 1 && close_to(boxes[0].size, 10, 0.01) && close_to(boxes[0].bounds.left, 40, 1.0) &&
              boxes[0].bounds.bottom < 60 && boxes[0].bounds.top > 60 && boxes[0].bounds.top - boxes[0].bounds.bottom < 14,
          "a 10 pt text box reads back 10 pt tall at its baseline", boxes.empty() ? "none" : std::to_string(boxes[0].size) + " " + rect_str(boxes[0].bounds));
    check(megapdf_move_text_box(p.page, index, 80, 90) == MEGAPDF_OK, "the text box moves");
    boxes = boxes_of(p.page);
    check(boxes.size() == 1 && close_to(boxes[0].bounds.left, 80, 0.05) && close_to(boxes[0].bounds.bottom, 90, 0.05),
          "the moved text box's corner is where it was dropped", boxes.empty() ? "none" : rect_str(boxes[0].bounds));
    auto run_text = utf16("Recreated");
    const int objects = FPDFPage_CountObjects_via_bounds_probe(p.page);
    check(megapdf_insert_text_run(p.page, objects, run_text.data(), "Helvetica", 12, 40, 500) == MEGAPDF_OK, "a run is recreated");
    megapdf_rect rb{};
    megapdf_object_bounds(p.page, objects, &rb);
    check(close_to(rb.left, 40, 2.0) && rb.bottom < 500 && rb.top > 505 && rb.top - rb.bottom < 16,
          "the recreated 12 pt run stands at its baseline in points", rect_str(rb));

    // Images: shrink sizes its target from the placed size in points.
    megapdf_images* images = megapdf_images_load(d.doc);
    megapdf_image_info info{};
    check(megapdf_image_count(images) == 1 && megapdf_image_get(images, 0, &info) == MEGAPDF_OK &&
              close_to(info.display_width, 100, 0.01) && close_to(info.display_height, 50, 0.01),
          "the image is placed 100 x 50 pt", std::to_string(info.display_width) + "x" + std::to_string(info.display_height));
    megapdf_images_free(images);

    // The layout guard's budgets are points: an untouched rewrite keeps the page.
    megapdf_layout_verdict page_verdict{};
    check(megapdf_page_regeneration_verdict(p.page, &page_verdict) == 1, "the page keeps its look when regenerated",
          std::to_string(page_verdict.cause));

    // A save keeps the unit, and everything stays where it was put.
    std::vector<unsigned char> out;
    check(megapdf_save(d.doc, collect, &out) == MEGAPDF_OK, "userunit.pdf saves");
    keep_saved("userunit", out);
    megapdf_document* again = megapdf_open(out.data(), out.size(), nullptr);
    megapdf_page* ap = again ? megapdf_load_page(again, 0) : nullptr;
    check(ap != nullptr && close_to(megapdf_page_user_unit(ap), 2.0, 1e-9) && close_to(megapdf_page_width(ap), 612),
          "the saved copy keeps /UserUnit 2");
    if (ap != nullptr) {
        auto reboxes = boxes_of(ap);
        check(reboxes.size() == 1 && close_to(reboxes[0].bounds.left, 80, 0.05) && close_to(reboxes[0].bounds.bottom, 90, 0.05),
              "the text box is where it was left after a reopen", reboxes.empty() ? "none" : rect_str(reboxes[0].bounds));
        megapdf_close_page(ap);
    }
    megapdf_close(again);
}

int main(int argc, char** argv) {
    if (argc < 4) {
        std::fprintf(stderr, "usage: %s <fixtures-dir> <schematic.pdf> <text_runs.txt>\n", argv[0]);
        return 2;
    }
    test_null_handles();
    test_open_failures(argv[1]);
    test_document_and_geometry(argv[1]);
    test_lifecycle(argv[1]);
    test_open_from_file(argv[1]);
    test_read_from_copy(argv[1]);
    test_fixture_square(argv[1]);
    test_search(argv[1]);
    test_schematic(argv[2]);
    test_text_runs(argv[1], argv[2], argv[3]);
    test_form_fields(argv[1]);
    test_stamps(argv[1]);
    test_whiteouts_and_text_boxes(argv[1]);
    test_save_flatten_images(argv[1]);
    test_protected_save(argv[1]);
    test_security(argv[1]);
    test_subset_font_glyphs();
    test_cid_font_glyphs();
    test_render();
    test_render_page(argv[1]);
    test_text_editing(argv[1]);
    test_rewrite_fidelity();
    test_edit_scenarios();
    test_edit_beside_fields_and_annotations();
    test_form_xobject_text();
    test_hidden_copies(argv[1]);
    test_far_hidden_copies(argv[1]);
    test_scaled_jpeg_render();
    test_text_in_one_pass(argv[1], argv[2]);
    test_large_image_stand_ins();
    test_verdicts_follow_changes();
    test_layout_verdicts();
    test_page_regeneration_verdict();
    test_page_check_cancel_and_concurrency();
    test_user_unit(argv[1]);
    if (failures == 0) std::printf("core tests: all passed\n");
    else std::fprintf(stderr, "core tests: %d failure(s)\n", failures);
    return failures == 0 ? 0 : 1;
}
