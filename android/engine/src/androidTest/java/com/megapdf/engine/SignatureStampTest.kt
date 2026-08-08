package com.megapdf.engine

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayOutputStream

/** Signature stamp engine behavior (#17): place, persist by MegaPDF_Id, read back, remove. */
@RunWith(AndroidJUnit4::class)
class SignatureStampTest {

    private val engine = PdfEngine()

    private fun fixtureBytes(): ByteArray =
        InstrumentationRegistry.getInstrumentation().context.assets
            .open("fixture.pdf").use { it.readBytes() }

    /** 12x8 opaque dark-blue block with a transparent border column. */
    private fun testPixels(w: Int = 12, h: Int = 8): IntArray =
        IntArray(w * h) { i ->
            if (i % w == 0) 0 else (0xFF shl 24) or (0x20 shl 16) or (0x30 shl 8) or 0x90
        }

    @Test
    fun placePersistReadbackRemove() {
        runBlocking {
            val rect = PdfRect(100.0, 500.0, 190.0, 560.0)
            val doc = engine.open(fixtureBytes())
            val saved = ByteArrayOutputStream()
            try {
                val page = doc.openPage(0)
                try {
                    page.addImageStamp(testPixels(), 12, 8, rect, "sig:test-1")
                    val stamps = page.stamps().filter { it.id.startsWith("sig:") }
                    assertEquals(1, stamps.size)
                    assertEquals(100.0, stamps[0].rect.left, 0.5)
                    assertEquals(560.0, stamps[0].rect.top, 0.5)
                } finally {
                    page.close()
                }
                doc.save(saved)
            } finally {
                doc.close()
            }

            // Interop contract: the stamp is still identifiable by MegaPDF_Id
            // after save/reopen, its image reads back at native resolution,
            // and remove works — the move/resize building blocks.
            val reopened = engine.open(saved.toByteArray())
            try {
                val page = reopened.openPage(0)
                try {
                    val stamp = page.stamps().single { it.id == "sig:test-1" }

                    val packed = page.stampImagePacked(stamp.annotIndex)
                    assertNotNull("stamp image should read back", packed)
                    assertEquals(12, packed!![0])
                    assertEquals(8, packed[1])
                    assertTrue("readback should contain visible pixels",
                        packed.drop(2).any { (it ushr 24) > 0 })

                    page.removeAnnot(stamp.annotIndex)
                    assertTrue(page.stamps().none { it.id.startsWith("sig:") })
                } finally {
                    page.close()
                }
            } finally {
                reopened.close()
            }
        }
    }

    @Test
    fun renderShowsStamp() {
        runBlocking {
            val doc = engine.open(fixtureBytes())
            try {
                val page = doc.openPage(1)  // page 2 is blank apart from a title
                try {
                    val before = renderInkCount(page)
                    page.addImageStamp(
                        testPixels(), 12, 8,
                        PdfRect(200.0, 300.0, 350.0, 400.0), "sig:visible")
                    val after = renderInkCount(page)
                    assertTrue(
                        "stamp should add visible pixels (before=$before after=$after)",
                        after > before + 1000)
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    private suspend fun renderInkCount(page: PdfPage): Int {
        val bitmap = android.graphics.Bitmap.createBitmap(
            306, 396, android.graphics.Bitmap.Config.ARGB_8888)
        page.render(bitmap)
        val pixels = IntArray(bitmap.width * bitmap.height)
        bitmap.getPixels(pixels, 0, bitmap.width, 0, 0, bitmap.width, bitmap.height)
        return pixels.count { it != -1 }
    }
}
