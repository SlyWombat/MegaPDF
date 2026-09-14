namespace MegaPDF.Core.Engine.Pdfium;

/// <summary>
/// The PDFium constants the desktop engine still names. Every PDFium call now goes
/// through the shared engine core (ADR-003, #105–#112), so there are no P/Invokes
/// into pdfium.dll left here — only the error codes <see cref="PdfLoadException"/>
/// reports and the object types the core's <c>megapdf_object_type</c> returns.
/// </summary>
internal static class PdfiumNative
{
    public const uint FPDF_ERR_FILE = 2;
    public const uint FPDF_ERR_FORMAT = 3;
    public const uint FPDF_ERR_PASSWORD = 4;
    /// <summary>A security handler PDFium does not support — a certificate handler, say (#131).</summary>
    public const uint FPDF_ERR_SECURITY = 6;
    public const int FPDF_PAGEOBJ_TEXT = 1;
}

/// <summary>
/// The process-wide lock the desktop adapter takes around multi-call sequences
/// (open, page load, dispose). The core serialises every individual call on its
/// own mutex; this keeps a count-then-fill pair from interleaving with another
/// thread's edit.
/// </summary>
internal static class PdfiumLibrary
{
    public static readonly object Lock = new();
}
