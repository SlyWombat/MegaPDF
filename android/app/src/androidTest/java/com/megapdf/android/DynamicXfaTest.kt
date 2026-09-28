package com.megapdf.android

import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * #456/#457: a dynamic-XFA document — built to be filled in Adobe Reader, which PDFium (and so
 * MegaPDF) cannot render — explains itself calmly instead of looking like a blank or broken
 * page, and a hybrid-XFA document (whose static content is the real, complete form, like CRA's
 * and Service Canada's own fillable forms) is completely unaffected. Fixtures are the synthetic
 * ones from `tools/gen_xfa_fixtures.py` (never a real Canadian government form: Crown
 * copyright, local-only per Dave's 2026-09-27 decision).
 */
@RunWith(AndroidJUnit4::class)
class DynamicXfaTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    @Test
    fun dynamicXfaShowsTheBannerAndFillingToolsExplainInsteadOfArming() {
        val file = Fixtures.testAsset(Fixtures.DYNAMIC_XFA_ASSET, "dynamic-xfa-457.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)

        // The banner (#457): calm, persistent — named the reason, and the way out — still on
        // screen once the page itself has settled, not a dialog and not a snackbar that times out.
        rule.waitForText(str(R.string.dynamic_xfa_banner_title))
        rule.waitForText(str(R.string.dynamic_xfa_banner_body))
        rule.waitForText(str(R.string.dynamic_xfa_get_reader))

        // Sign and Add text are still there and still enabled: nothing here looks disabled,
        // let alone broken (#457 — arming a filling tool explains rather than doing nothing).
        assertTrue(rule.onNodeWithContentDescription(str(R.string.add_text)).isEnabled())
        assertTrue(rule.onNodeWithContentDescription(str(R.string.sign)).isEnabled())

        rule.clickLabelled(str(R.string.add_text))
        rule.waitForText(str(R.string.dynamic_xfa_fill_notice))
        // Explained rather than acted: placement mode was never entered, so a tap on the page
        // does not open the add-text dialog the way it would on an ordinary document.
        assertFalse(rule.viewModel.isPlacingText)
        rule.page().tapAt(facts, facts.widthPoints / 2, facts.heightPoints / 2)
        assertFalse(rule.nodeExists(hasText(str(R.string.add_text_hint))))

        // Everything else keeps working (#457): the document's own commands are still on
        // screen, Save a copy and Export as Markdown are still offered from the menu, and
        // the page itself rendered (rule.open() above already waited for it).
        rule.waitForText(str(R.string.save))
        rule.clickLabelled(str(R.string.more_options))
        rule.waitForText(str(R.string.save_a_copy))
        assertTrue(rule.onNodeWithText(str(R.string.save_a_copy)).isEnabled())
        assertTrue(rule.onNodeWithText(str(R.string.export_markdown)).isEnabled())
    }

    @Test
    fun hybridXfaIsUnaffected() {
        val file = Fixtures.testAsset(Fixtures.HYBRID_XFA_ASSET, "hybrid-xfa-457.pdf")
        rule.open(file)

        // No banner: a hybrid document's static content is the real, complete form — the
        // shape of CRA's and Service Canada's own fillable forms, which must extract and
        // behave exactly as they do today.
        assertFalse(rule.nodeExists(hasText(str(R.string.dynamic_xfa_banner_title))))

        // Add text arms exactly as it always has: placement mode is entered at once.
        rule.clickLabelled(str(R.string.add_text))
        rule.waitUntil(SETTLE_MS) { rule.viewModel.isPlacingText }
        assertFalse(rule.nodeExists(hasText(str(R.string.dynamic_xfa_fill_notice))))
    }
}
