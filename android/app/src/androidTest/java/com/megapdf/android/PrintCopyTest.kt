package com.megapdf.android

import android.graphics.Bitmap
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.megapdf.engine.PageToolRefusal
import com.megapdf.engine.PdfDocument
import com.megapdf.engine.PdfEngine
import com.megapdf.engine.PdfPagesException
import com.megapdf.engine.PdfPermissions
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.io.OutputStream

/**
 * The print copy (#682): what [writePrintCopy] hands the system print service.
 *
 * The preview behind the print dialog draws no annotations, so a stamp that is still an
 * annotation in the copy is missing from it. The copy must therefore carry what prints as page
 * content, carry nothing that would not print, and leave the open document exactly as it was.
 *
 * Pixels are compared, not eyeballed. The copy is rendered by the engine, which always draws
 * annotations; that does not weaken the check, because the first assertion is that the copy has
 * none left, so every pixel it draws is page content.
 */
@RunWith(AndroidJUnit4::class)
class PrintCopyTest {

    private val engine = PdfEngine()
    private lateinit var scratch: File

    @Before
    fun scratch() {
        scratch = File(InstrumentationRegistry.getInstrumentation().targetContext.cacheDir, "print-copy-test-${System.nanoTime()}")
        scratch.mkdirs()
    }

    @After
    fun clean() {
        scratch.deleteRecursively()
    }

    /** The scratch folder the print copy used must be empty again: nothing is left after a print. */
    private fun assertNothingLeftBehind() {
        assertEquals("scratch files left behind: ${scratch.list()?.toList()}", 0, scratch.list()?.size ?: 0)
    }

    private fun printCopy(doc: PdfDocument, strip: Boolean = false, pages: List<Int>? = null): ByteArray = runBlocking {
        val out = ByteArrayOutputStream()
        writePrintCopy(engine, doc, strip, pages, scratch, out)
        out.toByteArray()
    }

    private fun renderBitmap(doc: PdfDocument, index: Int): Bitmap = runBlocking {
        val page = doc.openPage(index)
        try {
            val bitmap = Bitmap.createBitmap(612, 792, Bitmap.Config.ARGB_8888)
            bitmap.eraseColor(android.graphics.Color.WHITE)
            page.render(bitmap)
            bitmap
        } finally {
            page.close()
        }
    }

    /** Whether the block under [at] (PDF user space) is inked; white paper is not. */
    private fun inked(bitmap: Bitmap, at: Pair<Double, Double>): Boolean {
        val pixel = bitmap.getPixel(at.first.toInt(), 791 - at.second.toInt())
        return android.graphics.Color.blue(pixel) < 200
    }

    /** Every pixel of the printable block, so "the same pixels" is the whole block, not one sample. */
    private fun blockPixels(bitmap: Bitmap): IntArray {
        // The block is [100,190] x [500,560] in user space: columns 100..189, rows 232..291.
        val pixels = IntArray(90 * 60)
        bitmap.getPixels(pixels, 0, 90, 100, 232, 90, 60)
        return pixels
    }

    private fun stampCount(doc: PdfDocument, index: Int): Int = runBlocking {
        val page = doc.openPage(index)
        try {
            page.stamps().size
        } finally {
            page.close()
        }
    }

    @Test
    fun theWholeCopyHasTheStampBakedIn() = runBlocking {
        val doc = engine.open(TestPdfs.threeStamps())
        val copy = engine.open(printCopy(doc))
        try {
            val original = renderBitmap(doc, 0)
            // The fixture is what the test says it is: on screen the Print and the F 0 stamps show.
            assertTrue(inked(original, TestPdfs.PRINT_STAMP))
            assertTrue(inked(original, TestPdfs.SCREEN_STAMP))
            assertFalse(inked(original, TestPdfs.HIDDEN_STAMP))

            // (a) nothing is left as an annotation
            assertEquals("annotations left in the print copy", 0, stampCount(copy, 0))
            // (b) the Print stamp's pixels are page content, the same as the original shows
            val rendered = renderBitmap(copy, 0)
            assertTrue(inked(rendered, TestPdfs.PRINT_STAMP))
            assertTrue("the stamp's pixels differ from the original's", blockPixels(original).contentEquals(blockPixels(rendered)))
            // (c) a stamp that would not print is not in the copy, hidden or screen-only
            assertFalse("a no-print stamp was baked in", inked(rendered, TestPdfs.SCREEN_STAMP))
            assertFalse("a hidden stamp was baked in", inked(rendered, TestPdfs.HIDDEN_STAMP))
            // (d) the open document is untouched
            assertEquals(3, stampCount(doc, 0))
            assertTrue(inked(renderBitmap(doc, 0), TestPdfs.SCREEN_STAMP))
        } finally {
            copy.close()
            doc.close()
        }
        assertNothingLeftBehind()
    }

    @Test
    fun aPageRangeCopyHasTheStampBakedInToo() = runBlocking {
        val doc = engine.open(TestPdfs.threeStamps(pages = 2))
        val copy = engine.open(printCopy(doc, pages = listOf(1)))
        try {
            assertEquals(1, copy.pageCount())
            assertEquals(0, stampCount(copy, 0))
            val original = renderBitmap(doc, 1)
            val rendered = renderBitmap(copy, 0)
            assertTrue(blockPixels(original).contentEquals(blockPixels(rendered)))
            assertFalse(inked(rendered, TestPdfs.SCREEN_STAMP))
            assertFalse(inked(rendered, TestPdfs.HIDDEN_STAMP))
            assertEquals(3, stampCount(doc, 1))
            assertEquals(2, doc.pageCount())
        } finally {
            copy.close()
            doc.close()
        }
        assertNothingLeftBehind()
    }

    @Test
    fun aProtectedDocumentStillPrintsInFullButNotInPart() = runBlocking {
        // Print allowed, copy not: what an author who allows paper and forbids extraction sets.
        val protectedBytes = ByteArrayOutputStream().also { out ->
            val source = engine.open(TestPdfs.threeStamps(pages = 2))
            try {
                source.saveWithSecurity(out, userPassword = "", ownerPassword = "o-682", permissions = PdfPermissions.PRINT)
            } finally {
                source.close()
            }
        }.toByteArray()
        val doc = engine.open(protectedBytes)
        try {
            val copy = engine.open(printCopy(doc), password = "")
            try {
                assertEquals(2, copy.pageCount())
                assertEquals(0, stampCount(copy, 0))
                assertTrue(inked(renderBitmap(copy, 0), TestPdfs.PRINT_STAMP))
            } finally {
                copy.close()
            }
            try {
                printCopy(doc, pages = listOf(0))
                fail("a range of a no-copy document must be refused")
            } catch (e: PdfPagesException) {
                assertEquals(PageToolRefusal.RESTRICTED, e.refusal)
            }
            assertEquals(3, stampCount(doc, 0))
        } finally {
            doc.close()
        }
        assertNothingLeftBehind()
    }

    @Test
    fun anUnlockedDocumentPrintsWithItsSecurityStripped() = runBlocking {
        val protectedBytes = ByteArrayOutputStream().also { out ->
            val source = engine.open(TestPdfs.threeStamps())
            try {
                source.saveWithSecurity(out, userPassword = "u-682", ownerPassword = "o-682", permissions = PdfPermissions.PRINT)
            } finally {
                source.close()
            }
        }.toByteArray()
        val doc = engine.open(protectedBytes, password = "o-682")
        try {
            // No password given: the print service has to be able to read it as it is.
            val copy = engine.open(printCopy(doc, strip = true))
            try {
                assertEquals(0, stampCount(copy, 0))
                assertTrue(inked(renderBitmap(copy, 0), TestPdfs.PRINT_STAMP))
            } finally {
                copy.close()
            }
        } finally {
            doc.close()
        }
        assertNothingLeftBehind()
    }

    @Test
    fun noScratchFileSurvivesAFailedOrStoppedWrite() = runBlocking {
        val doc = engine.open(TestPdfs.threeStamps(pages = 2))
        try {
            val refusing = object : OutputStream() {
                override fun write(b: Int) = throw IOException("the spooler went away")
                override fun write(b: ByteArray, off: Int, len: Int) = throw IOException("the spooler went away")
            }
            for (pages in listOf<List<Int>?>(null, listOf(0))) {
                try {
                    writePrintCopy(engine, doc, false, pages, scratch, refusing)
                    fail("a write into a dead stream must fail")
                } catch (_: Exception) {
                    // expected: the engine reports the failed write
                }
                assertNothingLeftBehind()
            }
            // And the document is still whole and still has its stamps.
            assertEquals(3, stampCount(doc, 0))
            assertNotNull(printCopy(doc))
        } finally {
            doc.close()
        }
        assertNothingLeftBehind()
    }
}
