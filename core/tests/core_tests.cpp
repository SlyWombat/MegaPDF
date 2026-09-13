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

#include "megapdf_core.h"

namespace {

int failures = 0;

void check(bool ok, const char* what, const std::string& detail = "") {
    if (ok) return;
    failures++;
    std::fprintf(stderr, "FAIL: %s%s%s\n", what, detail.empty() ? "" : " — ", detail.c_str());
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

void test_text_editing(const std::string& fixtures) {
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
            check(megapdf_set_text(p.page, first.object_index, howdy.data(), 0, &outcome) == MEGAPDF_OK, "two-run edit returns OK");
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
            check(megapdf_set_text(p.page, r.object_index, latin.data(), 0, &outcome) == MEGAPDF_OK, "the edit returns OK");
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
        check(megapdf_set_text(p.page, 0, text.data(), 0, &outcome) == MEGAPDF_OK && outcome == MEGAPDF_EDIT_IN_PLACE, "a standard-font edit stays tier 1",
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
        check(megapdf_set_text(p.page, before.object_index, swapped.data(), 1, &outcome) == MEGAPDF_OK && outcome == MEGAPDF_EDIT_SUBSTITUTED,
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
        check(megapdf_set_text(p.page, 0, empty.data(), 0, &outcome) == MEGAPDF_ERR_ARGUMENT, "empty text is an argument error");
        int path_index = -1;
        for (int i = 0; megapdf_object_type(p.page, i) >= 0; i++) if (megapdf_object_type(p.page, i) == 2) { path_index = i; break; }
        check(path_index >= 0 && megapdf_set_text(p.page, path_index, text.data(), 0, &outcome) == MEGAPDF_ERR_ARGUMENT, "editing a path is an argument error");
        check(megapdf_set_text(nullptr, 0, text.data(), 0, &outcome) == MEGAPDF_ERR_ARGUMENT, "set_text rejects a null page");

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
        check(megapdf_set_text(p.page, tagged_index, retyped.data(), 1, &outcome) == MEGAPDF_OK && outcome == MEGAPDF_EDIT_SUBSTITUTED, "the tagged box substitutes");
        check(megapdf_find_text_box(p.page, tagged.data()) == tagged_index, "the substituted box keeps its id at its index");
        auto boxes = boxes_of(p.page);
        bool found = false;
        for (const auto& b : boxes) if (b.object_index == tagged_index) { found = true; check(b.text == "Retyped box" && b.font == face_before, "the substituted box keeps its face param", b.font + " vs " + face_before); }
        check(found, "the substituted box is still a text box");

        int legacy = -1;
        for (const auto& b : boxes) if (b.id.empty()) { legacy = b.object_index; break; }
        check(legacy >= 0, "a legacy box exists");
        check(megapdf_set_text(p.page, legacy, retyped.data(), 1, &outcome) == MEGAPDF_OK, "the legacy box substitutes");
        bool still_untagged = false;
        for (const auto& b : boxes_of(p.page)) if (b.object_index == legacy) still_untagged = b.id.empty();
        check(still_untagged, "a legacy box gains no fabricated id");
    }
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 4) {
        std::fprintf(stderr, "usage: %s <fixtures-dir> <schematic.pdf> <text_runs.txt>\n", argv[0]);
        return 2;
    }
    test_null_handles();
    test_open_failures(argv[1]);
    test_document_and_geometry(argv[1]);
    test_lifecycle(argv[1]);
    test_fixture_square(argv[1]);
    test_search(argv[1]);
    test_schematic(argv[2]);
    test_text_runs(argv[1], argv[2], argv[3]);
    test_form_fields(argv[1]);
    test_stamps(argv[1]);
    test_whiteouts_and_text_boxes(argv[1]);
    test_save_flatten_images(argv[1]);
    test_render();
    test_render_page(argv[1]);
    test_text_editing(argv[1]);
    if (failures == 0) std::printf("core tests: all passed\n");
    else std::fprintf(stderr, "core tests: %d failure(s)\n", failures);
    return failures == 0 ? 0 : 1;
}
