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
