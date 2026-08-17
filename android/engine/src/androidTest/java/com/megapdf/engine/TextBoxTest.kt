package com.megapdf.engine

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Added text (#34). A text box is a page text object carrying the
 * "MegaPDFTextBox" mark plus an "id" param — the desktop's representation — so
 * these assertions are the Android half of the cross-platform contract, mirrored
 * by iOS's AddTextTests.
 */
@RunWith(AndroidJUnit4::class)
class TextBoxTest {

    private val engine = PdfEngine()

    private fun asset(name: String): ByteArray =
        InstrumentationRegistry.getInstrumentation().context.assets
            .open(name).use { it.readBytes() }

    @Test
    fun addedTextIsReadableAndSurvivesSave() {
        runBlocking {
            val doc = engine.open(asset("fixture.pdf"))
            val saved: ByteArray
            try {
                val page = doc.openPage(0)
                try {
                    page.addTextBox("Jane Smith", 12.0, 100.0, 300.0, "text:a")
                    val boxes = page.textBoxes()
                    assertEquals(1, boxes.size)
                    assertEquals("text:a", boxes[0].id)
                    assertEquals("Jane Smith", boxes[0].text)
                    assertEquals(12.0, boxes[0].fontSize, 0.5)
                    assertEquals(100.0, boxes[0].rect.left, 2.0)
                } finally {
                    page.close()
                }
                saved = java.io.ByteArrayOutputStream().also { doc.save(it) }.toByteArray()
            } finally {
                doc.close()
            }

            val reopened = engine.open(saved)
            try {
                val page = reopened.openPage(0)
                try {
                    val boxes = page.textBoxes()
                    assertEquals("the box must survive save and reopen", 1, boxes.size)
                    assertEquals("Jane Smith", boxes[0].text)
                    assertEquals("its id is the handle undo relies on", "text:a", boxes[0].id)
                } finally {
                    page.close()
                }
            } finally {
                reopened.close()
            }
        }
    }

    @Test
    fun addedTextIsFoundBySearch() {
        runBlocking {
            val doc = engine.open(asset("fixture.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    page.addTextBox("Marmalade", 12.0, 100.0, 300.0, "text:b")
                    assertEquals("added text is real page text, not an overlay",
                        1, page.search("marmalade").size)
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun removeAndMoveAddressTheBoxById() {
        runBlocking {
            val doc = engine.open(asset("fixture.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    page.addTextBox("First", 12.0, 100.0, 300.0, "text:first")
                    page.addTextBox("Second", 12.0, 100.0, 200.0, "text:second")

                    page.moveTextBox("text:second", 250.0, 400.0)
                    var moved = page.textBoxes().first { it.id == "text:second" }
                    assertEquals(250.0, moved.rect.left, 2.0)
                    assertEquals(400.0, moved.rect.bottom, 2.0)

                    // Removing the first box shifts the second one's object index;
                    // the id must still find it.
                    page.removeTextBox("text:first")
                    val remaining = page.textBoxes()
                    assertEquals(1, remaining.size)
                    assertEquals("text:second", remaining[0].id)
                    assertEquals("Second", remaining[0].text)

                    // Already gone is a no-op, not a failure — undo may race a render.
                    page.removeTextBox("text:first")
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun textGoesWhereAskedOnACroppedPage() {
        runBlocking {
            val doc = engine.open(asset("cropped.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    // CropBox [0 100 612 700] -> the visible page is 612 x 600 (#30).
                    page.addTextBox("Signed", 12.0, 80.0, 120.0, "text:crop")
                    val box = page.textBoxes().single()
                    assertEquals(80.0, box.rect.left, 2.0)
                    assertEquals(120.0, box.rect.bottom, 4.0)
                    assertTrue("must land inside the visible page, was ${box.rect}",
                        box.rect.top <= page.heightPoints)
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun annotsCanBeRemovedByIdAfterIndicesShift() {
        runBlocking {
            val doc = engine.open(asset("fixture.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    val square = page.detectCheckboxSquares().first()
                    page.addCheckMark(square, "mark:one")
                    page.addCheckMark(square, "mark:two")
                    page.removeAnnot("mark:one")   // shifts "mark:two" down one index

                    val ids = page.stamps().map { it.id }
                    assertNull(ids.firstOrNull { it == "mark:one" })
                    assertNotNull(ids.firstOrNull { it == "mark:two" })

                    page.removeAnnot("mark:two")
                    assertTrue(page.stamps().none { it.id.startsWith("mark:") })
                    // Removing something already gone must not throw.
                    page.removeAnnot("mark:two")
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }
}
