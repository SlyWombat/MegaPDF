package com.megapdf.android

import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * A permission the document's author withheld, as an informed choice rather than a wall
 * (#558, ADR-004 decision 11).
 *
 * These are on a device because every part of what #558 decided only exists on the screen: that the
 * question comes up at all, that its words report a request rather than a lock, that Continue
 * actually gets the work done — which it can only do if the choice reached the C++ core, since the
 * core enforces the same bits underneath — that Cancel changes nothing, and that nobody is asked
 * twice. [PermissionOverrideTest] covers the grain of the remembering on the JVM.
 *
 * The fixture is [TestPdfs.multiPage] re-saved with an owner password and no permissions at all, so
 * it opens like any other document (no password is asked for) and allows nothing: the shape of a
 * real owner-restricted PDF, and the one the engine's own `owner-only.pdf` fixture has.
 */
@RunWith(AndroidJUnit4::class)
class PermissionPromptTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    private fun widths(): List<Int> =
        (rule.viewModel.uiState as ViewerUiState.Viewing).pageSizes.map { it.widthPoints.toInt() }

    private fun heights(): List<Int> =
        (rule.viewModel.uiState as ViewerUiState.Viewing).pageSizes.map { it.heightPoints.toInt() }

    /** A three-page document whose author asked that nothing be done to it. */
    private fun openRestricted(name: String) {
        rule.open(Fixtures.written(name, TestPdfs.restricted(TestPdfs.multiPage(3))))
        assertFalse(
            "the fixture has to be restricted, or none of this tests anything",
            rule.viewModel.capabilities.canAssemblePages,
        )
    }

    private fun openPages() {
        rule.menu(str(R.string.pages))
        rule.waitForText(str(R.string.pages_count, 3))
    }

    private fun tapPage(number: Int) {
        rule.onNodeWithContentDescription(str(R.string.page_n, number)).performClick()
        rule.waitForText(str(R.string.pages_selected, 1))
    }

    /** Selects page 2 in the grid and turns it a quarter turn clockwise. */
    private fun rotatePageTwo() {
        openPages()
        tapPage(2)
        rule.onNodeWithContentDescription(str(R.string.rotate_right)).performClick()
    }

    private fun waitForWidths(expected: List<Int>) {
        rule.waitUntil(SETTLE_MS) { widths() == expected }
        assertEquals(expected, widths())
    }

    @Test
    fun theQuestionSaysWhatTheAuthorAskedAndNeverCallsItALock() {
        openRestricted("permission-wording-558.pdf")

        // The tools are reachable: #131 greyed them out, which is the wall #558 replaced — a
        // disabled button cannot say what the author asked, and the person may be the author.
        rule.onNodeWithContentDescription(str(R.string.add_text)).performClick()

        rule.waitForText(str(R.string.permission_ask_title_editing))
        assertTrue(
            "the question has to say what was asked",
            rule.nodeExists(hasText(str(R.string.permission_ask_editing))),
        )
        assertTrue(
            "and that it is a request, and that it is asked once",
            rule.nodeExists(hasText(str(R.string.permission_ask_note))),
        )
        assertTrue("Continue is offered", rule.nodeExists(hasText(str(R.string.action_continue))))
        assertTrue("so is Cancel", rule.nodeExists(hasText(str(R.string.cancel))))

        // The old refusal must be gone from the screen, not merely joined by a question: it named
        // the owner password as the way through, and that is not what is needed here.
        assertFalse(
            "the refusal wording must not appear",
            rule.nodeExists(hasText(str(R.string.security_restricted_edit))),
        )
        // Nothing has happened yet — the tool is not armed behind the question.
        assertFalse("nothing is armed until the question is answered", rule.viewModel.isPlacingText)
    }

    @Test
    fun continuingGetsTheWorkDone() {
        openRestricted("permission-continue-558.pdf")
        assertEquals(listOf(600, 610, 620), widths())

        rotatePageTwo()
        rule.waitForText(str(R.string.permission_ask_title_assembly))
        rule.onNodeWithText(str(R.string.action_continue)).performClick()

        // A quarter turn swaps the page's reported size (contract 10). That it swapped at all is
        // the proof the choice reached the core: `PageToolsPreflight` would otherwise have answered
        // MEGAPDF_ERR_RESTRICTED and the refusal dialog would be up instead.
        waitForWidths(listOf(600, 792, 620))
        assertEquals(listOf(792, 610, 792), heights())
        assertTrue("turning a page is an edit", rule.viewModel.isDirty)
        assertFalse(
            "and no refusal was shown",
            rule.nodeExists(hasText(str(R.string.pages_refused_title))),
        )
    }

    @Test
    fun decliningLeavesTheDocumentExactlyAsItWas() {
        openRestricted("permission-cancel-558.pdf")

        rotatePageTwo()
        rule.waitForText(str(R.string.permission_ask_title_assembly))
        rule.onNodeWithText(str(R.string.cancel)).performClick()

        rule.waitForGone(hasText(str(R.string.permission_ask_title_assembly)))
        assertEquals("nothing turned", listOf(600, 610, 620), widths())
        assertEquals(listOf(792, 792, 792), heights())
        assertFalse("and there is nothing to save", rule.viewModel.isDirty)
        assertFalse("nor to undo", rule.undoButton().isEnabled())
    }

    @Test
    fun theSameRequestIsNotPutTwice() {
        openRestricted("permission-once-558.pdf")

        rotatePageTwo()
        rule.waitForText(str(R.string.permission_ask_title_assembly))
        rule.onNodeWithText(str(R.string.action_continue)).performClick()
        waitForWidths(listOf(600, 792, 620))

        // A second operation of the same kind, on the same document — the turned page is still the
        // selected one. A prompt per change would be nagging rather than consent, so the answer
        // already given stands.
        rule.onNodeWithContentDescription(str(R.string.pages_delete)).performClick()

        waitForWidths(listOf(600, 620))
        assertFalse(
            "the question must not come back for the same kind of work",
            rule.nodeExists(hasText(str(R.string.permission_ask_title_assembly))),
        )
    }

    @Test
    fun eachKindOfRequestIsAskedOnItsOwn() {
        openRestricted("permission-classes-558.pdf")

        rotatePageTwo()
        rule.waitForText(str(R.string.permission_ask_title_assembly))
        rule.onNodeWithText(str(R.string.action_continue)).performClick()
        waitForWidths(listOf(600, 792, 620))

        // Back out of the grid: the turned page is still selected, and the grid's own Back clears a
        // selection before it closes, so it is the two chrome buttons rather than two key presses.
        rule.clickLabelled(str(R.string.pages_clear_selection))
        rule.clickLabelled(str(R.string.pages_done))
        rule.waitFor(androidx.compose.ui.test.hasContentDescription(str(R.string.more_options)))

        // "Do not rearrange the pages" and "do not change the contents" are two things the author
        // said separately, and may well have meant differently — so a yes to one is not a yes to
        // the other, and the second one is asked in its own words.
        rule.onNodeWithContentDescription(str(R.string.add_text)).performClick()
        rule.waitForText(str(R.string.permission_ask_title_editing))
        assertTrue(
            rule.nodeExists(hasText(str(R.string.permission_ask_editing))),
        )
        assertFalse(
            "and it is the editing question, not the page one over again",
            rule.nodeExists(hasText(str(R.string.permission_ask_assembly))),
        )
    }
}
