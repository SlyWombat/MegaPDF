package com.megapdf.android

import android.app.Activity
import android.app.Instrumentation
import android.content.Intent
import android.net.Uri
import androidx.compose.ui.test.hasSetTextAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import androidx.test.espresso.intent.Intents
import androidx.test.espresso.intent.Intents.intended
import androidx.test.espresso.intent.Intents.intending
import androidx.test.espresso.intent.matcher.IntentMatchers.hasAction
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
 * #145's P2 half on Android: a name for what a structure operation is doing in the Pages
 * screen's strip rather than the generic "Applying…" (every one of them already reported
 * *somewhere* — Android never had the page-pinned-spinner defect #563/#568 found and fixed on
 * the desktops, because [ViewerViewModel.performPageEdit] already reports in the document strip,
 * never at a page index an operation might be about to remove), progress and Stop for search,
 * and Stop alone for extract.
 *
 * The fixtures here are large on purpose. A synthetic document's content-free pages are nothing
 * like the 2.5 GB, 1,000-picture fixture #563 measured shrink against, so crossing the busy
 * indicator's own 0.5 s show threshold — which every assertion below depends on — takes many
 * more pages than the handful #174's own [PageToolsTest] uses. These numbers were picked to
 * clear that bar comfortably on the slower, nested-virtualised emulator CI runs this on; see the
 * PR description for what was measured here.
 */
@RunWith(AndroidJUnit4::class)
class PageToolsProgressTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    @Before
    fun stubTheSystem() = Intents.init()

    @After
    fun releaseTheSystem() = Intents.release()

    private fun picked(uri: Uri) = Instrumentation.ActivityResult(Activity.RESULT_OK, Intent().setData(uri))

    private fun openPages(pageCount: Int) {
        rule.menu(str(R.string.pages))
        rule.waitForText(
            if (pageCount == 1) str(R.string.pages_count_one) else str(R.string.pages_count, pageCount),
        )
    }

    @Test
    fun combiningALotOfPagesNamesItselfInTheStripRatherThanTheGenericLabel() {
        val file = Fixtures.written("pages-progress-combine-145.pdf", TestPdfs.multiPage(2))
        val other = Fixtures.written("pages-progress-combine-other-145.pdf", TestPdfs.multiPage(HUGE_PAGE_COUNT))
        rule.open(file)

        openPages(2)
        intending(hasAction(Intent.ACTION_OPEN_DOCUMENT)).respondWith(picked(Fixtures.uri(other)))
        rule.menu(str(R.string.pages_add_from_file))
        intended(hasAction(Intent.ACTION_OPEN_DOCUMENT))

        // Named for what it is doing, not "Applying…" — this is the whole point of the change:
        // caught on the view model directly, which does not depend on the strip's own 0.5 s
        // delay or a screen redraw, only on the import itself still being in flight.
        rule.waitUntil(SETTLE_MS) { rule.viewModel.busy.document.label == BusyLabel.ADDING_PAGES }
        // And the same thing, on the real screen: the strip shows the specific string, not the
        // generic "busy_applying" one every structure operation used before this change.
        rule.waitForText(str(R.string.busy_adding_pages))
        assertFalse(
            "the generic label should not be what the strip shows for a page tool any more",
            rule.nodeExists(hasText(str(R.string.busy_applying))),
        )

        rule.waitUntil(SETTLE_MS) {
            (rule.viewModel.uiState as? ViewerUiState.Viewing)?.pageSizes?.size == 2 + HUGE_PAGE_COUNT
        }
    }

    @Test
    fun extractingALotOfPagesCanBeStoppedAndLeavesNothingBehind() {
        // multiPage's pages are a handful of bytes each, which is the wrong shape for this test:
        // reading a page's *size* (what opening a document does, for every page) only reads its
        // MediaBox, but writing a page out (what extract does, once, for every selected page) has
        // to carry its content — so a content-light fixture lets the one call finish before the
        // busy indicator's 0.5 s threshold however many pages it has (found running this on CI:
        // 5,000 content-light pages opened and extracted in well under a second put together).
        // multiPageBulky's padding inflates the second cost without inflating the first. Written
        // straight to the file (rather than built as a ByteArray and handed to Fixtures.written,
        // as every other fixture here is) because this one is tens of megabytes.
        val file = Fixtures.empty("pages-progress-extract-145.pdf")
        TestPdfs.multiPageBulky(file, EXTRACT_PAGE_COUNT, EXTRACT_PAGE_BYTES)
        rule.open(file)
        val out = Fixtures.empty("pages-progress-extract-145-out.pdf")

        openPages(EXTRACT_PAGE_COUNT)
        rule.menu(str(R.string.pages_select_all))
        rule.waitForText(str(R.string.pages_selected, EXTRACT_PAGE_COUNT))
        intending(allOf(hasAction(Intent.ACTION_CREATE_DOCUMENT), hasType("application/pdf")))
            .respondWith(picked(Fixtures.uri(out)))
        rule.menu(str(R.string.pages_save_selection))
        intended(allOf(hasAction(Intent.ACTION_CREATE_DOCUMENT), hasType("application/pdf")))

        val stopLabel = str(R.string.stop)
        val launched = System.currentTimeMillis()
        rule.waitUntilOrExplain(describe = { extractState(file, out, launched) }) {
            rule.nodeExists(hasText(stopLabel))
        }
        rule.onNodeWithText(stopLabel).performClick()

        rule.waitUntilOrExplain(describe = { extractState(file, out, launched) }) {
            rule.viewModel.statusMessage == str(R.string.work_stopped)
        }
        assertFalse("a stopped extract leaves nothing at the destination", out.exists() && out.length() > 0)
        // The document itself was only ever read from, never touched.
        assertFalse(rule.viewModel.isDirty)
        assertEquals(EXTRACT_PAGE_COUNT, (rule.viewModel.uiState as ViewerUiState.Viewing).pageSizes.size)
        // Waited for, not asserted on the spot (#611): "Stopped." is set in the catch and the
        // busy token is ended in the finally after it, so the instant the message is readable
        // the Snackbar is still up — and it then has its own exit animation to play out. The
        // claim is that Stop goes away, not that it has gone away by this line.
        rule.waitForGone(hasText(stopLabel))
    }

    /**
     * What the app was doing when a wait in [extractingALotOfPagesCanBeStoppedAndLeavesNothingBehind]
     * ran out (#611). The three outcomes this tells apart, which a bare timeout cannot:
     *
     *  * `active=false` with a `status` already set — the extract finished before the test could
     *    catch it, so there was never a Stop to press. This is the test racing the work.
     *  * `active=true` with `visible=false` — the work is running but the busy strip has not
     *    crossed its own 0.5 s threshold, so the Stop button is not on screen yet.
     *  * `active=false` with no status at all — [ViewerViewModel.extractSelectedPagesTo] returned
     *    before it began, e.g. on the permission question or a lock it did not expect.
     */
    private fun extractState(file: java.io.File, out: java.io.File, since: Long): String {
        val vm = rule.viewModel
        val strip = vm.busy.document
        return "${System.currentTimeMillis() - since} ms after the picker answered; " +
            "busy(active=${strip.isActive}, visible=${strip.isVisible}, label=${strip.label}, " +
            "canCancel=${strip.canCancel}, cancelling=${strip.isCancelling}, " +
            "locksDocument=${vm.busy.locksDocument}); " +
            "status=${vm.statusMessage}; refusal=${vm.pageToolRefusal}; " +
            "permissionQuestion=${vm.permissionQuestion}; " +
            "stopOnScreen=${rule.nodeExists(hasText(str(R.string.stop)))}; " +
            "fixture=${file.length()} bytes; " +
            "destination=${if (out.exists()) "${out.length()} bytes" else "absent"}; " +
            "pages=${(vm.uiState as? ViewerUiState.Viewing)?.pageSizes?.size}"
    }

    @Test
    fun searchingALotOfPagesReportsProgressAndCanBeStopped() {
        val file = Fixtures.written("pages-progress-search-145.pdf", TestPdfs.multiPage(SEARCH_PAGE_COUNT))
        rule.open(file)

        // The toolbar's own Search action (#26), not a menu row.
        rule.clickLabelled(str(R.string.search))
        rule.onNode(hasSetTextAction()).performTextInput("Page")

        // Every page of this fixture draws "Page N" on it (TestPdfs.multiPage), so the sweep
        // has somewhere to go on every one of them rather than finishing after the first miss.
        rule.waitUntil(SETTLE_MS) { rule.viewModel.busy.document.progressTotal == SEARCH_PAGE_COUNT }
        val doneBeforeStopping = rule.viewModel.busy.document.progressDone ?: 0
        assertTrue("the count should be somewhere inside the sweep, not past its end", doneBeforeStopping in 0..SEARCH_PAGE_COUNT)

        val stopLabel = str(R.string.stop)
        rule.waitFor(hasText(stopLabel))
        rule.onNodeWithText(stopLabel).performClick()

        rule.waitUntil(SETTLE_MS) { !rule.viewModel.isSearching }
        assertEquals(str(R.string.search_stopped), rule.viewModel.statusMessage)
        // The same wait the extract above needs, for the same reason (#611).
        rule.waitForGone(hasText(stopLabel))
    }

    @Test
    fun aShortSearchNeedsNoStopAndFindsItsMatches() {
        // The contrast case: a document far too small to need Stop at all still works exactly
        // as search did before this change — nobody is offered a button for nothing to abandon.
        val file = Fixtures.written("pages-progress-search-small-145.pdf", TestPdfs.multiPage(3))
        rule.open(file)

        rule.clickLabelled(str(R.string.search))
        rule.onNode(hasSetTextAction()).performTextInput("Page")

        rule.waitUntil(SETTLE_MS) { rule.viewModel.searchHits.isNotEmpty() }
        assertEquals(3, rule.viewModel.searchHits.size)
        assertFalse("too quick to need Stop", rule.nodeExists(hasText(str(R.string.stop))))
    }

    companion object {
        /**
         * Large enough that combine's one import call clears the busy indicator's 0.5 s show
         * threshold even on a slow, nested-virtualised CI emulator. Confirmed on CI: at this
         * count, opening the (content-light) primary document is fast — it is only ever a
         * 2-page file here — and the import itself is what takes the time.
         *
         * #611 note: "the import itself" is not what takes the time. Measured on the CI
         * emulator, `megapdf_pages_import` of 5,000 content-light pages is **149 ms** — well
         * under the threshold. What holds the busy token long enough is the rest of
         * [ViewerViewModel.performPageEdit] inside it: re-reading 5,002 page sizes off the
         * engine afterwards. That is why this one has never flaked where its two siblings did
         * (36 runs of 36 during #611), and it is left alone — but the margin here is in a place
         * the comment above did not know about, so re-measure before trusting this count if the
         * page-size resync ever gets cheaper.
         */
        private const val HUGE_PAGE_COUNT = 5_000

        /**
         * Extract's own fixture is [multiPageBulky], not [multiPage] (see the test for why):
         * confirmed on CI that [HUGE_PAGE_COUNT] content-light pages open *and* extract in well
         * under a second between them — a few hundred KB of real content, put together — so a
         * content-light fixture at any page count this test could reasonably use never offers a
         * Stop to press. 800 pages of ~100 KB of content each is an 80 MB document: still quick
         * to open (opening reads only page sizes, never page content), and three orders of
         * magnitude more content than the measurement above, for a write that takes a while.
         *
         * #611 **measured** that write instead of reasoning about it, and found the reasoning
         * wrong: with the old `q Q` padding the extract took 373 ms on the CI emulator — under
         * the busy indicator's own 0.5 s threshold, so most runs had nothing to press Stop on.
         * The padding is incompressible now (see [TestPdfs.multiPageBulky]) and the same
         * 800 x 100 KB shape takes 1,893 ms there, which is the margin this count exists to
         * buy. Extract runs at ~42 MB/s of *output* on that machine and barely cares about page
         * count, so bytes are what this dial really sets: do not shrink it without re-measuring.
         */
        private const val EXTRACT_PAGE_COUNT = 800
        private const val EXTRACT_PAGE_BYTES = 100_000

        /**
         * Large enough that the per-page sweep's own count is caught mid-way — and that the
         * sweep outlasts the busy indicator's 0.5 s show threshold, which is what the Stop this
         * test presses is gated on.
         *
         * #611 measured the sweep on the CI emulator rather than guessing at it, the same way it
         * had to for extract: 2,000 pages took 679 ms there, a margin of 1.4x over the
         * threshold, and it duly failed 3 times in 24 with no Stop ever offered. The sweep is
         * ~0.24 ms a page and all but linear (4,000: 1,044 ms; 8,000: 1,904 ms), so this count
         * is the one that buys the same 3.8x margin extract now has. The fixture is
         * content-light, so 8,000 pages is still under 2 MB and opens in a few milliseconds —
         * the cost here is the sweep, which is the point.
         */
        private const val SEARCH_PAGE_COUNT = 8_000
    }
}
