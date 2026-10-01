package com.megapdf.android

import android.app.Activity
import android.app.Instrumentation
import android.content.Intent
import android.net.Uri
import androidx.compose.ui.test.click
import androidx.compose.ui.test.hasSetTextAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.performTouchInput
import androidx.test.espresso.intent.Intents
import androidx.test.espresso.intent.Intents.intended
import androidx.test.espresso.intent.Intents.intending
import androidx.test.espresso.intent.matcher.IntentMatchers.hasAction
import androidx.test.espresso.intent.matcher.IntentMatchers.hasType
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.megapdf.engine.PdfEngine
import kotlinx.coroutines.runBlocking
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
 * #476/#481: MegaPDF's save re-serialises the whole file, which always invalidates an existing
 * digital signature (measured 33/33 on real GPO documents in #476) — so overwriting a signed
 * original warns at the point of Save, offering Save a copy as the prominent, already-safe
 * choice, rather than a banner on open (Dave's framing: the ordinary fill-in-and-save-a-copy
 * workflow never sees this). Fixtures are `tools/gen_signature_fixtures.py`'s synthetic
 * signed-approval.pdf / signed-certified.pdf — never one of the 33 real corpus documents,
 * which stay off this repo (staged read-only at ~/pdf-public on kdocker3).
 */
@RunWith(AndroidJUnit4::class)
class SignatureOverwriteTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    @Before
    fun stubTheSystem() = Intents.init()

    @After
    fun releaseTheSystem() = Intents.release()

    private fun header(file: File): String =
        file.inputStream().use { val b = ByteArray(4); it.read(b); String(b) }

    private fun picked(uri: Uri) = Instrumentation.ActivityResult(Activity.RESULT_OK, Intent().setData(uri))

    /** #576: reopens [file] through an engine of the test's own to check what it actually
     *  contains, rather than trusting the view model's own account of it. */
    private fun isSigned(file: File): Boolean = runBlocking {
        val engine = PdfEngine()
        val doc = engine.open(file.readBytes())
        try {
            doc.documentFlags().isSigned
        } finally {
            doc.close()
        }
    }

    /** A real edit: the signature fixtures carry no drawn checkbox to tick, unlike the demo. */
    private fun addTextEdit(facts: PageFacts) {
        rule.clickLabelled(str(R.string.add_text))
        val spot = rule.page().pointAt(facts, 200.0, 400.0)
        rule.page().performTouchInput { click(spot) }
        rule.waitForText(str(R.string.add_text_hint))
        rule.onNode(hasSetTextAction()).performTextInput("481")
        val add = str(R.string.add)
        rule.waitUntil(SETTLE_MS) { rule.onNodeWithText(add).isEnabled() }
        rule.onNodeWithText(add).performClick()
        rule.continuePastPageRewriteWarning { rule.viewModel.canUndo }
    }

    @Test
    fun savingACertifiedDocumentWarnsAndDefaultsToACopy() {
        val file = Fixtures.testAsset(Fixtures.SIGNED_CERTIFIED_ASSET, "signed-certified-481.pdf")
        val originalBytes = file.readBytes()
        val facts = PageFacts.of(file)
        rule.open(file)
        addTextEdit(facts)

        rule.clickText(str(R.string.save))
        // The certification wording (#481): stronger than the ordinary case, since a /DocMDP
        // permission-1 signature forbids modification outright rather than merely being
        // invalidated by one — the shape all 33 of #476's real corpus documents carried.
        rule.waitForText(str(R.string.signed_overwrite_title_certified))
        rule.waitForText(str(R.string.signed_overwrite_body_certified))
        // #576: the removal tick is offered in the same dialog, ticked by default.
        rule.waitForText(str(R.string.signed_overwrite_remove_signature))
        rule.waitForText(str(R.string.signed_overwrite_remove_offer))
        // Nothing was written yet: the confirmation is up, not a silent save.
        assertTrue(rule.viewModel.isDirty)
        assertTrue("the signed original is untouched while the dialog is up", file.readBytes().contentEquals(originalBytes))

        val copy = Fixtures.empty("signed-copy-481.pdf")
        intending(allOf(hasAction(Intent.ACTION_CREATE_DOCUMENT), hasType("application/pdf")))
            .respondWith(picked(Fixtures.uri(copy)))
        // The tick is left ticked by default (#576): this copy is saved without the signature.
        rule.clickText(str(R.string.save_a_copy))

        intended(allOf(hasAction(Intent.ACTION_CREATE_DOCUMENT), hasType("application/pdf")))
        rule.waitUntil(SETTLE_MS) { copy.length() > 0 && !rule.viewModel.isSaving }
        assertEquals("%PDF", header(copy))
        // #576: the tick was honoured, so the quiet notice says the signature was removed,
        // not that the copy keeps a now-invalid one.
        rule.waitForText(str(R.string.signature_removed_notice))
        assertFalse("the saved copy genuinely carries no signature when reopened", isSigned(copy))
        // The signed original was never written to, through the whole exchange.
        assertTrue("the signed original stays untouched by Save a copy", file.readBytes().contentEquals(originalBytes))
    }

    @Test
    fun uncheckingTheTickKeepsTheNowInvalidSignatureInTheCopy() {
        // #576's own worry, held here the same way the Windows and iOS self-tests hold it: the
        // tick must be a real choice, not a removal that happens regardless of it.
        val file = Fixtures.testAsset(Fixtures.SIGNED_CERTIFIED_ASSET, "signed-certified-481b.pdf")
        val originalBytes = file.readBytes()
        val facts = PageFacts.of(file)
        rule.open(file)
        addTextEdit(facts)

        rule.clickText(str(R.string.save))
        rule.waitForText(str(R.string.signed_overwrite_title_certified))
        // Unticks the box; the text itself is the click target, same as the box.
        rule.clickText(str(R.string.signed_overwrite_remove_signature))

        val copy = Fixtures.empty("signed-copy-481b.pdf")
        intending(allOf(hasAction(Intent.ACTION_CREATE_DOCUMENT), hasType("application/pdf")))
            .respondWith(picked(Fixtures.uri(copy)))
        rule.clickText(str(R.string.save_a_copy))

        intended(allOf(hasAction(Intent.ACTION_CREATE_DOCUMENT), hasType("application/pdf")))
        rule.waitUntil(SETTLE_MS) { copy.length() > 0 && !rule.viewModel.isSaving }
        // Save a copy says so, quietly and once (#481): the notice names what changed.
        rule.waitForText(str(R.string.signed_copy_notice))
        assertTrue("the saved copy still carries the (now invalid) signature", isSigned(copy))
        assertTrue("the signed original stays untouched by Save a copy", file.readBytes().contentEquals(originalBytes))
    }

    @Test
    fun savingAnApprovalSignatureWarnsAndOverwritingProceeds() {
        val file = Fixtures.testAsset(Fixtures.SIGNED_APPROVAL_ASSET, "signed-approval-481.pdf")
        val originalBytes = file.readBytes()
        val facts = PageFacts.of(file)
        rule.open(file)
        addTextEdit(facts)

        rule.clickText(str(R.string.save))
        // The ordinary (non-certification) wording (#481).
        rule.waitForText(str(R.string.signed_overwrite_title))
        rule.waitForText(str(R.string.signed_overwrite_body))
        assertFalse(rule.nodeExists(hasText(str(R.string.signed_overwrite_title_certified))))

        // The deliberate second tap: overwrite the signed original anyway. Nothing here is
        // refused — the person just has to choose it, rather than dismiss a warning to do
        // what they were always going to do. The removal tick is left ticked (#576).
        rule.clickText(str(R.string.redact_overwrite))
        rule.waitUntil(SETTLE_MS) { !rule.viewModel.isDirty && !rule.viewModel.isSaving }
        assertFalse("the edit actually landed on disk", file.readBytes().contentEquals(originalBytes))
        assertFalse("the overwritten file no longer claims a signature it cannot support", isSigned(file))
    }

    @Test
    fun uncheckingTheTickBeforeOverwritingLeavesTheNowInvalidSignatureInPlace() {
        val file = Fixtures.testAsset(Fixtures.SIGNED_APPROVAL_ASSET, "signed-approval-481b.pdf")
        val originalBytes = file.readBytes()
        val facts = PageFacts.of(file)
        rule.open(file)
        addTextEdit(facts)

        rule.clickText(str(R.string.save))
        rule.waitForText(str(R.string.signed_overwrite_title))
        rule.clickText(str(R.string.signed_overwrite_remove_signature))
        rule.clickText(str(R.string.redact_overwrite))

        rule.waitUntil(SETTLE_MS) { !rule.viewModel.isDirty && !rule.viewModel.isSaving }
        assertFalse("the edit actually landed on disk", file.readBytes().contentEquals(originalBytes))
        assertTrue("not asking to remove it leaves the (now invalid) signature in the overwritten file", isSigned(file))
    }

    @Test
    fun anUnsignedDocumentSavesWithNoWarningAtAll() {
        val file = Fixtures.demo("agreement-481.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        rule.page().tapCentreOf(facts, facts.unmarkedSquare())
        rule.waitForText("• " + file.name)
        assertTrue(rule.viewModel.isDirty)

        rule.clickText(str(R.string.save))
        // Save proceeds immediately — no #481 dialog for a document with no signature at all,
        // the same as before this change.
        rule.waitUntil(SETTLE_MS) { !rule.viewModel.isDirty && !rule.viewModel.isSaving }
        assertFalse(rule.nodeExists(hasText(str(R.string.signed_overwrite_title))))
        assertFalse(rule.nodeExists(hasText(str(R.string.signed_overwrite_title_certified))))
    }
}
