package com.megapdf.android

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.test.assertIsNotSelected
import androidx.compose.ui.test.assertIsSelected
import androidx.compose.ui.test.click
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.swipe
import androidx.test.espresso.Espresso
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Whiteout move/resize chrome on the phone (#3): a drag places one, a tap selects it, and
 * from there it is the same drag-to-move, corner-grip-to-resize chrome a signature already
 * gets (`SelectionOverlay`) — the house style this app already had for "something the user
 * put on the page," rather than a second interaction model invented for this one.
 *
 * Android has no whiteout tool at all before this (#3's premise — "place-and-remove today" —
 * holds for the Mac/Linux pass this mirrors, not for this platform), so this covers the
 * whole lifecycle: arm, place, move, resize, remove, and Undo/Redo of each step. There is no
 * crash-replay to prove here: `EditHistory` is single-session by design (no recovery journal
 * on Android), unlike the desktop's journal-backed history.
 *
 * Deliberately reads no view-model state this feature is what would introduce
 * (`selectedWhiteout`/`whiteoutMode` do not exist before it): every assertion goes through
 * the semantics tree — the ✕ chip a selection carries, and where it sits — the way a finger
 * and a screen reader both find it, and the way the same test file runs both red against
 * today's viewer and green against tomorrow's.
 *
 * Undo and Redo always drop the current selection (`ViewerViewModel.afterHistoryChange` —
 * the same rule a moved signature or text box already lives under), so every checkpoint
 * after one re-selects by tapping the spot again, the way a person actually would, rather
 * than reading chrome that was never meant to survive it.
 */
@RunWith(AndroidJUnit4::class)
class WhiteoutLifecycleTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    /** Arms Whiteout from the ⋮ menu and drags the given box, then waits for its chrome. */
    private fun placeWhiteout(facts: PageFacts, from: Pair<Double, Double>, to: Pair<Double, Double>) {
        rule.menu(str(R.string.whiteout))
        val start = rule.page().pointAt(facts, from.first, from.second)
        val end = rule.page().pointAt(facts, to.first, to.second)
        rule.page().performTouchInput { swipe(start, end, durationMillis = 400) }
        // One drag, one whiteout — the same shape as Redact's own drag (#173). The chrome
        // (✕ to remove, a resize grip) is the only sign it landed: unlike a redaction mark,
        // a placed whiteout is real page content and draws nothing of its own beyond that.
        rule.waitForText("✕")
    }

    private fun chromePosition() = rule.onNodeWithText("✕").fetchSemanticsNode().positionInRoot

    private fun tap(pt: Offset) = rule.page().performTouchInput { click(pt) }

    private fun undo() = rule.clickLabelled(str(R.string.undo))
    private fun redo() = rule.clickLabelled(str(R.string.redo))

    @Test
    fun menuRowArmsAndSaysSo() {
        val file = Fixtures.demo("whiteout-menu-3.pdf")
        rule.open(file)
        val whiteout = str(R.string.whiteout)

        rule.clickLabelled(str(R.string.more_options))
        rule.waitForText(whiteout)
        rule.onNodeWithText(whiteout).assertIsNotSelected()
        rule.onNodeWithText(whiteout).performClick()

        // Armed, the row carries the check mark's state for a screen reader, the same pair
        // Redact's own row carries (#173): the words and the `selected` trait.
        rule.clickLabelled(str(R.string.more_options))
        rule.waitForText(whiteout)
        rule.onNodeWithText(whiteout).assertIsSelected()
        Espresso.pressBack()
        rule.waitForGone(hasText(whiteout))
    }

    @Test
    fun placingOneIsOneUndoStep() {
        val file = Fixtures.demo("whiteout-place-3.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        assertFalse(rule.undoButton().isEnabled())
        assertFalse(rule.viewModel.isDirty)

        placeWhiteout(facts, 60.0 to 650.0, 260.0 to 560.0)

        // Unlike a redaction mark, a whiteout is real page content the moment it lands, so
        // the document is dirty at once (#3) rather than staying clean until a separate
        // apply step (#173's own rule for marks, which does not apply here).
        rule.assertUndoEnabled()
        rule.waitForText("• " + file.name)
        assertTrue(rule.viewModel.isDirty)

        undo()
        rule.waitUntil(SETTLE_MS) { !rule.viewModel.canUndo }
        rule.waitForGone(hasText("✕"))
    }

    @Test
    fun draggingTheSelectionMovesItAndUndoRedoTheMove() {
        val file = Fixtures.demo("whiteout-move-3.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        // Its centre is well clear of the ✕ and the resize grip in the chrome's corners, so
        // a drag there lands on the body — exactly like dragging a selected signature.
        placeWhiteout(facts, 60.0 to 650.0, 260.0 to 560.0)
        val bodyPt = rule.page().pointAt(facts, 160.0, 605.0)
        val droppedPt = bodyPt + Offset(0f, 200f)

        val before = chromePosition()
        rule.page().performTouchInput { swipe(bodyPt, droppedPt, durationMillis = 400) }
        // The commit briefly drops the selection before re-establishing it at the new
        // bounds (`commitWhiteoutRect`, same as a moved signature): waiting for the chip to
        // exist first keeps that gap from throwing out of `waitUntil`'s condition.
        rule.waitForText("✕")
        rule.waitUntil(SETTLE_MS) { chromePosition().y > before.y + 20f }

        // Undo drops the selection before it puts anything back (`afterHistoryChange`, the
        // same rule a moved signature or text box lives under) — waiting for the ✕ to go is
        // what guarantees the revert itself has actually finished before the next tap lands,
        // rather than racing a still-in-flight undo. Tapping the original spot then reselects
        // it there.
        undo()
        rule.waitForGone(hasText("✕"))
        tap(bodyPt)
        // Waits for the chip to exist before reading where it is: `waitUntil`'s condition
        // aborts on the first exception rather than retrying it, and reading a node's
        // position before the node exists throws — so the existence check has to be its own,
        // retry-safe step first (`waitForText`/`nodeExists` never throw on "not yet").
        rule.waitForText("✕")
        rule.waitUntil(SETTLE_MS) { kotlin.math.abs(chromePosition().y - before.y) < 20f }

        // Redo drops it again first, the same way; tapping where it was dropped reselects it there.
        redo()
        rule.waitForGone(hasText("✕"))
        tap(droppedPt)
        rule.waitForText("✕")
        rule.waitUntil(SETTLE_MS) { chromePosition().y > before.y + 20f }
    }

    @Test
    fun theCornerGripResizesWithAspectUnlocked() {
        val file = Fixtures.demo("whiteout-resize-3.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        placeWhiteout(facts, 60.0 to 650.0, 200.0 to 590.0)
        val centre = rule.page().pointAt(facts, 130.0, 620.0)

        val before = chromePosition()
        // The grip sits at the selection's own bottom-right corner (#347's touch-target
        // convention, already used for a signature's own grip); dragging it further out
        // grows the rectangle rightward, which the ✕ badge — anchored to the top-right
        // corner — follows. Aspect is unlocked (#3), unlike a signature: a whiteout is a
        // rectangle by nature, not an image with a shape to keep.
        val corner = rule.page().pointAt(facts, 200.0, 590.0)
        val grown = rule.page().pointAt(facts, 260.0, 530.0)
        rule.page().performTouchInput { swipe(corner, grown, durationMillis = 400) }
        rule.waitForText("✕")
        rule.waitUntil(SETTLE_MS) { chromePosition().x > before.x + 20f }

        // Undo drops the selection before it shrinks anything back — waiting for the ✕ to go
        // first guarantees the revert has actually finished. The original centre, still
        // inside the un-resized rectangle (never the grown one the corner drag moved out
        // from under it), then reselects it.
        undo()
        rule.waitForGone(hasText("✕"))
        tap(centre)
        rule.waitForText("✕")
        rule.waitUntil(SETTLE_MS) { kotlin.math.abs(chromePosition().x - before.x) < 20f }
    }

    @Test
    fun removingItAndUndoingBringsTheChromeBack() {
        val file = Fixtures.demo("whiteout-remove-3.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        placeWhiteout(facts, 60.0 to 650.0, 260.0 to 560.0)
        val centre = rule.page().pointAt(facts, 160.0, 605.0)

        // The ✕ at its centre (#347 — the whole 48 dp box takes the tap, not the glyph).
        rule.onNodeWithText("✕").performClick()
        rule.waitForGone(hasText("✕"))

        // Undo restores it. There is no ✕ to wait to disappear here (removing it already took
        // the chrome away), so canRedo — set only once the revert has actually run — is what
        // guarantees the object is back before the next tap lands. Tapping where it was then
        // reselects it.
        undo()
        rule.waitUntil(SETTLE_MS) { rule.viewModel.canRedo }
        tap(centre)
        rule.waitForText("✕")

        // Redo removes it again, the same way; the same tap now finds nothing.
        redo()
        rule.waitUntil(SETTLE_MS) { !rule.viewModel.canRedo }
        tap(centre)
        rule.waitForGone(hasText("✕"))
    }
}
