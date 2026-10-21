package com.megapdf.android

import androidx.compose.ui.test.hasSetTextAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.megapdf.engine.FormField
import com.megapdf.engine.PdfEngine
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/**
 * Filling a form's text field, through the screen (#701): a tap opens the editor on the field's
 * value, Done writes it through `megapdf_form_set_text`, Save puts it in the file, and a second
 * engine reads it back. `formtext.pdf` is `tools/gen_test_fixtures.py`'s one-text-field page,
 * its widget at (100,600)-(300,620); `forms.pdf` is its one-checkbox sibling.
 */
@RunWith(AndroidJUnit4::class)
class FormTextTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    /** The widgets on page 1 of [file], read by an engine of the test's own. */
    private fun fields(file: File): List<FormField> = runBlocking {
        val engine = PdfEngine()
        val doc = engine.open(file.readBytes())
        try {
            val page = doc.openPage(0)
            try { page.formFields() } finally { page.close() }
        } finally {
            doc.close()
        }
    }

    private fun save() {
        rule.waitUntil(SETTLE_MS) { !rule.viewModel.editingBlocked }
        rule.onNodeWithText(str(R.string.save)).performClick()
        rule.waitUntil(SETTLE_MS) { !rule.viewModel.isDirty && !rule.viewModel.isSaving }
    }

    private fun fill(facts: PageFacts, text: String) {
        rule.page().tapAt(facts, 200.0, 610.0)
        rule.waitForText(str(R.string.form_text_title))
        rule.onNode(hasSetTextAction()).performTextInput(text)
        rule.onNodeWithText(str(R.string.done)).performClick()
        rule.waitForGone(hasText(str(R.string.form_text_title)))
        rule.waitUntil(SETTLE_MS) { rule.viewModel.canUndo }
    }

    @Test
    fun tapFillsATextFieldSaveWritesItAndUndoPutsTheOldValueBack() {
        val file = Fixtures.testAsset("formtext.pdf", "form-text-701.pdf")
        val facts = PageFacts.of(file)
        assertEquals("", fields(file).single().value)
        rule.open(file)
        assertFalse(rule.viewModel.isDirty)

        fill(facts, "Ada Lovelace")
        rule.waitUntil(SETTLE_MS) { rule.viewModel.isDirty }
        save()
        val saved = fields(file).single()
        assertTrue(saved.isText)
        assertEquals("Ada Lovelace", saved.value)

        // The editor opens on what the field holds now.
        rule.page().tapAt(facts, 200.0, 610.0)
        rule.waitForText(str(R.string.form_text_title))
        rule.waitFor(hasText("Ada Lovelace"))
        rule.onNodeWithText(str(R.string.cancel)).performClick()
        rule.waitForGone(hasText(str(R.string.form_text_title)))

        // One undo step puts the old (empty) value back.
        rule.clickUndo()
        rule.waitUntil(SETTLE_MS) { rule.viewModel.isDirty }
        save()
        assertEquals("", fields(file).single().value)
    }

    @Test
    fun cancelChangesNothing() {
        val file = Fixtures.testAsset("formtext.pdf", "form-cancel-701.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        rule.page().tapAt(facts, 200.0, 610.0)
        rule.waitForText(str(R.string.form_text_title))
        rule.onNode(hasSetTextAction()).performTextInput("never written")
        rule.onNodeWithText(str(R.string.cancel)).performClick()
        rule.waitForGone(hasText(str(R.string.form_text_title)))
        assertFalse(rule.viewModel.canUndo)
        assertFalse(rule.viewModel.isDirty)
    }

    @Test
    fun tapOnACheckboxStillTogglesItWithoutAnEditor() {
        val file = Fixtures.testAsset("forms.pdf", "form-check-701.pdf")
        val facts = PageFacts.of(file)
        val box = fields(file).single()
        assertFalse(box.isText)
        assertFalse(box.isChecked)
        rule.open(file)
        rule.page().tapCentreOf(facts, box.rect)
        rule.waitUntil(SETTLE_MS) { rule.viewModel.canUndo }
        assertFalse(rule.nodeExists(hasText(str(R.string.form_text_title))))
        save()
        assertTrue(fields(file).single().isChecked)
    }

    /**
     * #712: the editor takes line breaks exactly when the field's own multi-line flag says so, on
     * `formflow.pdf` (a single-line "first", a multi-line "notes"). The flag is read in the engine
     * and the view model passes it through, so this is the screen path of the same fact
     * `FormFieldFlagsTest` reads from the core.
     */
    @Test
    fun theEditorIsMultilineOnlyForAFieldFlaggedMultiline() {
        val file = Fixtures.testAsset("formflow.pdf", "form-flags-712.pdf")
        val facts = PageFacts.of(file)
        val widgets = fields(file)
        rule.open(file)

        rule.page().tapCentreOf(facts, widgets[0].rect)
        rule.waitUntil(SETTLE_MS) { rule.viewModel.pendingFormText != null }
        assertFalse(rule.viewModel.pendingFormText!!.multiline)
        rule.onNodeWithText(str(R.string.cancel)).performClick()
        rule.waitUntil(SETTLE_MS) { rule.viewModel.pendingFormText == null }

        rule.page().tapCentreOf(facts, widgets[2].rect)
        rule.waitUntil(SETTLE_MS) { rule.viewModel.pendingFormText != null }
        assertTrue(rule.viewModel.pendingFormText!!.multiline)
        rule.onNodeWithText(str(R.string.cancel)).performClick()
    }
}
