package com.megapdf.android

import com.megapdf.engine.PdfPermissions
import com.megapdf.engine.PdfSecurity

/**
 * What the open document lets the user do, from the permissions its security grants this
 * open (#131, ADR-004 decision 2). Pure, so the mapping is tested on the JVM; the view
 * model asks it before every edit, and the toolbar asks it for what to enable.
 */
data class DocumentCapabilities(
    /** The document's own text: the modify permission. */
    val canEditContent: Boolean,
    /** Signatures, stamps and check marks: fill forms, which annotate also allows. */
    val canSign: Boolean,
    /** Ticking form fields: fill forms, which annotate also allows. */
    val canFillForms: Boolean,
    /** Adding, correcting, moving and removing text boxes: modify or fill forms. */
    val canAddText: Boolean,
    /** Setting, changing or removing the password: full access only (decision 3). */
    val canChangeSecurity: Boolean,
    val isEncrypted: Boolean,
    /** Protected, and this open is not the owner's: the restricted notice and Unlock apply. */
    val isRestricted: Boolean,
) {
    /** Whether this open may apply [operation]. An edit this list does not know needs full access. */
    fun allows(operation: PdfEditOperation): Boolean = when (operation) {
        is BodyTextEditOperation, is BodyTextDeleteOperation -> canEditContent
        is TextBoxOperation, is EditTextBoxOperation, is MoveTextBoxOperation -> canAddText
        is StampOperation, is MoveStampOperation, is MarkOperation -> canSign
        is FieldToggleOperation -> canFillForms
        else -> canChangeSecurity
    }

    companion object {
        /**
         * A form that allows filling lets people fill in everything it offers: its fields,
         * check marks, signatures and text boxes. Changing the document's own text needs
         * modify. Annotate implies form filling (ISO 32000).
         */
        fun fromSecurity(security: PdfSecurity): DocumentCapabilities {
            fun may(permission: Int) = security.hasFullAccess || security.allows(permission)
            val modify = may(PdfPermissions.MODIFY)
            val fillIn = may(PdfPermissions.FILL_FORMS) || may(PdfPermissions.ANNOTATE)
            return DocumentCapabilities(
                canEditContent = modify,
                canSign = fillIn,
                canFillForms = fillIn,
                canAddText = modify || fillIn,
                canChangeSecurity = security.hasFullAccess,
                isEncrypted = security.isEncrypted,
                isRestricted = security.isEncrypted && !security.hasFullAccess,
            )
        }

        /** Nothing open, or an unprotected document: everything is allowed. */
        val FULL = fromSecurity(PdfSecurity.UNPROTECTED)
    }
}

/** Which face the Password command's dialog shows (#131, ADR-004 decision 5). */
enum class PasswordCommandMode {
    /** Without full access: say why, and offer the owner password. */
    RESTRICTED,

    /** Unprotected: set one password, which opens the document with every permission. */
    SET,

    /** Protected, with full access: change the password or remove it. */
    CHANGE,
    ;

    companion object {
        fun of(capabilities: DocumentCapabilities): PasswordCommandMode = when {
            !capabilities.canChangeSecurity -> RESTRICTED
            !capabilities.isEncrypted -> SET
            else -> CHANGE
        }
    }
}

/** Why a new password can't be used yet. */
enum class NewPasswordProblem { EMPTY, MISMATCH }

/** Null when [password] may be set: not empty, and typed the same twice. */
fun newPasswordProblem(password: String, confirmation: String): NewPasswordProblem? = when {
    password.isEmpty() -> NewPasswordProblem.EMPTY
    password != confirmation -> NewPasswordProblem.MISMATCH
    else -> null
}
