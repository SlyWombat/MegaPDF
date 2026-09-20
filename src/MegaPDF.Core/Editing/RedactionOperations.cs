using MegaPDF.Core.Engine;
using MegaPDF.Core.Recovery;

namespace MegaPDF.Core.Editing;

/// <summary>
/// The marks one gesture made for redaction (#173, #329). Nothing is removed and nothing on
/// the page changes: a mark is the core's own and is never written to the file.
///
/// **One gesture is one operation**, however many marks it made: a drag across six lines is
/// six core marks and one press of Undo. That is why <see cref="Place"/> exists — the call
/// that answers "did this drag cover text?" *makes* the marks as it answers, so the gesture
/// is placed through that call and the operation is built from what came back, rather than
/// an operation that marks again on top of marks that are already there.
///
/// <see cref="Apply"/> is therefore the **redo** path only, and replays the recorded
/// rectangles rather than re-running the text selection: re-running it would re-derive
/// glyph runs from the page as it is *now*, and the person is owed the rectangle they saw.
/// A mark is an area; the glyph snapping only decided what the area was. The core never
/// reuses an id for the life of a document, so a re-mark takes fresh ids and the page's
/// are re-read after every apply.
/// </summary>
public sealed class MarkForRedactionOperation : IPageEditOperation
{
    private readonly IReadOnlyList<PdfRect> _rects;
    private List<int> _ids;

    /// <param name="rects">The areas as they now exist: what the core grew the drag to, not the raw drag.</param>
    /// <param name="ids">The marks the page carries for them at this moment.</param>
    private MarkForRedactionOperation(IPdfDocument document, int pageIndex,
                                      IReadOnlyList<PdfRect> rects, IReadOnlyList<int> ids)
    {
        Document = document;
        PageIndex = pageIndex;
        _rects = rects;
        _ids = [.. ids];
    }

    private IPdfDocument Document { get; }

    public int PageIndex { get; }

    public string Description => "redaction mark";

    /// <summary>A mark never reaches the file (ADR-005 decision 1).</summary>
    public bool ChangesTheFile => false;

    /// <summary>The marks this gesture made, so the view can select one of them.</summary>
    public IReadOnlyList<int> MarkIds => _ids;

    /// <summary>
    /// Marks the area a gesture covered, or null when it marked nothing — which is a
    /// gesture to forget rather than an entry in the history that undoes to nothing.
    ///
    /// A drag across text marks the text, grown to whole glyphs, so half a glyph is never
    /// left behind; a drag across a picture marks the rectangle. The core hands back the
    /// rectangles it made, and those are what an undo and a redo put back.
    /// </summary>
    public static MarkForRedactionOperation? Place(IPdfDocument document, int pageIndex, PdfRect selection)
    {
        using var page = document.GetPage(pageIndex);
        // The text selection MAKES the marks as it answers, so its answer is read as ids
        // rather than as a yes/no: using it as a test is what left marks outside the
        // history on all four platforms (#329).
        var ids = page.MarkTextForRedaction(selection).ToList();
        if (ids.Count == 0)
        {
            var id = page.MarkForRedaction(selection);
            if (id < 0)
                return null;
            ids.Add(id);
        }

        var rects = MarksOf(page, ids);
        return rects.Count == 0 ? null : new MarkForRedactionOperation(document, pageIndex, rects, ids);
    }

    /// <summary>What the page carries for these ids — the core's rectangles, which are the truth.</summary>
    private static List<PdfRect> MarksOf(IPdfPage page, IReadOnlyCollection<int> ids) =>
        [.. page.GetRedactionMarks().Where(m => ids.Contains(m.MarkId)).Select(m => m.Bounds).OrderBy(r => r.Y)];

    public void Apply()
    {
        using var page = Document.GetPage(PageIndex);
        _ids = [];
        foreach (var rect in _rects)
        {
            var id = page.MarkForRedaction(rect);
            if (id >= 0)
                _ids.Add(id);
        }
    }

    public void Revert()
    {
        using var page = Document.GetPage(PageIndex);
        foreach (var id in _ids)
            page.RemoveRedactionMark(id);
        _ids = [];
    }

    /// <summary>
    /// A mark is not journalled: the journal is what brings back what a crash lost, and a
    /// mark is not part of the document — a recovered file must not carry marks nobody can
    /// see the reason for. Kept honest for the journals older versions wrote.
    /// </summary>
    public JournalEntry ToJournalEntry(bool inverse)
    {
        var bounds = _rects.Count > 0 ? _rects[0] : new PdfRect(0, 0, 0, 0);
        return inverse
            ? new RedactionMarkRemoveEntry(PageIndex, bounds.X, bounds.Y, bounds.Width, bounds.Height)
            : new RedactionMarkAddEntry(PageIndex, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
}

/// <summary>
/// Drops every mark on the document as **one** undo step (#329): a person who says "clear
/// all marks" means one action, not one per mark and not one per page, and Undo puts every
/// one of them back where it was.
///
/// The rectangles are captured before the clear, because only the core knows them and a
/// clear that cannot be undone would be the bug this operation exists to fix.
/// </summary>
public sealed class ClearRedactionMarksOperation : IPageEditOperation
{
    private readonly IReadOnlyDictionary<int, IReadOnlyList<PdfRect>> _marksByPage;

    /// <param name="pageIndex">The page the UI treats as this operation's own. A clear spans pages.</param>
    private ClearRedactionMarksOperation(IPdfDocument document, int pageIndex,
                                         IReadOnlyDictionary<int, IReadOnlyList<PdfRect>> marksByPage)
    {
        Document = document;
        PageIndex = pageIndex;
        _marksByPage = marksByPage;
    }

    private IPdfDocument Document { get; }

    public int PageIndex { get; }

    public string Description => "clear redaction marks";

    /// <summary>Clearing marks changes nothing in the file either.</summary>
    public bool ChangesTheFile => false;

    /// <summary>Every mark on the document, or null when there is nothing to clear.</summary>
    public static ClearRedactionMarksOperation? Capture(IPdfDocument document, int pageIndex)
    {
        if (document.RedactionMarkCount == 0)
            return null;

        var marksByPage = new Dictionary<int, IReadOnlyList<PdfRect>>();
        for (var i = 0; i < document.PageCount; i++)
        {
            using var page = document.GetPage(i);
            var rects = page.GetRedactionMarks().Select(m => m.Bounds).ToList();
            if (rects.Count > 0)
                marksByPage[i] = rects;
        }
        return marksByPage.Count == 0 ? null : new ClearRedactionMarksOperation(document, pageIndex, marksByPage);
    }

    /// <summary>The pages this clear emptied, so the view knows which overlays to redraw.</summary>
    public IReadOnlyCollection<int> Pages => [.. _marksByPage.Keys];

    public void Apply() => Document.ClearRedactionMarks();

    public void Revert()
    {
        foreach (var (pageIndex, rects) in _marksByPage)
        {
            using var page = Document.GetPage(pageIndex);
            foreach (var rect in rects)
                page.MarkForRedaction(rect);
        }
    }

    public JournalEntry ToJournalEntry(bool inverse) =>
        new RedactionMarkClearEntry(PageIndex, [.. _marksByPage.SelectMany(p => p.Value.Select(r => (p.Key, r)))]);
}

/// <summary>
/// Removes a redaction mark — clicking one selects it, ✕ or Delete removes it. Undo puts it
/// back, which is safe for exactly the reason marking is: nothing has been removed yet.
/// </summary>
public sealed class RemoveRedactionMarkOperation(IPdfDocument document, int pageIndex, int markId, PdfRect bounds)
    : IPageEditOperation
{
    private int _markId = markId;

    public int PageIndex { get; } = pageIndex;

    public string Description => "remove redaction mark";

    /// <summary>Removing a mark takes nothing out of the file — nothing was ever in it.</summary>
    public bool ChangesTheFile => false;

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        page.RemoveRedactionMark(_markId);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        _markId = page.MarkForRedaction(bounds);
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new RedactionMarkAddEntry(PageIndex, bounds.X, bounds.Y, bounds.Width, bounds.Height)
        : new RedactionMarkRemoveEntry(PageIndex, bounds.X, bounds.Y, bounds.Width, bounds.Height);
}

/// <summary>
/// Moves or resizes a mark. Like the others here it changes nothing in the document.
/// </summary>
public sealed class MoveRedactionMarkOperation(IPdfDocument document, int pageIndex, int markId, PdfRect from,
                                               PdfRect to) : IPageEditOperation
{
    public int PageIndex { get; } = pageIndex;

    public string Description => "move redaction mark";

    /// <summary>A mark can be dragged and resized without the file noticing.</summary>
    public bool ChangesTheFile => false;

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        page.MoveRedactionMark(markId, to);
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        page.MoveRedactionMark(markId, from);
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new RedactionMarkMoveEntry(PageIndex, markId, to.X, to.Y, to.Width, to.Height,
                                     from.X, from.Y, from.Width, from.Height)
        : new RedactionMarkMoveEntry(PageIndex, markId, from.X, from.Y, from.Width, from.Height,
                                     to.X, to.Y, to.Width, to.Height);
}

/// <summary>
/// What the apps say after a redaction, built from the report (#173): "3 areas redacted:
/// 41 characters, 1 image, 2 form fields". The wording is assembled here so all four
/// platforms count the same things in the same order.
/// </summary>
public static class RedactionSummary
{
    /// <summary>
    /// The list of what was removed, e.g. "41 characters, 1 image". Callers pass their own
    /// localised formats, so this holds the logic and none of the words.
    /// </summary>
    /// <param name="counts">The report's counts.</param>
    /// <param name="plural">Formats the plural form of a kind, given the count.</param>
    /// <param name="singular">The singular form of a kind.</param>
    /// <param name="nothing">What to say when nothing countable was removed.</param>
    public static string Removed(RedactionCounts counts, Func<RedactionKind, int, string> plural,
                                 Func<RedactionKind, string> singular, string nothing)
    {
        var parts = new List<string>();
        void Add(RedactionKind kind, int n)
        {
            if (n <= 0)
                return;
            parts.Add(n == 1 ? singular(kind) : plural(kind, n));
        }

        // Characters first: it is what people mark, and what they want to hear went.
        Add(RedactionKind.Characters, counts.Characters);
        Add(RedactionKind.Images, counts.Images);
        Add(RedactionKind.FormFields, counts.FormFields);
        // Annotations minus the fields already counted: a widget is both, and saying so
        // twice would read as more than was removed.
        Add(RedactionKind.Annotations, Math.Max(0, counts.Annotations - counts.FormFields));
        return parts.Count == 0 ? nothing : string.Join(", ", parts);
    }

    /// <summary>The kinds the summary counts, in the order it says them.</summary>
    public enum RedactionKind
    {
        Characters,
        Images,
        FormFields,
        Annotations,
    }
}
