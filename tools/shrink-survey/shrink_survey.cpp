// tools/shrink-survey -- the #6 measurement step: what shrink-for-email does to real documents,
// and what the candidate rules (bilevel, grey, transparency) would do instead. It measures; it
// changes no policy and links nothing but the core's public C ABI, PDFium and libjpeg-turbo.
//
//   megapdf_shrink_survey <file.pdf> <doc-id>
//
// JSON lines on stdout and nothing else: one {"type":"image"...} per image, then one
// {"type":"doc"...}. The document is identified by <doc-id> (the driver's path hash), never by
// path or title (#6 design, step 1.2; the corpora are personal).
//
// Why this is not quite the design's tool. The design (#6, 2026-10-21, step 1.1) reads each
// image's outcome from a decision hook inside megapdf_shrink_images_cancellable. Neither exists
// yet: both arrive with #593's core step, and this measurement must not touch the engine that is
// changing. So the baseline decision is computed here from the same published facts the core's
// loop reads (megapdf_images_load's list and the 150 dpi / 1.2 / 100 KB / 8 KB / 0.9 rules in
// megapdf_shrink_images), and megapdf_shrink_images itself is then run on the same document as a
// cross-check: the doc line carries both replaced counts, and the summary reports any disagreement.
// When the hook lands, `real_replaced` can be replaced by the hook's per-image outcome.
//
// Per image, for the ones today's loop would render (top-level, not small): the pixels the loop
// would encode are rendered at the same target size, and encoded as libjpeg-turbo gives them at
// RGB q75 (today), q80, q85, and as greyscale JPEG q75 and q80. Sizes are libjpeg-turbo's. WIC and
// ImageIO differ at the same nominal quality, so a gate read from them is indicative for Windows
// and iOS, not exact (#6 trap 8).

#include <sys/resource.h>
#include <sys/stat.h>

#include <chrono>
#include <cmath>
#include <csetjmp>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <string>
#include <vector>

#include <jpeglib.h>

#include "fpdf_edit.h"
#include "fpdfview.h"
#include "megapdf_core.h"

namespace {

// ---------------------------------------------------------------- JPEG sizes (libjpeg-turbo)

struct JpegError {
    jpeg_error_mgr base;
    jmp_buf jump;
};

void OnJpegError(j_common_ptr cinfo) {
    longjmp(reinterpret_cast<JpegError*>(cinfo->err)->jump, 1);
}

// Encodes `pixels` (4 bytes BGRA per pixel, or 1 byte grey) and returns the JPEG, or an empty
// vector on failure. The caller owns nothing: the vector is a copy of libjpeg's buffer.
std::vector<unsigned char> EncodeJpeg(const unsigned char* pixels, int width, int height, bool grey, int quality) {
    jpeg_compress_struct cinfo;
    JpegError err;
    cinfo.err = jpeg_std_error(&err.base);
    err.base.error_exit = OnJpegError;
    unsigned char* buffer = nullptr;
    unsigned long size = 0;
    std::vector<unsigned char> out;
    if (setjmp(err.jump)) {
        jpeg_destroy_compress(&cinfo);
        std::free(buffer);
        return {};
    }
    jpeg_create_compress(&cinfo);
    jpeg_mem_dest(&cinfo, &buffer, &size);
    cinfo.image_width = static_cast<JDIMENSION>(width);
    cinfo.image_height = static_cast<JDIMENSION>(height);
    cinfo.input_components = grey ? 1 : 4;
    cinfo.in_color_space = grey ? JCS_GRAYSCALE : JCS_EXT_BGRA;
    jpeg_set_defaults(&cinfo);   // 4:2:0 for colour, as the platform encoders default
    jpeg_set_quality(&cinfo, quality, TRUE);
    jpeg_start_compress(&cinfo, TRUE);
    const size_t stride = static_cast<size_t>(width) * (grey ? 1 : 4);
    while (cinfo.next_scanline < cinfo.image_height) {
        JSAMPROW row = const_cast<unsigned char*>(pixels + cinfo.next_scanline * stride);
        jpeg_write_scanlines(&cinfo, &row, 1);
    }
    jpeg_finish_compress(&cinfo);
    out.assign(buffer, buffer + size);
    jpeg_destroy_compress(&cinfo);
    std::free(buffer);
    return out;
}

// The encoder megapdf_shrink_images is given for the cross-check. Same encoder, same quality.
struct ShrinkContext {
    int calls = 0;
};

int CrossCheckEncode(void* context, const unsigned char* bgra, int width, int height, double quality,
                     unsigned char** out_jpeg, size_t* out_length) {
    static_cast<ShrinkContext*>(context)->calls++;
    std::vector<unsigned char> jpeg = EncodeJpeg(bgra, width, height, false, static_cast<int>(std::lround(quality * 100)));
    if (jpeg.empty()) return 0;
    auto* copy = static_cast<unsigned char*>(std::malloc(jpeg.size()));
    if (copy == nullptr) return 0;
    std::memcpy(copy, jpeg.data(), jpeg.size());
    *out_jpeg = copy;
    *out_length = jpeg.size();
    return 1;
}

void CrossCheckRelease(void*, unsigned char* jpeg) { std::free(jpeg); }

int CountingWrite(void* context, const void*, size_t size) {
    *static_cast<unsigned long long*>(context) += size;
    return 1;
}

// ---------------------------------------------------------------- the walk

struct ImageFacts {
    int page = 0;
    int obj = -1;            // top-level page-object index; -1 inside a form
    int ord = -1;            // which image on its page, in drawing order; -1 inside a form (for finding it by hand)
    bool in_form = false;
    std::string filters;     // "A+B", names only
    std::string codec;
    int bpp = 0;
    int colorspace = 0;
    long long px_w = 0, px_h = 0;
    double placed_w = 0, placed_h = 0;   // points; inside a form, in the form's own space
    long long stored = 0;
};

std::string FilterNames(FPDF_PAGEOBJECT obj, std::string* last) {
    std::string joined;
    const int count = FPDFImageObj_GetImageFilterCount(obj);
    for (int k = 0; k < count; k++) {
        char name[48] = {};
        const unsigned long n = FPDFImageObj_GetImageFilter(obj, k, name, sizeof name);
        if (n == 0 || n > sizeof name) { *last = "other"; joined += joined.empty() ? "?" : "+?"; continue; }
        std::string clean;
        for (const char* p = name; *p; p++) if ((*p >= '0' && *p <= '9') || (*p >= 'A' && *p <= 'Z') || (*p >= 'a' && *p <= 'z')) clean += *p;
        if (!joined.empty()) joined += "+";
        joined += clean;
        *last = clean;
    }
    return joined;
}

std::string CodecName(const std::string& lastFilter, bool any) {
    if (!any) return "none";
    if (lastFilter == "DCTDecode" || lastFilter == "DCT") return "DCT";
    if (lastFilter == "JPXDecode") return "JPX";
    if (lastFilter == "JBIG2Decode") return "JBIG2";
    if (lastFilter == "CCITTFaxDecode") return "CCITT";
    if (lastFilter == "FlateDecode" || lastFilter == "Fl") return "Flate";
    return "other";
}

// #696: FPDFImageObj_GetImageMetadata decodes the whole of a JPX or JBIG2 image, so it is never
// called on one. CCITT is 1 bit by definition (ISO 32000-1 7.4.6) and needs no call either.
void ReadFacts(FPDF_PAGE page, FPDF_PAGEOBJECT obj, ImageFacts* f) {
    std::string last;
    f->filters = FilterNames(obj, &last);
    f->codec = CodecName(last, !f->filters.empty());
    if (f->codec == "JBIG2" || f->codec == "CCITT") { f->bpp = 1; return; }
    if (f->codec == "JPX") return;   // unknown, 0
    FPDF_IMAGEOBJ_METADATA meta{};
    if (FPDFImageObj_GetImageMetadata(obj, page, &meta)) {
        f->bpp = static_cast<int>(meta.bits_per_pixel);
        f->colorspace = meta.colorspace;
    }
}

struct Walk {
    std::vector<ImageFacts> forms;     // images found inside form XObjects
    long long visited = 0;
    bool truncated = false;
};

constexpr long long kVisitBudget = 4000000;

void WalkForm(FPDF_PAGE page, int page_index, FPDF_PAGEOBJECT form, int depth, Walk* walk) {
    if (depth > 8) { walk->truncated = true; return; }
    const int count = FPDFFormObj_CountObjects(form);
    for (int i = 0; i < count; i++) {
        if (++walk->visited > kVisitBudget) { walk->truncated = true; return; }
        FPDF_PAGEOBJECT child = FPDFFormObj_GetObject(form, static_cast<unsigned long>(i));
        if (child == nullptr) continue;
        const int type = FPDFPageObj_GetType(child);
        if (type == FPDF_PAGEOBJ_FORM) { WalkForm(page, page_index, child, depth + 1, walk); continue; }
        if (type != FPDF_PAGEOBJ_IMAGE) continue;
        unsigned int pw = 0, ph = 0;
        if (!FPDFImageObj_GetImagePixelSize(child, &pw, &ph)) continue;
        ImageFacts f;
        f.page = page_index;
        f.in_form = true;
        f.px_w = pw;
        f.px_h = ph;
        float l = 0, b = 0, r = 0, t = 0;
        FPDFPageObj_GetBounds(child, &l, &b, &r, &t);
        f.placed_w = r - l;
        f.placed_h = t - b;
        f.stored = static_cast<long long>(FPDFImageObj_GetImageDataRaw(child, nullptr, 0));
        ReadFacts(page, child, &f);
        walk->forms.push_back(f);
    }
}

// ---------------------------------------------------------------- output

void PrintCommon(const ImageFacts& f, const std::string& doc) {
    std::printf("{\"type\":\"image\",\"doc\":\"%s\",\"page\":%d,\"obj\":%d,\"ord\":%d,\"in_form\":%s,\"filters\":\"%s\","
                "\"codec\":\"%s\",\"bpp\":%d,\"cs\":%d,\"px_w\":%lld,\"px_h\":%lld,\"placed_w_pt\":%.1f,"
                "\"placed_h_pt\":%.1f,\"stored_bytes\":%lld",
                doc.c_str(), f.page, f.obj, f.ord, f.in_form ? "true" : "false", f.filters.c_str(), f.codec.c_str(), f.bpp,
                f.colorspace, f.px_w, f.px_h, f.placed_w, f.placed_h, f.stored);
}

}  // namespace

int main(int argc, char** argv) {
    if (argc != 3) {
        std::fprintf(stderr, "usage: megapdf_shrink_survey <file.pdf> <doc-id>\n");
        return 2;
    }
    const char* path = argv[1];
    const std::string doc = argv[2];
    const auto started = std::chrono::steady_clock::now();

    struct stat st {};
    const long long file_bytes = stat(path, &st) == 0 ? static_cast<long long>(st.st_size) : -1;

    auto finish = [&](const char* outcome, int pages, int counts[5], int images, int in_forms, bool truncated, long long resaved,
                      long long shrunk, int real_replaced, int calls, int status, long long ms_shrink = -1) {
        const long long ms = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - started).count();
        rusage usage{};
        getrusage(RUSAGE_SELF, &usage);
        std::printf("{\"type\":\"doc\",\"doc\":\"%s\",\"outcome\":\"%s\",\"pages\":%d,\"file_bytes\":%lld,\"resaved_bytes\":%lld,"
                    "\"shrunk_bytes\":%lld,\"counts\":[%d,%d,%d,%d,%d],\"real_replaced\":%d,\"encoder_calls\":%d,"
                    "\"shrink_status\":%d,\"ms_shrink\":%lld,\"images\":%d,\"images_in_forms\":%d,\"form_walk_truncated\":%s,\"ms\":%lld,"
                    "\"rss_mb\":%ld}\n",
                    doc.c_str(), outcome, pages, file_bytes, resaved, shrunk, counts[0], counts[1], counts[2], counts[3], counts[4],
                    real_replaced, calls, status, ms_shrink, images, in_forms, truncated ? "true" : "false", ms, usage.ru_maxrss / 1024);
        std::fflush(stdout);
    };
    int none[5] = {0, 0, 0, 0, 0};

    megapdf_document* d = megapdf_open_file(path, nullptr);
    if (d == nullptr) {
        finish(megapdf_last_error() == 4 /* FPDF_ERR_PASSWORD */ ? "encrypted" : "open_failed", 0, none, 0, 0, false, -1, -1, 0, 0, 0);
        return 0;
    }

    // What a plain rewrite weighs, for design decision D7: a full rewrite can lose the original's
    // object streams, so smaller images do not by themselves promise a smaller file.
    unsigned long long resaved = 0;
    const int resave_status = megapdf_save(d, CountingWrite, &resaved);

    megapdf_images* list = megapdf_images_load(d);
    if (list == nullptr) {
        finish("images_failed", 0, none, 0, 0, false, resave_status == MEGAPDF_OK ? static_cast<long long>(resaved) : -1, -1, 0, 0, 0);
        megapdf_close(d);
        return 0;
    }
    std::map<long long, megapdf_image_info> listed;   // (page << 32 | object) -> the core's own numbers
    for (size_t i = 0; i < megapdf_image_count(list); i++) {
        megapdf_image_info info{};
        if (megapdf_image_get(list, i, &info) == MEGAPDF_OK)
            listed[(static_cast<long long>(info.page_index) << 32) | static_cast<unsigned>(info.object_index)] = info;
    }

    FPDF_DOCUMENT raw = FPDF_LoadDocument(path, nullptr);   // the core has initialised PDFium by now
    int counts[5] = {0, 0, 0, 0, 0};   // considered, replaced, kept_small, kept_no_saving, failed (render or encode)
    int images = 0, in_forms = 0;
    bool truncated = false;
    int pages = 0;

    if (raw != nullptr) {
        pages = FPDF_GetPageCount(raw);
        for (int pi = 0; pi < pages; pi++) {
            FPDF_PAGE page = FPDF_LoadPage(raw, pi);
            if (page == nullptr) continue;
            Walk walk;
            const int total = FPDFPage_CountObjects(page);
            int ordinal = 0;
            for (int oi = 0; oi < total; oi++) {
                FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, oi);
                if (obj == nullptr) continue;
                const int type = FPDFPageObj_GetType(obj);
                if (type == FPDF_PAGEOBJ_FORM) { WalkForm(page, pi, obj, 1, &walk); continue; }
                if (type != FPDF_PAGEOBJ_IMAGE) continue;
                const int this_ordinal = ordinal++;
                const auto it = listed.find((static_cast<long long>(pi) << 32) | static_cast<unsigned>(oi));
                if (it == listed.end()) continue;
                const megapdf_image_info& info = it->second;

                ImageFacts f;
                f.page = pi;
                f.obj = oi;
                f.ord = this_ordinal;
                f.px_w = info.pixel_width;
                f.px_h = info.pixel_height;
                f.placed_w = info.display_width;
                f.placed_h = info.display_height;
                f.stored = info.stored_bytes;
                ReadFacts(page, obj, &f);
                images++;
                counts[0]++;

                // Today's loop (megapdf_shrink_images), rule for rule.
                int target_w = static_cast<int>(std::nearbyint(info.display_width / 72.0 * 150.0));
                int target_h = static_cast<int>(std::nearbyint(info.display_height / 72.0 * 150.0));
                const bool oversized = info.pixel_width > target_w * 1.2;
                const bool small = (!oversized && info.stored_bytes < 100000) || info.stored_bytes < 8000 ||
                                   info.pixel_width < 8 || info.pixel_height < 8;
                if (small) {
                    counts[2]++;
                    PrintCommon(f, doc);
                    std::printf(",\"base\":\"small\"}\n");
                    continue;
                }
                target_w = target_w < 8 ? 8 : (target_w > info.pixel_width ? info.pixel_width : target_w);
                target_h = target_h < 8 ? 8 : (target_h > info.pixel_height ? info.pixel_height : target_h);

                megapdf_image* pixels = megapdf_render_image(d, pi, oi, target_w, target_h);
                if (pixels == nullptr) {
                    counts[4]++;
                    PrintCommon(f, doc);
                    std::printf(",\"base\":\"failed\",\"tw\":%d,\"th\":%d}\n", target_w, target_h);
                    continue;
                }
                const int w = megapdf_image_width(pixels), h = megapdf_image_height(pixels);
                std::vector<unsigned char> bgra(static_cast<size_t>(w) * h * 4);
                megapdf_image_pixels(pixels, bgra.data(), bgra.size());
                megapdf_image_free(pixels);

                // One pass: grey? transparent (not counting the outermost 1-px ring)? and the luma plane.
                bool is_grey = true;
                long long alpha_inner = 0, alpha_any = 0, alpha_inner2 = 0, alpha_inner4 = 0;
                int alpha_min = 255;
                std::vector<unsigned char> luma(static_cast<size_t>(w) * h);
                for (int y = 0; y < h; y++) {
                    // Distance from the nearest edge, for the ring widths G5 may need (1, 2 and 4 px).
                    const int dy = y < h - 1 - y ? y : h - 1 - y;
                    const unsigned char* row = bgra.data() + static_cast<size_t>(y) * w * 4;
                    for (int x = 0; x < w; x++) {
                        const unsigned char* p = row + static_cast<size_t>(x) * 4;
                        const int B = p[0], G = p[1], R = p[2], A = p[3];
                        if (std::abs(R - G) > 2 || std::abs(G - B) > 2) is_grey = false;
                        if (A < 255) {
                            alpha_any++;
                            if (A < alpha_min) alpha_min = A;
                            const int dx = x < w - 1 - x ? x : w - 1 - x;
                            const int edge = dx < dy ? dx : dy;
                            if (edge >= 1) alpha_inner++;
                            if (edge >= 2) alpha_inner2++;
                            if (edge >= 4) alpha_inner4++;
                        }
                        luma[static_cast<size_t>(y) * w + x] = static_cast<unsigned char>((299 * R + 587 * G + 114 * B) / 1000);
                    }
                }
                const size_t rgb75 = EncodeJpeg(bgra.data(), w, h, false, 75).size();
                const size_t rgb80 = EncodeJpeg(bgra.data(), w, h, false, 80).size();
                const size_t rgb85 = EncodeJpeg(bgra.data(), w, h, false, 85).size();
                const size_t g75 = EncodeJpeg(luma.data(), w, h, true, 75).size();
                const size_t g80 = EncodeJpeg(luma.data(), w, h, true, 80).size();

                const char* base;
                if (rgb75 == 0) { base = "failed"; counts[4]++; }
                else if (static_cast<double>(rgb75) < info.stored_bytes * 0.9) { base = "replaced"; counts[1]++; }
                else { base = "no_saving"; counts[3]++; }

                PrintCommon(f, doc);
                std::printf(",\"base\":\"%s\",\"tw\":%d,\"th\":%d,\"is_grey\":%s,\"alpha_inner\":%lld,\"alpha_inner2\":%lld,\"alpha_inner4\":%lld,\"alpha_any\":%lld,\"alpha_min\":%d,"
                            "\"rgb_q75\":%zu,\"rgb_q80\":%zu,\"rgb_q85\":%zu,\"grey_q75\":%zu,\"grey_q80\":%zu}\n",
                            base, w, h, is_grey ? "true" : "false", alpha_inner, alpha_inner2, alpha_inner4, alpha_any, alpha_min, rgb75, rgb80, rgb85, g75, g80);
            }
            for (const ImageFacts& f : walk.forms) {
                images++;
                in_forms++;
                PrintCommon(f, doc);
                std::printf(",\"base\":\"form\"}\n");
            }
            truncated = truncated || walk.truncated;
            FPDF_ClosePage(page);
        }
        FPDF_CloseDocument(raw);
    }
    megapdf_images_free(list);

    // The cross-check: the engine's own loop, with the same encoder, on the same document.
    ShrinkContext ctx;
    int real_replaced = 0;
    const auto shrink_started = std::chrono::steady_clock::now();
    const int status = megapdf_shrink_images(d, CrossCheckEncode, CrossCheckRelease, &ctx, &real_replaced);
    const long long ms_shrink =
        std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - shrink_started).count();
    unsigned long long shrunk = 0;
    const int save_status = megapdf_save(d, CountingWrite, &shrunk);
    megapdf_close(d);

    finish(raw == nullptr ? "facts_unavailable" : "ok", pages, counts, images, in_forms, truncated,
           resave_status == MEGAPDF_OK ? static_cast<long long>(resaved) : -1,
           save_status == MEGAPDF_OK ? static_cast<long long>(shrunk) : -1, real_replaced, ctx.calls, status, ms_shrink);
    return 0;
}
