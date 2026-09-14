import Foundation

/// What the app lets the user do to the open document, from its security (#131).
/// The mapping is ADR-004 §2, the same on every platform:
///
/// | permission | what it gates |
/// |---|---|
/// | modify | editing the document's text, text boxes |
/// | annotate | signatures, stamps and check marks |
/// | fill forms | form fields (also allowed by annotate) |
///
/// Pure, so the policy is tested without a document.
struct DocumentCapabilities: Equatable {
    /// Retyping the document's own text; adding, correcting, moving and removing text boxes.
    let canEditContent: Bool
    /// Placing, moving and removing signatures, and adding or removing check marks.
    let canSign: Bool
    /// Ticking the document's own form fields.
    let canFillForms: Bool
    /// Setting, changing or removing the password: only an open with full access (ADR-004 §3).
    let canChangeSecurity: Bool
    /// Protected and opened without full access: the app says so and offers the owner password.
    let isRestricted: Bool

    init(security: PdfSecurity) {
        // Full access is the owner's open: it may change the security itself, so it may
        // change anything, whatever the permission bits say.
        let full = security.hasFullAccess
        canEditContent = full || security.allows(.modify)
        canSign = full || security.allows(.annotate)
        canFillForms = full || security.allows(.fillForms) || security.allows(.annotate)
        canChangeSecurity = full
        isRestricted = security.isEncrypted && !full
    }

    /// Whether this open may apply `operation` — the backstop behind every gated tool.
    func allows(_ operation: PdfEditOperation) -> Bool {
        switch operation {
        case is BodyTextEditOperation, is BodyTextDeleteOperation,
             is TextBoxOperation, is EditTextBoxOperation, is MoveTextBoxOperation:
            return canEditContent
        case is StampOperation, is MoveStampOperation, is MarkOperation:
            return canSign
        case is FieldToggleOperation:
            return canFillForms
        default:
            // An operation this list doesn't know yet needs every permission.
            return canEditContent && canSign && canFillForms
        }
    }
}
