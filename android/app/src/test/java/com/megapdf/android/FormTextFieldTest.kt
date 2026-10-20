package com.megapdf.android

import com.megapdf.engine.FormField
import com.megapdf.engine.PdfRect
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The pure parts of filling a form text field (#701). The write itself is `FormTextTest`, on a device. */
class FormTextFieldTest {

    private fun field(height: Double, value: String = "") = FormField(
        isRadio = false, isChecked = false, rect = PdfRect(100.0, 600.0, 300.0, 600.0 + height),
        isText = true, value = value,
    )

    @Test
    fun `a one-line box is single-line`() {
        assertFalse(isMultilineField(field(20.0)))
    }

    @Test
    fun `a tall box is multi-line`() {
        assertTrue(isMultilineField(field(60.0)))
    }

    @Test
    fun `a value that already has a break is multi-line whatever the box`() {
        assertTrue(isMultilineField(field(20.0, "one\ntwo")))
        assertTrue(isMultilineField(field(20.0, "one\rtwo")))
    }

    @Test
    fun `filling a text field follows the fill-form permission, like a checkbox`() {
        val op = FormTextOperation(0, 5.0, 5.0, "", "Ada")
        assertEquals("fill field", op.name)
        assertEquals(PermissionClass.EDITING, DocumentCapabilities.FULL.permissionClassOf(op))
        val noFill = DocumentCapabilities.FULL.copy(canFillForms = false)
        assertFalse(noFill.allows(op))
        assertTrue(DocumentCapabilities.FULL.allows(op))
        assertTrue(DocumentCapabilities.FULL.isFillingOperation(op))
    }
}
