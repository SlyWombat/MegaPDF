package com.megapdf.engine

import android.graphics.Bitmap
import android.graphics.Color
import androidx.test.ext.junit.runners.AndroidJUnit4
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayOutputStream
import kotlin.math.abs

/**
 * Shrink for email through the Android JNI binding (#593), on a device. The core's own suite
 * covers the rules, the progress, the cancel flag and the lock (`core_tests.cpp`,
 * `test_shrink_progress_and_cancel`); what only a device can say is that this binding carries
 * them across: that `Bitmap.compress` is reached through the JNI upcall and its JPEG lands in the
 * document, that the BGRA the core hands over arrives as ARGB (not with red and blue swapped),
 * and that a flag raised from Kotlin stops the run.
 */
@RunWith(AndroidJUnit4::class)
class ShrinkEngineTest {

    private val engine = PdfEngine()

    @Test
    fun shrinkReplacesTheOversizedImageAndTheSavedCopyIsSmaller() {
        runBlocking {
            val input = LargeImagePdf.bytes(1)
            val doc = engine.open(input)
            try {
                val seen = ArrayList<Pair<Int, Int>>()
                val outcome = doc.shrinkImagesForEmail(PdfCancelFlag()) { done, total -> synchronized(seen) { seen += done to total } }
                assertEquals(ShrinkOutcome.Done(considered = 1, replaced = 1), outcome)
                assertEquals(listOf(0 to 1, 1 to 1), synchronized(seen) { seen.toList() })
                val saved = ByteArrayOutputStream().also { doc.save(it) }.toByteArray()
                assertTrue("saved ${saved.size} bytes against ${input.size}", saved.size < input.size)

                // Still the blue it was (R 30-61, G 70-101, B 185-216), give or take the JPEG. Swapped
                // channels would read about (200, 85, 45).
                val page = doc.openPage(0)
                try {
                    val bitmap = Bitmap.createBitmap(612, 792, Bitmap.Config.ARGB_8888)
                    page.render(bitmap)
                    // The picture is drawn at x 100..172, y 600..672 in PDF space: 136 pt from the top.
                    val pixel = bitmap.getPixel(136, 792 - 636)
                    assertTrue(
                        "expected the fixture's blue, got (${Color.red(pixel)}, ${Color.green(pixel)}, ${Color.blue(pixel)})",
                        // Within 35 of the fixture's mean (45, 85, 200) on every channel: a swap
                        // reads about (200, 85, 45), a misaligned byte order about (85, 45, 255).
                        abs(Color.red(pixel) - 45) < 35 && abs(Color.green(pixel) - 85) < 35 &&
                            abs(Color.blue(pixel) - 200) < 35,
                    )
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun aRaisedFlagStopsTheShrink() {
        runBlocking {
            val doc = engine.open(LargeImagePdf.bytes(3))
            try {
                val cancel = PdfCancelFlag()
                // Raised after the first of three pictures, from the shrink's own thread.
                val outcome = doc.shrinkImagesForEmail(cancel) { done, _ -> if (done == 1) cancel.cancel() }
                assertEquals(ShrinkOutcome.Stopped, outcome)
            } finally {
                doc.close()
            }
        }
    }
}
