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

    /**
     * The anchor contract the app's edit and remove operations rest on (#36):
     * `addTextBox` places the text **baseline** on the point given, while
     * `textBoxes()` reports **bounds** and `moveTextBox` anchors bounds. So a box
     * moved to its own reported lower-left must not shift — if it does, undoing a
     * removal or a text correction would put the box back a descender too high.
     *
     * The text carries a descender ("g", "y") on purpose: without one the two
     * conventions coincide and the test proves nothing.
     */
    @Test
    fun movingABoxToItsOwnRectIsANoOp() {
        runBlocking {
            val doc = engine.open(asset("fixture.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    page.addTextBox("paging gravy", 12.0, 100.0, 300.0, "text:anchor")
                    val before = page.textBoxes().first { it.id == "text:anchor" }.rect
                    assertTrue("the fixture text must have a descender below the baseline",
                        before.bottom < 300.0)

                    page.moveTextBox("text:anchor", before.left, before.bottom)
                    val after = page.textBoxes().first { it.id == "text:anchor" }.rect
                    assertEquals(before.left, after.left, 0.01)
                    assertEquals(before.bottom, after.bottom, 0.01)

                    // And it must stay a no-op when repeated — the app normalizes
                    // through move on every re-add.
                    page.moveTextBox("text:anchor", after.left, after.bottom)
                    val third = page.textBoxes().first { it.id == "text:anchor" }.rect
                    assertEquals(before.left, third.left, 0.01)
                    assertEquals(before.bottom, third.bottom, 0.01)
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    /**
     * Correcting a typo (#36) is remove + re-add under the same id, anchored to
     * the old bounds lower-left. The width changes with the new text; the corner
     * the user placed does not, and reverting restores the original exactly.
     */
    @Test
    fun correctingTheTextKeepsTheBoxWhereItWas() {
        runBlocking {
            val doc = engine.open(asset("fixture.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    page.addTextBox("Jhon Smithy", 12.0, 100.0, 300.0, "text:typo")
                    val original = page.textBoxes().first { it.id == "text:typo" }.rect

                    suspend fun replaceWith(text: String) {
                        page.removeTextBox("text:typo")
                        page.addTextBox(text, 12.0, original.left, original.bottom, "text:typo")
                        page.moveTextBox("text:typo", original.left, original.bottom)
                    }

                    replaceWith("John Smithy")
                    val fixed = page.textBoxes().first { it.id == "text:typo" }
                    assertEquals("John Smithy", fixed.text)
                    assertEquals(original.left, fixed.rect.left, 0.01)
                    assertEquals(original.bottom, fixed.rect.bottom, 0.01)

                    // Undo: the same operation run with the old text.
                    replaceWith("Jhon Smithy")
                    val reverted = page.textBoxes().first { it.id == "text:typo" }
                    assertEquals("Jhon Smithy", reverted.text)
                    assertEquals(original.left, reverted.rect.left, 0.01)
                    assertEquals(original.bottom, reverted.rect.bottom, 0.01)
                    assertEquals("reverting must restore the original width",
                        original.right, reverted.rect.right, 0.5)
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    /**
     * #43: the face is carried on the mark, not inferred from the font resource —
     * pdfium is free to normalise a standard font's reported name, so the only
     * thing that can be a cross-platform contract is what we wrote down.
     */
    @Test
    fun theChosenFaceAndSizeSurviveSaveAndReopen() {
        runBlocking {
            val doc = engine.open(asset("fixture.pdf"))
            val saved: ByteArray
            try {
                val page = doc.openPage(0)
                try {
                    page.addTextBox("Eighteen point Times", 18.0, 100.0, 300.0,
                                    "text:serif", fontName = "Times-Roman")
                    page.addTextBox("Twelve point Courier", 12.0, 100.0, 250.0,
                                    "text:mono", fontName = "Courier")
                    val boxes = page.textBoxes().associateBy { it.id }
                    assertEquals("Times-Roman", boxes.getValue("text:serif").fontName)
                    assertEquals(18.0, boxes.getValue("text:serif").fontSize, 0.5)
                    assertEquals("Courier", boxes.getValue("text:mono").fontName)
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
                    val boxes = page.textBoxes().associateBy { it.id }
                    assertEquals(2, boxes.size)
                    assertEquals("Times-Roman", boxes.getValue("text:serif").fontName)
                    assertEquals(18.0, boxes.getValue("text:serif").fontSize, 0.5)
                    assertEquals("Courier", boxes.getValue("text:mono").fontName)
                } finally {
                    page.close()
                }
            } finally {
                reopened.close()
            }
        }
    }

    /**
     * Deliberately strict: the app passes one of three constants, so anything else
     * is a bug and should fail loudly rather than silently render in the wrong face.
     */
    @Test
    fun aFaceOutsideTheThreeIsRejected() {
        runBlocking {
            val doc = engine.open(asset("fixture.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    var threw = false
                    try {
                        page.addTextBox("Nope", 12.0, 100.0, 300.0, "text:bad",
                                        fontName = "Comic Sans MS")
                    } catch (e: IllegalArgumentException) {
                        threw = true
                    }
                    assertTrue("an unsupported face must be rejected", threw)
                    assertTrue("and must not leave a box behind", page.textBoxes().isEmpty())
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
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

    /**
     * Interop: a box written by another platform must read back here. The
     * fixture carries the marked-content section the engines write, not one this
     * platform produced.
     */
    @Test
    fun readsATextBoxWrittenElsewhere() {
        runBlocking {
            val doc = engine.open(asset("textbox.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    val boxes = page.textBoxes()
                    assertEquals("ordinary body text must not read as a box", 4, boxes.size)
                    assertEquals("Fixture text box", boxes[0].text)
                    assertEquals("text:fixture-1", boxes[0].id)
                    assertEquals("a box with no recorded face is Helvetica",
                        DEFAULT_FONT, boxes[0].fontName)

                    // The box that chose its face and size (#43).
                    val times = boxes.first { it.id == "text:fixture-times" }
                    assertEquals("Eighteen point Times", times.text)
                    assertEquals("Times-Roman", times.fontName)
                    assertEquals(18.0, times.fontSize, 0.5)

                    // The two marked-but-unidentified boxes are what MegaPDF for
                    // Windows wrote before it stamped ids. They must be told
                    // apart: one shared id would let a remove delete an
                    // arbitrary one.
                    val legacy = boxes.filter { it.id.startsWith("text:untagged#") }
                    assertEquals("untagged boxes are Helvetica too",
                        listOf(DEFAULT_FONT, DEFAULT_FONT), legacy.map { it.fontName })
                    assertEquals(2, legacy.size)
                    assertEquals("untagged boxes must not share an id",
                        2, legacy.map { it.id }.toSet().size)
                    assertEquals(listOf("Legacy box one", "Legacy box two"),
                        legacy.map { it.text })
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    /**
     * Removing one untagged box must leave the other alone — the failure this
     * guards against is a shared handle resolving to whichever came first.
     */
    @Test
    fun removingOneUntaggedBoxLeavesTheOther() {
        runBlocking {
            val doc = engine.open(asset("textbox.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    val target = page.textBoxes().first { it.text == "Legacy box one" }
                    page.removeTextBox(target.id)

                    val after = page.textBoxes()
                    assertEquals("only the targeted box goes", 3, after.size)
                    assertTrue("the other untagged box must survive",
                        after.any { it.text == "Legacy box two" })
                    assertTrue(after.none { it.text == "Legacy box one" })
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
