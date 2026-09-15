/* #152: writes the lossless (SOF3) grayscale JPEG that tools/gen_scaled_jpeg_fixture.py puts on the
 * third page of tests/MegaPDF.Core.Tests/Fixtures/scaled-jpeg.pdf: mid gray with a dark bar and a dark
 * frame. Pillow cannot write lossless JPEG; libjpeg-turbo 3 can (jpeg_enable_lossless).
 *
 * Built against the libjpeg-turbo of a local PDFium checkout (Chromium mangles its symbols, and its
 * objects are ThinLTO bitcode, so use that checkout's clang):
 *
 *   P=~/pdfium-build/pdfium
 *   find "$P/out/obj/third_party/libjpeg_turbo" -name "*.o" > objs.txt
 *   "$P/third_party/llvm-build/Release+Asserts/bin/clang" -O1 -fuse-ld=lld -flto=thin -DMANGLE_JPEG_NAMES \
 *       -I "$P/third_party/libjpeg_turbo/src" tools/gen_lossless_jpeg.c -o gen_lossless_jpeg @objs.txt -lm
 *   ./gen_lossless_jpeg lossless.jpg 661 855
 *
 * Any libjpeg-turbo 3 build works the same way without the mangling define.
 */
#include <stdio.h>
#include <stdlib.h>
#include "jpeglib.h"

int main(int argc, char** argv) {
    if (argc < 4) {
        fprintf(stderr, "usage: gen_lossless_jpeg <out.jpg> <width> <height>\n");
        return 2;
    }
    const int w = atoi(argv[2]), h = atoi(argv[3]);
    FILE* f = fopen(argv[1], "wb");
    if (!f) return 1;
    struct jpeg_compress_struct c;
    struct jpeg_error_mgr err;
    c.err = jpeg_std_error(&err);
    jpeg_create_compress(&c);
    jpeg_stdio_dest(&c, f);
    c.image_width = (JDIMENSION)w;
    c.image_height = (JDIMENSION)h;
    c.input_components = 1;
    c.in_color_space = JCS_GRAYSCALE;
    jpeg_set_defaults(&c);
    jpeg_enable_lossless(&c, 1, 0);
    jpeg_start_compress(&c, TRUE);
    JSAMPLE* row = (JSAMPLE*)malloc((size_t)w);
    for (int y = 0; y < h; y++) {
        for (int x = 0; x < w; x++) {
            int v = 128;
            if (y > h / 3 && y < h / 2) v = 20;
            if (x < w / 20 || x >= w - w / 20 || y < h / 20 || y >= h - h / 20) v = 40;
            row[x] = (JSAMPLE)v;
        }
        JSAMPROW rows[1] = {row};
        jpeg_write_scanlines(&c, rows, 1);
    }
    jpeg_finish_compress(&c);
    jpeg_destroy_compress(&c);
    free(row);
    fclose(f);
    return 0;
}
