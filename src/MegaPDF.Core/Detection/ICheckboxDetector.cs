using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Detection;

/// <summary>
/// A drawn (non-AcroForm) checkbox candidate found on a page: a small axis-aligned
/// stroked square or ballot-box glyph (SDD §3.2).
/// </summary>
public sealed record DetectedCheckbox(PdfRect Bounds);

/// <summary>
/// Heuristic detector for drawn squares that users mean as checkboxes (SDD §3.2).
/// Runs lazily on visible pages; results are cached per page by the caller.
/// Targets: ≥95% hit rate, &lt;1% false positives on the internal corpus.
/// </summary>
public interface ICheckboxDetector
{
    IReadOnlyList<DetectedCheckbox> Detect(IPdfPage page);
}
