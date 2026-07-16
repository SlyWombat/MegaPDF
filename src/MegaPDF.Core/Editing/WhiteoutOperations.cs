using MegaPDF.Core.Engine;
using MegaPDF.Core.Recovery;

namespace MegaPDF.Core.Editing;

/// <summary>Places a whiteout rectangle over page content (covers images and text beneath).</summary>
public sealed class AddWhiteoutOperation(IPdfDocument document, int pageIndex, PdfRect bounds) : IPageEditOperation
{
    private int _objectIndex = -1;

    public int PageIndex { get; } = pageIndex;

    public string Description => "whiteout";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        _objectIndex = page.AppendWhiteout(bounds);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.DetachObjectAt(_objectIndex);
        _objectIndex = -1;
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new WhiteoutRemoveEntry(PageIndex, bounds.X, bounds.Y, bounds.Width, bounds.Height)
        : new WhiteoutAddEntry(PageIndex, bounds.X, bounds.Y, bounds.Width, bounds.Height);
}

/// <summary>Removes a whiteout (clicking one selects it; ✕/Delete removes). Undo restores it.</summary>
public sealed class RemoveWhiteoutOperation(IPdfDocument document, int pageIndex, int objectIndex, PdfRect bounds) : IPageEditOperation
{
    private int _currentIndex = objectIndex;
    private DetachedTextRun? _detached;

    public int PageIndex { get; } = pageIndex;

    public string Description => "remove whiteout";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        _detached = page.DetachObjectAt(_currentIndex);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.RestoreTextRun(_detached!, _currentIndex);
        _detached = null;
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new WhiteoutAddEntry(PageIndex, bounds.X, bounds.Y, bounds.Width, bounds.Height)
        : new WhiteoutRemoveEntry(PageIndex, bounds.X, bounds.Y, bounds.Width, bounds.Height);
}

/// <summary>
/// Adds a new text box (standard font, appended above any whiteout). The result is a
/// regular text run — subsequent edits go through the normal line machinery.
/// </summary>
public sealed class AddTextBoxOperation(IPdfDocument document, int pageIndex, string text, double fontSize, PdfPoint topLeft) : IPageEditOperation
{
    private int _objectIndex = -1;
    private DetachedTextRun? _detached;

    public int PageIndex { get; } = pageIndex;

    public string Description => "add text";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        if (_detached is not null)
        {
            // Redo: put the exact original object back.
            page.RestoreTextRun(_detached, _objectIndex);
            _detached = null;
        }
        else
        {
            _objectIndex = page.AppendTextBox(text, fontSize, topLeft);
        }
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        _detached = page.DetachObjectAt(_objectIndex);
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new TextDeleteEntry(PageIndex, _objectIndex)
        // Replay re-adds through AppendTextBox so the recovered box keeps its movable tag.
        : new TextBoxAddEntry(PageIndex, text, fontSize, topLeft.X, topLeft.Y);
}

/// <summary>Reversible text-box move/nudge (drag or arrow keys, SDD §3.3). Translates in place.</summary>
public sealed class MoveTextBoxOperation(
    IPdfDocument document, int pageIndex, int objectIndex, PdfRect oldBounds, PdfRect newBounds) : IPageEditOperation
{
    public int PageIndex { get; } = pageIndex;

    public string Description => "move text";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        page.MoveTextBox(objectIndex, newBounds);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.MoveTextBox(objectIndex, oldBounds);
    }

    public JournalEntry ToJournalEntry(bool inverse)
    {
        var (from, to) = inverse ? (newBounds, oldBounds) : (oldBounds, newBounds);
        return new MoveTextBoxEntry(PageIndex,
            from.X, from.Y, from.Width, from.Height, to.X, to.Y, to.Width, to.Height);
    }
}

/// <summary>Reversible text-box removal (✕/Delete on the selection). Undo restores it byte-identical.</summary>
public sealed class RemoveTextBoxOperation(IPdfDocument document, int pageIndex, int objectIndex, PdfTextRun run) : IPageEditOperation
{
    private DetachedTextRun? _detached;

    public int PageIndex { get; } = pageIndex;

    public string Description => "remove text";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        _detached = page.DetachObjectAt(objectIndex);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.RestoreTextRun(_detached!, objectIndex);
        _detached = null;
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new TextRestoreEntry(PageIndex, objectIndex, run.Text, run.FontName, run.FontSize,
            run.Bounds.X, run.Bounds.Y, run.Bounds.Width, run.Bounds.Height)
        : new TextDeleteEntry(PageIndex, objectIndex);
}
