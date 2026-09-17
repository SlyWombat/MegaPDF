// tools/leakcheck — the #173 leak checker as a standalone tool.
//
// It drives the core the way an app does: mark areas, apply, save; then it asks
// leakcheck.h whether the removed content can be recovered from the saved file by any
// means. The core tests run the same searches over the fixtures (core/tests/core_tests.cpp),
// so the suite and the corpus battery can never disagree about what a leak is.
//
//   leakcheck fixture  <pdf> <canary> <out.pdf> [--areas x0,y0,x1,y1[:page] ...]
//       Redact the given areas (default: every area whose text contains the canary, plus
//       every image object that intersects one) and report what is left.
//
//   leakcheck battery  <pdf> <out-dir> [--seed N] [--pages N] [--baseline]
//       The corpus battery (#173): a random text area and a random image area per page.
//       There is no canary to hunt for in someone else's document, so what is checked is
//       that the text the areas covered no longer extracts, that the pixels inside are the
//       redaction colour, that nothing outside the areas changed, and that the file still
//       opens and passes qpdf. --baseline applies no redaction and only times the open,
//       render and save, so a battery run can be compared with one.
//
// Nothing about the document is printed beyond counts and timings: the corpus is personal.
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <random>
#include <string>
#include <vector>

#include "leakcheck.h"
#include "megapdf_core.h"
#include "fpdfview.h"

namespace {

struct SaveSink {
    std::vector<unsigned char> bytes;
};

int WriteBlock(void* context, const void* data, size_t size) {
    auto* sink = static_cast<SaveSink*>(context);
    const auto* p = static_cast<const unsigned char*>(data);
    sink->bytes.insert(sink->bytes.end(), p, p + size);
    return 1;
}

bool WriteFile(const std::string& path, const std::vector<unsigned char>& bytes) {
    FILE* f = std::fopen(path.c_str(), "wb");
    if (f == nullptr) return false;
    const bool ok = bytes.empty() || std::fwrite(bytes.data(), 1, bytes.size(), f) == bytes.size();
    std::fclose(f);
    return ok;
}

std::string Utf8Of(megapdf_text* text, size_t run) {
    const size_t n = megapdf_text_run_string(text, run, MEGAPDF_TEXT_RUN_TEXT, nullptr, 0);
    std::vector<unsigned short> u(n + 1, 0);
    megapdf_text_run_string(text, run, MEGAPDF_TEXT_RUN_TEXT, u.data(), u.size());
    std::string out;
    for (size_t i = 0; i < n; i++) out += u[i] < 0x80 ? static_cast<char>(u[i]) : '?';
    return out;
}

const char* ReasonName(int reason) {
    switch (reason) {
        case MEGAPDF_REDACT_TYPE3_FONT: return "type3-font";
        case MEGAPDF_REDACT_FONT_CANNOT_REDRAW: return "font-cannot-redraw";
        case MEGAPDF_REDACT_LAYOUT: return "layout-guard";
        case MEGAPDF_REDACT_SHARED_FORM: return "shared-form";
        case MEGAPDF_REDACT_IMAGE: return "image";
        case MEGAPDF_REDACT_ANNOTATION: return "annotation";
        case MEGAPDF_REDACT_PDFIUM: return "pdfium";
        default: return "unknown";
    }
}

// How many times `word` appears in the document's extracted text. A corpus word can only
// stand in for a canary when the answer is 1: any other occurrence is found again after the
// redaction because the document says it somewhere that was never marked.
int OccurrencesInText(const std::string& path, const std::string& word) {
    megapdf_document* doc = megapdf_open_file(path.c_str(), nullptr);
    if (doc == nullptr) return 0;
    int found = 0;
    for (int p = 0; p < megapdf_page_count(doc) && found < 2; p++) {
        megapdf_page* page = megapdf_load_page(doc, p);
        if (page == nullptr) continue;
        megapdf_text* text = megapdf_text_load(page, MEGAPDF_TEXT_ALL);
        for (size_t r = 0; r < megapdf_text_run_count(text); r++) {
            const std::string run = Utf8Of(text, r);
            for (size_t at = run.find(word); at != std::string::npos; at = run.find(word, at + 1)) found++;
        }
        megapdf_text_free(text);
        megapdf_close_page(page);
    }
    megapdf_close(doc);
    return found;
}

void PrintRefusals(megapdf_redaction_report* report, const char* prefix) {
    const size_t n = megapdf_redaction_refusals(report, nullptr, 0);
    std::vector<megapdf_redaction_refusal> refusals(n);
    if (n > 0) megapdf_redaction_refusals(report, refusals.data(), n);
    for (size_t i = 0; i < n; i++) {
        std::vector<char> msg(megapdf_redaction_refusal_message(report, i, nullptr, 0) + 1, 0);
        megapdf_redaction_refusal_message(report, i, msg.data(), msg.size());
        std::printf("%srefused page %d (%s): %s\n", prefix, refusals[i].page_index, ReasonName(refusals[i].reason),
                    msg.data());
    }
}

// --------------------------------------------------------------------------
// fixture mode
// --------------------------------------------------------------------------

int Fixture(int argc, char** argv) {
    const std::string in = argv[2], canary = argv[3], out = argv[4];
    std::vector<leakcheck::Area> areas;
    for (int i = 5; i < argc; i++) {
        if (std::strcmp(argv[i], "--areas") == 0) continue;
        double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        int page = 0;
        if (std::sscanf(argv[i], "%lf,%lf,%lf,%lf:%d", &x0, &y0, &x1, &y1, &page) >= 4) {
            areas.push_back(leakcheck::Area{page, x0, y0, x1, y1});
        }
    }

    megapdf_document* doc = megapdf_open_file(in.c_str(), nullptr);
    if (doc == nullptr) {
        std::printf("FAIL open: %s\n", megapdf_last_error_message());
        return 2;
    }
    // With no areas given, mark exactly where the canary is drawn — the core's own search,
    // which spans text objects, so a canary split across two runs is still found — and every
    // image object on the page. That is what a user redacting "this word" and "that picture"
    // would mark, and it leaves the KEEP words on either side to be checked.
    if (areas.empty()) {
        std::vector<unsigned short> term;
        for (char c : canary) term.push_back(static_cast<unsigned short>(c));
        term.push_back(0);
        for (int p = 0; p < megapdf_page_count(doc); p++) {
            megapdf_page* page = megapdf_load_page(doc, p);
            if (page == nullptr) continue;
            const size_t doubles = megapdf_search_page(page, term.data(), nullptr, 0);
            std::vector<double> hits(doubles);
            if (doubles > 0) megapdf_search_page(page, term.data(), hits.data(), hits.size());
            for (size_t i = 0; i + 1 <= hits.size();) {
                const size_t rects = static_cast<size_t>(hits[i++]);
                for (size_t k = 0; k < rects && i + 4 <= hits.size(); k++, i += 4) {
                    areas.push_back(leakcheck::Area{p, hits[i], hits[i + 1], hits[i + 2], hits[i + 3]});
                }
            }
            const int objects = megapdf_page_object_count(page);
            for (int o = 0; o < objects; o++) {
                if (megapdf_object_type(page, o) != 3) continue;   // FPDF_PAGEOBJ_IMAGE
                megapdf_rect b{};
                if (megapdf_object_bounds(page, o, &b) == MEGAPDF_OK) {
                    areas.push_back(leakcheck::Area{p, b.left, b.bottom, b.right, b.top});
                }
            }
            megapdf_close_page(page);
        }
    }
    if (areas.empty()) {
        std::printf("FAIL: nothing to redact — no run carries the canary and the page has no image\n");
        megapdf_close(doc);
        return 2;
    }
    for (const leakcheck::Area& a : areas) {
        megapdf_page* page = megapdf_load_page(doc, a.page_index);
        megapdf_rect r{a.left, a.bottom, a.right, a.top};
        megapdf_redaction_mark(page, &r, nullptr);
        megapdf_close_page(page);
    }
    std::printf("marked %zu areas\n", areas.size());

    megapdf_redaction_report* report = nullptr;
    const int rc = megapdf_redact_apply(doc, nullptr, &report);
    if (rc != MEGAPDF_OK) {
        std::printf("REFUSED (%d): %s\n", rc, megapdf_last_error_message());
        if (report != nullptr) {
            PrintRefusals(report, "  ");
            megapdf_redaction_report_free(report);
        }
        megapdf_close(doc);
        return 3;
    }
    megapdf_redaction_counts counts{};
    megapdf_redaction_report_counts(report, &counts);
    // What the page may look different in, which is the mark grown to whatever straddling
    // glyph came off with it: the render check is judged against that, not against the mark.
    const size_t applied_count = megapdf_redaction_applied_areas(report, nullptr, 0);
    std::vector<megapdf_redaction_applied> applied(applied_count);
    if (applied_count > 0) megapdf_redaction_applied_areas(report, applied.data(), applied_count);
    for (leakcheck::Area& a : areas) {
        for (const megapdf_redaction_applied& one : applied) {
            if (one.page_index != a.page_index) continue;
            a.affected_left = one.affected.left;
            a.affected_bottom = one.affected.bottom;
            a.affected_right = one.affected.right;
            a.affected_top = one.affected.top;
        }
    }
    std::printf("applied: %d areas on %d pages — %d characters, %d runs, %d partial runs, %d hidden copies, "
                "%d images, %d paths, %d annotations\n",
                counts.areas, counts.pages, counts.characters, counts.text_runs, counts.partial_runs,
                counts.hidden_copies, counts.images, counts.paths, counts.annotations);
    megapdf_redaction_report_free(report);

    SaveSink sink;
    if (megapdf_save(doc, WriteBlock, &sink) != MEGAPDF_OK || !WriteFile(out, sink.bytes)) {
        std::printf("FAIL save: %s\n", megapdf_last_error_message());
        megapdf_close(doc);
        return 2;
    }
    megapdf_close(doc);
    std::printf("saved %zu bytes to %s\n", sink.bytes.size(), out.c_str());

    bool qpdf_ran = false;
    leakcheck::PixelResult pixels;
    const std::vector<leakcheck::Finding> findings =
        leakcheck::Check(out, in, canary, areas, 0x000000, &qpdf_ran, &pixels);
    std::printf("pixels: %lld inside (%lld wrong), %lld outside (%lld changed); qpdf %s\n", pixels.inside,
                pixels.inside_wrong, pixels.outside, pixels.outside_changed, qpdf_ran ? "ran" : "NOT AVAILABLE");
    if (pixels.outside_changed > 0) {
        std::printf("  changed pixels lie in [%.1f %.1f %.1f %.1f]\n", pixels.changed_left, pixels.changed_bottom,
                    pixels.changed_right, pixels.changed_top);
    }
    for (const leakcheck::Finding& f : findings) {
        std::printf("LEAK [%s] %s\n", f.where.c_str(), f.detail.c_str());
    }
    std::printf("%s: %zu findings\n", findings.empty() ? "CLEAN" : "LEAKED", findings.size());
    return findings.empty() ? 0 : 1;
}

// --------------------------------------------------------------------------
// battery mode
// --------------------------------------------------------------------------

struct BatteryResult {
    int pages = 0, areas = 0, refused = 0, leaks = 0;
    long long outside_changed = 0, inside_wrong = 0;
    double seconds = 0;
};

int Battery(int argc, char** argv) {
    const std::string in = argv[2], outdir = argv[3];
    unsigned seed = 1;
    int page_limit = 8;
    bool baseline = false;
    for (int i = 4; i < argc; i++) {
        if (std::strcmp(argv[i], "--seed") == 0 && i + 1 < argc) seed = static_cast<unsigned>(std::atoi(argv[++i]));
        else if (std::strcmp(argv[i], "--pages") == 0 && i + 1 < argc) page_limit = std::atoi(argv[++i]);
        else if (std::strcmp(argv[i], "--baseline") == 0) baseline = true;
    }
    const auto started = std::chrono::steady_clock::now();
    megapdf_document* doc = megapdf_open_file(in.c_str(), nullptr);
    if (doc == nullptr) {
        std::printf("result=open-failed\n");
        return 2;
    }
    std::mt19937 rng(seed);
    BatteryResult result;
    const int pages = megapdf_page_count(doc);
    std::vector<leakcheck::Area> areas;
    std::vector<std::string> covered;    // the text each area covered, to hunt for afterwards

    for (int p = 0; p < pages && p < page_limit; p++) {
        megapdf_page* page = megapdf_load_page(doc, p);
        if (page == nullptr) continue;
        result.pages++;
        if (!baseline) {
            // A random text run, and a random image object.
            megapdf_text* text = megapdf_text_load(page, MEGAPDF_TEXT_ALL);
            const size_t runs = megapdf_text_run_count(text);
            if (runs > 0) {
                const size_t pick = rng() % runs;
                megapdf_text_run run{};
                megapdf_text_run_get(text, pick, &run);
                const std::string word = Utf8Of(text, pick);
                if (run.bounds.right > run.bounds.left && run.bounds.top > run.bounds.bottom && word.size() >= 4) {
                    areas.push_back(
                        leakcheck::Area{p, run.bounds.left, run.bounds.bottom, run.bounds.right, run.bounds.top});
                    covered.push_back(word);
                    megapdf_rect r{run.bounds.left, run.bounds.bottom, run.bounds.right, run.bounds.top};
                    megapdf_redaction_mark(page, &r, nullptr);
                    result.areas++;
                }
            }
            megapdf_text_free(text);
            std::vector<int> images;
            const int objects = megapdf_page_object_count(page);
            for (int o = 0; o < objects; o++) if (megapdf_object_type(page, o) == 3) images.push_back(o);
            if (!images.empty()) {
                const int pick = images[rng() % images.size()];
                megapdf_rect b{};
                if (megapdf_object_bounds(page, pick, &b) == MEGAPDF_OK && b.right > b.left && b.top > b.bottom) {
                    // Half the image, so the check has pixels on both sides of the edge.
                    megapdf_rect half{b.left, b.bottom, (b.left + b.right) / 2, b.top};
                    areas.push_back(leakcheck::Area{p, half.left, half.bottom, half.right, half.top});
                    megapdf_redaction_mark(page, &half, nullptr);
                    result.areas++;
                }
            }
        }
        megapdf_close_page(page);
    }

    int rc = MEGAPDF_OK;
    if (!baseline && result.areas > 0) {
        megapdf_redaction_report* report = nullptr;
        rc = megapdf_redact_apply(doc, nullptr, &report);
        if (report != nullptr) {
            if (rc != MEGAPDF_OK) {
                result.refused = 1;
                PrintRefusals(report, "refusal: ");
            } else {
                // Where the page may look different: the marks grown to what a straddling
                // glyph took with it. The render check is judged against that, exactly as
                // the core's own guard is.
                const size_t n = megapdf_redaction_applied_areas(report, nullptr, 0);
                std::vector<megapdf_redaction_applied> applied(n);
                if (n > 0) megapdf_redaction_applied_areas(report, applied.data(), n);
                for (leakcheck::Area& a : areas) {
                    for (const megapdf_redaction_applied& one : applied) {
                        if (one.page_index != a.page_index) continue;
                        a.affected_left = one.affected.left;
                        a.affected_bottom = one.affected.bottom;
                        a.affected_right = one.affected.right;
                        a.affected_top = one.affected.top;
                    }
                }
                if (std::getenv("MEGAPDF_LEAKCHECK_VERBOSE") != nullptr) {
                    for (const megapdf_redaction_applied& one : applied) {
                        std::printf("  page %d marked [%.0f %.0f %.0f %.0f] affected [%.0f %.0f %.0f %.0f]\n",
                                    one.page_index, one.marked.left, one.marked.bottom, one.marked.right,
                                    one.marked.top, one.affected.left, one.affected.bottom, one.affected.right,
                                    one.affected.top);
                    }
                }
            }
            megapdf_redaction_report_free(report);
        }
    }
    SaveSink sink;
    const bool saved = megapdf_save(doc, WriteBlock, &sink) == MEGAPDF_OK;
    megapdf_close(doc);
    const std::string out = outdir + "/redacted.pdf";
    if (saved) WriteFile(out, sink.bytes);
    result.seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();

    if (baseline) {
        // A baseline run redacts nothing, so it times the open, render and save alone — and
        // it renders the saved copy against the original with no areas at all, which is the
        // only way to tell what SAVING changes from what REDACTING changes. A save
        // regenerates content and can normalise a form field's appearance; that difference
        // belongs to the save, and a battery run must not be blamed for it.
        leakcheck::PixelResult pixels;
        std::vector<leakcheck::Finding> findings;
        if (saved) {
            FPDF_DOCUMENT after = FPDF_LoadDocument(out.c_str(), nullptr);
            FPDF_DOCUMENT before = FPDF_LoadDocument(in.c_str(), nullptr);
            if (after != nullptr) {
                leakcheck::CheckPixels(after, before, {}, 0x000000, 72, &pixels, &findings);
            }
            if (before != nullptr) FPDF_CloseDocument(before);
            if (after != nullptr) FPDF_CloseDocument(after);
        }
        std::printf("result=baseline pages=%d seconds=%.3f bytes=%zu worst_page=%d worst_pct=%.4f\n",
                    result.pages, result.seconds, sink.bytes.size(), pixels.changed_page,
                    pixels.worst_page_fraction * 100.0);
        return 0;
    }
    if (rc != MEGAPDF_OK) {
        std::printf("result=refused pages=%d areas=%d seconds=%.3f\n", result.pages, result.areas, result.seconds);
        return 0;
    }
    if (!saved) {
        std::printf("result=save-failed pages=%d areas=%d\n", result.pages, result.areas);
        return 2;
    }
    // Nothing the areas covered may still extract, and the pixels must be right.
    //
    // A corpus document has no canary planted in it, so the word a random area covered has
    // to serve as one — and only a word the document says exactly ONCE can. Any other is
    // found again in the file because the document says it somewhere that was never marked,
    // which is not a leak. `unique_canary` is the first such word, or empty when the sampled
    // pages offered none; then the string searches are skipped and the in-area checks (the
    // core's own, plus the pixels below) are what judge the document.
    bool qpdf_ran = false;
    leakcheck::PixelResult pixels;
    int leaks = 0;
    std::string canary;
    for (const std::string& word : covered) {
        if (word.size() < 6) continue;
        if (OccurrencesInText(in, word) == 1) { canary = word; break; }
    }
    const std::vector<leakcheck::Finding> findings =
        leakcheck::Check(out, in, canary.empty() ? std::string("\x01unmatchable") : canary, areas, 0x000000,
                         &qpdf_ran, &pixels);
    for (const leakcheck::Finding& one : findings) {
        // The pixel findings are reported once, below, not as string leaks.
        if (one.where.rfind("pixels", 0) == 0) continue;
        leaks++;
        std::printf("leak: %s\n", one.where.c_str());
    }
    result.leaks = leaks;
    result.inside_wrong = pixels.inside_wrong;
    result.outside_changed = pixels.worst_page_fraction > leakcheck::kRenderBudget ? pixels.outside_changed : 0;
    std::printf("result=ok pages=%d areas=%d leaks=%d inside_wrong=%lld outside_changed=%lld seconds=%.3f bytes=%zu "
                "qpdf=%d canary=%d worst_page=%d worst_pct=%.4f changed_box=[%.0f %.0f %.0f %.0f]\n",
                result.pages, result.areas, result.leaks, result.inside_wrong, result.outside_changed, result.seconds,
                sink.bytes.size(), qpdf_ran ? 1 : 0, canary.empty() ? 0 : 1, pixels.changed_page,
                pixels.worst_page_fraction * 100.0, pixels.changed_left,
                pixels.changed_bottom, pixels.changed_right, pixels.changed_top);
    return result.leaks == 0 && result.outside_changed == 0 ? 0 : 1;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc >= 5 && std::strcmp(argv[1], "fixture") == 0) return Fixture(argc, argv);
    if (argc >= 4 && std::strcmp(argv[1], "battery") == 0) return Battery(argc, argv);
    std::printf("usage:\n"
                "  leakcheck fixture <pdf> <canary> <out.pdf> [x0,y0,x1,y1:page ...]\n"
                "  leakcheck battery <pdf> <out-dir> [--seed N] [--pages N] [--baseline]\n");
    return 64;
}
