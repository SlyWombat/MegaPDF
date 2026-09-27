package com.megapdf.android

import androidx.compose.ui.test.click
import androidx.compose.ui.test.hasSetTextAction
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTextInput
import androidx.compose.ui.test.performTouchInput
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The everyday edits, driven through the screen (#346): a tap ticks a box, Add text puts a
 * box on the page, and Save writes both to the file the document came from.
 */
@RunWith(AndroidJUnit4::class)
class EditingTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    @Test
    fun tapTicksACheckboxAndSaveWritesIt() {
        val file = Fixtures.demo("agreement-346.pdf")
        val before = PageFacts.of(file)
        val square = before.unmarkedSquare()
        rule.open(file)

        // Clean: nothing to undo, nothing to save.
        assertFalse(rule.undoButton().isEnabled())
        assertFalse(rule.onNodeWithText(str(R.string.save)).isEnabled())

        rule.page().tapCentreOf(before, square)

        // The tap went through the history (#34): Undo lights up, the title gets its dot.
        rule.assertUndoEnabled()
        rule.waitForText("• " + file.name)
        assertTrue(rule.viewModel.isDirty)

        rule.onNodeWithText(str(R.string.save)).performClick()
        rule.waitUntil(SETTLE_MS) { !rule.viewModel.isDirty && !rule.viewModel.isSaving }
        rule.waitForText(file.name)

        // The file on disk carries one more check mark, on that square: a second engine reads it back.
        val after = PageFacts.of(file)
        assertEquals(before.checkMarks.size + 1, after.checkMarks.size)
        assertTrue(after.checkMarks.any { square.contains(it.rect.centerX, it.rect.centerY) })
    }

    @Test
    fun addTextPlacesABoxWhereTheTapLanded() {
        val file = Fixtures.demo("agreement-346.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)

        rule.clickLabelled(str(R.string.add_text))
        // Under the signature rule, where the store capture prints a name: blank page there.
        val spot = rule.page().pointAt(facts, 72.0, 372.0)
        rule.page().performTouchInput { click(spot) }

        rule.waitForText(str(R.string.add_text_hint))
        rule.onNode(hasSetTextAction()).performTextInput("Hello 346")
        val add = str(R.string.add)
        rule.waitUntil(SETTLE_MS) { rule.onNodeWithText(add).isEnabled() }
        rule.onNodeWithText(add).performClick()

        rule.continuePastPageRewriteWarning { rule.viewModel.canUndo }
        rule.waitForGone(hasText(str(R.string.add_text_hint)))
        rule.waitForText("• " + file.name)

        // Tapping the spot again finds the box the way a person would: its selection chrome
        // comes up, ✎ to correct it and ✕ to remove it (#36, #347).
        rule.page().performTouchInput { click(spot) }
        rule.waitForText("✎")
        rule.waitForText("✕")
        assertEquals("Hello 346", rule.viewModel.selectedTextBox?.text)
    }
}
