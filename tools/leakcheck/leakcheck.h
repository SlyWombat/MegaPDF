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
//
// (std::min) and (std::max) in parentheses throughout: core/tests/core_tests.cpp includes
// this after PDFium's headers, which pull in <windef.h> on Windows, and that defines min
// and max as macros. The core does the same for the same reason.
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
#include "fpdf_transformpage.h"
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

    // Every annotation's strings: a field value, a note's contents, a tooltip.
    static const char* kAnnotKeys[] = {"V", "DV", "Contents", "T", "TU", "RC"};
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
            // A link's address is in its ACTION, not on the annotation, which is why
            // reading /URI off the annotation found nothing: FPDFAction_GetURIPath is where
            // it lives, and a redacted word can certainly survive in a link address.
            if (FPDFAnnot_GetSubtype(annot) == FPDF_ANNOT_LINK) {
                FPDF_LINK link = FPDFAnnot_GetLink(annot);
                FPDF_ACTION action = link != nullptr ? FPDFLink_GetAction(link) : nullptr;
                if (action != nullptr) {
                    const unsigned long bytes = FPDFAction_GetURIPath(doc, action, nullptr, 0);
                    if (bytes > 1) {
                        std::vector<char> uri(bytes + 1, 0);
                        FPDFAction_GetURIPath(doc, action, uri.data(), bytes);
                        if (std::string(uri.data()).find(canary) != std::string::npos) {
                            out->push_back(Finding{"link URI page " + std::to_string(i),
                                                   "the canary is still in a link's address"});
                        }
                    }
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
// qpdf annotates a --qdf file with comments of its own: the page a section belongs to, and
// the object numbers the file had before it was rewritten. Those are qpdf's words about the
// document, not the document's, and searching them reported a redaction that covered the
// words "Page 2" as a leak. Drop them before searching, matching only the forms qpdf emits
// and only at the start of a line, so decoded stream bytes that happen to contain "%%"
// survive untouched.
inline std::vector<unsigned char> WithoutQpdfComments(const std::vector<unsigned char>& qdf) {
    static const char* const kComments[] = {"%% Original object ID:", "%% Contents for page ", "%% Page ", "%QDF-"};
    std::vector<unsigned char> out;
    out.reserve(qdf.size());
    size_t i = 0;
    while (i < qdf.size()) {
        size_t eol = i;
        while (eol < qdf.size() && qdf[eol] != '\n') eol++;
        bool is_comment = false;
        for (const char* prefix : kComments) {
            const size_t n = std::strlen(prefix);
            if (eol - i >= n && std::memcmp(&qdf[i], prefix, n) == 0) {
                is_comment = true;
                break;
            }
        }
        if (!is_comment) out.insert(out.end(), qdf.begin() + static_cast<long>(i),
                                    qdf.begin() + static_cast<long>((std::min)(eol + 1, qdf.size())));
        i = eol + 1;
    }
    return out;
}

inline bool CheckDecodedStreams(const std::string& pdf_path, const std::string& scratch_path,
                                const std::string& canary, std::vector<Finding>* out) {
    const std::string cmd = "qpdf --qdf --object-streams=disable --decode-level=all '" + pdf_path + "' '" +
                            scratch_path + "' >/dev/null 2>&1";
    // qpdf exits 0 for a clean file and 3 for one with warnings, and writes the output
    // either way. Rather than decode an exit status portably, the result is judged by what
    // was written: nothing means qpdf is not on this machine, or would not read the file.
    std::system(cmd.c_str());
    const std::vector<unsigned char> written = ReadFile(scratch_path);
    if (written.empty()) {
        std::remove(scratch_path.c_str());
        return false;
    }
    const std::vector<unsigned char> decoded = WithoutQpdfComments(written);
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
    // Where the changed pixels outside the areas are, in the render's own device pixels: a
    // box tells a drifted glyph from an edge the whole way round, without anything about the
    // document leaving the machine.
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
        const unsigned char cr = static_cast<unsigned char>((colour >> 16) & 0xFF);
        const unsigned char cg = static_cast<unsigned char>((colour >> 8) & 0xFF);
        const unsigned char cb = static_cast<unsigned char>(colour & 0xFF);

        // The areas are projected INTO device space, once per page, rather than every pixel
        // being projected out of it. FPDF_PageToDevice does what PDFium's own render does —
        // /Rotate included, which a page renders by and a page's content does not live in.
        // Mapping pixels back by hand treated a rotated page's 792 x 612 render as though
        // it were its 612 x 792 content, and compared the wrong regions entirely.
        FPDF_PAGE mapping = FPDF_LoadPage(redacted, i);
        // An area arrives in crop space — user space less the CropBox origin, times the
        // page's /UserUnit (#150) — because that is the space the core's API speaks.
        // FPDF_PageToDevice speaks page space. Most pages make the two the same and hid the
        // difference; a page whose CropBox is [0 382.1 612.1 1224.1] does not, and its mask
        // landed 382 points down the page, so the redaction's own box counted as a change
        // outside itself. Convert exactly as the core's InX/InY do.
        double unit = 1.0;
        double crop_x = 0.0, crop_y = 0.0;
        if (mapping != nullptr) {
            unit = FPDFPage_GetUserUnit(mapping);
            if (!(unit > 0.0)) unit = 1.0;
            float cl = 0, cb = 0, cr2 = 0, ct = 0;
            if (FPDFPage_GetCropBox(mapping, &cl, &cb, &cr2, &ct) && cr2 > cl && ct > cb) {
                crop_x = cl;
                crop_y = cb;
            }
        }
        struct DeviceBox { int left, top, right, bottom; bool valid; };
        auto to_device = [&](double l, double b, double r, double t, double pad) -> DeviceBox {
            DeviceBox box{w, h, -1, -1, false};
            if (mapping == nullptr) return box;
            // The padding is in points, and crop space is points; the division takes it into
            // user space units along with the coordinates, as the core's PaintBox does.
            // Shrinking (a negative pad) past the middle would invert the box, and the
            // min/max below would hide that by putting it back the right way round and
            // wider than the area. Such an area is all edge, and has no inside at all.
            if (pad < 0.0 && (r + pad <= l - pad || t + pad <= b - pad)) return box;
            const double xs[2] = {(l - pad) / unit + crop_x, (r + pad) / unit + crop_x};
            const double ys[2] = {(b - pad) / unit + crop_y, (t + pad) / unit + crop_y};
            for (double x : xs) {
                for (double y : ys) {
                    int dx = 0, dy = 0;
                    if (!FPDF_PageToDevice(mapping, 0, 0, w, h, 0, x, y, &dx, &dy)) return box;
                    box.left = (std::min)(box.left, dx);
                    box.right = (std::max)(box.right, dx);
                    box.top = (std::min)(box.top, dy);
                    box.bottom = (std::max)(box.bottom, dy);
                    box.valid = true;
                }
            }
            return box;
        };
        std::vector<DeviceBox> inside_boxes, edge_boxes;
        for (const Area& a : page_areas) {
            inside_boxes.push_back(to_device(a.left, a.bottom, a.right, a.top, -kEdgePt));
            edge_boxes.push_back(to_device(a.left, a.bottom, a.right, a.top, kEdgePt));
            if (a.has_affected()) {
                edge_boxes.push_back(to_device(a.affected_left, a.affected_bottom, a.affected_right,
                                               a.affected_top, kEdgePt));
            }
        }
        if (mapping != nullptr) FPDF_ClosePage(mapping);

        auto covers = [](const DeviceBox& box, int x, int y) {
            return box.valid && x >= box.left && x <= box.right && y >= box.top && y <= box.bottom;
        };
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                // The box's own edge is antialiased, so a band of kEdgePt points either side
                // of every boundary counts as neither inside nor outside: a pixel there is
                // the edge being drawn, not content leaking or content lost. The same band
                // covers where a straddling glyph took its own outside part with it.
                bool inside = false, near_edge = false;
                for (const DeviceBox& box : inside_boxes) {
                    if (covers(box, x, y)) inside = true;
                }
                if (!inside) {
                    for (const DeviceBox& box : edge_boxes) {
                        if (covers(box, x, y)) near_edge = true;
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
                            result->changed_left = result->changed_right = x;
                            result->changed_bottom = result->changed_top = y;
                        } else {
                            result->changed_left = (std::min)(result->changed_left, static_cast<double>(x));
                            result->changed_right = (std::max)(result->changed_right, static_cast<double>(x));
                            result->changed_bottom = (std::min)(result->changed_bottom, static_cast<double>(y));
                            result->changed_top = (std::max)(result->changed_top, static_cast<double>(y));
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
