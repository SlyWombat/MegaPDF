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
        CurrentId = page.AddCheckMarkStamp(squareBounds);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.RemoveStampAnnotation(CurrentId!);
        CurrentId = null;
    }

    internal string? CurrentId { get; private set; }
}

/// <summary>Reversible removal of a previously placed mark (clicking a checked box unchecks it).</summary>
public sealed class RemoveMarkOperation(IPdfDocument document, int pageIndex, string annotationId, PdfRect squareBounds) : IPageEditOperation
{
    private string _currentId = annotationId;

    public int PageIndex { get; } = pageIndex;

    public string Description => "uncheck box";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        page.RemoveStampAnnotation(_currentId);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        _currentId = page.AddCheckMarkStamp(squareBounds);
    }
}
