package com.megapdf.android

import com.megapdf.engine.PdfEngine
import kotlinx.coroutines.runBlocking
import java.io.File

/**
 * The fixtures the page tools are driven against (#174), built here rather than checked in.
 *
 * Two of them, and each is built by hand because what it has to prove is in its *structure*:
 *
 *  * [multiPage] gives every page a width of its own, so the order of the pages is readable
 *    straight off the view model's `pageSizes`. A test for reordering has to say which page went
 *    where, and page numbers drawn on the page cannot be read back without OCR.
 *  * [parentFields] is a form whose two widgets take their names from a /Parent field, which is
 *    the shape a page copy could not carry before PDFium patch 0033 and the one that is still
 *    refused when its top-level name would clash (MEGAPDF_ERR_FIELDS). It is the same fixture
 *    `core/tests/core_tests.cpp` (`parent_fields_pdf`) and `ios/MegaPDFTests/
 *    PagesFieldHierarchyTests.swift` build, written out the same way so the three agree.
 *
 * Both are written with a hand-built cross-reference table, in Latin-1, so every byte offset in
 * the file is the offset of the character that produced it.
 */
object TestPdfs {

    /**
     * A [count]-page PDF whose page *n* is (600 + 10n) × 792 points, with its number drawn on it.
     * Page 0 is 600 wide, page 1 is 610 wide, and so on — so `pageSizes.map { it.widthPoints }`
     * spells out the page order.
     */
    fun multiPage(count: Int): ByteArray {
        val objects = ArrayList<String>()
        // Object numbers are needed before the bodies can name each other: 1 catalog, 2 page tree,
        // 3 font, then a page and a content stream for each page.
        val firstPage = 4
        fun pageObject(index: Int) = firstPage + index * 2
        fun contentObject(index: Int) = firstPage + index * 2 + 1

        val kids = (0 until count).joinToString(" ") { "${pageObject(it)} 0 R" }
        objects += "<< /Type /Catalog /Pages 2 0 R >>"
        objects += "<< /Type /Pages /Kids [$kids] /Count $count >>"
        objects += "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        for (index in 0 until count) {
            val width = 600 + index * 10
            objects += "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 $width 792] " +
                "/Resources << /Font << /F1 3 0 R >> >> /Contents ${contentObject(index)} 0 R >>"
            val body = "BT /F1 36 Tf 72 700 Td (Page ${index + 1}) Tj ET"
            objects += "<< /Length ${body.length} >>\nstream\n$body\nendstream"
        }
        return assemble(objects)
    }

    /**
     * [count] pages, each with its content stream padded to roughly [contentBytes], written
     * straight to [file] (#145): reading a page's size (`megapdf_page_width`/`height`, what
     * opening a document does for every page) only reads its `/MediaBox`, so it costs the same
     * whatever this is — but *copying* a page, which is what extract and combine do, has to
     * carry its content with it, so this is what makes one engine call over a modest page count
     * take a real amount of wall time without making opening the document anywhere near as
     * slow. `multiPage`'s own content is a few bytes; a test timing a single whole-document
     * engine call against it would be timing something that finishes before the busy
     * indicator's 0.5 s show threshold however many pages there are.
     *
     * This is the fixture #145's own [PageToolsProgressTest.
     * extractingALotOfPagesCanBeStoppedAndLeavesNothingBehind] needed, and it took two wrong
     * shapes first: a content-light fixture at any page count never crosses that threshold at
     * all, and a first version of this one built the whole padded document as a single Kotlin
     * `String`/`ByteArray` in memory (`assemble`'s own approach, fine for every other fixture
     * here because they are tiny) — tens of megabytes of it, several times over by the time a
     * `StringBuilder` grows and copies and a final `.toByteArray()` copies again, which is more
     * than the instrumentation process's heap allows. Writing straight to [file] through a
     * buffered stream keeps memory use down to the buffer, whatever the file's final size.
     *
     * **The padding has to be incompressible, and the third wrong shape was `q Q` repeated**
     * (#611). A no-op pair over and over is the most compressible thing a content stream can
     * hold: `megapdf_pages_extract` deflates what it copies, so 800 pages of 100 KB of `q Q`
     * — an 80 MB document, by every measure the fixture was sized on — came out the other side
     * as a 332 KB file, and the extract that was supposed to take "a real amount of wall time"
     * was measured on the CI emulator at **373 ms**: *under* the busy indicator's own 0.5 s show
     * threshold, so most runs had no Stop to press and the test timed out. The reasoning above
     * was right that copying a page carries its content; what it missed is that the copy carries
     * the *compressed* content, and compressing a no-op pair costs almost nothing.
     *
     * So the padding is random bytes in a single literal string instead — a bare operand with no
     * operator, which parses as one token, draws nothing, and deflates to its own size. Measured
     * on the same emulator in the same round: the same 800 x 100 KB shape now extracts in
     * **1,893 ms** to an 80 MB file, which is 3.8x the threshold the test depends on. Extract
     * throughput there is ~42 MB/s of *output* and barely varies with page count (150 x 100 KB:
     * 367 ms; 2,000 x 8 KB: 269 ms), so bytes are the only dial: a cheaper fixture is a faster
     * extract, and this size is what buys a margin rather than a coin toss.
     *
     * The seed is fixed, so two runs build byte-identical fixtures and a failure can be repeated.
     */
    fun multiPageBulky(file: File, count: Int, contentBytes: Int) {
        val firstPage = 4
        fun pageObject(index: Int) = firstPage + index * 2
        fun contentObject(index: Int) = firstPage + index * 2 + 1
        val objectCount = 3 + count * 2

        java.io.BufferedOutputStream(java.io.FileOutputStream(file)).use { out ->
            var pos = 0L
            fun write(s: String) {
                val bytes = s.toByteArray(Charsets.ISO_8859_1)
                out.write(bytes)
                pos += bytes.size
            }
            val offsets = ArrayList<Long>(objectCount)
            fun obj(n: Int, body: String) {
                offsets += pos
                write("$n 0 obj\n$body\nendobj\n")
            }

            write("%PDF-1.4\n")
            val kids = (0 until count).joinToString(" ") { "${pageObject(it)} 0 R" }
            obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            obj(2, "<< /Type /Pages /Kids [$kids] /Count $count >>")
            obj(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")

            // Built once and written per page: one array of this size is nothing beside the
            // file, and rebuilding it 800 times is what the heap could not take.
            val padding = incompressiblePadding(contentBytes)
            for (index in 0 until count) {
                obj(
                    pageObject(index),
                    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                        "/Resources << /Font << /F1 3 0 R >> >> /Contents ${contentObject(index)} 0 R >>",
                )
                val header = "BT /F1 36 Tf 72 700 Td (Page ${index + 1}) Tj ET\n"
                offsets += pos
                write("${contentObject(index)} 0 obj\n<< /Length ${header.length + padding.size} >>\nstream\n$header")
                out.write(padding)
                pos += padding.size
                write("\nendstream\nendobj\n")
            }

            val xref = pos
            write("xref\n0 ${objectCount + 1}\n0000000000 65535 f \n")
            for (offset in offsets) write(offset.toString().padStart(10, '0') + " 00000 n \n")
            write("trailer\n<< /Size ${objectCount + 1} /Root 1 0 R >>\nstartxref\n$xref\n%%EOF\n")
        }
    }

    /**
     * [bytes] of content-stream padding a deflate stream cannot shrink (#611): random bytes
     * wrapped in one PDF literal string, so the whole lot is a single token.
     *
     * A bare operand with no operator after it is pushed and dropped, so this is syntactically a
     * complete content stream that draws nothing — the same property `q Q` had, without being
     * compressible. `(`, `)` and `\` are the three bytes a literal string must escape; escaping
     * every `\` also means no byte after one can be read as an octal escape, so the string ends
     * exactly where it is meant to and the stream's `/Length` is the array's own size.
     */
    private fun incompressiblePadding(bytes: Int): ByteArray {
        val random = java.util.Random(611)
        val out = java.io.ByteArrayOutputStream(bytes + 8)
        out.write('('.code)
        val source = ByteArray(4096)
        var written = 0
        while (written < bytes) {
            random.nextBytes(source)
            for (byte in source) {
                if (written >= bytes) break
                val value = byte.toInt() and 0xFF
                if (value == '('.code || value == ')'.code || value == '\\'.code) {
                    out.write('\\'.code)
                    written++
                }
                out.write(value)
                written++
            }
        }
        out.write(')'.code)
        out.write('\n'.code)
        return out.toByteArray()
    }

    /**
     * A one-page form whose two text widgets ("first", "last") are kids of a parent field
     * ("person"): the top-level name lives on the parent dictionary, not on the widgets. Importing
     * it into a document that already has a field called "person" is refused whole with
     * MEGAPDF_ERR_FIELDS — the rename that gets a flat field out of the way cannot reach a name
     * that is not on the widget.
     */
    fun parentFields(): ByteArray {
        val objects = ArrayList<String>()
        objects += "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] " +
            "/DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 4 0 R >> >> >> >>"
        objects += "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"
        objects += "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [7 0 R 8 0 R] >>"
        objects += "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
        objects += stream("", "BT /F1 14 Tf 72 720 Td (Two fields under one parent) Tj ET")
        objects += "<< /FT /Tx /T (person) /Kids [7 0 R 8 0 R] >>"
        objects += "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /T (first) /V (Ada) " +
            "/DA (/Helv 12 Tf 0 g) /Rect [100 600 300 620] /F 4 /P 3 0 R /AP << /N 9 0 R >> >>"
        objects += "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /T (last) /V (Lovelace) " +
            "/DA (/Helv 12 Tf 0 g) /Rect [100 560 300 580] /F 4 /P 3 0 R /AP << /N 10 0 R >> >>"
        objects += stream(
            "/Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 4 0 R >> >>",
            "0.13 G 1 w 0.5 0.5 199 19 re S BT /Helv 12 Tf 0 g 2 5 Td (Ada) Tj ET",
        )
        objects += stream(
            "/Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 4 0 R >> >>",
            "0.13 G 1 w 0.5 0.5 199 19 re S BT /Helv 12 Tf 0 g 2 5 Td (Lovelace) Tj ET",
        )
        return assemble(objects)
    }

    private fun stream(dict: String, body: String): String =
        "<< $dict /Length ${body.length} >>\nstream\n$body\nendstream"

    /** The objects, an xref table over their real offsets, and a trailer. */
    private fun assemble(objects: List<String>): ByteArray {
        val out = StringBuilder("%PDF-1.4\n")
        val offsets = ArrayList<Int>(objects.size)
        objects.forEachIndexed { index, body ->
            offsets += out.length
            out.append("${index + 1} 0 obj\n").append(body).append("\nendobj\n")
        }
        val xref = out.length
        out.append("xref\n0 ${objects.size + 1}\n0000000000 65535 f \n")
        for (offset in offsets) out.append(offset.toString().padStart(10, '0')).append(" 00000 n \n")
        out.append("trailer\n<< /Size ${objects.size + 1} /Root 1 0 R >>\nstartxref\n$xref\n%%EOF\n")
        return out.toString().toByteArray(Charsets.ISO_8859_1)
    }

    /**
     * [source] again, encrypted with an owner password and no permissions at all (#558) — a
     * document whose author asked that nothing be done to it.
     *
     * Written by the engine rather than checked in, for the reason the fixtures above are: the page
     * tools need several pages of known sizes, which `tests/.../security/owner-only.pdf` (the one
     * committed owner-restricted fixture, and the one the engine module's tests use) is not. There
     * is no user password, so the viewer opens it like any other document and the permission bits
     * are the only thing standing between the person and the work.
     */
    fun restricted(source: ByteArray, ownerPassword: String = "o-558"): ByteArray = runBlocking {
        val engine = PdfEngine()
        val doc = engine.open(source)
        try {
            val out = java.io.ByteArrayOutputStream()
            doc.saveWithSecurity(out, userPassword = "", ownerPassword = ownerPassword, permissions = 0)
            out.toByteArray()
        } finally {
            doc.close()
        }
    }

    /** Every page's width in points, read by an engine of the test's own. */
    fun pageWidths(file: File): List<Double> = runBlocking {
        val engine = PdfEngine()
        val doc = engine.open(file.readBytes())
        try {
            (0 until doc.pageCount()).map { index ->
                val page = doc.openPage(index)
                try {
                    page.widthPoints
                } finally {
                    page.close()
                }
            }
        } finally {
            doc.close()
        }
    }
}
