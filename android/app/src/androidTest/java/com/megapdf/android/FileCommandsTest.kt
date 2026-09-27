package com.megapdf.android

import android.app.Activity
import android.app.Instrumentation
import android.content.Intent
import android.net.Uri
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
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
import java.io.File

/**
 * The ⋮ menu's file commands (#346), with the system's part played by Espresso-Intents:
 * the pickers and the share sheet are answered in-process, so the test sees exactly what
 * the app asked the system for and what it did with the answer.
 *
 * - Export as Markdown (#386, #409) asks for a `text/markdown` document with a `.md` name,
 *   writes real Markdown through it, and leaves the document alone.
 * - Save a copy (#409) asks for a PDF, writes one, and the copy becomes the document.
 * - Share (#378) asks about unsaved changes first — Save, Share without saving, Cancel —
 *   and Share without saving hands the OS chooser a content:// grant on a copy.
 */
@RunWith(AndroidJUnit4::class)
class FileCommandsTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    @Before
    fun stubTheSystem() = Intents.init()

    @After
    fun releaseTheSystem() = Intents.release()

    private fun header(file: File): String =
        file.inputStream().use { val b = ByteArray(4); it.read(b); String(b) }

    private fun picked(uri: Uri) = Instrumentation.ActivityResult(Activity.RESULT_OK, Intent().setData(uri))

    private fun makeDirty(file: File, facts: PageFacts) {
        rule.page().tapCentreOf(facts, facts.unmarkedSquare())
        rule.waitForText("• " + file.name)
    }

    @Test
    fun exportAsMarkdownWritesTheDocumentsText() {
        val file = Fixtures.demo("lease-346.pdf")
        rule.open(file)
        val md = Fixtures.empty("export-346.md")
        intending(allOf(hasAction(Intent.ACTION_CREATE_DOCUMENT), hasType("text/markdown")))
            .respondWith(picked(Fixtures.uri(md)))

        rule.menu(str(R.string.export_markdown))

        // The picker was typed Markdown and offered lease-346.md — never a PDF name (#409).
        intended(allOf(
            hasAction(Intent.ACTION_CREATE_DOCUMENT),
            hasType("text/markdown"),
            hasExtra(Intent.EXTRA_TITLE, "lease-346.md"),
        ))
        rule.waitUntil(SETTLE_MS) { md.length() > 0 && !rule.viewModel.isSaving }
        val text = md.readText()
        assertContains(text, "# Equipment Rental Agreement")
        assertContains(text, "## Options")
        assertContains(text, "Customer signature")

        // An export, not a save: same title, no dot, still the same document.
        rule.waitForText(file.name)
        assertFalse(rule.viewModel.isDirty)
        assertFalse(rule.nodeExists(hasText("• " + file.name)))
    }

    @Test
    fun saveACopyWritesAPdfThatBecomesTheDocument() {
        val file = Fixtures.demo("lease-346.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        makeDirty(file, facts)
        val copy = Fixtures.empty("lease-copy-346.pdf")
        intending(allOf(hasAction(Intent.ACTION_CREATE_DOCUMENT), hasType("application/pdf")))
            .respondWith(picked(Fixtures.uri(copy)))

        rule.menu(str(R.string.save_a_copy))

        intended(allOf(
            hasAction(Intent.ACTION_CREATE_DOCUMENT),
            hasType("application/pdf"),
            hasExtra(Intent.EXTRA_TITLE, "lease-346.pdf"),
        ))
        // The copy is the document now (#409): its name in the title, clean.
        rule.waitForText(copy.name)
        rule.waitUntil(SETTLE_MS) { !rule.viewModel.isDirty && !rule.viewModel.isSaving }
        assertTrue(copy.length() > 0)
        assertEquals("%PDF", header(copy))
        // ...and it carries the edit; the original was never written.
        assertEquals(facts.checkMarks.size + 1, PageFacts.of(copy).checkMarks.size)
        assertEquals(facts.checkMarks.size, PageFacts.of(file).checkMarks.size)
    }

    @Test
    fun shareAsksAboutUnsavedChangesFirst() {
        val file = Fixtures.demo("lease-346.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        makeDirty(file, facts)
        intending(hasAction(Intent.ACTION_CHOOSER))
            .respondWith(Instrumentation.ActivityResult(Activity.RESULT_CANCELED, null))

        // Dirty: the question, with its three answers (#378).
        rule.menu(str(R.string.share))
        rule.waitForText(str(R.string.unsaved_changes))
        rule.waitForText(str(R.string.unsaved_changes_body_share))
        rule.waitForText(str(R.string.save))
        rule.waitForText(str(R.string.share_without_saving))
        rule.clickText(str(R.string.cancel))
        rule.waitForGone(hasText(str(R.string.unsaved_changes)))
        assertTrue("Cancel keeps the changes", rule.viewModel.isDirty)
        assertTrue(Intents.getIntents().none { it.action == Intent.ACTION_CHOOSER })

        // Share without saving: the chooser gets a content:// grant on a copy under the
        // app's own FileProvider, and the edits stay exactly where Cancel left them.
        rule.menu(str(R.string.share))
        rule.clickText(str(R.string.share_without_saving))
        rule.waitUntil(SETTLE_MS) { Intents.getIntents().any { it.action == Intent.ACTION_CHOOSER } }
        val chooser = Intents.getIntents().first { it.action == Intent.ACTION_CHOOSER }
        @Suppress("DEPRECATION")
        val send = chooser.getParcelableExtra<Intent>(Intent.EXTRA_INTENT)!!
        assertEquals(Intent.ACTION_SEND, send.action)
        assertEquals("application/pdf", send.type)
        assertTrue(send.flags and Intent.FLAG_GRANT_READ_URI_PERMISSION != 0)
        @Suppress("DEPRECATION")
        val stream = send.getParcelableExtra<Uri>(Intent.EXTRA_STREAM)!!
        assertEquals("content", stream.scheme)
        assertEquals("${Fixtures.appContext.packageName}.fileprovider", stream.authority)
        assertEquals(file.name, stream.lastPathSegment)
        val shared = shareFileFor(Fixtures.appContext.cacheDir, file.name)
        assertEquals("%PDF", header(shared))
        assertTrue("the edits are still pending", rule.viewModel.isDirty)
    }
}
