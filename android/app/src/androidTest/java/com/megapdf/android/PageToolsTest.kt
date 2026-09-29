package com.megapdf.android

import android.app.Activity
import android.app.Instrumentation
import android.content.Intent
import android.net.Uri
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.hasContentDescription
import androidx.compose.ui.test.hasSetTextAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import androidx.test.espresso.intent.Intents
import androidx.test.espresso.intent.Intents.intended
import androidx.test.espresso.intent.Intents.intending
import androidx.test.espresso.intent.matcher.IntentMatchers.hasAction
import androidx.test.espresso.intent.matcher.IntentMatchers.hasExtra
import androidx.test.espresso.intent.matcher.IntentMatchers.hasType
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.hamcrest.CoreMatchers.allOf
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The page tools on a device (#174): the Pages grid, and rotate, delete, reorder, combine, extract
 * and insert driven through it the way a person would, against the real core.
 *
 * The fixtures give every page a width of its own ([TestPdfs.multiPage]), so the page *order* is
 * readable straight off the view model — which is what nearly every assertion here is about. A
 * test that could only say "there are three pages now" would pass on a delete that took the wrong
 * one.
 *
 * Two of these are about limits rather than features, and they are the reason this file exists at
 * all rather than a unit test of the operations: what the person is *told* when the engine refuses
 * only exists on the screen. [aClashingFieldHierarchyIsRefusedInWordsAndNothingChanges] is the
 * 0.8%-of-the-corpus refusal, and [theLastPageCannotBeDeleted] is the one-page rule.
 */
@RunWith(AndroidJUnit4::class)
class PageToolsTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    @Before
    fun stubTheSystem() = Intents.init()

    @After
    fun releaseTheSystem() = Intents.release()

    private fun picked(uri: Uri) = Instrumentation.ActivityResult(Activity.RESULT_OK, Intent().setData(uri))

    /** The open document's page widths, which spell out which page is where. */
    private fun widths(): List<Int> =
        (rule.viewModel.uiState as ViewerUiState.Viewing).pageSizes.map { it.widthPoints.toInt() }

    private fun heights(): List<Int> =
        (rule.viewModel.uiState as ViewerUiState.Viewing).pageSizes.map { it.heightPoints.toInt() }

    private fun openPages(pageCount: Int) {
        rule.menu(str(R.string.pages))
        rule.waitForText(str(R.string.pages_count, pageCount))
    }

    /** Taps a page in the grid, selecting or deselecting it. Page numbers are 1-based, as shown. */
    private fun tapPage(number: Int) {
        rule.waitFor(hasContentDescription(str(R.string.page_n, number)))
        rule.onNodeWithContentDescription(str(R.string.page_n, number)).performClick()
    }

    private fun waitForWidths(expected: List<Int>) {
        rule.waitUntil(SETTLE_MS) { widths() == expected }
        assertEquals(expected, widths())
    }

    @Test
    fun theGridShowsEveryPageAndBackReturnsToTheDocument() {
        val file = Fixtures.written("pages-grid-174.pdf", TestPdfs.multiPage(4))
        rule.open(file)

        openPages(4)
        for (number in 1..4) {
            assertTrue(
                "page $number is missing from the grid",
                rule.nodeExists(hasContentDescription(str(R.string.page_n, number))),
            )
        }
        // The viewer's own chrome is not composed behind this screen, so a TalkBack swipe cannot
        // reach it — the rule Settings and reading mode already follow (#507).
        assertFalse(rule.nodeExists(hasText(str(R.string.save))))

        rule.pressBack()
        rule.waitFor(hasContentDescription(str(R.string.more_options)))
        assertTrue(rule.nodeExists(hasText(str(R.string.save))))
    }

    @Test
    fun rotatingAPageTurnsItAndUndoTurnsItBack() {
        val file = Fixtures.written("pages-rotate-174.pdf", TestPdfs.multiPage(3))
        rule.open(file)
        assertEquals(listOf(600, 610, 620), widths())
        assertEquals(listOf(792, 792, 792), heights())

        openPages(3)
        tapPage(2)
        rule.waitForText(str(R.string.pages_selected, 1))
        rule.onNodeWithContentDescription(str(R.string.rotate_right)).performClick()

        // A quarter turn swaps the page's reported size, because megapdf_page_width/height answer
        // the rotated size (contract 10) — which is also why the page list lays it out the new way
        // up with no rotation term of its own.
        waitForWidths(listOf(600, 792, 620))
        assertEquals(listOf(792, 610, 792), heights())
        assertTrue("turning a page is an edit", rule.viewModel.isDirty)

        rule.undoButton().performClick()
        waitForWidths(listOf(600, 610, 620))
    }

    @Test
    fun deletingASelectionIsOneUndoStepAndEveryPageComesBack() {
        val file = Fixtures.written("pages-delete-174.pdf", TestPdfs.multiPage(4))
        rule.open(file)

        openPages(4)
        tapPage(1)
        tapPage(3)
        rule.waitForText(str(R.string.pages_selected, 2))
        rule.onNodeWithContentDescription(str(R.string.pages_delete)).performClick()

        // The two selected pages went, and the two that were between and after them are what is
        // left — in order.
        waitForWidths(listOf(610, 630))
        rule.waitForText(str(R.string.pages_count, 2))

        rule.undoButton().performClick()
        // One press, all four back, in the order they were in: the pages themselves, kept by the
        // core for exactly this (`megapdf_page_restore`).
        waitForWidths(listOf(600, 610, 620, 630))
        assertFalse("one gesture is one undo step", rule.undoButton().isEnabled())
    }

    @Test
    fun theLastPageCannotBeDeleted() {
        // A PDF must keep a page, and the engine refuses to empty one (MEGAPDF_ERR_ARGUMENT). The
        // screen says so by not offering it, rather than by a dialog after the tap.
        val file = Fixtures.written("pages-last-174.pdf", TestPdfs.multiPage(2))
        rule.open(file)

        openPages(2)
        rule.menu(str(R.string.pages_select_all))
        rule.waitForText(str(R.string.pages_selected, 2))

        rule.onNodeWithContentDescription(str(R.string.pages_delete)).assertIsNotEnabled()
        assertEquals(listOf(600, 610), widths())
    }

    @Test
    fun moveToReordersOnePageAndUndoPutsItBack() {
        val file = Fixtures.written("pages-move-174.pdf", TestPdfs.multiPage(4))
        rule.open(file)

        openPages(4)
        tapPage(1)
        rule.waitForText(str(R.string.pages_selected, 1))
        rule.menu(str(R.string.pages_move_to))
        rule.waitForText(str(R.string.pages_move_to_body, 1))
        // The dialog's only field, found the way a field is: by being one.
        rule.onNode(hasSetTextAction()).performTextInput("3")
        rule.onNodeWithText(str(R.string.pages_move)).performClick()

        waitForWidths(listOf(610, 620, 600, 630))
        // The page that moved is still the selected one, wherever it went.
        rule.waitForText(str(R.string.pages_selected, 1))

        rule.undoButton().performClick()
        waitForWidths(listOf(600, 610, 620, 630))
    }

    @Test
    fun aBlankPageGoesInAfterTheSelection() {
        val file = Fixtures.written("pages-blank-174.pdf", TestPdfs.multiPage(3))
        rule.open(file)

        openPages(3)
        tapPage(2)
        rule.menu(str(R.string.pages_insert_blank))

        // The new page is the size of the page it follows, so it fits a document of mixed sizes.
        waitForWidths(listOf(600, 610, 610, 620))

        rule.undoButton().performClick()
        waitForWidths(listOf(600, 610, 620))
    }

    @Test
    fun addingPagesFromAnotherFileCombinesThemAndUndoTakesThemOut() {
        val file = Fixtures.written("pages-combine-174.pdf", TestPdfs.multiPage(3))
        val other = Fixtures.written("pages-other-174.pdf", TestPdfs.multiPage(2))
        rule.open(file)

        openPages(3)
        intending(hasAction(Intent.ACTION_OPEN_DOCUMENT)).respondWith(picked(Fixtures.uri(other)))
        rule.menu(str(R.string.pages_add_from_file))

        intended(hasAction(Intent.ACTION_OPEN_DOCUMENT))
        // Nothing selected, so they go at the end, in the order the other file has them.
        waitForWidths(listOf(600, 610, 620, 600, 610))
        assertTrue(rule.viewModel.isDirty)

        rule.undoButton().performClick()
        waitForWidths(listOf(600, 610, 620))

        // And a redo puts back the pages that arrived, rather than reading the file again — which
        // is why this is safe even though the picked file's cached copy is long gone.
        rule.onNodeWithContentDescription(str(R.string.redo)).performClick()
        waitForWidths(listOf(600, 610, 620, 600, 610))
    }

    @Test
    fun aClashingFieldHierarchyIsRefusedInWordsAndNothingChanges() {
        // The refusal #174 has to surface rather than hide: about 0.8% of a real corpus carries
        // form fields whose names live on a /Parent field, and a copy cannot rename one out of the
        // way of a name this document already has. The engine refuses the pages whole and changes
        // nothing; what the person is owed is a sentence saying so, which only the screen has.
        val form = Fixtures.written("pages-hierarchy-174.pdf", TestPdfs.parentFields())
        rule.open(form)

        openPages(1)
        // Into itself: every name it carries is a name the destination already has.
        intending(hasAction(Intent.ACTION_OPEN_DOCUMENT)).respondWith(picked(Fixtures.uri(form)))
        rule.menu(str(R.string.pages_add_from_file))

        rule.waitForText(str(R.string.pages_refused_title))
        assertTrue(
            "the refusal has to say what happened, not that something failed",
            rule.nodeExists(hasText(str(R.string.pages_refused_fields))),
        )
        rule.onNodeWithText(str(R.string.redact_ok)).performClick()

        // Nothing was added, nothing was turned, and the document is not even unsaved.
        rule.waitForGone(hasText(str(R.string.pages_refused_title)))
        assertEquals(1, widths().size)
        assertFalse("a refusal leaves the document exactly as it was", rule.viewModel.isDirty)
        assertFalse(rule.undoButton().isEnabled())
    }

    @Test
    fun savingTheSelectionWritesOnlyThosePagesAndLeavesTheDocumentAlone() {
        val file = Fixtures.written("pages-extract-174.pdf", TestPdfs.multiPage(4))
        rule.open(file)
        val out = Fixtures.empty("pages-extract-174-out.pdf")

        openPages(4)
        tapPage(1)
        tapPage(3)
        rule.waitForText(str(R.string.pages_selected, 2))
        intending(allOf(hasAction(Intent.ACTION_CREATE_DOCUMENT), hasType("application/pdf")))
            .respondWith(picked(Fixtures.uri(out)))
        rule.menu(str(R.string.pages_save_selection))

        // The name offered is the document's, marked as a copy of some of its pages — never the
        // document's own name (#409's rule for Save a copy, and the same reason).
        intended(allOf(
            hasAction(Intent.ACTION_CREATE_DOCUMENT),
            hasType("application/pdf"),
            hasExtra(Intent.EXTRA_TITLE, "pages-extract-174-pages.pdf"),
        ))
        rule.waitUntil(SETTLE_MS) { out.length() > 0 && !rule.viewModel.busy.locksDocument }

        assertEquals("only the pages chosen, in the order chosen", listOf(600.0, 620.0), TestPdfs.pageWidths(out))
        // An extract is a copy, not an edit: the document is untouched and there is nothing to undo.
        assertEquals(listOf(600, 610, 620, 630), widths())
        assertFalse(rule.viewModel.isDirty)
        assertFalse(rule.undoButton().isEnabled())
    }

    @Test
    fun undoAfterTakingBackADeleteStillTakesBackTheRotationBeforeIt() {
        // The shape #429 and #441 were both filed for, in the page tools: an operation recorded
        // before another one has to still act on the right page when its own turn to be undone
        // comes. Contract 10 says no index rewriting is needed for that — a history undone
        // backwards puts the numbering back as it goes — and this is that claim on a device.
        val file = Fixtures.written("pages-undo-order-174.pdf", TestPdfs.multiPage(4))
        rule.open(file)

        openPages(4)
        tapPage(3)
        rule.onNodeWithContentDescription(str(R.string.rotate_right)).performClick()
        waitForWidths(listOf(600, 610, 792, 630))

        tapPage(3)                                   // deselect it
        tapPage(1)
        rule.waitForText(str(R.string.pages_selected, 1))
        rule.onNodeWithContentDescription(str(R.string.pages_delete)).performClick()
        waitForWidths(listOf(610, 792, 630))

        rule.undoButton().performClick()             // the delete: page 1 comes back
        waitForWidths(listOf(600, 610, 792, 630))

        rule.undoButton().performClick()             // the rotation: off the page it went on
        waitForWidths(listOf(600, 610, 620, 630))
        assertEquals(listOf(792, 792, 792, 792), heights())
    }
}
