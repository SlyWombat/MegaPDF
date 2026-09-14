namespace MegaPDF.Core.Engine;

/// <summary>
/// What an open of a document may do: the standard security handler's permission bits
/// (ISO 32000-2, Table 22), as the shared core reports them (#131).
/// </summary>
[Flags]
public enum PdfPermissions : uint
{
    None = 0,
    Print = 1u << 2,
    Modify = 1u << 3,
    Copy = 1u << 4,
    Annotate = 1u << 5,
    FillForms = 1u << 8,
    Accessibility = 1u << 9,
    Assemble = 1u << 10,
    PrintHighQuality = 1u << 11,
    All = Print | Modify | Copy | Annotate | FillForms | Accessibility | Assemble | PrintHighQuality,
}

/// <summary>
/// A document's security as this open sees it (#131). <see cref="HasFullAccess"/> is true
/// for an unprotected document, an open with the owner password, or a document that
/// restricts nothing — the opens that may change or remove its security.
/// </summary>
public sealed record PdfSecurity(bool IsEncrypted, int Revision, PdfPermissions Permissions, bool HasFullAccess)
{
    public static PdfSecurity Unprotected { get; } = new(false, -1, PdfPermissions.All, true);

    public bool Allows(PdfPermissions permissions) => (Permissions & permissions) == permissions;
}

/// <summary>The document's security does not allow this; opening it with its owner password would (#131).</summary>
public sealed class DocumentRestrictedException()
    : InvalidOperationException("The document's security does not allow this without its owner password.");
