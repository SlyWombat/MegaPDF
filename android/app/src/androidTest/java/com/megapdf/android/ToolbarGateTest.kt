package com.megapdf.android

import android.app.Activity
import android.app.Instrumentation
import android.content.Intent
import android.net.Uri
import androidx.compose.ui.test.hasContentDescription
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.performClick
import androidx.test.espresso.intent.Intents
import androidx.test.espresso.intent.Intents.intended
import androidx.test.espresso.intent.Intents.intending
import androidx.test.espresso.intent.matcher.IntentMatchers.hasAction
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * One invariant, on the toolbar as a whole: **a command the toolbar offers is a command the view
 * model will act on.**
 *
 * It is not a timing test, deliberately. The defect it exists for was two predicates that mostly
 * agreed: the controls greyed out on `ViewerViewModel.toolsDisabled`
 * (`locksDocument || page.isVisible`) while the actions were dropped on
 * [ViewerViewModel.editingBlocked] (`editsInFlight > 0 || pageRewriteDeciding || locksDocument`).
 * Nothing was wrong at any single instant anybody looked at; the two simply came apart for a
 * few hundred milliseconds after every edit, and a person who tapped Undo in that window saw
 * nothing happen — which is reported as lost work, not as a flicker. #145 accepted that in
 * exchange for a toolbar that does not blink; Dave reversed it for 2.2 on the grounds that
 * silently discarding a tap is the worse of the two.
 *
 * Where the two predicates come apart is not a knife-edge, which is what makes this testable at
 * all: a page operation driven from the Pages screen runs under
 * [ViewerViewModel.performPageEdit], whose busy token neither locks the document nor pins a page
 * spinner for its first half-second. So for that half-second `editingBlocked` is true while the
 * old `toolsDisabled` was false, and the toolbar offered an Undo that `launchEdit` would throw
 * away. A 5,000-page import holds it open for roughly a second.
 */
@RunWith(AndroidJUnit4::class)
class ToolbarGateTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    @Before
    fun stubTheSystem() = Intents.init()

    @After
    fun releaseTheSystem() = Intents.release()

    private fun picked(uri: Uri) = Instrumentation.ActivityResult(Activity.RESULT_OK, Intent().setData(uri))

    /** What the toolbar is offering right now, as a person would read it. */
    private fun offered(): List<String> = buildList {
        if (rule.undoButton().isEnabled()) add("Undo")
        if (rule.onNodeWithContentDescription(str(R.string.redo)).isEnabled()) add("Redo")
    }

    @Test
    fun theToolbarNeverOffersACommandTheViewModelWouldRefuse() {
        val file = Fixtures.written("toolbar-gate-host.pdf", TestPdfs.multiPage(3))
        val other = Fixtures.written("toolbar-gate-other.pdf", TestPdfs.multiPage(IMPORT_PAGE_COUNT))
        rule.open(file)

        rule.menu(str(R.string.pages))
        rule.waitForText(str(R.string.pages_count, 3))

        // Two rotations and one undo, so both Undo and Redo have something to do: this test is
        // about the gate, and a button disabled merely because the history is empty would prove
        // nothing about it.
        rule.waitFor(hasContentDescription(str(R.string.page_n, 1)))
        rule.onNodeWithContentDescription(str(R.string.page_n, 1)).performClick()
        rule.waitForText(str(R.string.pages_selected, 1))
        rule.onNodeWithContentDescription(str(R.string.rotate_right)).performClick()
        rule.waitUntil(SETTLE_MS) { widths()[0] == 792 }
        rule.onNodeWithContentDescription(str(R.string.rotate_right)).performClick()
        rule.waitUntil(SETTLE_MS) { widths()[0] == 600 }
        rule.clickUndo()
        rule.waitUntil(SETTLE_MS) { widths()[0] == 792 }
        rule.waitUntil(SETTLE_MS) { rule.viewModel.canUndo && rule.viewModel.canRedo }

        // Now the long one. The picker is answered with a 5,000-page document, so the import
        // holds `editingBlocked` for about a second.
        intending(hasAction(Intent.ACTION_OPEN_DOCUMENT)).respondWith(picked(Fixtures.uri(other)))
        rule.menu(str(R.string.pages_add_from_file))
        intended(hasAction(Intent.ACTION_OPEN_DOCUMENT))

        // Read the two in step, over and over, for as long as the change is going in. Each
        // reading is one honest observation of "what is offered" against "what would be
        // accepted"; a single sample would be the kind of knife-edge check this file argues
        // against. The loop is bounded so a failure is a failure rather than a hang.
        val violations = ArrayList<String>()
        var sawBlocked = false
        for (round in 1..400) {
            val blocked = rule.viewModel.editingBlocked
            if (!blocked) {
                if (sawBlocked) break else continue
            }
            sawBlocked = true
            val offered = offered()
            if (offered.isNotEmpty()) {
                violations += "round $round: offered $offered while the view model would refuse it"
            }
        }

        assertTrue("the import never blocked editing, so this proved nothing", sawBlocked)
        assertEquals(emptyList<String>(), violations.take(3))

        // And the other half of the invariant, which matters as much: the gate opens again, and
        // what it then offers is *taken up* rather than discarded. "Accepted" is observable
        // without waiting for the work to finish — `launchEdit` either starts the job, which
        // raises `editingBlocked`, or drops the request and leaves it false.
        rule.waitUntil(SETTLE_MS) { !rule.viewModel.editingBlocked }
        rule.waitUntil(SETTLE_MS) { widths().size == 3 + IMPORT_PAGE_COUNT }
        rule.assertUndoEnabled()
        rule.undoButton().performClick()
        rule.waitUntil(SETTLE_MS) { rule.viewModel.editingBlocked || widths().size == 3 }

        // Then let it land. Its own timeout, not [SETTLE_MS]: taking back an import of
        // IMPORT_PAGE_COUNT pages is the largest single undo anywhere in this suite, and 20 s
        // is the budget for an ordinary engine call rather than for this.
        rule.waitUntil(UNDO_A_HUGE_IMPORT_MS) { widths().size == 3 }
    }

    private fun widths(): List<Int> =
        (rule.viewModel.uiState as ViewerUiState.Viewing).pageSizes.map { it.widthPoints.toInt() }

    companion object {
        /**
         * Big enough that the import holds `editingBlocked` for far longer than the loop above
         * takes to read the toolbar once — ~1 s, against the half-second in which the old
         * `toolsDisabled` disagreed with it. The fixture is content-light, so this is under
         * 1.5 MB on disk and opens in milliseconds (measured in #611).
         */
        private const val IMPORT_PAGE_COUNT = 5_000

        /** See the last wait in the test: the biggest single undo in the suite. */
        private const val UNDO_A_HUGE_IMPORT_MS = 90_000L
    }
}
