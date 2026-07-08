using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Editing;

/// <summary>
/// Reversible body-text edit (SDD §3.1 tier 1): replaces a text run's content in place.
/// The captured run carries the object index and the original text for revert.
/// </summary>
public sealed class TextEditOperation(IPdfDocument document, int pageIndex, PdfTextRun run, string newText) : IEditOperation
{
    public int PageIndex { get; } = pageIndex;

    public string Description => "text edit";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        page.SetTextRunText(run, newText);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.SetTextRunText(run, run.Text);
    }
}
