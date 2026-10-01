package com.megapdf.android

import androidx.compose.ui.test.click
import androidx.compose.ui.test.hasSetTextAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.performTouchInput
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Text box ergonomics on the phone (#4): the size chips already on the inline editor
 * (#43) plus a new note of more than one line, where the phone's own Enter key is the
 * gesture a desktop reads as Shift+Enter — there is no separate key to reserve for it on
 * a soft keyboard. Each line lands as its own ordinary, separately-selectable text box
 * (#34/#36), and Undo takes the whole note back as the one gesture that placed it.
 */
@RunWith(AndroidJUnit4::class)
class TextBoxMultilineTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    /**
     * Where the note is placed: further down the page than [EditingTest]'s own single-line
     * spot (72, 372) — safe for a tap that lands exactly there, but this test also has to tap
     * *above* a line's baseline to find it (see [findAndSelectLine]), and "Sign above the
     * line" — real body text on the demo page — sits only a handful of points above 372
     * (confirmed by running this file's own diagnostic against it: bounds (72, 386)-(148,
     * 395), which touch-slop grows to reach down past 380). A tap that misses a 12pt note's
     * own box by a hair can land on that line instead and open the body-text editor, which
     * (once committed by a later, unrelated click) is a second history entry undo doesn't
     * expect. Moved well clear of it rather than trimmed to fit around it.
     */
    private val noteXPt = 72.0
    private val noteYPt = 300.0

    /** Clicks Undo and waits for the history to actually empty, reporting what a swallowed
     *  failure (`launchEdit`'s catch-all) would otherwise leave silent — and, since
     *  `canRedo` alongside a stuck `canUndo` is the signature of a second, unwanted entry
     *  in the history, where the demo page's own drawn checkboxes sit, so a stray scan tap
     *  landing on one is a checkable explanation rather than a guess. */
    private fun undoAndWaitDone(facts: PageFacts) {
        rule.clickUndo()
        try {
            rule.waitUntil(SETTLE_MS) { !rule.viewModel.canUndo }
        } catch (e: androidx.compose.ui.test.ComposeTimeoutException) {
            throw AssertionError(
                "undo never finished: canUndo=${rule.viewModel.canUndo} " +
                    "canRedo=${rule.viewModel.canRedo} statusMessage=${rule.viewModel.statusMessage} " +
                    "checkboxSquares=${facts.checkboxSquares} checkMarks=${facts.checkMarks.map { it.rect }} " +
                    "allLines=${facts.lines.map { it.text to it.rect }} " +
                    "allStamps=${facts.stamps.map { it.id to it.rect }}",
                e,
            )
        }
    }

    private fun openAddTextDialog(facts: PageFacts) {
        rule.clickLabelled(str(R.string.add_text))
        val spot = rule.page().pointAt(facts, noteXPt, noteYPt)
        rule.page().performTouchInput { click(spot) }
        rule.waitForText(str(R.string.add_text_hint))
    }

    /**
     * Taps [xPt], [yPt] and waits until *that* box answers as selected — not merely until
     * some selection chrome exists, which a still-selected previous box would already
     * satisfy the instant a second tap lands. True on success, without asserting, so
     * [findAndSelectLine] can try another point when this one lands on the wrong box.
     */
    private fun tapAndWaitSelected(facts: PageFacts, xPt: Double, yPt: Double, expectedText: String): Boolean {
        rule.page().performTouchInput { click(rule.page().pointAt(facts, xPt, yPt)) }
        return try {
            rule.waitUntil(4_000L) { rule.viewModel.selectedTextBox?.text == expectedText }
            true
        } catch (e: androidx.compose.ui.test.ComposeTimeoutException) {
            false
        }
    }

    /**
     * Finds [expectedText] near [baselineY] by trying a handful of points above the baseline.
     *
     * A text run's glyph box sits mostly *above* its baseline, not straddling it — how far
     * above depends on the specific glyphs in that run (an "S" and an "l" don't reach the
     * same height), not a fixed fraction of the font size — and touch slop can pull in a
     * line stacked close enough below to answer for a point that missed its own box high or
     * low. Trying several candidate offsets is what a finger effectively does when the first
     * tap doesn't land the intended thing: try again, slightly differently, rather than the
     * app knowing in advance exactly which pixel is safe.
     *
     * Kept to a narrow band around the middle of a line's own height (not the wider 0.3–1.1
     * range this started as): a miss that strays far enough from every placed box can land on
     * the demo page's own content underneath — a drawn checkbox ticks itself rather than
     * leaving the tap unanswered — which stops this from being a search for the note at all.
     */
    private fun findAndSelectLine(facts: PageFacts, xPt: Double, baselineY: Double, fontSize: Double, expectedText: String) {
        val offsets = listOf(0.55, 0.7, 0.85)
        for (fraction in offsets) {
            if (tapAndWaitSelected(facts, xPt, baselineY + fontSize * fraction, expectedText)) return
        }
        throw AssertionError(
            "never found \"$expectedText\" near baseline $baselineY (font $fontSize) across " +
                "offsets $offsets; last selectedTextBox was ${rule.viewModel.selectedTextBox}",
        )
    }

    @Test
    fun twoLineNoteAtLargeBecomesTwoIndividuallySelectableBoxes() {
        val file = Fixtures.demo("multiline-4.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        assertFalse(rule.undoButton().isEnabled())

        openAddTextDialog(facts)
        // "Large" (#4's acceptance wording): one of the chips #43 already put on this dialog.
        // Size is the last of six in a horizontally-scrolling row (#43): on a narrow dialog
        // "24" can sit past the visible edge, and a synthetic click at its laid-out position
        // lands outside the clipped viewport and does nothing — scrolling it into view first
        // is what a finger swiping to it would do anyway.
        rule.onNodeWithText("24").performScrollTo().performClick()
        rule.onNode(hasSetTextAction()).performTextInput("First line\nSecond line")
        val add = str(R.string.add)
        rule.waitUntil(SETTLE_MS) { rule.onNodeWithText(add).isEnabled() }
        rule.onNodeWithText(add).performClick()

        // One gesture, one history entry — the whole note is one undo step.
        rule.continuePastPageRewriteWarning { rule.viewModel.canUndo }
        rule.waitForGone(hasText(str(R.string.add_text_hint)))
        rule.assertUndoEnabled()
        rule.waitForText("• " + file.name)

        // Each line stayed its own separately-selectable box, at the chosen (Large) size.
        val secondBaseline = noteYPt - 24.0 * 1.2
        findAndSelectLine(facts, noteXPt, noteYPt, 24.0, "First line")
        assertEquals(24.0, rule.viewModel.selectedTextBox?.fontSize)

        // The second line sits one line-height below the first (top to bottom, #4).
        findAndSelectLine(facts, noteXPt, secondBaseline, 24.0, "Second line")
        assertEquals(24.0, rule.viewModel.selectedTextBox?.fontSize)

        // Undo removes the whole note as one step: neither line answers a tap afterwards,
        // anywhere across the same span either line's box could have occupied.
        undoAndWaitDone(facts)
        for (fraction in listOf(0.55, 0.7, 0.85)) {
            rule.page().performTouchInput { click(rule.page().pointAt(facts, noteXPt, noteYPt + 24.0 * fraction)) }
            rule.waitUntil(SETTLE_MS) { rule.viewModel.selectedTextBox == null }
            rule.page().performTouchInput { click(rule.page().pointAt(facts, noteXPt, secondBaseline + 24.0 * fraction)) }
            rule.waitUntil(SETTLE_MS) { rule.viewModel.selectedTextBox == null }
        }
    }

    @Test
    fun blankLinesInTheMiddleOfANoteAreDropped() {
        // #4's "individually editable afterwards" is `twoLineNoteAt...`'s job, with a font
        // large enough that a tap can reliably tell two real lines of text apart (#4's size
        // chips are its own ask). This test's own job is narrower and does not depend on
        // that: a blank middle line contributes no box at all, so a note of "One / (blank) /
        // Two" is two objects, never three — proved by where the *third* object would be if
        // the blank had counted, which nothing answers, rather than by re-finding "One" and
        // "Two" themselves at the default 12pt size where their boxes sit close enough
        // together that telling the right one apart by a tap alone is its own small
        // engineering problem (see [findAndSelectLine]'s own doc comment).
        val file = Fixtures.demo("multiline-blank-4.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)

        openAddTextDialog(facts)
        rule.onNode(hasSetTextAction()).performTextInput("One\n\nTwo")
        val add = str(R.string.add)
        rule.waitUntil(SETTLE_MS) { rule.onNodeWithText(add).isEnabled() }
        rule.onNodeWithText(add).performClick()
        rule.continuePastPageRewriteWarning { rule.viewModel.canUndo }
        rule.assertUndoEnabled()

        // If the blank line had been kept, a third box would sit a further line-height below
        // "Two" (at the position "Two" itself would occupy a line later). Nothing answers a
        // tap there, because there is no third box — the blank line placed nothing at all.
        val wouldBeThirdLineBaseline = noteYPt - DEFAULT_FONT_SIZE * 1.2 * 2
        val probeY = wouldBeThirdLineBaseline + DEFAULT_FONT_SIZE * 0.7
        rule.page().performTouchInput { click(rule.page().pointAt(facts, noteXPt, probeY)) }
        rule.waitUntil(SETTLE_MS) { rule.viewModel.selectedTextBox == null }

        // Whatever the blank line did or didn't place, the whole note is one undo step.
        undoAndWaitDone(facts)
    }

    @Test
    fun correctingAnExistingBoxStaysSingleLine() {
        // Editing an existing box restyles it in place (#43) and does not grow into more
        // than one line even if a newline is typed (#4): a different shape of edit than
        // placing a new note, deliberately out of scope for this pass.
        val file = Fixtures.demo("multiline-edit-4.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)

        openAddTextDialog(facts)
        rule.onNode(hasSetTextAction()).performTextInput("Original")
        val add = str(R.string.add)
        rule.waitUntil(SETTLE_MS) { rule.onNodeWithText(add).isEnabled() }
        rule.onNodeWithText(add).performClick()
        rule.continuePastPageRewriteWarning { rule.viewModel.canUndo }

        findAndSelectLine(facts, noteXPt, noteYPt, DEFAULT_FONT_SIZE, "Original")
        rule.onNodeWithText("✎").performClick()
        rule.waitForText(str(R.string.edit_text_hint))

        // The field opened on the existing box is single-line: typing Enter would be a text
        // character with nowhere to go, not a request for a second box.
        assertTrue(rule.viewModel.pendingTextTap?.editingId != null)
    }
}
