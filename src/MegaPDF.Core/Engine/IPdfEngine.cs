namespace MegaPDF.Core.Engine;

/// <summary>
/// The single seam to the underlying PDF library (SDD §4.2, §4.3).
/// Nothing outside the engine adapter may reference PDFium directly.
/// </summary>
public interface IPdfEngine : IDisposable
{
    IPdfDocument Open(string filePath);
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

    /// <summary>Places the ✗/✓ mark stamp over a drawn square; returns the stamp id (SDD §3.2).</summary>
    string AddCheckMarkStamp(PdfRect squareBounds);

    /// <summary>Tiered body-text edit — see SDD §3.1. Throws <see cref="TextEditException"/> per tier rules.</summary>
    TextEditOutcome SetTextRunText(PdfTextRun run, string newText);

    void SetFormFieldValue(PdfFormField field, string value);
    void ToggleCheckbox(PdfFormField field);

    /// <summary>Places an image stamp annotation (signature or checkbox mark) and returns its id (SDD §3.2, §3.3).</summary>
    string AddStampAnnotation(ReadOnlyMemory<byte> pngBytes, PdfRect bounds);
    void MoveStampAnnotation(string annotationId, PdfRect newBounds);
    void RemoveStampAnnotation(string annotationId);
}

/// <summary>A rendered page bitmap: 32-bit BGRA, top-down rows.</summary>
public sealed record RenderedPage(int PixelWidth, int PixelHeight, byte[] Bgra);

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
