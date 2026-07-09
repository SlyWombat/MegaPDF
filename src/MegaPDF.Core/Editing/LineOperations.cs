using MegaPDF.Core.Engine;
using MegaPDF.Core.Recovery;

namespace MegaPDF.Core.Editing;

/// <summary>
/// Edits a visual line (1.1 paragraph-grade editing): the merged text goes into the
/// line's first run; the remaining runs are detached but kept alive, so undo restores
/// the original fragmentation, fonts, and layout byte-identical.
/// </summary>
public sealed class LineEditOperation(IPdfDocument document, int pageIndex, PdfTextLine line, string newText) : IPageEditOperation
{
    private readonly List<(PdfTextRun Run, DetachedTextRun Handle)> _detached = [];

    public int PageIndex { get; } = pageIndex;

    public string Description => "text edit";

    public TextEditOutcome? LastOutcome { get; private set; }

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        LastOutcome = page.SetTextRunText(line.Runs[0], newText);
        // Detach the rest, highest object index first so earlier indexes stay valid.
        _detached.Clear();
        foreach (var run in line.Runs.Skip(1).OrderByDescending(r => r.ObjectIndex))
            _detached.Add((run, page.DetachTextRun(run)));
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        // Reinsert ascending so every original index lands where it came from.
        foreach (var (run, handle) in _detached.OrderBy(d => d.Run.ObjectIndex))
            page.RestoreTextRun(handle, run.ObjectIndex);
        _detached.Clear();
        page.SetTextRunText(line.Runs[0], line.Runs[0].Text);
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new LineRestoreEntry(PageIndex, line.Runs[0].ObjectIndex, line.Runs[0].Text,
            line.Runs.Skip(1).OrderBy(r => r.ObjectIndex).Select(RestoreRun.From).ToArray())
        : new LineEditEntry(PageIndex, line.Runs[0].ObjectIndex, newText,
            line.Runs.Skip(1).Select(r => r.ObjectIndex).OrderByDescending(i => i).ToArray());
}

/// <summary>Deletes a whole visual line (clearing the inline editor); fully undoable.</summary>
public sealed class DeleteLineOperation(IPdfDocument document, int pageIndex, PdfTextLine line) : IPageEditOperation
{
    private readonly List<(PdfTextRun Run, DetachedTextRun Handle)> _detached = [];

    public int PageIndex { get; } = pageIndex;

    public string Description => "delete text";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        _detached.Clear();
        foreach (var run in line.Runs.OrderByDescending(r => r.ObjectIndex))
            _detached.Add((run, page.DetachTextRun(run)));
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        foreach (var (run, handle) in _detached.OrderBy(d => d.Run.ObjectIndex))
            page.RestoreTextRun(handle, run.ObjectIndex);
        _detached.Clear();
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new LineRestoreEntry(PageIndex, FirstIndex: -1, FirstText: null,
            line.Runs.OrderBy(r => r.ObjectIndex).Select(RestoreRun.From).ToArray())
        : new LineDeleteEntry(PageIndex, line.Runs.Select(r => r.ObjectIndex).OrderByDescending(i => i).ToArray());
}
