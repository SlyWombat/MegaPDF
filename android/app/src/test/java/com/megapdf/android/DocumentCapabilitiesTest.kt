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
    private val editTextBox = EditTextBoxOperation(
        0, "text:a", TextBoxStyle("hi", 12.0, "Helvetica"), TextBoxStyle("hello", 14.0, "Helvetica"), 1.0, 1.0)
    private val signature = StampOperation(0, "sig:a", IntArray(1), 1, 1, rect, adding = true)
    private val mark = MarkOperation(0, rect, "mark:a", true)
    private val checkbox = FieldToggleOperation(0, 5.0, 5.0)

    // Redaction marks (#329). They remove the document's own content when applied, so they
    // follow the modify permission and nothing weaker.
    private val redactMark = RedactMarkOperation(0, listOf(rect), listOf(7), adding = true)
    private val removeMark = RedactMarkOperation(0, listOf(rect), listOf(7), adding = false)
    private val moveMark = MoveRedactionMarkOperation(0, 7, rect, PdfRect(1.0, 1.0, 5.0, 5.0))
    private val clearMarks = ClearRedactionMarksOperation(0, mapOf(0 to listOf(rect)))

    /** The edits that change the document's own text, which is what modify buys. */
    private val contentEdits: List<PdfEditOperation> =
        listOf(bodyEdit, redactMark, removeMark, moveMark, clearMarks)

    /** The edits that only touch the form, which annotate and fill forms buy. */
    private val formEdits: List<PdfEditOperation> =
        listOf(textBox, editTextBox, moveTextBox, signature, mark, checkbox)

    @Test
    fun `a mark is not a change to the document`() {
        // #329: a mark lives in the core and is never written into the file, so nothing
        // that touches one may dirty the document — no unsaved flag, no journal entry, no
        // re-render. Everything else does; the default is the safe one.
        listOf(redactMark, removeMark, moveMark, clearMarks).forEach {
            assertFalse("${it.name} must not dirty the document", it.changesDocument)
        }
        formEdits.forEach {
            assertTrue("${it.name} must dirty the document", it.changesDocument)
        }
    }

    @Test
    fun `an unprotected document allows everything`() {
        val c = DocumentCapabilities.fromSecurity(PdfSecurity.UNPROTECTED)
        assertTrue(c.canEditContent && c.canSign && c.canFillForms && c.canAddText && c.canChangeSecurity)
        assertFalse(c.isEncrypted)
        assertFalse(c.isRestricted)
        assertEquals(DocumentCapabilities.FULL, c)
        (contentEdits + formEdits).forEach { assertTrue(c.allows(it)) }
    }

    @Test
    fun `an owner-only document that allows nothing is restricted everywhere`() {
        val c = DocumentCapabilities.fromSecurity(restricted(0))
        assertFalse(c.canEditContent || c.canSign || c.canFillForms || c.canAddText || c.canChangeSecurity)
        assertTrue(c.isEncrypted)
        assertTrue(c.isRestricted)
        (contentEdits + formEdits).forEach { assertFalse(c.allows(it)) }
    }

    @Test
    fun `modify allows body text, redaction and text boxes, but not filling in`() {
        val c = DocumentCapabilities.fromSecurity(restricted(PdfPermissions.MODIFY))
        assertTrue(c.canEditContent && c.canAddText)
        assertFalse(c.canSign || c.canFillForms)
        assertTrue(c.allows(bodyEdit) && c.allows(textBox) && c.allows(editTextBox) && c.allows(moveTextBox))
        assertFalse(c.allows(signature) || c.allows(mark) || c.allows(checkbox))
        contentEdits.forEach { assertTrue("${it.name} follows modify", c.allows(it)) }
        assertFalse(c.canChangeSecurity)
    }

    @Test
    fun `fill forms allows filling in everything the form offers, but not its own text`() {
        val c = DocumentCapabilities.fromSecurity(restricted(PdfPermissions.FILL_FORMS or PdfPermissions.PRINT))
        assertTrue(c.canFillForms && c.canSign && c.canAddText)
        assertFalse(c.canEditContent || c.canChangeSecurity)
        formEdits.forEach { assertTrue(c.allows(it)) }
        assertFalse(c.allows(bodyEdit))
        // Filling in a form does not buy removing the document's own content (#329).
        contentEdits.forEach { assertFalse("${it.name} must not follow fill forms", c.allows(it)) }
    }

    @Test
    fun `annotate allows the same as fill forms`() {
        assertEquals(
            DocumentCapabilities.fromSecurity(restricted(PdfPermissions.FILL_FORMS)),
            DocumentCapabilities.fromSecurity(restricted(PdfPermissions.ANNOTATE)),
        )
    }

    @Test
    fun `text box edits need canAddText and nothing else`() {
        val onlyTextBoxes = DocumentCapabilities.fromSecurity(restricted(0)).copy(canAddText = true)
        val allButTextBoxes = DocumentCapabilities.FULL.copy(canAddText = false)
        listOf(textBox, editTextBox, moveTextBox).forEach {
            assertTrue(onlyTextBoxes.allows(it))
            assertFalse(allButTextBoxes.allows(it))
        }
        assertFalse(onlyTextBoxes.allows(bodyEdit))
    }

    @Test
    fun `the owner's open of a protected document has full access and is not restricted`() {
        val c = DocumentCapabilities.fromSecurity(
            PdfSecurity(isEncrypted = true, revision = 6, permissions = PdfPermissions.ALL, hasFullAccess = true))
        assertTrue(c.canChangeSecurity && c.canEditContent && c.canSign && c.canFillForms && c.canAddText)
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
