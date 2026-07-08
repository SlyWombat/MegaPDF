using MegaPDF.Core.Engine;
using MegaPDF.Core.Recovery;

namespace MegaPDF.Core.Editing;

/// <summary>
/// Reversible body-text edit (SDD §3.1 tier 1): replaces a text run's content in place.
/// The captured run carries the object index and the original text for revert.
/// </summary>
public sealed class TextEditOperation(IPdfDocument document, int pageIndex, PdfTextRun run, string newText) : IPageEditOperation
{
    public int PageIndex { get; } = pageIndex;

    public string Description => "text edit";

    /// <summary>Set by Apply: whether the edit needed tier-2 font substitution (SDD §3.1).</summary>
    public TextEditOutcome? LastOutcome { get; private set; }

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        LastOutcome = page.SetTextRunText(run, newText);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.SetTextRunText(run, run.Text);
    }

    public JournalEntry ToJournalEntry(bool inverse) =>
        new TextEditEntry(PageIndex, run.ObjectIndex, inverse ? run.Text : newText);
}

/// <summary>
/// Deletes a text run (SDD §3.1: clearing all text in the inline editor removes it).
/// The native object is kept alive across undo/redo, so revert restores the original
/// font and layout byte-identical.
/// </summary>
public sealed class DeleteTextOperation(IPdfDocument document, int pageIndex, PdfTextRun run) : IPageEditOperation
{
    private DetachedTextRun? _detached;

    public int PageIndex { get; } = pageIndex;

    public string Description => "delete text";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        _detached = page.DetachTextRun(run);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.RestoreTextRun(_detached!, run.ObjectIndex);
        _detached = null;
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new TextRestoreEntry(PageIndex, run.ObjectIndex, run.Text, run.FontName, run.FontSize,
            run.Bounds.X, run.Bounds.Y, run.Bounds.Width, run.Bounds.Height)
        : new TextDeleteEntry(PageIndex, run.ObjectIndex);
}
