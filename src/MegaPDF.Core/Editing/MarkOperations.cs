using MegaPDF.Core.Engine;

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
        // AddCheckMarkStamp insets by 10% of the square; expand the mark rect by the
        // inverse (12.5%) so the restored mark lands exactly where it was.
        var grow = Math.Max(markBounds.Width, markBounds.Height) * 0.125;
        var square = new PdfRect(markBounds.X - grow, markBounds.Y - grow, markBounds.Width + 2 * grow, markBounds.Height + 2 * grow);
        page.AddCheckMarkStamp(square, annotationId);
    }
}
