namespace MegaPDF.Core.Engine;

/// <summary>
/// An area marked for redaction (#173, SDD §6.2 contract 5).
/// </summary>
/// <remarks>
/// A mark is the core's own: it is never a page object and is never written to the file, so
/// a document saved with marks on it cannot carry them. That is the Acrobat failure this
/// feature exists to stop — a file that looks redacted, carrying both the content and a set
/// of rectangles announcing where the interesting content is — made structurally impossible
/// rather than left to a rule every platform has to remember on every save path.
///
/// The apps therefore draw marks themselves, in the overlay layer where find highlights and
/// selection handles already live. In exchange, marking costs no content regeneration and
/// invalidates no layout verdict, so marking a dozen words on a heavy page is instant.
/// </remarks>
/// <param name="MarkId">Stable for the mark's life; never reused by the document.</param>
/// <param name="Bounds">View space, as every rectangle the engine hands out.</param>
public readonly record struct RedactionMark(int MarkId, PdfRect Bounds);

/// <summary>Why <see cref="IPdfDocument.ApplyRedactions"/> refused.</summary>
public enum RedactionRefusalReason
{
    /// <summary>A partly covered run in a Type 3 font, whose glyphs are content streams.</summary>
    Type3Font = 1,

    /// <summary>The glyphs outside the area cannot be drawn back in the run's own font.</summary>
    FontCannotRedraw = 2,

    /// <summary>The guard saw something outside the area move or change (#118, #128).</summary>
    LayoutGuard = 3,

    /// <summary>
    /// A form XObject reaches into the area. PDFium does not write back an edit made inside
    /// a form at all (tools/pdfium/README.md), so the redaction refuses rather than leave
    /// the content behind.
    /// </summary>
    FormXObject = 4,

    /// <summary>An image's stored pixels could not be read or written back.</summary>
    Image = 5,

    /// <summary>An annotation or form field could not be removed with its value.</summary>
    Annotation = 6,

    /// <summary>PDFium refused a step.</summary>
    Engine = 7,
}

/// <summary>One page and area the redaction would not touch, and why.</summary>
public readonly record struct RedactionRefusal(int PageIndex, RedactionRefusalReason Reason, PdfRect Area,
                                               string Message);

/// <summary>
/// What a page actually lost. <paramref name="Affected"/> is <paramref name="Marked"/> grown
/// to the bounds of everything removed, which can reach past the mark: a glyph cannot be half
/// removed, so one straddling the edge takes its outside part with it. The box drawn covers
/// the mark, not the affected area — covering more would cover content still in the file.
/// </summary>
public readonly record struct RedactionAffectedArea(int PageIndex, PdfRect Marked, PdfRect Affected);

/// <summary>What a completed redaction removed, for the summary shown after saving.</summary>
public readonly record struct RedactionCounts(
    int Areas, int Pages,
    int Characters, int TextRuns, int PartialRuns, int HiddenCopies,
    int Images, int InlineImages, int SoftMasks,
    int Paths, int Shadings, int FormXObjects,
    int Annotations, int FormFields, int Links,
    int OutlineEntries, int StructureEntries, int PageLabels,
    int MetadataFields);

/// <summary>
/// The result of applying every mark on a document. <see cref="Applied"/> is true when the
/// redaction went through; otherwise nothing was touched and <see cref="Refusals"/> says why.
/// </summary>
public sealed class RedactionReport
{
    public required bool Applied { get; init; }
    public required RedactionCounts Counts { get; init; }
    public required IReadOnlyList<RedactionRefusal> Refusals { get; init; }
    public required IReadOnlyList<RedactionAffectedArea> AffectedAreas { get; init; }
}

/// <summary>
/// A redaction could not be completed. Thrown only for the case the rehearsal says cannot
/// happen — a failure part-way through applying — after which the document is poisoned and
/// can no longer be saved, so a half-redacted file can never be written.
/// </summary>
public sealed class RedactionFailedException(string message) : Exception(message);
