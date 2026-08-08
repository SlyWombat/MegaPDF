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

/** Tap-to-check engine behavior (#15) against the SDD §6.2 parity fixtures. */
@RunWith(AndroidJUnit4::class)
class CheckboxTest {

    private val engine = PdfEngine()

    private fun asset(name: String): ByteArray =
        InstrumentationRegistry.getInstrumentation().context.assets
            .open(name).use { it.readBytes() }

    @Test
    fun formCheckboxTogglesAndSurvivesSave() {
        runBlocking {
            val doc = engine.open(asset("forms.pdf"))
            val saved = ByteArrayOutputStream()
            try {
                val page = doc.openPage(0)
                try {
                    val before = page.formFields()
                    assertEquals(1, before.size)
                    assertFalse(before[0].isRadio)
                    assertFalse(before[0].isChecked)
                    // Widget rect from the fixture: (100,600)-(115,615).
                    assertEquals(100.0, before[0].rect.left, 0.01)
                    assertEquals(615.0, before[0].rect.top, 0.01)

                    page.clickAt(before[0].rect.centerX, before[0].rect.centerY)
                    assertTrue("click should check the box", page.formFields()[0].isChecked)

                    page.clickAt(before[0].rect.centerX, before[0].rect.centerY)
                    assertFalse("second click should uncheck", page.formFields()[0].isChecked)

                    page.clickAt(before[0].rect.centerX, before[0].rect.centerY)
                } finally {
                    page.close()
                }
                doc.save(saved)
            } finally {
                doc.close()
            }

            val reopened = engine.open(saved.toByteArray())
            try {
                val page = reopened.openPage(0)
                try {
                    assertTrue("checked state must survive save/reopen",
                        page.formFields()[0].isChecked)
                } finally {
                    page.close()
                }
            } finally {
                reopened.close()
            }
        }
    }

    @Test
    fun drawnSquareDetectedMarkAddedAndRoundTrips() {
        runBlocking {
            val doc = engine.open(asset("fixture.pdf"))
            val saved = ByteArrayOutputStream()
            try {
                val page = doc.openPage(0)
                try {
                    // Fixture square: 12x12pt at (72,600); bounds may include stroke.
                    val squares = page.detectCheckboxSquares()
                    assertEquals(1, squares.size)
                    assertEquals(72.0, squares[0].left, 1.0)
                    assertEquals(600.0, squares[0].bottom, 1.0)
                    assertEquals(84.0, squares[0].right, 1.0)
                    assertEquals(612.0, squares[0].top, 1.0)

                    page.addCheckMark(squares[0], "mark:test-1")
                    val stamps = page.stamps()
                    assertEquals(1, stamps.size)
                    assertEquals("mark:test-1", stamps[0].id)
                    assertTrue("mark rect should sit inside the square",
                        stamps[0].rect.left >= squares[0].left &&
                            stamps[0].rect.top <= squares[0].top)
                } finally {
                    page.close()
                }
                doc.save(saved)
            } finally {
                doc.close()
            }

            // The MegaPDF_Id contract: the mark is still identifiable after
            // save/reopen (this is what desktop interop relies on), and removable.
            val reopened = engine.open(saved.toByteArray())
            try {
                val page = reopened.openPage(0)
                try {
                    val stamps = page.stamps()
                    assertEquals(1, stamps.size)
                    assertEquals("mark:test-1", stamps[0].id)

                    page.removeAnnot(stamps[0].annotIndex)
                    assertTrue("mark removed", page.stamps().isEmpty())
                } finally {
                    page.close()
                }
            } finally {
                reopened.close()
            }
        }
    }

    @Test
    fun pageTwoHasNoSquares() {
        runBlocking {
            val doc = engine.open(asset("fixture.pdf"))
            try {
                val page = doc.openPage(1)
                try {
                    assertTrue(page.detectCheckboxSquares().isEmpty())
                    assertTrue(page.formFields().isEmpty())
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }
}
