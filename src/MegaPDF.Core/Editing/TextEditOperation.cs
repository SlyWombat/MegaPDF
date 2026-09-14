using MegaPDF.Core.Engine;
using MegaPDF.Core.Recovery;

namespace MegaPDF.Core.Editing;

/// <summary>
/// Reversible body-text edit (SDD §3.1): replaces a text run's content, in its own font
/// when that can carry the new text and a standard face otherwise. The engine hands back
/// the untouched original run, and revert puts it back byte-identical — whichever tier the
/// edit took (#117).
/// </summary>
public sealed class TextEditOperation(IPdfDocument document, int pageIndex, PdfTextRun run, string newText) : IPageEditOperation
{
    private DetachedTextRun? _original;
    private IReadOnlyList<DetachedPart> _taken = [];

    public int PageIndex { get; } = pageIndex;

    public string Description => "text edit";

    /// <summary>Set by Apply: whether the edit needed tier-2 font substitution (SDD §3.1).</summary>
    public TextEditOutcome? LastOutcome { get; private set; }

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        LastOutcome = page.SetTextRunText(run, newText, out var original);
        _original = original;
        _taken = original.Parts;
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.RestoreOriginalTextRun(_original!, run.ObjectIndex);
        _original = null;
    }

    // A run drawn with hidden copies (#136) is undone on replay like a line of one.
    public JournalEntry ToJournalEntry(bool inverse) => inverse && _taken.Count > 1
        ? LineJournal.EditUndo(PageIndex, [run], _taken)
        : new TextEditEntry(PageIndex, run.ObjectIndex, inverse ? run.Text : newText);
}

/// <summary>
/// Deletes a text run (SDD §3.1: clearing all text in the inline editor removes it).
/// The native object is kept alive across undo/redo, so revert restores the original
/// font and layout byte-identical.
/// </summary>
public sealed class DeleteTextOperation(IPdfDocument document, int pageIndex, PdfTextRun run) : IPageEditOperation
{
    private DetachedTextRun? _detached;
    private IReadOnlyList<DetachedPart> _taken = [];

    public int PageIndex { get; } = pageIndex;

    public string Description => "delete text";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        // The hidden copies drawn under the run leave with it (#136).
        _detached = page.DetachTextRun(run);
        _taken = _detached.Parts;
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.RestoreTextRun(_detached!, run.ObjectIndex);
        _detached = null;
    }

    // A run taken with hidden copies (#136) is undone on replay like a line of one, copies recreated.
    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? _taken.Count > 1
            ? LineJournal.DeleteUndo(PageIndex, [run], _taken)
            : new TextRestoreEntry(PageIndex, run.ObjectIndex, run.Text, run.FontName, run.FontSize,
                run.Bounds.X, run.Bounds.Y, run.Bounds.Width, run.Bounds.Height)
        : new TextDeleteEntry(PageIndex, run.ObjectIndex);
}
