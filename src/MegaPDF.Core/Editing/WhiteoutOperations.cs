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
        : new TextRestoreEntry(PageIndex, _objectIndex, text, "Helvetica", fontSize,
            topLeft.X, topLeft.Y, 0, fontSize);
}
