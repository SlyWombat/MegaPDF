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
/// Reversible whiteout move/resize (drag or corner handle, SDD §3.3, #3).
///
/// A whiteout has no native "move in place" the way a signature or a redaction mark
/// does — it is page content, not an annotation, and there is no primitive that
/// repositions one without touching the object list. So a move is the same two
/// primitives Add and Remove already use: detach the rectangle that is there, append
/// a fresh one at the new bounds. That is also, byte for byte, what dragging this
/// chrome replaced — "remove, then redraw it by hand" — so a move costs nothing a
/// person could not already do, it just takes one gesture and one undo step instead
/// of two. The object index changes every time, which is why callers re-read
/// <see cref="CurrentObjectIndex"/> after Apply rather than assuming the one they
/// passed in still points at anything.
/// </summary>
public sealed class MoveWhiteoutOperation(
    IPdfDocument document, int pageIndex, int objectIndex, PdfRect oldBounds, PdfRect newBounds) : IPageEditOperation
{
    private int _currentIndex = objectIndex;

    public int PageIndex { get; } = pageIndex;

    public string Description => "move whiteout";

    /// <summary>Where the rectangle landed after the last Apply/Revert — a fresh object each time.</summary>
    public int CurrentObjectIndex => _currentIndex;

    public void Apply() => Move(newBounds);

    public void Revert() => Move(oldBounds);

    private void Move(PdfRect bounds)
    {
        using var page = document.GetPage(PageIndex);
        page.DetachObjectAt(_currentIndex);
        _currentIndex = page.AppendWhiteout(bounds);
    }

    public JournalEntry ToJournalEntry(bool inverse)
    {
        var (from, to) = inverse ? (newBounds, oldBounds) : (oldBounds, newBounds);
        return new MoveWhiteoutEntry(PageIndex,
            from.X, from.Y, from.Width, from.Height, to.X, to.Y, to.Width, to.Height);
    }
}

/// <summary>
/// Adds a new text box (standard font, appended above any whiteout). The result is a
/// regular text run — subsequent edits go through the normal line machinery.
/// </summary>
public sealed class AddTextBoxOperation(IPdfDocument document, int pageIndex, string text, double fontSize, PdfPoint topLeft, string fontName = StandardTextBoxFonts.Default) : IPageEditOperation
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
            _objectIndex = page.AppendTextBox(text, fontSize, topLeft, fontName);
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
        : new TextBoxAddEntry(PageIndex, text, fontSize, topLeft.X, topLeft.Y, fontName);
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

/// <summary>
/// Adds a Shift+Enter note of more than one line as one undo step (#4).
///
/// There is no multi-line text object in the format this app writes — a "line" of
/// added text is already the whole of what <see cref="AddTextBoxOperation"/> places —
/// so a note of several lines is that many objects, one per line, stacked top to
/// bottom at the face's own line height. That is also what keeps each line
/// individually editable afterwards: it never stopped being an ordinary text box,
/// there are just more than one of them, and Undo takes the whole note rather than
/// one line at a time because it is the gesture that made all of them.
/// </summary>
public sealed class AddTextBoxesOperation(
    IPdfDocument document, int pageIndex, IReadOnlyList<string> lines, double fontSize,
    PdfPoint topLeft, string fontName = StandardTextBoxFonts.Default) : IPageEditOperation
{
    /// <summary>Single spacing for the face's own size — the same rule a wrapped paragraph reads at.</summary>
    public const double LineHeightFactor = 1.2;

    private readonly int[] _objectIndexes = new int[lines.Count];
    private readonly DetachedTextRun?[] _detached = new DetachedTextRun?[lines.Count];
    private bool _everApplied;

    public int PageIndex { get; } = pageIndex;

    public string Description => "add text";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        if (!_everApplied)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var lineTop = new PdfPoint(topLeft.X, topLeft.Y + (i * fontSize * LineHeightFactor));
                _objectIndexes[i] = page.AppendTextBox(lines[i], fontSize, lineTop, fontName);
            }
            _everApplied = true;
            return;
        }

        // Redo: put every detached line back at its own recorded index, lowest first —
        // the exact reverse of the descending detach Revert() does below, and the order
        // they were first created in.
        foreach (var i in Enumerable.Range(0, lines.Count).OrderBy(i => _objectIndexes[i]))
        {
            page.RestoreTextRun(_detached[i]!, _objectIndexes[i]);
            _detached[i] = null;
        }
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        // Highest index first: taking a lower one off first would shift every index
        // still to come out from under this loop (the same rule LineDeleteEntry's
        // descending order exists for).
        foreach (var i in Enumerable.Range(0, lines.Count).OrderByDescending(i => _objectIndexes[i]))
            _detached[i] = page.DetachObjectAt(_objectIndexes[i]);
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new TextBoxesDeleteEntry(PageIndex, [.. _objectIndexes])
        // Replay re-adds through AppendTextBox, top to bottom, so every line keeps its
        // movable tag (mirrors TextBoxAddEntry for the single-line case).
        : new TextBoxesAddEntry(PageIndex, [.. lines], fontSize, topLeft.X, topLeft.Y, fontName);
}

/// <summary>
/// Restyling a placed text box (#43): text, size and face change together, under the
/// same id, anchored to the bottom-left corner the box already sits on — so growing
/// 12 pt to 18 pt makes it taller upward rather than sinking it through the rule.
///
/// Both directions are byte-identical: the object being replaced is detached and kept,
/// never rebuilt from a description, so undo and redo each restore the exact object
/// that was there.
/// </summary>
public sealed class RestyleTextBoxOperation(
    IPdfDocument document, int pageIndex, int objectIndex, PdfTextRun run,
    string newText, string newFontName, double newFontSize) : IPageEditOperation
{
    private DetachedTextRun? _original;
    private DetachedTextRun? _restyled;

    /// <summary>The box keeps its identity across the restyle, or gains one if it had none.</summary>
    private string Id { get; } = run.TextBoxId ?? $"text:{Guid.NewGuid()}";

    private PdfPoint Anchor => new(run.Bounds.X, run.Bounds.Bottom);

    public int PageIndex { get; } = pageIndex;

    public string Description => run.Text == newText ? "restyle text" : "edit text";

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        _original = page.DetachObjectAt(objectIndex);
        if (_restyled is not null)
        {
            // Redo: put the exact restyled object back.
            page.RestoreTextRun(_restyled, objectIndex);
            _restyled = null;
        }
        else
        {
            page.InsertStyledTextBox(objectIndex, newText, newFontName, newFontSize, Anchor, Id);
        }
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        _restyled = page.DetachObjectAt(objectIndex);
        page.RestoreTextRun(_original!, objectIndex);
        _original = null;
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new TextBoxRestyleEntry(PageIndex, objectIndex, run.Text,
            run.TextBoxFont ?? StandardTextBoxFonts.Default, run.FontSize,
            Anchor.X, Anchor.Y, Id)
        : new TextBoxRestyleEntry(PageIndex, objectIndex, newText, newFontName, newFontSize,
            Anchor.X, Anchor.Y, Id);
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
