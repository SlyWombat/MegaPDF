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
/// | canAssemblePages | assemble or modify | rotating, deleting, moving, inserting, importing pages |
/// | canExtractPages | copy | saving a selection of pages as a new file |
///
/// A form that allows filling lets people fill in everything it offers: its fields,
/// check marks, signatures and text boxes. Changing the document's own text needs
/// modify. Annotate implies form filling (ISO 32000).
///
/// **The last two read the Assemble bit** (P-bit 11, ISO 32000-2 Table 22: "assemble the
/// document — insert, rotate or delete pages"), which before #174 no platform but Android
/// consulted at all. This leg consults it, and it does so to match Android and the engine:
/// contract 10 refuses every changing call with `MEGAPDF_ERR_RESTRICTED` unless the open has
/// assemble **or** modify, so an app that did not ask would simply be showing the engine's
/// refusal after the fact instead of explaining before it. Whether that is the rule all four
/// platforms should follow — and in particular whether taking pages *out* is copying rather
/// than assembling — is **#558's** decision, not this file's; this is the honest statement of
/// what iOS does today, not a vote.
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
    /// Turning, deleting, moving, inserting and importing pages (#174, contract 10).
    let canAssemblePages: Bool
    /// Saving a selection of pages as a new file (#174): a copy the person may make of a
    /// document whose security permits copying, which is the permission `megapdf_pages_extract`
    /// itself checks.
    let canExtractPages: Bool
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
        // Assemble is its own bit: a document can permit form filling while forbidding page
        // reordering, which is exactly the shape of a form somebody is meant to complete but
        // not restructure (#558). Modify grants it too, as contract 10's own check does.
        canAssemblePages = modify || security.allows(.assemble)
        canExtractPages = full || security.allows(.copy)
        canChangeSecurity = full
        isRestricted = security.isEncrypted && !full
    }

    /// Whether this open may apply `operation` — the backstop behind every gated tool.
    func allows(_ operation: PdfEditOperation) -> Bool {
        switch operation {
        case is BodyTextEditOperation, is BodyTextDeleteOperation,
             is RedactMarkOperation, is MoveRedactionMarkOperation, is ClearRedactionMarksOperation,
             is WhiteoutOperation, is MoveWhiteoutOperation:
            // A redaction removes the document's own content, so it needs the same
            // permission the Redact command itself does (#329). A whiteout rewrites the
            // page's content stream to cover part of it (#3), which is the same `modify`
            // permission -- covering is not annotating, whatever it looks like.
            return canEditContent
        case is TextBoxOperation, is EditTextBoxOperation, is MoveTextBoxOperation,
             is AddTextBoxesOperation:
            // A note of several lines is several text boxes (#4), and needs exactly what one
            // text box needs.
            return canAddText
        case is StampOperation, is MoveStampOperation, is MarkOperation:
            return canSign
        case is FieldToggleOperation:
            return canFillForms
        case is PageStructureOperation:
            // #174: every page change needs assemble (or modify), which is what contract 10
            // checks too — so this is the backstop, not the only check. An extract is not here
            // because it is not an operation: it changes nothing and is never recorded.
            return canAssemblePages
        default:
            // An operation this list doesn't know yet needs every permission.
            return canEditContent && canSign && canFillForms && canAddText
        }
    }

    /// Whether `operation` is one of the "fill this form" tools #457 explains rather than
    /// performs on a dynamic-XFA document: signing/stamping, added text, a check mark, a
    /// form field. Redaction and the document's own text (`allows`'s `canEditContent`
    /// group) are page tools, not filling, and keep working exactly as they do today.
    static func isFillingOperation(_ operation: PdfEditOperation) -> Bool {
        switch operation {
        case is TextBoxOperation, is EditTextBoxOperation, is MoveTextBoxOperation,
             is AddTextBoxesOperation,
             is StampOperation, is MoveStampOperation, is MarkOperation,
             is FieldToggleOperation:
            return true
        default:
            return false
        }
    }
}
