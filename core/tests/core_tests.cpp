// Tests for the shared engine core, run where the code lives (#104, ADR-003).
//
// The per-platform suites remain the parity gate; this target exists so a
// divergence in `core/` fails on the same commit, on every CI OS, with an
// AddressSanitizer build on Linux, instead of three jobs later. Deliberately
// framework-free: a test is a function, a failure is a line on stderr and a
// non-zero exit.
//
// Usage: megapdf_core_tests <fixtures-dir> <schematic.pdf>
//   fixtures-dir  output of tools/gen_test_fixtures.py
//   schematic.pdf tests/MegaPDF.Core.Tests/Fixtures/microbit-v2-schematic.pdf (#98)

#include <cmath>
#include <cstdio>
#include <fstream>
#include <iterator>
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
    check(megapdf_document_raw(nullptr) == nullptr && megapdf_page_raw(nullptr) == nullptr, "raw accessors tolerate NULL");
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
    check(megapdf_document_raw(d.doc) != nullptr, "raw FPDF_DOCUMENT is available");
    check(megapdf_document_form_raw(d.doc) != nullptr, "form-fill environment was initialised");

    Page p(d.doc, 0);
    check(p.page != nullptr, "page 1 loads");
    if (!p.page) return;
    check(megapdf_page_raw(p.page) != nullptr, "raw FPDF_PAGE is available");
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

}  // namespace

int main(int argc, char** argv) {
    if (argc < 3) {
        std::fprintf(stderr, "usage: %s <fixtures-dir> <schematic.pdf>\n", argv[0]);
        return 2;
    }
    test_null_handles();
    test_open_failures(argv[1]);
    test_document_and_geometry(argv[1]);
    test_lifecycle(argv[1]);
    test_fixture_square(argv[1]);
    test_search(argv[1]);
    test_schematic(argv[2]);
    if (failures == 0) std::printf("core tests: all passed\n");
    else std::fprintf(stderr, "core tests: %d failure(s)\n", failures);
    return failures == 0 ? 0 : 1;
}
