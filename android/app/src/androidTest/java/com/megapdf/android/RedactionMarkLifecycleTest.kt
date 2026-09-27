package com.megapdf.android

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.test.assertIsNotSelected
import androidx.compose.ui.test.assertIsSelected
import androidx.compose.ui.test.hasContentDescription
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.swipe
import androidx.test.espresso.Espresso
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.megapdf.engine.PdfRect
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Ignore
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The mark lifecycle on the phone (#329), through the app's own wiring rather than the
 * core's: the ⋮ row that arms Redact (#328), the drag that places a mark, the tap that
 * selects it, the drag that moves it, the ✕ that removes it (#347) — and Undo taking each
 * of those back, one step per gesture, with Clear all marks as one step for every mark.
 */
@RunWith(AndroidJUnit4::class)
class RedactionMarkLifecycleTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    private val markName get() = str(R.string.redact_mark_name)
    private val marks get() = rule.viewModel.redactionMarks[0].orEmpty()

    /** Arms Redact from the ⋮ menu and drags along [line], then waits for the mark to land. */
    private fun markLine(facts: PageFacts, line: com.megapdf.engine.TextLine): PdfRect {
        val countBefore = marks.size
        rule.menu(str(R.string.redact))
        rule.waitUntil(SETTLE_MS) { rule.viewModel.redactMode }
        val y = line.rect.centerY
        val start = rule.page().pointAt(facts, line.rect.left + 2, y)
        val end = rule.page().pointAt(facts, line.rect.right - 2, y)
        rule.page().performTouchInput { swipe(start, end, durationMillis = 400) }
        // One drag, one history entry, and the tool disarms itself once the mark is placed.
        rule.waitUntil(SETTLE_MS) { marks.size > countBefore }
        rule.waitUntil(SETTLE_MS) { !rule.viewModel.redactMode }
        return marks.last().rect
    }

    @Test
    fun menuRowArmsAndSaysSo() {
        val file = Fixtures.demo("redact-346.pdf")
        rule.open(file)
        val redact = str(R.string.redact)

        rule.clickLabelled(str(R.string.more_options))
        rule.waitForText(redact)
        rule.onNodeWithText(redact).assertIsNotSelected()
        rule.onNodeWithText(redact).performClick()
        rule.waitUntil(SETTLE_MS) { rule.viewModel.redactMode }

        // Armed, the row carries the check mark's state for a screen reader (#328).
        rule.clickLabelled(str(R.string.more_options))
        rule.waitForText(redact)
        rule.onNodeWithText(redact).assertIsSelected()
        Espresso.pressBack()
        rule.waitForGone(hasText(redact))
        assertTrue(rule.viewModel.redactMode)
    }

    /** Places a mark on the paragraph's last line and selects it: the chrome with its ✕ is up. */
    private fun placeAndSelect(facts: PageFacts): PdfRect {
        val placed = markLine(facts, facts.line("described in sections"))
        rule.waitFor(hasContentDescription(markName))
        rule.onNodeWithContentDescription(markName).performClick()
        rule.waitForText("✕")
        assertEquals(marks.first().markId, rule.viewModel.selectedRedactionMark?.markId)
        return placed
    }

    /**
     * Drags the selected mark down by four of its own heights and waits for the commit. The
     * chrome sits over the mark, so a drag on the mark's node lands on the chrome's drag
     * detector, exactly as a finger's would.
     */
    private fun moveSelectedMarkDown(placed: PdfRect): PdfRect {
        val markNode = rule.onNodeWithContentDescription(markName)
        val dropPx = markNode.fetchSemanticsNode().size.height * 4f
        markNode.performTouchInput { swipe(center, center + Offset(0f, dropPx), durationMillis = 400) }
        rule.waitUntil(SETTLE_MS) { marks.firstOrNull()?.rect?.top?.let { !it.near(placed.top) } == true }
        val moved = marks.first().rect
        assertTrue("moved down the page: ${moved.top} < ${placed.top}", moved.top < placed.top)
        assertEquals(placed.top - placed.bottom, moved.top - moved.bottom, 0.5)
        return moved
    }

    private fun Double.near(other: Double) = kotlin.math.abs(this - other) < 0.5

    private fun undo() = rule.clickLabelled(str(R.string.undo))
    private fun redo() = rule.clickLabelled(str(R.string.redo))

    @Test
    fun markSelectMoveRemoveAndUndoEachStep() {
        val file = Fixtures.demo("redact-346.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        assertFalse(rule.undoButton().isEnabled())

        // Place: one drag, one mark, one history entry.
        val placed = placeAndSelect(facts)
        rule.waitFor(hasContentDescription(str(R.string.redact_mark_count_one)))
        rule.assertUndoEnabled()
        // A mark leaves the document clean (#173): the title has no dot, yet Save is offered.
        rule.waitForText(file.name)
        assertFalse(rule.viewModel.isDirty)
        assertTrue(rule.onNodeWithText(str(R.string.save)).isEnabled())

        // Move, and Undo/Redo the move: back where it was drawn, then back where it was dropped.
        val moved = moveSelectedMarkDown(placed)
        undo()
        rule.waitUntil(SETTLE_MS) { marks.firstOrNull()?.rect?.top?.near(placed.top) == true }
        redo()
        rule.waitUntil(SETTLE_MS) { marks.firstOrNull()?.rect?.top?.near(moved.top) == true }

        // Remove: the ✕ at its centre (#347 — the whole 48 dp box takes the tap, not the glyph).
        rule.waitForText("✕")
        rule.onNodeWithText("✕").performClick()
        rule.waitUntil(SETTLE_MS) { marks.isEmpty() }
        rule.waitForGone(hasContentDescription(markName))
        assertEquals(0, rule.viewModel.redactionMarkCount)

        // Undo the removal: the mark is back exactly where it was; Redo takes it off again.
        undo()
        rule.waitUntil(SETTLE_MS) { marks.size == 1 }
        assertEquals(moved.top, marks.first().rect.top, 0.5)
        rule.waitFor(hasContentDescription(markName))
        redo()
        rule.waitUntil(SETTLE_MS) { marks.isEmpty() }
        rule.waitForGone(hasContentDescription(markName))
    }

    /**
     * Undo all the way back through a removal (#429): the mark comes back from the removal
     * with a new id, so the move recorded before it cannot find the mark to put it back, and
     * that Undo does nothing. Red until #429 is fixed; take the annotation off with the fix.
     */
    @Test
    @Ignore("#429: the move's undo names a mark id the removal's undo no longer has")
    fun undoAfterARemovalTakesBackTheMoveBeforeIt() {
        val file = Fixtures.demo("redact-346.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        val placed = placeAndSelect(facts)
        val moved = moveSelectedMarkDown(placed)
        rule.onNodeWithText("✕").performClick()
        rule.waitUntil(SETTLE_MS) { marks.isEmpty() }

        undo()   // the removal
        rule.waitUntil(SETTLE_MS) { marks.size == 1 && marks.first().rect.top.near(moved.top) }
        undo()   // the move
        rule.waitUntil(SETTLE_MS) { marks.firstOrNull()?.rect?.top?.near(placed.top) == true }
        undo()   // the placement
        rule.waitUntil(SETTLE_MS) { marks.isEmpty() }
        assertFalse(rule.viewModel.canUndo)
    }

    @Test
    fun clearAllMarksIsOneUndoStep() {
        val file = Fixtures.demo("redact-346.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        val clear = str(R.string.redact_clear_marks)

        // No marks, no row (#329).
        rule.clickLabelled(str(R.string.more_options))
        rule.waitForText(str(R.string.redact))
        assertFalse(rule.nodeExists(hasText(clear)))
        Espresso.pressBack()
        rule.waitForGone(hasText(str(R.string.redact)))

        markLine(facts, facts.line("described in sections"))
        markLine(facts, facts.line("customer named"))
        val two = marks.size
        assertTrue("two drags, at least two marks: $two", two >= 2)
        rule.waitFor(hasContentDescription(str(R.string.redact_mark_count, two.toString())))

        rule.menu(clear)
        rule.waitUntil(SETTLE_MS) { marks.isEmpty() }
        rule.waitForGone(hasContentDescription(markName))

        // One Undo brings every mark back, and the row with them.
        rule.clickLabelled(str(R.string.undo))
        rule.waitUntil(SETTLE_MS) { marks.size == two }
        rule.clickLabelled(str(R.string.more_options))
        rule.waitForText(clear)
        Espresso.pressBack()
        rule.waitForGone(hasText(clear))
    }
}
