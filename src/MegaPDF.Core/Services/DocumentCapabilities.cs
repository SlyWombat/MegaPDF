using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Services;

/// <summary>
/// Which of the desktop apps' tools an open of a document may use (#131, ADR-004 §2).
///
/// The permission bits are the document owner's; this is the one place they are mapped
/// to MegaPDF's tools, so Windows and macOS cannot disagree about what "modify" covers:
///
/// | capability | granted by | tools |
/// |---|---|---|
/// | CanFillForms | fill forms or annotate | form fields |
/// | CanSign | fill forms or annotate | signatures, stamps and check marks |
/// | CanAddText | modify, fill forms or annotate | text boxes and their font and size |
/// | CanEditContent | modify | editing the document's own text, whiteout |
/// | CanShrink | modify | shrink-for-email |
/// | CanPrint | print | printing |
///
/// A form that allows filling lets people fill in everything it offers: its fields,
/// check marks on printed boxes, signatures and text boxes. Changing the document
/// itself needs modify. Annotate implies form filling (ISO 32000).
///
/// Saving is not gated: a restricted open has nothing it may change, and Save a copy
/// stays available. Changing or removing security needs full access.
/// </summary>
public sealed record DocumentCapabilities(
    bool CanEditContent,
    bool CanSign,
    bool CanFillForms,
    bool CanAddText,
    bool CanPrint,
    bool CanShrink,
    bool CanChangeSecurity,
    bool IsRestricted)
{
    /// <summary>An unprotected document: every tool, nothing to unlock.</summary>
    public static DocumentCapabilities Unprotected { get; } = From(PdfSecurity.Unprotected);

    public static DocumentCapabilities From(PdfSecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);

        // Full access is every tool whatever the bits say: an owner-password open reports
        // them all, and so does a document that restricts nothing.
        bool Allows(PdfPermissions permission) => security.HasFullAccess || security.Allows(permission);

        var modify = Allows(PdfPermissions.Modify);
        var fillIn = Allows(PdfPermissions.FillForms) || Allows(PdfPermissions.Annotate);
        return new DocumentCapabilities(
            CanEditContent: modify,
            CanSign: fillIn,
            CanFillForms: fillIn,
            CanAddText: modify || fillIn,
            CanPrint: Allows(PdfPermissions.Print),
            CanShrink: modify,
            CanChangeSecurity: security.HasFullAccess,
            IsRestricted: security.IsEncrypted && !security.HasFullAccess);
    }

    /// <summary>
    /// Whether a click on this kind of page region may do anything. Asked before an
    /// editor opens or a stamp is selected, so a restricted document never shows an
    /// editor it would then refuse.
    /// </summary>
    public bool Allows(PageHitKind kind) => kind switch
    {
        PageHitKind.FormCheckbox or PageHitKind.FormTextField => CanFillForms,
        PageHitKind.DrawnCheckbox or PageHitKind.StampAnnotation => CanSign,
        PageHitKind.TextBox => CanAddText,
        PageHitKind.TextRun or PageHitKind.Whiteout => CanEditContent,
        _ => true,
    };

    /// <summary>
    /// The central gate: whether this edit may be applied at all. Unknown operations
    /// change page content, so they need modify.
    /// </summary>
    public bool Allows(IPageEditOperation operation) => operation switch
    {
        CheckboxToggleOperation or FormTextEditOperation => CanFillForms,
        AddMarkOperation or RemoveMarkOperation
            or AddSignatureOperation or MoveSignatureOperation or RemoveSignatureOperation => CanSign,
        AddTextBoxOperation or MoveTextBoxOperation
            or RestyleTextBoxOperation or RemoveTextBoxOperation => CanAddText,
        _ => CanEditContent,
    };
}
