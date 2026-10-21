package com.megapdf.android

import com.megapdf.engine.FormField
import com.megapdf.engine.PdfRect
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The pure parts of filling a form text field (#701, #712). The write itself is `FormTextTest`, on a device. */
class FormTextFieldTest {

    private fun field(flags: Int, maxLength: Int = 0) = FormField(
        isRadio = false, isChecked = false, rect = PdfRect(100.0, 600.0, 300.0, 620.0),
        isText = true, value = "", flags = flags, maxLength = maxLength,
    )

    @Test
    fun `multi-line is the core's flag, whatever the box`() {
        assertFalse(field(0).isMultiline)
        assertTrue(field(FormField.FLAG_MULTILINE).isMultiline)
        // A tall box is no longer a reason (#712): the 30 pt guess is gone.
        val tall = field(0).copy(rect = PdfRect(100.0, 600.0, 300.0, 700.0))
        assertFalse(tall.isMultiline)
    }

    @Test
    fun `read-only is the core's flag and is independent of multi-line`() {
        assertTrue(field(FormField.FLAG_READONLY).isReadOnly)
        assertFalse(field(FormField.FLAG_READONLY).isMultiline)
        assertFalse(field(FormField.FLAG_MULTILINE).isReadOnly)
        assertTrue(field(FormField.FLAG_READONLY or FormField.FLAG_MULTILINE).run { isReadOnly && isMultiline })
    }

    @Test
    fun `a checkbox is never multi-line even with the bit set`() {
        val box = field(FormField.FLAG_MULTILINE).copy(isText = false)
        assertFalse(box.isMultiline)
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
