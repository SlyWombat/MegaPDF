import Foundation

/// What the app lets the user do to the open document, from its security (#131).
/// The mapping is ADR-004 §2, the same on every platform:
///
/// | capability | granted by | what it gates |
/// |---|---|---|
/// | canFillForms | fill forms or annotate | form fields |
/// | canSign | fill forms or annotate | signatures, stamps and check marks |
/// | canAddText | modify, fill forms or annotate | text boxes |
/// | canEditContent | modify | editing the document's own text |
///
/// A form that allows filling lets people fill in everything it offers: its fields,
/// check marks, signatures and text boxes. Changing the document's own text needs
/// modify. Annotate implies form filling (ISO 32000).
///
/// Pure, so the policy is tested without a document.
struct DocumentCapabilities: Equatable {
    /// Retyping or deleting the document's own text.
    let canEditContent: Bool
    /// Placing, moving and removing signatures, and adding or removing check marks.
    let canSign: Bool
    /// Ticking the document's own form fields.
    let canFillForms: Bool
    /// Adding, correcting, restyling, moving and removing text boxes.
    let canAddText: Bool
    /// Setting, changing or removing the password: only an open with full access (ADR-004 §3).
    let canChangeSecurity: Bool
    /// Protected and opened without full access: the app says so and offers the owner password.
    let isRestricted: Bool

    init(security: PdfSecurity) {
        // Full access is the owner's open: it may change the security itself, so it may
        // change anything, whatever the permission bits say.
        let full = security.hasFullAccess
        let modify = full || security.allows(.modify)
        let fillIn = full || security.allows(.fillForms) || security.allows(.annotate)
        canEditContent = modify
        canSign = fillIn
        canFillForms = fillIn
        canAddText = modify || fillIn
        canChangeSecurity = full
        isRestricted = security.isEncrypted && !full
    }

    /// Whether this open may apply `operation` — the backstop behind every gated tool.
    func allows(_ operation: PdfEditOperation) -> Bool {
        switch operation {
        case is BodyTextEditOperation, is BodyTextDeleteOperation,
             is RedactMarkOperation, is MoveRedactionMarkOperation, is ClearRedactionMarksOperation:
            // A redaction removes the document's own content, so it needs the same
            // permission the Redact command itself does (#329).
            return canEditContent
        case is TextBoxOperation, is EditTextBoxOperation, is MoveTextBoxOperation:
            return canAddText
        case is StampOperation, is MoveStampOperation, is MarkOperation:
            return canSign
        case is FieldToggleOperation:
            return canFillForms
        default:
            // An operation this list doesn't know yet needs every permission.
            return canEditContent && canSign && canFillForms && canAddText
        }
    }
}
