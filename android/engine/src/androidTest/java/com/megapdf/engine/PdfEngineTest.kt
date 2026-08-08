package com.megapdf.engine

import android.graphics.Bitmap
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayOutputStream

@RunWith(AndroidJUnit4::class)
class PdfEngineTest {

    private val engine = PdfEngine()

    private fun fixtureBytes(): ByteArray =
        InstrumentationRegistry.getInstrumentation().context.assets
            .open("fixture.pdf").use { it.readBytes() }

    // JUnit requires void test methods, so each test wraps its body in a
    // statement-position runBlocking rather than an expression body.

    @Test
    fun opensAndReportsGeometry() {
        runBlocking {
            val doc = engine.open(fixtureBytes())
            try {
                assertEquals(2, doc.pageCount())
                val page = doc.openPage(0)
                try {
                    assertEquals(612.0, page.widthPoints, 0.01)  // US Letter
                    assertEquals(792.0, page.heightPoints, 0.01)
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun renderProducesInk() {
        runBlocking {
            val doc = engine.open(fixtureBytes())
            try {
                val page = doc.openPage(0)
                try {
                    val bitmap = Bitmap.createBitmap(612, 792, Bitmap.Config.ARGB_8888)
                    page.render(bitmap)
                    val pixels = IntArray(bitmap.width * bitmap.height)
                    bitmap.getPixels(pixels, 0, bitmap.width, 0, 0, bitmap.width, bitmap.height)
                    val inked = pixels.count { it != -1 }  // -1 == opaque white ARGB
                    assertTrue("expected ink on the page, found $inked non-white pixels", inked > 100)
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun saveRoundTrips() {
        runBlocking {
            val doc = engine.open(fixtureBytes())
            val saved = ByteArrayOutputStream()
            try {
                doc.save(saved)
            } finally {
                doc.close()
            }
            assertTrue("saved document is empty", saved.size() > 0)

            val reopened = engine.open(saved.toByteArray())
            try {
                assertEquals(2, reopened.pageCount())
            } finally {
                reopened.close()
            }
        }
    }

    @Test
    fun invalidBytesThrowLoadException() {
        runBlocking {
            try {
                engine.open("not a pdf".toByteArray())
                fail("expected PdfLoadException")
            } catch (expected: PdfLoadException) {
                // expected
            }
        }
    }

    @Test
    fun concurrentRendersAreSerialized() {
        runBlocking {
            val doc = engine.open(fixtureBytes())
            try {
                val page = doc.openPage(0)
                try {
                    // Two coroutines hammering render; the single-threaded dispatcher
                    // must serialize the native calls (PDFium is not thread-safe).
                    List(2) {
                        async {
                            repeat(10) {
                                val bitmap = Bitmap.createBitmap(306, 396, Bitmap.Config.ARGB_8888)
                                page.render(bitmap)
                            }
                        }
                    }.awaitAll()
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }
}
