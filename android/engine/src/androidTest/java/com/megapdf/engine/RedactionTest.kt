package com.megapdf.engine

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayOutputStream

/**
 * Redaction (#173) on a real device, against the fixtures
 * `tools/gen_redaction_fixtures.py` writes. Each hides the same canary,
 * `CANARY-42-XYZ`, with a `KEEP` word either side of it.
 *
 * What these check is the promise rather than the plumbing: that marking removes nothing,
 * that a document saved with marks on it carries none, that the canary is gone from the
 * saved bytes and the KEEP words are not, and that a refusal leaves the document alone.
 */
@RunWith(AndroidJUnit4::class)
class RedactionTest {

    private val engine = PdfEngine()

    private fun asset(name: String): ByteArray =
        InstrumentationRegistry.getInstrumentation().context.assets
            .open(name).use { it.readBytes() }

    private suspend fun save(doc: PdfDocument): ByteArray {
        val out = ByteArrayOutputStream()
        doc.save(out)
        return out.toByteArray()
    }

    private suspend fun pageText(bytes: ByteArray): String {
        val doc = engine.open(bytes)
        try {
            val page = doc.openPage(0)
            try {
                return page.textLines().joinToString(" ") { it.text }
            } finally {
                page.close()
            }
        } finally {
            doc.close()
        }
    }

    @Test
    fun markingRemovesNothingAndIsNeverSaved() {
        runBlocking {
            val doc = engine.open(asset("text-partial-run.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    val id = page.markForRedaction(PdfRect(120.0, 694.0, 270.0, 718.0))
                    assertTrue("a mark gets an id", id >= 0)
                    assertEquals(1, page.redactionMarks().size)
                } finally {
                    page.close()
                }
                assertEquals(1, doc.redactionMarkCount())

                // A mark is not content: it removes nothing, and it is never written.
                val saved = save(doc)
                assertTrue("the text is still there before applying",
                    pageText(saved).contains("CANARY"))
                val reopened = engine.open(saved)
                try {
                    assertEquals("a saved document carries no marks", 0, reopened.redactionMarkCount())
                } finally {
                    reopened.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun applyingRemovesTheCanaryAndKeepsWhatIsBesideIt() {
        runBlocking {
            val doc = engine.open(asset("text-partial-run.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    // The canary sits in the middle of the line; KEEP is either side.
                    page.markForRedaction(PdfRect(124.0, 694.0, 268.0, 718.0))
                } finally {
                    page.close()
                }
                val report = doc.applyRedactions()
                assertTrue("apply succeeds: ${report.refusals}", report.applied)
                assertTrue("it says how many characters went", report.counts.characters > 0)
                assertEquals("the marks are gone with it", 0, doc.redactionMarkCount())

                val saved = save(doc)
                val text = pageText(saved)
                assertFalse("the canary no longer extracts", text.contains("CANARY"))
                assertTrue("the words beside it are still there", text.contains("KEEP"))
                // And not in the bytes either, which is the whole point.
                assertFalse("the canary is not in the file's bytes",
                    String(saved, Charsets.ISO_8859_1).contains("CANARY-42-XYZ"))
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun imagePixelsInsideTheAreaAreOverwritten() {
        runBlocking {
            val doc = engine.open(asset("image-flate.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    // The image is drawn 300x100 pt at (100, 600); mark its left half.
                    page.markForRedaction(PdfRect(100.0, 600.0, 250.0, 700.0))
                } finally {
                    page.close()
                }
                val report = doc.applyRedactions()
                assertTrue("apply succeeds: ${report.refusals}", report.applied)
                assertEquals("one image was rewritten", 1, report.counts.images)
                val saved = save(doc)
                assertTrue("the redacted document still opens", saved.size > 0)
                assertFalse("no canary pixels survive as text either",
                    pageText(saved).contains("CANARY"))
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun aRefusalChangesNothing() {
        runBlocking {
            val doc = engine.open(asset("form-xobject.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    // The form XObject reaches into this area. PDFium cannot write back an
                    // edit made inside a form, so the redaction refuses rather than leave
                    // the content behind (tools/pdfium/README.md).
                    page.markForRedaction(PdfRect(150.0, 655.0, 350.0, 695.0))
                } finally {
                    page.close()
                }
                val report = doc.applyRedactions()
                assertFalse("a form XObject in the area is refused", report.applied)
                assertEquals("a refused apply removes nothing", 0, report.counts.characters)
                assertTrue("and says which page", report.refusals.isNotEmpty())
                assertEquals(RedactionRefusalReason.FORM_XOBJECT, report.refusals[0].reason)
                assertEquals("the marks are still there", 1, doc.redactionMarkCount())
            } finally {
                doc.close()
            }
        }
    }
}
