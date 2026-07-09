namespace MegaPDF.Core.Engine;

/// <summary>
/// The single seam to the underlying PDF library (SDD §4.2, §4.3).
/// Nothing outside the engine adapter may reference PDFium directly.
/// </summary>
public interface IPdfEngine : IDisposable
{
    /// <summary>Opens a document; <paramref name="password"/> for protected files.
    /// Throws <see cref="Pdfium.PdfLoadException"/> with a password error code when one is required/wrong.</summary>
    IPdfDocument Open(string filePath, string? password = null);
}

public interface IPdfDocument : IDisposable
{
    int PageCount { get; }

    IPdfPage GetPage(int pageIndex);

    /// <summary>
    /// Writes the document to <paramref name="target"/>, using an incremental
    /// (append-only) update when possible, falling back to a full rewrite (SDD §3.4).
    /// Callers own atomicity — see <see cref="Services.AtomicFileWriter"/>.
    /// </summary>
    void Save(Stream target);

    /// <summary>
    /// Bakes marks, signatures, and form values permanently into page content
    /// (SDD §3.3 "flatten on save", off by default). Irreversible: annotations
    /// and fields stop being interactive afterwards.
    /// </summary>
    void FlattenAllPages();
}

public interface IPdfPage : IDisposable
{
    int Index { get; }

    /// <summary>Page size in PDF points.</summary>
    double Width { get; }
    double Height { get; }

    /// <summary>Renders the page to 32-bit BGRA at the given pixel size.</summary>
    RenderedPage Render(int pixelWidth, int pixelHeight);

    /// <summary>What is under this point? Drives cursor affordances and click routing (SDD §2.2).</summary>
    PageHit HitTest(PdfPoint point);

    IReadOnlyList<PdfTextRun> GetTextRuns();
    IReadOnlyList<PdfFormField> GetFormFields();

    /// <summary>
    /// Heuristic detection of drawn (non-form) checkbox squares: small, roughly
    /// square, stroked-not-filled paths (SDD §3.2). Results in top-left page space.
    /// </summary>
    IReadOnlyList<PdfRect> DetectCheckboxSquares();

    /// <summary>
    /// Places the mark stamp over a drawn square; returns the stamp id (SDD §3.2).
    /// Pass <paramref name="stampId"/> to restore a previously removed stamp under its
    /// original id — ids must stay stable across undo/redo cycles.
    /// </summary>
    string AddCheckMarkStamp(PdfRect squareBounds, string? stampId = null, CheckMarkStyle style = CheckMarkStyle.Cross);

    /// <summary>Tiered body-text edit — see SDD §3.1. Throws <see cref="TextEditException"/> per tier rules.</summary>
    TextEditOutcome SetTextRunText(PdfTextRun run, string newText);

    /// <summary>
    /// Removes a text run from the page, keeping the native object alive so
    /// <see cref="RestoreTextRun"/> can put it back byte-identical (undo).
    /// </summary>
    DetachedTextRun DetachTextRun(PdfTextRun run);

    /// <summary>Re-inserts a detached run at its original object index.</summary>
    void RestoreTextRun(DetachedTextRun detached, int objectIndex);

    /// <summary>
    /// Recreates a deleted run from recorded properties (crash-recovery replay only —
    /// uses the closest standard font, not the original).
    /// </summary>
    void InsertTextRun(int objectIndex, string text, string fontName, double fontSize, PdfRect bounds);

    void SetFormFieldValue(PdfFormField field, string value);
    void ToggleCheckbox(PdfFormField field);

    /// <summary>Places a BGRA image (alpha respected) as a stamp annotation — a signature (SDD §3.3).</summary>
    string AddImageStamp(ReadOnlyMemory<byte> bgra, int pixelWidth, int pixelHeight, PdfRect bounds, string? stampId = null);

    /// <summary>Reads back a placed image stamp's pixels, e.g. to make removal undoable.</summary>
    StampImage? GetStampImage(string annotationId);

    /// <summary>Moves/resizes a placed image stamp (signature drag/resize, SDD §3.3).</summary>
    void MoveStampAnnotation(string annotationId, PdfRect newBounds);

    /// <summary>All MegaPDF-placed stamps (marks and signatures) on this page.</summary>
    IReadOnlyList<StampInfo> GetStamps();

    void RemoveStampAnnotation(string annotationId);
}

/// <summary>A rendered page bitmap: 32-bit BGRA, top-down rows.</summary>
public sealed record RenderedPage(int PixelWidth, int PixelHeight, byte[] Bgra);

/// <summary>Pixels of a placed image stamp (BGRA).</summary>
public sealed record StampImage(byte[] Bgra, int PixelWidth, int PixelHeight);

/// <summary>A MegaPDF-placed stamp: id (mark:/sig: prefixed) and bounds in top-left page space.</summary>
public sealed record StampInfo(string Id, PdfRect Bounds);

/// <summary>Check-mark styles (SDD §3.2 + Appendix B #3: ✗ default, ✓ and regional ■).</summary>
public enum CheckMarkStyle
{
    Cross,
    Check,
    FilledSquare,
}

/// <summary>Opaque handle to a text object removed from its page but kept alive for undo.</summary>
public sealed class DetachedTextRun
{
    internal DetachedTextRun(IntPtr handle) => Handle = handle;
    internal IntPtr Handle { get; }
}

public enum PageHitKind
{
    None,
    TextRun,
    FormTextField,
    FormCheckbox,

    /// <summary>A drawn (non-form) square that reads as a checkbox — SDD §3.2.</summary>
    DrawnCheckbox,

    /// <summary>A MegaPDF-placed stamp (check mark or signature).</summary>
    StampAnnotation,
}

public sealed record PageHit(
    PageHitKind Kind,
    PdfTextRun? TextRun = null,
    PdfFormField? Field = null,
    string? AnnotationId = null,
    PdfRect? Bounds = null);

/// <summary>A contiguous run of body text sharing one font/size/color.</summary>
public sealed record PdfTextRun(int ObjectIndex, string Text, PdfRect Bounds, string FontName, double FontSize);

public enum FormFieldKind
{
    Text,
    Checkbox,
    RadioButton,
    Other,
}

public sealed record PdfFormField(string Name, FormFieldKind Kind, PdfRect Bounds, string Value, bool IsChecked = false);

/// <summary>How a body-text edit was performed (SDD §3.1 tiers 1 and 2).</summary>
public enum TextEditOutcome
{
    /// <summary>Tier 1: the document's own font rendered the new text.</summary>
    EditedInPlace,

    /// <summary>Tier 2: the font couldn't cover the new text; a similar standard font was used.</summary>
    EditedWithSubstitutedFont,
}

/// <summary>Why a body-text edit could not be performed (SDD §3.1 tier rules).</summary>
public sealed class TextEditException(TextEditFailure reason, string message) : Exception(message)
{
    public TextEditFailure Reason { get; } = reason;
}

public enum TextEditFailure
{
    /// <summary>Font lacks needed glyphs and no acceptable substitute was found (tier 2 failed).</summary>
    NoUsableFont,

    /// <summary>The text is rasterized (scanned) and cannot be edited (tier 3).</summary>
    NotExtractable,
}
