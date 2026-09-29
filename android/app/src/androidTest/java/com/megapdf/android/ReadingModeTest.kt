package com.megapdf.android

import android.graphics.Color
import androidx.compose.ui.test.click
import androidx.compose.ui.test.hasContentDescription
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.AndroidComposeTestRule
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performTouchInput
import androidx.test.ext.junit.rules.ActivityScenarioRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Reading mode on a device (#507, #513) — the half of it that only a real screen can show:
 * that the chrome is *not composed* rather than merely invisible, that a tap on the page
 * toggles the bar, that Back leaves the mode before it leaves the document, and that the
 * page colours actually reach the pixels.
 *
 * Every assertion here goes through the semantics tree, the way TalkBack and the QA rig read
 * the screen: a label that goes missing fails a test rather than a manual pass (#346).
 * `fetchSemanticsNodes().isEmpty()` is the load-bearing one — an alpha-0 or an
 * `IsVisible=false` bar is still a node with semantics and would still be found, so "no
 * node" is the only assertion that tells "not composed" from "composed and hidden".
 */
@RunWith(AndroidJUnit4::class)
class ReadingModeTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    private fun moreOptions() = hasContentDescription(str(R.string.more_options))
    private fun readingBar() = hasContentDescription(str(R.string.reading_controls))

    private fun enterReadingMode() {
        rule.menu(str(R.string.reading_mode))
        rule.waitFor(readingBar())
    }

    @Test
    fun readingModeTakesTheBarsOutOfCompositionAndBackBringsThemBack() {
        val file = Fixtures.demo("reading-chrome-507.pdf")
        rule.open(file)

        // The chrome is there to begin with: the top bar's overflow and Save, the bottom
        // bar's tools.
        rule.waitFor(moreOptions())
        assertTrue(rule.nodeExists(hasContentDescription(str(R.string.sign))))
        assertTrue(rule.nodeExists(hasContentDescription(str(R.string.undo))))
        assertTrue(rule.nodeExists(hasText(str(R.string.save))))

        enterReadingMode()

        // None of it is in the tree at all. This is the accessibility promise of the mode:
        // TalkBack's swipe navigation cannot reach a node that does not exist, where it
        // would happily land on an invisible one.
        rule.waitForGone(moreOptions())
        assertFalse("the top bar's overflow is still composed", rule.nodeExists(moreOptions()))
        assertFalse("Save is still composed", rule.nodeExists(hasText(str(R.string.save))))
        assertFalse(
            "the bottom bar's Sign is still composed",
            rule.nodeExists(hasContentDescription(str(R.string.sign))),
        )
        assertFalse(
            "the bottom bar's Undo is still composed",
            rule.nodeExists(hasContentDescription(str(R.string.undo))),
        )
        // Add text, not Search: the reading bar carries a magnifier of its own, labelled
        // with the same word because it opens the same find bar, so Search is no longer a
        // label that belongs to the bottom bar alone. CI caught this — the magnifier was
        // added after the test was written.
        assertFalse(
            "the bottom bar's Add text is still composed",
            rule.nodeExists(hasContentDescription(str(R.string.add_text))),
        )
        // The magnifier that replaced it is on the bar, where Find is reached from now.
        assertTrue(
            "reading mode has no way to reach Find",
            rule.nodeExists(hasContentDescription(str(R.string.search))),
        )

        // The page host is untouched: same page, still rendered.
        rule.waitForPage()

        rule.pressBack()

        rule.waitFor(moreOptions())
        assertTrue(rule.nodeExists(hasContentDescription(str(R.string.sign))))
        assertTrue(rule.nodeExists(hasContentDescription(str(R.string.undo))))
        assertFalse("the bar goes with the mode", rule.nodeExists(readingBar()))
    }

    @Test
    fun aTapOnThePageTogglesTheBar() {
        val file = Fixtures.demo("reading-tap-bar-507.pdf")
        rule.open(file)
        enterReadingMode()

        // The bar comes up with the mode. A tap now takes it away — and it has to be the tap
        // that did it, so the elapsed time is measured: the bound below is comfortably under
        // the ~2 s idle timeout, and that countdown started at or just before shownAt, so a
        // pass here cannot have been the idle fade.
        val shownAt = System.currentTimeMillis()
        rule.page().performTouchInput { click() }
        rule.waitForGone(readingBar())
        val elapsed = System.currentTimeMillis() - shownAt
        assertTrue(
            "the bar went after ${elapsed}ms, which is long enough that the idle fade rather " +
                "than the tap may have been what hid it",
            elapsed < 1_500L,
        )

        // Clear of the double-tap window, so the next tap is a tap and not a zoom gesture.
        Thread.sleep(400)

        // And a tap brings it back. Nothing but a tap can: the idle timer only ever hides.
        rule.page().performTouchInput { click() }
        rule.waitFor(readingBar())
    }

    @Test
    fun theBarHidesItselfWhenNothingHappens() {
        val file = Fixtures.demo("reading-idle-507.pdf")
        rule.open(file)
        enterReadingMode()
        // ~2 s of nothing and it is gone — and gone means out of the tree, not faded out.
        rule.waitForGone(readingBar())
    }

    @Test
    fun backLeavesReadingModeBeforeItAsksAboutUnsavedChanges() {
        // The risk the plan names (§7): Back on Android already asks about unsaved changes,
        // and a level added in front of it must not turn "give me the toolbar back" into a
        // Save/Discard/Cancel dialog.
        val file = Fixtures.demo("reading-back-507.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)

        // Make the document dirty, so the prompt is armed and ready to go off.
        rule.page().tapCentreOf(facts, facts.unmarkedSquare())
        rule.assertUndoEnabled()
        rule.waitForText("• " + file.name)
        assertTrue(rule.viewModel.isDirty)

        enterReadingMode()

        rule.pressBack()

        // First press: the chrome is back and nothing was asked.
        rule.waitFor(moreOptions())
        assertFalse(
            "the first Back out of reading mode asked about saving",
            rule.nodeExists(hasText(str(R.string.unsaved_changes))),
        )
        assertTrue("the edit is still there", rule.viewModel.isDirty)

        rule.pressBack()

        // Second press: this one is leaving the document, and this one asks.
        rule.waitForText(str(R.string.unsaved_changes))
    }

    @Test
    fun aPageTapInReadingModeCannotEditThePage() {
        // Outside reading mode this exact tap ticks a checkbox — EditingTest proves it does.
        val file = Fixtures.demo("reading-suppress-507.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        enterReadingMode()

        rule.page().tapCentreOf(facts, facts.unmarkedSquare())
        rule.waitForIdle()
        // Long enough for the edit to have gone in if it were going to: the tap dispatch is
        // deferred ~300 ms by the double-tap disambiguation, and the engine edit after that.
        Thread.sleep(1_500)

        assertFalse("a tap on the page edited the document in reading mode", rule.viewModel.isDirty)

        rule.pressBack()
        rule.waitFor(moreOptions())
        assertFalse("something reached the history", rule.undoButton().isEnabled())
    }

    @Test
    fun exitOnTheBarLeavesReadingMode() {
        val file = Fixtures.demo("reading-exit-507.pdf")
        rule.open(file)
        enterReadingMode()

        // The bar scrolls sideways on a narrow phone, so the last control may be off screen;
        // scrolling to it is what a finger would do.
        rule.onNodeWithContentDescription(str(R.string.exit_reading_mode))
            .performScrollTo()
            .performClick()

        rule.waitFor(moreOptions())
        assertFalse(rule.nodeExists(readingBar()))
    }

    @Test
    fun nightPageColoursReachThePixels() {
        val file = Fixtures.demo("reading-night-513.pdf")
        rule.open(file)

        // The setting is written to DataStore and outlives this run, so the test starts by
        // putting it where it expects to find it rather than trusting the last run.
        choosePageColours(R.string.page_colours_normal)
        rule.waitForPage()
        val before = pageCorner()
        assertTrue(
            "the corner of an unrendered or non-white page proves nothing; got $before",
            luminance(before) > 200,
        )

        choosePageColours(R.string.page_colours_night)
        // Only the pages on screen are re-rendered (#513), and page 1 is one of them.
        rule.waitUntil(SETTLE_MS) { luminance(pageCorner()) < 80 }
        val after = pageCorner()
        assertTrue(
            "night has to darken the page's own ground: $before became $after",
            luminance(after) < luminance(before) - 100,
        )

        // And back, which is the same rule running the other way — and leaves the setting as
        // this test found it.
        choosePageColours(R.string.page_colours_normal)
        rule.waitUntil(SETTLE_MS) { luminance(pageCorner()) > 200 }
    }

    /** Settings → Reading → Page colours → [labelId], and back to the document. */
    private fun choosePageColours(labelId: Int) {
        rule.menu(str(R.string.settings))
        rule.waitForText(str(R.string.settings_reading))
        // The trade-off is a decision, not a defect, and it belongs on the screen that makes
        // the choice (#168 decision 3) — so it is checked every time this walks past it.
        rule.waitForText(str(R.string.night_inverts_pictures))
        rule.clickText(str(labelId))
        rule.pressBack()
        rule.waitFor(moreOptions())
    }

    /** The page's top-left corner, which is margin on the demo agreement: the page's ground. */
    private fun pageCorner(): Int = rule.viewModel.pageBitmaps[0]?.getPixel(2, 2) ?: Color.BLACK

    private fun luminance(argb: Int): Int =
        (Color.red(argb) * 299 + Color.green(argb) * 587 + Color.blue(argb) * 114) / 1000
}

/**
 * The system Back button, pressed the way the OS presses it — through the dispatcher the
 * app's `BackHandler`s are registered on, which is the thing under test.
 */
fun AndroidComposeTestRule<ActivityScenarioRule<MainActivity>, MainActivity>.pressBack() {
    activityRule.scenario.onActivity { it.onBackPressedDispatcher.onBackPressed() }
    waitForIdle()
}
