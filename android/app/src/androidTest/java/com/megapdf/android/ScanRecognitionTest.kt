package com.megapdf.android

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import androidx.test.ext.junit.runners.AndroidJUnit4
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The real recogniser (#676): ML Kit's bundled model reading a page drawn as pixels, on a device.
 * What `ScanSearchTest` scripts, this does for real: that words come back, that they land where
 * they were drawn, and that accents survive.
 */
@RunWith(AndroidJUnit4::class)
class ScanRecognitionTest {

    /** A 612 x 792 pt page at 150 dpi, with the given lines drawn at 100 pt from the top, 120 pt apart. */
    private fun page(lines: List<String>): Bitmap {
        val scale = 150f / 72f
        val bitmap = Bitmap.createBitmap((612 * scale).toInt(), (792 * scale).toInt(), Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bitmap)
        canvas.drawColor(Color.WHITE)
        val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.BLACK; textSize = 44f * scale }
        lines.forEachIndexed { i, line -> canvas.drawText(line, 72f * scale, (100f + 120f * i + 44f) * scale, paint) }
        return bitmap
    }

    private fun read(lines: List<String>): OcrPage =
        runBlocking { MlKitPageRecogniser().read(page(lines), 612.0, 792.0) }

    @Test
    fun readsADrawnPageAndSaysWhereTheWordIs() {
        val read = read(listOf("Invoice number 4821", "Total due on receipt"))
        val hits = read.matches("invoice")
        assertEquals("read: ${read.lines.map { it.text }}", 1, hits.size)
        val rect = hits[0].rects.single()
        // Drawn at x = 72, 100 pt from the top of a 792 pt page: the word's top edge is near
        // 792 - 100 = 692 in PDF space, and it starts near x = 72.
        assertEquals(72.0, rect.left, 20.0)
        assertEquals(692.0, rect.top, 30.0)
        assertTrue("wider than a letter, narrower than the page", rect.right - rect.left in 100.0..300.0)
        assertEquals(1, read.matches("due on").size)
    }

    @Test
    fun readsAFrenchPageWithItsAccents() {
        val read = read(listOf("Facture numéro 4821", "Montant à payer avant échéance"))
        val words = read.lines.joinToString(" | ") { it.text }
        assertEquals("read: $words", 1, read.matches("facture").size)
        assertEquals("accents survive: $words", 1, read.matches("échéance").size)
        assertEquals("read: $words", 1, read.matches("à payer").size)
    }
}
