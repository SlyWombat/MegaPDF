package com.megapdf.engine

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * A page drawn in units larger than a point (#150). userunit.pdf (tools/gen_test_fixtures.py)
 * is a 306 x 396 MediaBox with CropBox [0 50 306 350] and /UserUnit 2, so it measures
 * 612 x 600 pt. Ignoring /UserUnit shows it at half size and puts every tap, mark and
 * text box at half its distance from the corner.
 */
@RunWith(AndroidJUnit4::class)
class UserUnitTest {

    private val engine = PdfEngine()

    private fun asset(name: String): ByteArray =
        InstrumentationRegistry.getInstrumentation().context.assets
            .open(name).use { it.readBytes() }

    @Test
    fun pageMeasuresInPointsAndContentIsWhereItIsDrawn() {
        runBlocking {
            val doc = engine.open(asset("userunit.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    assertEquals(612.0, page.widthPoints, 1.0)
                    assertEquals(600.0, page.heightPoints, 1.0)

                    val square = page.detectCheckboxSquares().single()
                    assertEquals(100.0, square.left, 1.0)
                    assertEquals(400.0, square.bottom, 1.0)

                    val field = page.formFields().single()
                    assertEquals(100.0, field.rect.left, 0.1)
                    assertEquals(300.0, field.rect.bottom, 0.1)
                    assertEquals(300.0, field.rect.right, 0.1)
                    assertEquals(320.0, field.rect.top, 0.1)

                    val rect = page.search("megapdf").single().rects.single()
                    assertTrue("the hit should straddle the 550 pt baseline, was $rect",
                        rect.bottom < 556.0 && rect.top > 544.0)
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun placementsLandWhereAsked() {
        runBlocking {
            val doc = engine.open(asset("userunit.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    page.addTextBox("Signed", 12.0, 80.0, 120.0, "text:unit")
                    var box = page.textBoxes().single()
                    assertEquals(12.0, box.fontSize, 0.01)
                    assertEquals(80.0, box.rect.left, 2.0)
                    assertEquals(120.0, box.rect.bottom, 4.0)
                    assertTrue("a 12 pt box is about 12 pt tall, was ${box.rect}",
                        box.rect.top - box.rect.bottom in 8.0..16.0)
                    page.moveTextBox("text:unit", 200.0, 220.0)
                    box = page.textBoxes().single()
                    assertEquals(200.0, box.rect.left, 0.1)
                    assertEquals(220.0, box.rect.bottom, 0.1)

                    val placed = PdfRect(350.0, 100.0, 470.0, 160.0)
                    page.addImageStamp(IntArray(16) { 0xFF404040.toInt() }, 4, 4, placed, "sig:unit")
                    val stamp = page.stamps().single { it.id == "sig:unit" }
                    assertEquals(placed.left, stamp.rect.left, 0.1)
                    assertEquals(placed.bottom, stamp.rect.bottom, 0.1)
                    assertEquals(placed.right, stamp.rect.right, 0.1)
                    assertEquals(placed.top, stamp.rect.top, 0.1)
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }
}
