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
#include <string>
#include <vector>

#include "fpdfview.h"
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

struct Doc {
    FPDF_DOCUMENT doc = nullptr;
    explicit Doc(const std::string& path) { doc = FPDF_LoadDocument(path.c_str(), nullptr); }
    ~Doc() { if (doc) FPDF_CloseDocument(doc); }
};

struct Page {
    FPDF_PAGE page = nullptr;
    Page(FPDF_DOCUMENT d, int i) { page = d ? FPDF_LoadPage(d, i) : nullptr; }
    ~Page() { if (page) FPDF_ClosePage(page); }
};

std::vector<megapdf_rect> squares(FPDF_PAGE page) {
    const size_t n = megapdf_detect_checkbox_squares(page, nullptr, 0);
    std::vector<megapdf_rect> out(n);
    if (n > 0) megapdf_detect_checkbox_squares(page, out.data(), n);
    return out;
}

// SDD §6.2 contract 2 on the shared fixture: page 1 of fixture.pdf draws exactly
// one 12x12 pt stroked square at (72,600). The Android, iOS and desktop suites
// assert this same rect against their bindings; here it is asserted against the
// implementation itself.
void test_fixture_square(const std::string& fixtures) {
    Doc d(fixtures + "/fixture.pdf");
    check(d.doc != nullptr, "fixture.pdf opens");
    if (!d.doc) return;
    Page p(d.doc, 0);
    check(p.page != nullptr, "fixture.pdf page 1 loads");
    if (!p.page) return;

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

    // Page 2 has no square.
    Page p2(d.doc, 1);
    check(p2.page != nullptr && squares(p2.page).empty(), "fixture.pdf page 2 has no candidates");
}

// The #30 root cause: content is reported in MediaBox space while the CropBox is
// what renders. cropped.pdf has CropBox [0 100 612 700]; the core must report the
// origin it subtracts.
void test_crop_origin(const std::string& fixtures) {
    Doc d(fixtures + "/cropped.pdf");
    check(d.doc != nullptr, "cropped.pdf opens");
    if (!d.doc) return;
    Page p(d.doc, 0);
    if (!p.page) { check(false, "cropped.pdf page loads"); return; }
    double x = -1, y = -1;
    megapdf_crop_origin(p.page, &x, &y);
    check(close_to(x, 0) && close_to(y, 100), "cropped.pdf crop origin is (0,100)", std::to_string(x) + "," + std::to_string(y));
    check(squares(p.page).empty(), "cropped.pdf has no drawn squares");

    // An uncropped page reports (0,0).
    Doc f(fixtures + "/fixture.pdf");
    Page fp(f.doc, 0);
    x = y = -1;
    megapdf_crop_origin(fp.page, &x, &y);
    check(close_to(x, 0) && close_to(y, 0), "fixture.pdf crop origin is (0,0)");
}

// The #98 canary. Search moves into the core with #105; until then this proves the
// fixture reaches the target and pins the page count so the search assertion has
// somewhere to land.
void test_schematic(const std::string& schematic) {
    Doc d(schematic);
    check(d.doc != nullptr, "micro:bit schematic opens");
    if (!d.doc) return;
    check(FPDF_GetPageCount(d.doc) == 3, "schematic has 3 pages");
    for (int i = 0; i < 3; i++) {
        Page p(d.doc, i);
        check(p.page != nullptr, "schematic page loads");
        if (p.page) (void)squares(p.page);   // must not crash or read out of bounds (ASan)
    }
}

void test_null_handles() {
    check(megapdf_detect_checkbox_squares(nullptr, nullptr, 0) == 0, "null page yields 0 candidates");
    double x = 1, y = 1;
    megapdf_crop_origin(nullptr, &x, &y);
    check(x == 0 && y == 0, "null page yields crop origin (0,0)");
}

}  // namespace

int main(int argc, char** argv) {
    if (argc < 3) {
        std::fprintf(stderr, "usage: %s <fixtures-dir> <schematic.pdf>\n", argv[0]);
        return 2;
    }
    FPDF_InitLibrary();
    test_null_handles();
    test_fixture_square(argv[1]);
    test_crop_origin(argv[1]);
    test_schematic(argv[2]);
    FPDF_DestroyLibrary();
    if (failures == 0) std::printf("core tests: all passed\n");
    else std::fprintf(stderr, "core tests: %d failure(s)\n", failures);
    return failures == 0 ? 0 : 1;
}
