using MegaPDF.Core.Engine;
using MegaPDF.Core.Recovery;

namespace MegaPDF.Core.Editing;

/// <summary>
/// Reversible placement of a ✗ mark on a drawn (non-form) checkbox square (SDD §3.2).
/// Re-applying after undo creates a fresh annotation, so the id is re-captured each time.
/// </summary>
public sealed class AddMarkOperation(IPdfDocument document, int pageIndex, PdfRect squareBounds) : IPageEditOperation
{
    public int PageIndex { get; } = pageIndex;

    public string Description => "check box";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        // Reusing the id across undo/redo keeps other operations' references valid.
        CurrentId = page.AddCheckMarkStamp(squareBounds, CurrentId);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.RemoveStampAnnotation(CurrentId!);
    }

    internal string? CurrentId { get; private set; }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new RemoveStampEntry(PageIndex, CurrentId!)
        : new AddMarkEntry(PageIndex, squareBounds.X, squareBounds.Y, squareBounds.Width, squareBounds.Height, CurrentId!);
}

/// <summary>Reversible removal of a previously placed mark (clicking a checked box unchecks it).</summary>
public sealed class RemoveMarkOperation(IPdfDocument document, int pageIndex, string annotationId, PdfRect markBounds) : IPageEditOperation
{
    public int PageIndex { get; } = pageIndex;

    public string Description => "uncheck box";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        page.RemoveStampAnnotation(annotationId);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.AddCheckMarkStamp(EquivalentSquare, annotationId);
    }

    /// <summary>
    /// AddCheckMarkStamp insets by 10% of the square; expanding the mark rect by the
    /// inverse (12.5%) reproduces the original square, so the restored mark lands
    /// exactly where it was.
    /// </summary>
    private PdfRect EquivalentSquare
    {
        get
        {
            var grow = Math.Max(markBounds.Width, markBounds.Height) * 0.125;
            return new PdfRect(markBounds.X - grow, markBounds.Y - grow, markBounds.Width + 2 * grow, markBounds.Height + 2 * grow);
        }
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new AddMarkEntry(PageIndex, EquivalentSquare.X, EquivalentSquare.Y, EquivalentSquare.Width, EquivalentSquare.Height, annotationId)
        : new RemoveStampEntry(PageIndex, annotationId);
}
