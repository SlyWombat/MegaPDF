package com.megapdf.android

import android.content.ClipboardManager
import android.content.Context
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.test.down
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.longClick
import androidx.compose.ui.test.moveBy
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.onAllNodesWithContentDescription
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.up
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Selecting a document's text and copying it, through the screen (#710), on `selection.pdf` (the
 * core's fixture, committed copy): page 1 is a title over two columns whose lines share baselines,
 * "Left one alpha" at (72..158, baseline 680) beside "Right one golf" at x=330. Long-press selects
 * the word; the end handle is dragged across into the right column; Copy puts the reading-order
 * text on the real system clipboard and it is read back from there.
 *
 * The floating toolbar is a system window that Compose's test rule cannot see, so Copy is invoked
 * through [ViewerViewModel.copySelectedText], which is exactly what the toolbar's Copy item calls.
 */
@RunWith(AndroidJUnit4::class)
class TextSelectCopyTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    private fun clipboardText(): String? {
        val clipboard = Fixtures.appContext.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        return clipboard.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.text?.toString()
    }

    private fun clearClipboard() {
        val clipboard = Fixtures.appContext.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        InstrumentationRegistry.getInstrumentation().runOnMainSync { clipboard.clearPrimaryClip() }
    }

    private fun longPressAlpha(facts: PageFacts) {
        val at = rule.page().pointAt(facts, 140.0, 684.0)
        rule.page().performTouchInput { longClick(at) }
        rule.waitUntil(SETTLE_MS) { rule.viewModel.textSelection != null }
    }

    @Test
    fun longPressSelectsTheWordADragCrossesTheColumnsAndCopyReachesTheClipboard() {
        val file = Fixtures.testAsset("selection.pdf", "select-copy-710.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        clearClipboard()
        assertNull(rule.viewModel.textSelection)

        longPressAlpha(facts)
        assertEquals("alpha", rule.viewModel.selectedTextForCopy())

        // Drag the end handle from the end of "alpha" to the end of "golf", the same line across
        // the gutter; the handle follows the finger by delta, so move by the distance between them.
        rule.waitUntil(SETTLE_MS) {
            rule.onAllNodesWithTag("selection-end-handle").fetchSemanticsNodes().isNotEmpty()
        }
        val from = rule.page().pointAt(facts, 158.0, 680.0)
        val to = rule.page().pointAt(facts, 414.0, 680.0)
        rule.onNodeWithTag("selection-end-handle").performTouchInput {
            down(center)
            moveBy(Offset(to.x - from.x, to.y - from.y), delayMillis = 100)
            up()
        }
        val expected =
            "alpha\nLeft two bravo and more\nLeft three charlie\nLeft four delta\nLeft five echo\nRight one golf"
        rule.waitUntilOrExplain(describe = { "selected: ${rule.viewModel.selectedTextForCopy()}" }) {
            rule.viewModel.selectedTextForCopy() == expected
        }

        assertTrue(rule.viewModel.copySelectedText())
        assertEquals(expected, clipboardText())
        assertNull("Copy releases the selection, as a text view does", rule.viewModel.textSelection)
    }

    /**
     * TalkBack's way to move a handle (#710): the handle is found by its spoken name and its custom
     * action is run by its spoken label, which is what TalkBack's actions menu does. The label and
     * the name come from the app's own resources, so a rename in any locale keeps the test honest.
     */
    private fun runHandleAction(handleName: String, label: String) {
        val node = rule.onNodeWithContentDescription(handleName).fetchSemanticsNode()
        val action = node.config[SemanticsActions.CustomActions].first { it.label == label }
        rule.runOnUiThread { assertTrue("$label did not move the handle", action.action()) }
        rule.waitForIdle()
    }

    @Test
    fun theEndHandleExtendsByAWordWithoutADragAndCopyHoldsOneMoreWord() {
        val file = Fixtures.testAsset("selection.pdf", "select-talkback-end-710.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        clearClipboard()
        longPressAlpha(facts)
        assertEquals("alpha", rule.viewModel.selectedTextForCopy())

        val end = Fixtures.appContext.getString(R.string.selection_end_handle)
        val next = Fixtures.appContext.getString(R.string.selection_next_word)
        val previous = Fixtures.appContext.getString(R.string.selection_previous_word)
        rule.waitUntil(SETTLE_MS) { rule.onAllNodesWithContentDescription(end).fetchSemanticsNodes().isNotEmpty() }

        // After "alpha" in reading order comes the next line's "Left", then "two".
        runHandleAction(end, next)
        assertEquals("alpha\nLeft", rule.viewModel.selectedTextForCopy())
        runHandleAction(end, next)
        assertEquals("alpha\nLeft two", rule.viewModel.selectedTextForCopy())
        runHandleAction(end, previous)
        assertEquals("alpha\nLeft", rule.viewModel.selectedTextForCopy())

        assertTrue(rule.viewModel.copySelectedText())
        assertEquals("alpha\nLeft", clipboardText())
    }

    @Test
    fun theStartHandleStepsBackAWordAndForwardAgain() {
        val file = Fixtures.testAsset("selection.pdf", "select-talkback-start-710.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        longPressAlpha(facts)

        val start = Fixtures.appContext.getString(R.string.selection_start_handle)
        val next = Fixtures.appContext.getString(R.string.selection_next_word)
        val previous = Fixtures.appContext.getString(R.string.selection_previous_word)
        rule.waitUntil(SETTLE_MS) { rule.onAllNodesWithContentDescription(start).fetchSemanticsNodes().isNotEmpty() }

        runHandleAction(start, previous)
        assertEquals("one alpha", rule.viewModel.selectedTextForCopy())
        runHandleAction(start, next)
        assertEquals("alpha", rule.viewModel.selectedTextForCopy())
    }

    @Test
    fun aTapLetsTheSelectionGoAndDoesNotEditWhatIsUnderIt() {
        val file = Fixtures.testAsset("selection.pdf", "select-tap-710.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        longPressAlpha(facts)

        rule.page().tapAt(facts, 140.0, 684.0)
        rule.waitUntil(SETTLE_MS) { rule.viewModel.textSelection == null }
        // The tap that cleared the selection did not also open the line editor on that text.
        rule.waitForIdle()
        assertNull(rule.viewModel.pendingBodyEdit)
        assertFalse(rule.viewModel.isDirty)
    }

    @Test
    fun longPressOnBlankPageDoesNothing() {
        val file = Fixtures.testAsset("selection.pdf", "select-blank-710.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        val at = rule.page().pointAt(facts, 560.0, 40.0)
        rule.page().performTouchInput { longClick(at) }
        rule.waitForIdle()
        Thread.sleep(500)
        assertNull(rule.viewModel.textSelection)
    }
}
