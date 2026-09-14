package com.megapdf.android

import com.megapdf.engine.PdfPermissions
import com.megapdf.engine.PdfRect
import com.megapdf.engine.PdfSecurity
import com.megapdf.engine.TextLine
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DocumentCapabilitiesTest {

    private fun restricted(permissions: Int) =
        PdfSecurity(isEncrypted = true, revision = 6, permissions = permissions, hasFullAccess = false)

    private val rect = PdfRect(0.0, 0.0, 10.0, 10.0)
    private val bodyEdit = BodyTextDeleteOperation(0, TextLine(emptyList(), rect))
    private val textBox = TextBoxOperation(0, "text:a", "hi", 12.0, 1.0, 1.0, adding = true)
    private val moveTextBox = MoveTextBoxOperation(0, "text:a", 1.0, 1.0, 2.0, 2.0)
    private val signature = StampOperation(0, "sig:a", IntArray(1), 1, 1, rect, adding = true)
    private val mark = MarkOperation(0, rect, "mark:a", true)
    private val checkbox = FieldToggleOperation(0, 5.0, 5.0)

    @Test
    fun `an unprotected document allows everything`() {
        val c = DocumentCapabilities.fromSecurity(PdfSecurity.UNPROTECTED)
        assertTrue(c.canEditContent && c.canSign && c.canFillForms && c.canChangeSecurity)
        assertFalse(c.isEncrypted)
        assertFalse(c.isRestricted)
        assertEquals(DocumentCapabilities.FULL, c)
        listOf(bodyEdit, textBox, moveTextBox, signature, mark, checkbox).forEach { assertTrue(c.allows(it)) }
    }

    @Test
    fun `an owner-only document that allows nothing is restricted everywhere`() {
        val c = DocumentCapabilities.fromSecurity(restricted(0))
        assertFalse(c.canEditContent || c.canSign || c.canFillForms || c.canChangeSecurity)
        assertTrue(c.isEncrypted)
        assertTrue(c.isRestricted)
        listOf(bodyEdit, textBox, moveTextBox, signature, mark, checkbox).forEach { assertFalse(c.allows(it)) }
    }

    @Test
    fun `modify gates body text and text boxes only`() {
        val c = DocumentCapabilities.fromSecurity(restricted(PdfPermissions.MODIFY))
        assertTrue(c.allows(bodyEdit) && c.allows(textBox) && c.allows(moveTextBox))
        assertFalse(c.allows(signature) || c.allows(mark) || c.allows(checkbox))
        assertFalse(c.canChangeSecurity)
    }

    @Test
    fun `annotate allows signatures, marks and form filling`() {
        val c = DocumentCapabilities.fromSecurity(restricted(PdfPermissions.ANNOTATE))
        assertTrue(c.allows(signature) && c.allows(mark) && c.allows(checkbox))
        assertFalse(c.allows(bodyEdit) || c.allows(textBox))
    }

    @Test
    fun `fill forms alone allows form fields only`() {
        val c = DocumentCapabilities.fromSecurity(restricted(PdfPermissions.FILL_FORMS or PdfPermissions.PRINT))
        assertTrue(c.canFillForms)
        assertFalse(c.canSign || c.canEditContent)
        assertTrue(c.allows(checkbox))
        assertFalse(c.allows(mark))
    }

    @Test
    fun `the owner's open of a protected document has full access and is not restricted`() {
        val c = DocumentCapabilities.fromSecurity(
            PdfSecurity(isEncrypted = true, revision = 6, permissions = PdfPermissions.ALL, hasFullAccess = true))
        assertTrue(c.canChangeSecurity && c.canEditContent && c.canSign && c.canFillForms)
        assertTrue(c.isEncrypted)
        assertFalse(c.isRestricted)
    }

    @Test
    fun `the password command offers set, change or unlock`() {
        assertEquals(PasswordCommandMode.SET, PasswordCommandMode.of(DocumentCapabilities.FULL))
        assertEquals(
            PasswordCommandMode.CHANGE,
            PasswordCommandMode.of(DocumentCapabilities.FULL.copy(isEncrypted = true)),
        )
        assertEquals(
            PasswordCommandMode.RESTRICTED,
            PasswordCommandMode.of(DocumentCapabilities.fromSecurity(restricted(PdfPermissions.ALL and PdfPermissions.MODIFY.inv()))),
        )
    }

    @Test
    fun `a new password must be non-empty and confirmed`() {
        assertEquals(NewPasswordProblem.EMPTY, newPasswordProblem("", ""))
        assertEquals(NewPasswordProblem.MISMATCH, newPasswordProblem("abc", "abd"))
        assertEquals(NewPasswordProblem.MISMATCH, newPasswordProblem("abc", ""))
        assertNull(newPasswordProblem("abc", "abc"))
        assertNull(newPasswordProblem("clé-🔒", "clé-🔒"))
    }
}
