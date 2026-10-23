package com.megapdf.android

import android.app.Activity
import android.app.Instrumentation
import android.content.Intent
import android.net.Uri
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.espresso.intent.Intents
import androidx.test.espresso.intent.Intents.intended
import androidx.test.espresso.intent.Intents.intending
import androidx.test.espresso.intent.matcher.IntentMatchers.hasAction
import androidx.test.espresso.intent.matcher.IntentMatchers.hasExtra
import androidx.test.espresso.intent.matcher.IntentMatchers.hasType
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.megapdf.engine.PdfRect
import org.hamcrest.CoreMatchers.allOf
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Shrink for email (#593), from the ⋮ menu to a saved file, with the system's picker answered by
 * Espresso-Intents as [FileCommandsTest] does. What it proves: the row is there, the result sheet
 * appears with Share and Save a copy, Save a copy writes a smaller PDF where the picker said, and
 * the open document is left alone — its name, its unsaved state and its file.
 *
 * No Stop test here: a 300 px shrink takes milliseconds (CLAUDE.md, measure the duration). Stop is
 * proven at the engine (`ShrinkEngineTest.aRaisedFlagStopsTheShrink`) and in the core.
 */
@RunWith(AndroidJUnit4::class)
class ShrinkForEmailTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    @Before
    fun stubTheSystem() = Intents.init()

    @After
    fun releaseTheSystem() = Intents.release()

    private fun picked(uri: Uri) = Instrumentation.ActivityResult(Activity.RESULT_OK, Intent().setData(uri))

    @Test
    fun shrinkForEmailOffersASmallerCopyAndLeavesTheDocumentAlone() {
        val source = Fixtures.written("shrink-593.pdf", LargeImagePdf.bytes(1))
        val sourceBytes = source.readBytes()
        rule.open(source)
        val out = Fixtures.empty("shrink-593-out.pdf")
        intending(allOf(hasAction(Intent.ACTION_CREATE_DOCUMENT), hasType("application/pdf")))
            .respondWith(picked(Fixtures.uri(out)))

        rule.menu(str(R.string.shrink_for_email))

        rule.waitForText(str(R.string.shrink_ready_title))
        rule.waitForText(str(R.string.share))
        rule.clickText(str(R.string.save_a_copy))

        // The picker was typed PDF and offered the shrunk copy's own name.
        rule.waitUntil(SETTLE_MS) { out.length() > 0 && !rule.viewModel.busy.locksDocument }
        intended(allOf(
            hasAction(Intent.ACTION_CREATE_DOCUMENT),
            hasType("application/pdf"),
            hasExtra(Intent.EXTRA_TITLE, str(R.string.shrink_file_name, "shrink-593") + ".pdf"),
        ))
        assertTrue("wrote ${out.length()} bytes against ${sourceBytes.size}", out.length() < sourceBytes.size)
        rule.waitUntil(SETTLE_MS) { rule.viewModel.statusMessage == str(R.string.smaller_copy_saved) }

        // The open document is the one that was opened: same name, clean, and its file untouched.
        assertEquals("shrink-593.pdf", (rule.viewModel.uiState as ViewerUiState.Viewing).displayName)
        assertFalse(rule.viewModel.isDirty)
        assertArrayEquals(sourceBytes, source.readBytes())
        assertEquals(null, rule.viewModel.shrinkReady)
    }

    /**
     * A copy made to be emailed is the worst place for marked-but-unapplied content to go, and
     * the phones shrink what is on screen (D6), so Shrink for email asks the redaction question
     * first, as Export as Markdown does (coordinator, 2026-10-22). Cancel is the safe answer: no
     * result sheet, no file, and the mark is still on the page to be applied or removed.
     */
    @Test
    fun shrinkForEmailWithAMarkAsksTheRedactionQuestionAndCancelMakesNothing() {
        val source = Fixtures.written("shrink-593-marked.pdf", LargeImagePdf.bytes(1))
        val sourceBytes = source.readBytes()
        rule.open(source)
        // The picture sits at (100, 600) and is 72 pt square: mark it as a drag across it would.
        rule.viewModel.markForRedaction(0, PdfRect(100.0, 600.0, 172.0, 672.0))
        rule.waitUntil(SETTLE_MS) { rule.viewModel.redactionMarkCount == 1 }

        rule.menu(str(R.string.shrink_for_email))

        // The question, with the shrink as its action, and nothing started behind it.
        rule.waitForText(str(R.string.redact_confirm_title))
        assertEquals(null, rule.viewModel.shrinkReady)
        assertFalse(rule.viewModel.busy.locksDocument)

        rule.clickText(str(R.string.cancel))
        rule.waitForGone(androidx.compose.ui.test.hasText(str(R.string.redact_confirm_title)))

        // Cancel leaves no result sheet, no copy in the share folder, and the mark in place.
        assertEquals(null, rule.viewModel.shrinkReady)
        assertEquals(1, rule.viewModel.redactionMarkCount)
        val copy = java.io.File(shareDirectory(Fixtures.appContext.cacheDir),
            str(R.string.shrink_file_name, "shrink-593-marked") + ".pdf")
        assertFalse("no shrunk copy was made", copy.exists())
        assertArrayEquals(sourceBytes, source.readBytes())
    }

    /**
     * D7: a copy that is not strictly smaller than the on-screen document (serialised) is not
     * offered: no result sheet, no file, and the message says so. The sizes come through the view
     * model's `shrinkSizeOf` seam: the core only replaces a picture it can make at least 10%
     * smaller and both files come from the same writer, so no fixture makes a real copy come out
     * no smaller. Equal counts as not smaller, so the seam reports the same size for both.
     */
    @Test
    fun aCopyThatIsNotSmallerIsNotOffered() {
        val source = Fixtures.written("shrink-593-equal.pdf", LargeImagePdf.bytes(1))
        val sourceBytes = source.readBytes()
        rule.open(source)
        rule.viewModel.shrinkSizeOf = { 1000L }

        rule.menu(str(R.string.shrink_for_email))

        rule.waitUntil(SETTLE_MS) { rule.viewModel.statusMessage == str(R.string.shrink_not_smaller) }
        assertEquals(null, rule.viewModel.shrinkReady)
        val copy = java.io.File(shareDirectory(Fixtures.appContext.cacheDir),
            str(R.string.shrink_file_name, "shrink-593-equal") + ".pdf")
        assertFalse("no copy is left to share or save", copy.exists())
        assertFalse(rule.viewModel.busy.locksDocument)
        assertEquals("shrink-593-equal.pdf", (rule.viewModel.uiState as ViewerUiState.Viewing).displayName)
        assertArrayEquals(sourceBytes, source.readBytes())
    }
}
