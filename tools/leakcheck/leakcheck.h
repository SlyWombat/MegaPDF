// The #173 leak checker: after a redaction is applied and saved, can the removed text be
// recovered from the file by ANY means? This header holds the searches; leakcheck.cpp is
// the standalone tool (the corpus battery), and core/tests/core_tests.cpp runs the same
// searches over the fixtures, so the test suite and the battery can never disagree.
//
// A redaction is only done when nothing inside the area can be recovered. So the checker
// does not ask PDFium whether the text is gone — PDFium is the thing that removed it. It
// looks at the bytes:
//
//   1. PDFium's own text extraction, per page             (the easy one)
//   2. every stream decompressed by `qpdf --qdf --decode-level=all` (what any tool sees)
//   3. the raw file, as ASCII, UTF-16LE, UTF-16BE, and as PDF hex-string digits
//   4. /Info, the XMP packet, the outline, and every annotation
//   5. the pixels inside the areas, which must be the redaction colour
//
// Header-only and dependency-free apart from PDFium and, for check 2, the qpdf binary.
#ifndef MEGAPDF_LEAKCHECK_H
#define MEGAPDF_LEAKCHECK_H

#include <algorithm>
#include <cctype>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include "fpdf_annot.h"
#include "fpdf_doc.h"
#include "fpdf_edit.h"
#include "fpdf_text.h"
#include "fpdfview.h"

namespace leakcheck {

struct Area {
    int page_index;
    double left, bottom, right, top;   // crop space, as the redaction marked it
    // The pixels inside the marked area must be the redaction colour, and the page outside
    // must be unchanged. In between sits the part of a straddling glyph that came off with
    // it (see megapdf_redaction_applied_areas): neither covered nor left, and checked by
    // neither test. `affected` is that outer bound, and is the marked area when the core
    // reported none.
    double affected_left = 0, affected_bottom = 0, affected_right = 0, affected_top = 0;

    bool has_affected() const { return affected_right > affected_left && affected_top > affected_bottom; }
};

struct Finding {
    std::string where;    // "pdfium-text page 0", "qpdf stream", "raw utf-16le", ...
    std::string detail;
};

// ---------------------------------------------------------------------------
// Byte searching
// ---------------------------------------------------------------------------

inline bool Contains(const std::vector<unsigned char>& hay, const unsigned char* needle, size_t n) {
    if (n == 0 || hay.size() < n) return false;
    return std::search(hay.begin(), hay.end(), needle, needle + n) != hay.end();
}

inline bool Contains(const std::vector<unsigned char>& hay, const std::string& needle) {
    return Contains(hay, reinterpret_cast<const unsigned char*>(needle.data()), needle.size());
}

// The canary as the four shapes a PDF can carry it in.
inline std::vector<std::pair<std::string, std::vector<unsigned char>>> Forms(const std::string& canary) {
    std::vector<std::pair<std::string, std::vector<unsigned char>>> out;
    out.emplace_back("ascii", std::vector<unsigned char>(canary.begin(), canary.end()));

    std::vector<unsigned char> le, be;
    for (char c : canary) {
        le.push_back(static_cast<unsigned char>(c));
        le.push_back(0);
        be.push_back(0);
        be.push_back(static_cast<unsigned char>(c));
    }
    out.emplace_back("utf-16le", le);
    out.emplace_back("utf-16be", be);

    // A PDF hex string writes each byte as two digits, in either case: <43414E...>.
    static const char* kDigits = "0123456789ABCDEF";
    std::vector<unsigned char> upper, lower;
    for (char c : canary) {
        const auto b = static_cast<unsigned char>(c);
        upper.push_back(static_cast<unsigned char>(kDigits[b >> 4]));
        upper.push_back(static_cast<unsigned char>(kDigits[b & 0xF]));
        lower.push_back(static_cast<unsigned char>(std::tolower(kDigits[b >> 4])));
        lower.push_back(static_cast<unsigned char>(std::tolower(kDigits[b & 0xF])));
    }
    out.emplace_back("hex-upper", upper);
    out.emplace_back("hex-lower", lower);
    return out;
}

inline std::vector<unsigned char> ReadFile(const std::string& path) {
    std::vector<unsigned char> out;
    FILE* f = std::fopen(path.c_str(), "rb");
    if (f == nullptr) return out;
    unsigned char buf[65536];
    size_t n;
    while ((n = std::fread(buf, 1, sizeof(buf), f)) > 0) out.insert(out.end(), buf, buf + n);
    std::fclose(f);
    return out;
}

// ---------------------------------------------------------------------------
// The checks
// ---------------------------------------------------------------------------

inline std::string Utf16ToUtf8(const std::vector<unsigned short>& u) {
    std::string out;
    for (unsigned short c : u) {
        if (c == 0) continue;
        if (c < 0x80) out += static_cast<char>(c);
        else out += '?';   // the canary is ASCII; anything else only has to not match
    }
    return out;
}

// 1. PDFium's text extraction, page by page.
inline void CheckExtractedText(FPDF_DOCUMENT doc, const std::string& canary, std::vector<Finding>* out) {
    for (int i = 0; i < FPDF_GetPageCount(doc); i++) {
        FPDF_PAGE page = FPDF_LoadPage(doc, i);
        if (page == nullptr) continue;
        FPDF_TEXTPAGE text = FPDFText_LoadPage(page);
        if (text != nullptr) {
            const int chars = FPDFText_CountChars(text);
            std::vector<unsigned short> buf(static_cast<size_t>(chars) + 1, 0);
            if (chars > 0) FPDFText_GetText(text, 0, chars, buf.data());
            const std::string page_text = Utf16ToUtf8(buf);
            if (page_text.find(canary) != std::string::npos) {
                out->push_back(Finding{"pdfium-text page " + std::to_string(i), "the canary extracts as text"});
            }
            FPDFText_ClosePage(text);
        }
        FPDF_ClosePage(page);
    }
}

// 4. /Info, the XMP packet, the outline and every annotation.
inline void CheckMetadataOutlineAnnots(FPDF_DOCUMENT doc, const std::string& canary, std::vector<Finding>* out) {
    static const char* kKeys[] = {"Title", "Author", "Subject", "Keywords", "Creator", "Producer"};
    for (const char* key : kKeys) {
        const unsigned long bytes = FPDF_GetMetaText(doc, key, nullptr, 0);
        if (bytes <= 2) continue;
        std::vector<unsigned short> buf(bytes / 2 + 1, 0);
        FPDF_GetMetaText(doc, key, buf.data(), bytes);
        if (Utf16ToUtf8(buf).find(canary) != std::string::npos) {
            out->push_back(Finding{std::string("metadata /") + key, "the canary is still in the document info"});
        }
    }

    // The outline, depth first.
    struct Walk {
        static void Go(FPDF_DOCUMENT doc, FPDF_BOOKMARK parent, const std::string& canary, std::vector<Finding>* out,
                       int depth) {
            if (depth > 32) return;
            for (FPDF_BOOKMARK b = FPDFBookmark_GetFirstChild(doc, parent); b != nullptr;
                 b = FPDFBookmark_GetNextSibling(doc, b)) {
                const unsigned long bytes = FPDFBookmark_GetTitle(b, nullptr, 0);
                if (bytes > 2) {
                    std::vector<unsigned short> buf(bytes / 2 + 1, 0);
                    FPDFBookmark_GetTitle(b, buf.data(), bytes);
                    if (Utf16ToUtf8(buf).find(canary) != std::string::npos) {
                        out->push_back(Finding{"outline", "an outline entry still carries the canary"});
                    }
                }
                Go(doc, b, canary, out, depth + 1);
            }
        }
    };
    Walk::Go(doc, nullptr, canary, out, 0);

    // Every annotation's strings: a field value, a link's URI, a note's contents.
    static const char* kAnnotKeys[] = {"V", "DV", "Contents", "T", "TU", "RC", "URI", "A"};
    for (int i = 0; i < FPDF_GetPageCount(doc); i++) {
        FPDF_PAGE page = FPDF_LoadPage(doc, i);
        if (page == nullptr) continue;
        for (int a = 0; a < FPDFPage_GetAnnotCount(page); a++) {
            FPDF_ANNOTATION annot = FPDFPage_GetAnnot(page, a);
            if (annot == nullptr) continue;
            for (const char* key : kAnnotKeys) {
                const unsigned long bytes = FPDFAnnot_GetStringValue(annot, key, nullptr, 0);
                if (bytes <= 2) continue;
                std::vector<unsigned short> buf(bytes / 2 + 1, 0);
                FPDFAnnot_GetStringValue(annot, key, buf.data(), bytes);
                if (Utf16ToUtf8(buf).find(canary) != std::string::npos) {
                    out->push_back(Finding{"annotation /" + std::string(key) + " page " + std::to_string(i),
                                           "the canary is still in an annotation"});
                }
            }
            FPDFPage_CloseAnnot(annot);
        }
        FPDF_ClosePage(page);
    }
}

// 3. The raw file, in each of the forms a PDF can carry a string in.
inline void CheckRawBytes(const std::vector<unsigned char>& file, const std::string& canary,
                          std::vector<Finding>* out) {
    for (const auto& form : Forms(canary)) {
        if (Contains(file, form.second.data(), form.second.size())) {
            out->push_back(Finding{"raw " + form.first, "the canary is in the file's bytes"});
        }
    }
}

// 2. Every stream, decompressed. `qpdf --qdf --decode-level=all` writes the whole document
// with each stream's filters applied, so a canary hidden in a Flate content stream, an
// image, a font or an object stream shows up as plain bytes. Returns false when qpdf is not
// on the machine, which the caller reports as a skipped check rather than a pass.
inline bool CheckDecodedStreams(const std::string& pdf_path, const std::string& scratch_path,
                                const std::string& canary, std::vector<Finding>* out) {
    const std::string cmd = "qpdf --qdf --object-streams=disable --decode-level=all '" + pdf_path + "' '" +
                            scratch_path + "' >/dev/null 2>&1";
    // qpdf exits 0 for a clean file and 3 for one with warnings, and writes the output
    // either way. Rather than decode an exit status portably, the result is judged by what
    // was written: nothing means qpdf is not on this machine, or would not read the file.
    std::system(cmd.c_str());
    const std::vector<unsigned char> decoded = ReadFile(scratch_path);
    if (decoded.empty()) {
        std::remove(scratch_path.c_str());
        return false;
    }
    for (const auto& form : Forms(canary)) {
        if (Contains(decoded, form.second.data(), form.second.size())) {
            out->push_back(Finding{"qpdf-decoded " + form.first, "the canary is in a decompressed stream"});
        }
    }
    std::remove(scratch_path.c_str());
    return true;
}

// 5. The pixels inside each area are the redaction colour, and the page outside them is
// unchanged. `original` may be NULL to check only the inside.
// How far the redaction box's antialiased edge reaches, in points.
constexpr double kEdgePt = 2.0;

// The #118 guard's page budget: a change under this share of a page's pixels is accepted.
constexpr double kRenderBudget = 0.0005;

struct PixelResult {
    long long inside = 0;
    long long inside_wrong = 0;
    long long outside = 0;
    long long outside_changed = 0;
    // Where the changed pixels outside the areas are, in crop space: a box tells a drifted
    // glyph from an edge the whole way round, without anything about the document leaving
    // the machine.
    double changed_left = 0, changed_bottom = 0, changed_right = 0, changed_top = 0;
    int changed_page = -1;          // the page with the worst fraction of changed pixels
    int pages_with_areas = 0;       // pages the redaction marked
    // The worst page's share of changed pixels outside the areas. The core's #118 guard
    // accepts a change under 0.05% of a page — anti-aliasing at rewritten coordinates, and
    // the colour a codec shifts by when an image is re-encoded (tools/pdfium/README.md
    // "Known limits"). This is judged by the same budget, per page, so the checker and the
    // guard cannot disagree about what "unchanged outside" means.
    double worst_page_fraction = 0.0;
};

inline bool RenderPage(FPDF_PAGE page, int dpi, std::vector<unsigned char>* out, int* w, int* h) {
    const double scale = dpi / 72.0;
    *w = static_cast<int>(FPDF_GetPageWidth(page) * scale + 0.5);
    *h = static_cast<int>(FPDF_GetPageHeight(page) * scale + 0.5);
    if (*w <= 0 || *h <= 0 || static_cast<long long>(*w) * *h > 40000000LL) return false;
    FPDF_BITMAP bmp = FPDFBitmap_Create(*w, *h, 0);
    if (bmp == nullptr) return false;
    FPDFBitmap_FillRect(bmp, 0, 0, *w, *h, 0xFFFFFFFF);
    // Annotations are drawn, as a reader draws them: a redaction that removes a widget or
    // a link has to leave the page looking right with them on. MEGAPDF_LEAKCHECK_NO_ANNOT
    // turns them off, which is how a difference is told from an annotation that went.
    static const bool annots = std::getenv("MEGAPDF_LEAKCHECK_NO_ANNOT") == nullptr;
    FPDF_RenderPageBitmap(bmp, page, 0, 0, *w, *h, 0, annots ? FPDF_ANNOT : 0);
    const auto* px = static_cast<const unsigned char*>(FPDFBitmap_GetBuffer(bmp));
    const int stride = FPDFBitmap_GetStride(bmp);
    out->assign(static_cast<size_t>(*w) * static_cast<size_t>(*h) * 4, 0);
    for (int y = 0; y < *h; y++) {
        std::memcpy(out->data() + static_cast<size_t>(y) * *w * 4, px + static_cast<size_t>(y) * stride,
                    static_cast<size_t>(*w) * 4);
    }
    FPDFBitmap_Destroy(bmp);
    return true;
}

inline bool CheckPixels(FPDF_DOCUMENT redacted, FPDF_DOCUMENT original, const std::vector<Area>& areas,
                        unsigned int colour, int dpi, PixelResult* result, std::vector<Finding>* out) {
    for (int i = 0; i < FPDF_GetPageCount(redacted); i++) {
        std::vector<Area> page_areas;
        for (const Area& a : areas) if (a.page_index == i) page_areas.push_back(a);
        if (!page_areas.empty()) result->pages_with_areas++;

        FPDF_PAGE page = FPDF_LoadPage(redacted, i);
        if (page == nullptr) continue;
        std::vector<unsigned char> now;
        int w = 0, h = 0;
        const bool rendered = RenderPage(page, dpi, &now, &w, &h);
        FPDF_ClosePage(page);
        if (!rendered) continue;

        std::vector<unsigned char> was;
        int ow = 0, oh = 0;
        bool have_original = false;
        if (original != nullptr && i < FPDF_GetPageCount(original)) {
            FPDF_PAGE before = FPDF_LoadPage(original, i);
            if (before != nullptr) {
                have_original = RenderPage(before, dpi, &was, &ow, &oh) && ow == w && oh == h;
                FPDF_ClosePage(before);
            }
        }
        long long page_outside = 0, page_changed = 0;
        const double scale = dpi / 72.0;
        const unsigned char cr = static_cast<unsigned char>((colour >> 16) & 0xFF);
        const unsigned char cg = static_cast<unsigned char>((colour >> 8) & 0xFF);
        const unsigned char cb = static_cast<unsigned char>(colour & 0xFF);
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                // Device to crop space: y is flipped.
                const double px = (x + 0.5) / scale;
                const double py = (h - y - 0.5) / scale;
                // The box's own edge is antialiased, so a band of kEdgePt points either side
                // of every boundary counts as neither inside nor outside: a pixel there is
                // the edge being drawn, not content leaking or content lost.
                bool inside = false, near_edge = false;
                for (const Area& a : page_areas) {
                    if (px > a.left + kEdgePt && px < a.right - kEdgePt && py > a.bottom + kEdgePt &&
                        py < a.top - kEdgePt) {
                        inside = true;
                    } else if (px > a.left - kEdgePt && px < a.right + kEdgePt && py > a.bottom - kEdgePt &&
                               py < a.top + kEdgePt) {
                        near_edge = true;
                    } else if (a.has_affected() && px > a.affected_left - kEdgePt && px < a.affected_right + kEdgePt &&
                               py > a.affected_bottom - kEdgePt && py < a.affected_top + kEdgePt) {
                        near_edge = true;   // where a straddling glyph took its own outside part with it
                    }
                }
                if (near_edge && !inside) continue;
                const size_t k = (static_cast<size_t>(y) * w + x) * 4;
                if (inside) {
                    result->inside++;
                    // BGRA.
                    if (std::abs(now[k] - cb) + std::abs(now[k + 1] - cg) + std::abs(now[k + 2] - cr) > 24) {
                        result->inside_wrong++;
                    }
                } else if (have_original) {
                    result->outside++;
                    page_outside++;
                    if (std::abs(now[k] - was[k]) + std::abs(now[k + 1] - was[k + 1]) +
                        std::abs(now[k + 2] - was[k + 2]) > 60) {
                        if (result->outside_changed == 0) {
                            result->changed_left = result->changed_right = px;
                            result->changed_bottom = result->changed_top = py;
                        } else {
                            result->changed_left = (std::min)(result->changed_left, px);
                            result->changed_right = (std::max)(result->changed_right, px);
                            result->changed_bottom = (std::min)(result->changed_bottom, py);
                            result->changed_top = (std::max)(result->changed_top, py);
                        }
                        result->outside_changed++;
                        page_changed++;
                    }
                }
            }
        }
        if (page_outside > 0) {
            const double fraction = static_cast<double>(page_changed) / static_cast<double>(page_outside);
            if (fraction > result->worst_page_fraction) {
                result->worst_page_fraction = fraction;
                result->changed_page = i;
            }
        }
    }
    if (result->inside_wrong > 0) {
        out->push_back(Finding{"pixels inside the area",
                               std::to_string(result->inside_wrong) + " of " + std::to_string(result->inside) +
                                   " are not the redaction colour"});
    }
    // Over the guard's budget on some page: a change the redaction is not entitled to.
    if (result->worst_page_fraction > kRenderBudget) {
        out->push_back(Finding{"pixels outside the area",
                               std::to_string(result->outside_changed) + " of " + std::to_string(result->outside) +
                                   " changed; worst page " + std::to_string(result->changed_page) + " at " +
                                   std::to_string(result->worst_page_fraction * 100.0) + "%"});
    }
    return true;
}

// Everything at once, on a saved file. `original_path` may be empty to skip the render
// compare. `qpdf_ran` comes back false when qpdf is not on the machine.
inline std::vector<Finding> Check(const std::string& redacted_path, const std::string& original_path,
                                  const std::string& canary, const std::vector<Area>& areas, unsigned int colour,
                                  bool* qpdf_ran, PixelResult* pixels = nullptr) {
    std::vector<Finding> findings;
    const std::vector<unsigned char> file = ReadFile(redacted_path);
    if (file.empty()) {
        findings.push_back(Finding{"file", "the redacted file could not be read"});
        return findings;
    }
    CheckRawBytes(file, canary, &findings);
    if (qpdf_ran != nullptr) {
        *qpdf_ran = CheckDecodedStreams(redacted_path, redacted_path + ".qdf", canary, &findings);
    }
    FPDF_DOCUMENT doc = FPDF_LoadDocument(redacted_path.c_str(), nullptr);
    if (doc == nullptr) {
        findings.push_back(Finding{"file", "the redacted file does not open"});
        return findings;
    }
    CheckExtractedText(doc, canary, &findings);
    CheckMetadataOutlineAnnots(doc, canary, &findings);
    FPDF_DOCUMENT before = original_path.empty() ? nullptr : FPDF_LoadDocument(original_path.c_str(), nullptr);
    PixelResult local;
    CheckPixels(doc, before, areas, colour, 72, pixels != nullptr ? pixels : &local, &findings);
    if (before != nullptr) FPDF_CloseDocument(before);
    FPDF_CloseDocument(doc);
    return findings;
}

}  // namespace leakcheck

#endif  // MEGAPDF_LEAKCHECK_H
