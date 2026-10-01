package com.megapdf.android

import com.megapdf.engine.DocumentFlags
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
    /**
     * Rotating, deleting, reordering, inserting and combining pages (#174): the assemble
     * permission, or modify, which contract 10 accepts either of — ISO 32000-2 Table 22 names bit
     * 11 "assemble the document (insert, rotate or delete pages …)" and bit 4 covers it as well.
     * Until #174 nothing on any platform consulted `Assemble` at all.
     */
    val canAssemblePages: Boolean,
    /**
     * Saving a selection of pages as a new file (#174): the copy permission, which is what
     * `megapdf_pages_extract` asks for — the new file is a copy of part of this document, so a
     * document that may not be copied from may not be split either.
     */
    val canExtractPages: Boolean,
    /** Setting, changing or removing the password: full access only (decision 3). */
    val canChangeSecurity: Boolean,
    val isEncrypted: Boolean,
    /** Protected, and this open is not the owner's: the restricted notice and Unlock apply. */
    val isRestricted: Boolean,
    /**
     * The document is dynamic XFA (#456/#457): built to be filled in Adobe Reader, which
     * MegaPDF cannot do. Deliberately does not turn [canSign]/[canFillForms]/[canAddText]
     * off — those tools stay armable, so the view model can explain instead of silently
     * doing nothing, rather than a greyed-out button that looks like nothing is wrong here
     * either. View, save, share, export and every page tool are untouched.
     */
    val isDynamicXfa: Boolean = false,
    /**
     * The document carries an existing digital signature (#476/#481). Deliberately does not
     * restrict any capability here either — nothing is refused (a person may fill in and
     * overwrite a signed document if that is genuinely what they want) — it only drives the
     * confirmation [ViewerViewModel] asks before a save that would overwrite the signed
     * original; Save a copy is unaffected because the signed original stays untouched.
     */
    val isSigned: Boolean = false,
    /**
     * At least one of the document's signatures is a certification (`/DocMDP`) signature,
     * which can forbid modification outright rather than merely being invalidated by a save.
     * Always accompanied by [isSigned]; the two are separate because the overwrite warning's
     * wording differs, not because either changes what is allowed.
     */
    val isCertificationSigned: Boolean = false,
) {
    /** Whether this open may apply [operation]. An edit this list does not know needs full access. */
    fun allows(operation: PdfEditOperation): Boolean = when (operation) {
        // Redaction removes the document's own content, so it needs the same permission the
        // Redact command itself does (#329). A whiteout covers rather than removes, but it is
        // still the document's own page content rather than an annotation, so it needs the
        // same permission as the rest of this group (#3).
        is BodyTextEditOperation, is BodyTextDeleteOperation,
        is RedactMarkOperation, is MoveRedactionMarkOperation, is ClearRedactionMarksOperation,
        is WhiteoutAddOperation, is WhiteoutRemoveOperation, is MoveWhiteoutOperation,
        -> canEditContent
        is TextBoxOperation, is EditTextBoxOperation, is MoveTextBoxOperation, is AddTextBoxesOperation -> canAddText
        is StampOperation, is MoveStampOperation, is MarkOperation -> canSign
        is FieldToggleOperation -> canFillForms
        // The page tools (#174). Extract is not here: it changes nothing and is not an operation.
        is RotatePagesOperation, is DeletePagesOperation, is MovePageOperation,
        is InsertBlankPageOperation, is ImportPagesOperation,
        -> canAssemblePages
        else -> canChangeSecurity
    }

    /**
     * Whether [operation] is one of the "fill this form" tools #457 explains rather than
     * performs on a dynamic-XFA document: signing/stamping, added text, a check mark, a form
     * field. Redaction and the document's own text ([allows]'s [canEditContent] group) are
     * page tools, not filling, and keep working exactly as they do today.
     */
    fun isFillingOperation(operation: PdfEditOperation): Boolean = when (operation) {
        is TextBoxOperation, is EditTextBoxOperation, is MoveTextBoxOperation, is AddTextBoxesOperation,
        is StampOperation, is MoveStampOperation, is MarkOperation,
        is FieldToggleOperation,
        -> true
        else -> false
    }

    companion object {
        /**
         * A form that allows filling lets people fill in everything it offers: its fields,
         * check marks, signatures and text boxes. Changing the document's own text needs
         * modify. Annotate implies form filling (ISO 32000).
         */
        fun fromSecurity(security: PdfSecurity, flags: DocumentFlags = DocumentFlags.NONE): DocumentCapabilities {
            fun may(permission: Int) = security.hasFullAccess || security.allows(permission)
            val modify = may(PdfPermissions.MODIFY)
            val fillIn = may(PdfPermissions.FILL_FORMS) || may(PdfPermissions.ANNOTATE)
            return DocumentCapabilities(
                canEditContent = modify,
                canSign = fillIn,
                canFillForms = fillIn,
                canAddText = modify || fillIn,
                // Either bit, the same pair contract 10's own preflight accepts (#174).
                canAssemblePages = may(PdfPermissions.ASSEMBLE) || modify,
                canExtractPages = may(PdfPermissions.COPY),
                canChangeSecurity = security.hasFullAccess,
                isEncrypted = security.isEncrypted,
                isRestricted = security.isEncrypted && !security.hasFullAccess,
                isDynamicXfa = flags.isDynamicXfa,
                isSigned = flags.isSigned,
                isCertificationSigned = flags.isCertificationSigned,
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
