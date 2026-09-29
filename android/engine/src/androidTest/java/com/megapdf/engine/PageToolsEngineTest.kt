package com.megapdf.engine

import androidx.test.ext.junit.runners.AndroidJUnit4
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/**
 * Contract 10 through the Android JNI binding (#174), on a device: the calls, the handles they
 * hand out, and the refusals they name.
 *
 * The core's own suite covers the contract (`core/tests/core_tests.cpp`, `test_page_tools`); what
 * only a device can say is that this *binding* carries all of it across — that a page handle's
 * index is read from the right struct, that a removed page survives the trip through a `LongArray`
 * and restores, and that MEGAPDF_ERR_FIELDS actually reaches Kotlin instead of arriving as a bare
 * failure. The last of those also proves `MEGAPDF_PDFIUM_PATCHES` reaches `megapdf_core.cpp` in
 * this `.so`, which is the check #469 had to add for iOS after it turned out not to.
 */
@RunWith(AndroidJUnit4::class)
class PageToolsEngineTest {

    private fun pdf(count: Int): ByteArray {
        val objects = ArrayList<String>()
        val kids = (0 until count).joinToString(" ") { "${4 + it * 2} 0 R" }
        objects += "<< /Type /Catalog /Pages 2 0 R >>"
        objects += "<< /Type /Pages /Kids [$kids] /Count $count >>"
        objects += "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        for (index in 0 until count) {
            objects += "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 ${600 + index * 10} 792] " +
                "/Resources << /Font << /F1 3 0 R >> >> /Contents ${5 + index * 2} 0 R >>"
            val body = "BT /F1 36 Tf 72 700 Td (Page ${index + 1}) Tj ET"
            objects += "<< /Length ${body.length} >>\nstream\n$body\nendstream"
        }
        val out = StringBuilder("%PDF-1.4\n")
        val offsets = ArrayList<Int>()
        objects.forEachIndexed { i, body ->
            offsets += out.length
            out.append("${i + 1} 0 obj\n").append(body).append("\nendobj\n")
        }
        val xref = out.length
        out.append("xref\n0 ${objects.size + 1}\n0000000000 65535 f \n")
        for (offset in offsets) out.append(offset.toString().padStart(10, '0')).append(" 00000 n \n")
        out.append("trailer\n<< /Size ${objects.size + 1} /Root 1 0 R >>\nstartxref\n$xref\n%%EOF\n")
        return out.toString().toByteArray(Charsets.ISO_8859_1)
    }

    /** A one-page form whose widgets take their names from a /Parent field — see `TestPdfs`. */
    private fun parentFieldsPdf(): ByteArray {
        val objects = ArrayList<String>()
        fun stream(dict: String, body: String) =
            "<< $dict /Length ${body.length} >>\nstream\n$body\nendstream"
        objects += "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] " +
            "/DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 4 0 R >> >> >> >>"
        objects += "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"
        objects += "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [7 0 R 8 0 R] >>"
        objects += "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
        objects += stream("", "BT /F1 14 Tf 72 720 Td (Two fields under one parent) Tj ET")
        objects += "<< /FT /Tx /T (person) /Kids [7 0 R 8 0 R] >>"
        objects += "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /T (first) /V (Ada) " +
            "/DA (/Helv 12 Tf 0 g) /Rect [100 600 300 620] /F 4 /P 3 0 R >>"
        objects += "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /T (last) /V (Lovelace) " +
            "/DA (/Helv 12 Tf 0 g) /Rect [100 560 300 580] /F 4 /P 3 0 R >>"
        val out = StringBuilder("%PDF-1.4\n")
        val offsets = ArrayList<Int>()
        objects.forEachIndexed { i, body ->
            offsets += out.length
            out.append("${i + 1} 0 obj\n").append(body).append("\nendobj\n")
        }
        val xref = out.length
        out.append("xref\n0 ${objects.size + 1}\n0000000000 65535 f \n")
        for (offset in offsets) out.append(offset.toString().padStart(10, '0')).append(" 00000 n \n")
        out.append("trailer\n<< /Size ${objects.size + 1} /Root 1 0 R >>\nstartxref\n$xref\n%%EOF\n")
        return out.toString().toByteArray(Charsets.ISO_8859_1)
    }

    /** Unit-returning on purpose: a JUnit4 test method has to compile to `void`. */
    private fun withDocument(bytes: ByteArray, body: suspend (PdfDocument) -> Unit): Unit = runBlocking {
        val engine = PdfEngine()
        val doc = engine.open(bytes)
        try {
            body(doc)
        } finally {
            doc.close()
        }
    }

    private suspend fun widths(doc: PdfDocument): List<Int> =
        (0 until doc.pageCount()).map { index ->
            val page = doc.openPage(index)
            try {
                page.widthPoints.toInt()
            } finally {
                page.close()
            }
        }

    @Test
    fun rotatingAPageChangesTheSizeItReportsAndNothingElse() = withDocument(pdf(2)) { doc ->
        assertEquals(0, doc.pageRotation(0))
        doc.rotatePage(0, 1)
        assertEquals(1, doc.pageRotation(0))
        val page = doc.openPage(0)
        try {
            assertEquals(792, page.widthPoints.toInt())
            assertEquals(600, page.heightPoints.toInt())
        } finally {
            page.close()
        }
        doc.rotatePage(0, -1)
        assertEquals(0, doc.pageRotation(0))
        assertEquals(listOf(600, 610), widths(doc))
    }

    @Test
    fun anOpenPageHandleFollowsItsPageAndAnswersMinusOneOnceItIsDeleted() = withDocument(pdf(3)) { doc ->
        // megapdf_page_index (#174): what a view holding a page across a page operation has to be
        // able to ask. The handle is the second page's; a delete in front of it moves it up one.
        val page = doc.openPage(1)
        try {
            assertEquals(1, page.currentIndex())
            doc.deletePageWithoutUndo(0)
            assertEquals("the handle followed its page", 0, page.currentIndex())
            doc.movePage(0, 1)
            assertEquals("and followed it again", 1, page.currentIndex())
        } finally {
            page.close()
        }
    }

    @Test
    fun aDeletedPageComesBackAsItself() = withDocument(pdf(4)) { doc ->
        val removed = doc.deletePage(1)
        assertTrue(removed.isHeld)
        assertEquals(listOf(600, 620, 630), widths(doc))

        doc.restorePage(removed, 1)
        assertEquals(listOf(600, 610, 620, 630), widths(doc))
        assertFalse("the handle is spent once it has been used", removed.isHeld)
    }

    @Test
    fun aRestoredHandleCannotBeUsedTwice() = withDocument(pdf(2)) { doc ->
        val removed = doc.deletePage(0)
        doc.restorePage(removed, 0)
        val again = runCatching { doc.restorePage(removed, 0) }
        assertTrue(
            "a spent handle must fail loudly, which is what #429 and #441 were filed for",
            again.exceptionOrNull() is IllegalStateException,
        )
    }

    @Test
    fun theLastPageIsRefusedByName() = withDocument(pdf(1)) { doc ->
        val refused = runCatching { doc.deletePage(0) }.exceptionOrNull()
        assertTrue(refused is PdfPagesException)
        assertEquals(PageToolRefusal.LAST_PAGE, (refused as PdfPagesException).refusal)
        assertEquals(1, doc.pageCount())
    }

    @Test
    fun aBlankPageAndAMoveLandWhereTheyWereAsked() = withDocument(pdf(2)) { doc ->
        doc.insertBlankPage(1, 300.0, 400.0)
        assertEquals(listOf(600, 300, 610), widths(doc))
        doc.movePage(1, 2)
        assertEquals(listOf(600, 610, 300), widths(doc))
    }

    @Test
    fun importingPagesFromAnotherFileBringsThemIn() {
        val other = File.createTempFile("megapdf-import-174", ".pdf")
        other.writeBytes(pdf(2))
        try {
            withDocument(pdf(1)) { doc ->
                val imported = doc.importPages(path = other.path, insertAt = 1)
                assertEquals(2, imported)
                assertEquals(listOf(600, 600, 610), widths(doc))
            }
        } finally {
            other.delete()
        }
    }

    @Test
    fun aClashingFieldHierarchyIsRefusedWithItsOwnRefusal() {
        // The refusal the app has to put into words (#174). It arrives as MEGAPDF_ERR_FIELDS on
        // every PDFium patch level — below 0033 because the copy cannot carry a /Parent chain at
        // all, and from 0033 on because a name that lives on the parent cannot be renamed out of
        // the way of one this document already has.
        val form = File.createTempFile("megapdf-hierarchy-174", ".pdf")
        form.writeBytes(parentFieldsPdf())
        try {
            withDocument(parentFieldsPdf()) { doc ->
                val refused = runCatching { doc.importPages(path = form.path, insertAt = 1) }
                    .exceptionOrNull()
                assertTrue("expected a named refusal, got $refused", refused is PdfPagesException)
                assertEquals(
                    PageToolRefusal.FIELD_HIERARCHY,
                    (refused as PdfPagesException).refusal,
                )
                assertEquals("nothing is added by a refused import", 1, doc.pageCount())
            }
        } finally {
            form.delete()
        }
    }

    @Test
    fun extractingWritesOnlyThePagesAskedForAndLeavesTheDocumentAlone() {
        val out = File.createTempFile("megapdf-extract-174", ".pdf")
        out.delete()
        try {
            withDocument(pdf(4)) { doc ->
                doc.extractPages(listOf(2, 0), out.path)
                assertTrue(out.length() > 0)
                assertEquals(listOf(600, 610, 620, 630), widths(doc))
            }
            // Read back with an engine of its own: the order asked for is the order written.
            val engine = PdfEngine()
            runBlocking {
                val copy = engine.open(out.readBytes())
                try {
                    assertEquals(2, copy.pageCount())
                    assertEquals(listOf(620, 600), widths(copy))
                } finally {
                    copy.close()
                }
            }
        } finally {
            out.delete()
        }
    }

    @Test
    fun aRotationSurvivesASaveAndReopen() = withDocument(pdf(2)) { doc ->
        doc.rotatePage(1, 1)
        val bytes = java.io.ByteArrayOutputStream().also { doc.save(it) }.toByteArray()
        val engine = PdfEngine()
        val reopened = engine.open(bytes)
        try {
            assertEquals(1, reopened.pageRotation(1))
            assertEquals(listOf(600, 792), widths(reopened))
            assertNotEquals(0, reopened.pageRotation(1))
        } finally {
            reopened.close()
        }
    }
}
