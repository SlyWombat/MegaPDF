using MegaPDF.Core.Engine;
using MegaPDF.Core.Recovery;

namespace MegaPDF.Core.Editing;

/// <summary>
/// Edits a visual line (1.1 paragraph-grade editing): the merged text goes into the
/// line's first run; the remaining runs leave the page but are kept alive, and so do the
/// hidden copies a producer drew under any run for fake bold, an outline or a shadow (#136).
/// The first run's untouched original is handed back by the engine, so undo restores the
/// original fragmentation, fonts, and layout byte-identical (#117).
/// </summary>
public sealed class LineEditOperation(IPdfDocument document, int pageIndex, PdfTextLine line, string newText) : IPageEditOperation
{
    private DetachedTextRun? _originals;
    // What the last apply took, for the undo's journal entry once the handle is spent.
    private IReadOnlyList<DetachedPart> _taken = [];

    public int PageIndex { get; } = pageIndex;

    public string Description => "text edit";

    public TextEditOutcome? LastOutcome { get; private set; }

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        // One engine call for the whole line: taking runs and copies one at a time moves the
        // indexes of those still to be taken (#136).
        LastOutcome = page.SetLineText(line.Runs, newText, out var originals);
        _originals = originals;
        _taken = originals.Parts;
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        // Every object goes back at its own index, the edited run off first.
        page.RestoreDetached(_originals!);
        _originals = null;
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? LineJournal.EditUndo(PageIndex, line.Runs, _taken)
        : new LineEditEntry(PageIndex, line.Runs[0].ObjectIndex, newText,
            line.Runs.Skip(1).Select(r => r.ObjectIndex).OrderByDescending(i => i).ToArray());
}

/// <summary>Deletes a whole visual line (clearing the inline editor), hidden copies and all (#136); fully undoable.</summary>
public sealed class DeleteLineOperation(IPdfDocument document, int pageIndex, PdfTextLine line) : IPageEditOperation
{
    private DetachedTextRun? _detached;
    private IReadOnlyList<DetachedPart> _taken = [];

    public int PageIndex { get; } = pageIndex;

    public string Description => "delete text";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        _detached = page.DetachTextRuns(line.Runs);
        _taken = _detached.Parts;
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.RestoreDetached(_detached!);
        _detached = null;
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? LineJournal.DeleteUndo(PageIndex, line.Runs, _taken)
        : new LineDeleteEntry(PageIndex, line.Runs.Select(r => r.ObjectIndex).OrderByDescending(i => i).ToArray());
}

/// <summary>
/// The journal entries that undo body-text edits on replay (#136). Replay recreates text in a
/// standard face, so it cannot bring back a producer's hidden copies byte for byte; it recreates
/// each copy exactly like the run it copies, at the copy's own index. PDFium hides the second of
/// two identical objects again, and every index a later journalled edit names lines up with the
/// page that edit was made on.
/// </summary>
internal static class LineJournal
{
    /// <summary>
    /// Undo of an edit of <paramref name="runs"/> (the first took the text) that took
    /// <paramref name="taken"/>. When the first run had hidden copies, its own text cannot simply be
    /// set back, since its copies are recreated in a standard face: the entry has no FirstText,
    /// and replay takes the edited run off and recreates the whole line.
    /// </summary>
    public static LineRestoreEntry EditUndo(int pageIndex, IReadOnlyList<PdfTextRun> runs, IReadOnlyList<DetachedPart> taken)
    {
        var first = runs[0];
        if (taken.Count == 0)
            return new LineRestoreEntry(pageIndex, first.ObjectIndex, first.Text,
                runs.Skip(1).OrderBy(r => r.ObjectIndex).Select(RestoreRun.From).ToArray());
        if (taken.Any(p => p.CopyOf == first.ObjectIndex))
            return new LineRestoreEntry(pageIndex, first.ObjectIndex, FirstText: null, Restores(runs, taken));
        return new LineRestoreEntry(pageIndex, first.ObjectIndex, first.Text,
            Restores(runs, taken.Where(p => p.ObjectIndex != first.ObjectIndex)));
    }

    /// <summary>Undo of a delete of <paramref name="runs"/> that took <paramref name="taken"/>.</summary>
    public static LineRestoreEntry DeleteUndo(int pageIndex, IReadOnlyList<PdfTextRun> runs, IReadOnlyList<DetachedPart> taken) =>
        new(pageIndex, FirstIndex: -1, FirstText: null,
            taken.Count == 0 ? runs.OrderBy(r => r.ObjectIndex).Select(RestoreRun.From).ToArray() : Restores(runs, taken));

    /// <summary>A run, or a hidden copy as the run it copies, at the part's own index; ascending.</summary>
    private static RestoreRun[] Restores(IReadOnlyList<PdfTextRun> runs, IEnumerable<DetachedPart> parts)
    {
        var byIndex = runs.ToDictionary(r => r.ObjectIndex);
        return parts
            .Where(p => byIndex.ContainsKey(p.CopyOf >= 0 ? p.CopyOf : p.ObjectIndex))
            .OrderBy(p => p.ObjectIndex)
            .Select(p => RestoreRun.From(byIndex[p.CopyOf >= 0 ? p.CopyOf : p.ObjectIndex]) with { Index = p.ObjectIndex })
            .ToArray();
    }
}
