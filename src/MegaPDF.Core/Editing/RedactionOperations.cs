using MegaPDF.Core.Engine;
using MegaPDF.Core.Recovery;

namespace MegaPDF.Core.Editing;

/// <summary>A mark that has come back under a new core id, from the operation that re-marked it (#429).</summary>
public readonly record struct RedactionMarkRename(int PageIndex, int OldId, int NewId);

/// <summary>
/// An operation that names redaction marks by the id the core gave them.
///
/// <c>MarkForRedaction</c> hands out a fresh id for every area marked and the core never
/// reuses one for the life of the document, so a mark that an undo puts back is not the id
/// anything recorded earlier is holding (#429). An operation that re-marks reports the swap in
/// <see cref="LastRenames"/>; <see cref="UndoStack"/> hands that to every other operation,
/// which follows it in <see cref="Rebind"/>. Without it the undo of a move recorded before a
/// removal names a mark the removal's own undo has already replaced, the engine answers false,
/// and the Undo the person pressed does nothing at all.
/// </summary>
public interface IRedactionMarkEdit : IEditOperation
{
    /// <summary>What the last Apply or Revert re-marked. Empty unless this one handed out ids.</summary>
    IReadOnlyList<RedactionMarkRename> LastRenames { get; }

    /// <summary>Follows <paramref name="renames"/>: the same marks, under the ids the core has now.</summary>
    void Rebind(IReadOnlyList<RedactionMarkRename> renames);
}

/// <summary>Bookkeeping shared by the operations that hold mark ids.</summary>
internal static class RedactionMarkIds
{
    /// <summary>Points every id in <paramref name="ids"/> that <paramref name="rename"/> renames at the new one.</summary>
    internal static void Rebind(List<int?> ids, RedactionMarkRename rename)
    {
        for (var i = 0; i < ids.Count; i++)
        {
            if (ids[i] == rename.OldId)
                ids[i] = rename.NewId;
        }
    }
}

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
/// reuses an id for the life of a document, so a re-mark takes fresh ids, and what those
/// ids replace is reported as a rename so the rest of the history follows them (#429).
/// </summary>
public sealed class MarkForRedactionOperation : IPageEditOperation, IRedactionMarkEdit
{
    private readonly IReadOnlyList<PdfRect> _rects;

    /// <summary>Per rect, the id the core has for its mark now — null while the mark is off the page.</summary>
    private readonly List<int?> _live;

    /// <summary>
    /// Per rect, the last id its mark carried. Kept across a removal, because that is the id
    /// the rest of the history is still holding when this operation marks the area again.
    /// </summary>
    private readonly List<int?> _named;

    private IReadOnlyList<RedactionMarkRename> _renames = [];

    /// <param name="rects">The areas as they now exist: what the core grew the drag to, not the raw drag.</param>
    /// <param name="ids">The marks the page carries for them at this moment, one per rect in order.</param>
    private MarkForRedactionOperation(IPdfDocument document, int pageIndex,
                                      IReadOnlyList<PdfRect> rects, IReadOnlyList<int> ids)
    {
        Document = document;
        PageIndex = pageIndex;
        _rects = rects;
        _live = [.. rects.Select((_, i) => i < ids.Count ? ids[i] : (int?)null)];
        _named = [.. _live];
    }

    private IPdfDocument Document { get; }

    public int PageIndex { get; }

    public string Description => "redaction mark";

    /// <summary>A mark never reaches the file (ADR-005 decision 1).</summary>
    public bool ChangesTheFile => false;

    /// <summary>The marks this gesture made, so the view can select one of them.</summary>
    public IReadOnlyList<int> MarkIds => [.. _live.Where(id => id.HasValue).Select(id => id!.Value)];

    public IReadOnlyList<RedactionMarkRename> LastRenames => _renames;

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

        // Rectangle and id are taken from the same marks in the same order: the operation
        // pairs them, and a rename that paired a rect with another mark's id would move the
        // wrong mark (#429).
        var marks = MarksOf(page, ids);
        return marks.Count == 0
            ? null
            : new MarkForRedactionOperation(document, pageIndex,
                                            [.. marks.Select(m => m.Bounds)], [.. marks.Select(m => m.MarkId)]);
    }

    /// <summary>What the page carries for these ids — the core's marks, which are the truth.</summary>
    private static List<RedactionMark> MarksOf(IPdfPage page, IReadOnlyCollection<int> ids) =>
        [.. page.GetRedactionMarks().Where(m => ids.Contains(m.MarkId)).OrderBy(m => m.Bounds.Y)];

    public void Apply()
    {
        using var page = Document.GetPage(PageIndex);
        var renames = new List<RedactionMarkRename>();
        for (var i = 0; i < _rects.Count; i++)
        {
            var id = page.MarkForRedaction(_rects[i]);
            _live[i] = id >= 0 ? id : null;
            if (id < 0)
                continue;   // nothing in that area: no mark, no rename
            if (_named[i] is { } was && was != id)
                renames.Add(new RedactionMarkRename(PageIndex, was, id));
            _named[i] = id;
        }
        _renames = renames;
    }

    public void Revert()
    {
        using var page = Document.GetPage(PageIndex);
        for (var i = 0; i < _live.Count; i++)
        {
            if (_live[i] is { } id)
                page.RemoveRedactionMark(id);
            // _named keeps the id, so a redo can say which one its fresh mark replaces.
            _live[i] = null;
        }
        _renames = [];
    }

    public void Rebind(IReadOnlyList<RedactionMarkRename> renames)
    {
        foreach (var rename in renames)
        {
            if (rename.PageIndex != PageIndex)
                continue;
            RedactionMarkIds.Rebind(_live, rename);
            RedactionMarkIds.Rebind(_named, rename);
        }
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
public sealed class ClearRedactionMarksOperation : IPageEditOperation, IRedactionMarkEdit
{
    private readonly IReadOnlyDictionary<int, IReadOnlyList<PdfRect>> _marksByPage;

    /// <summary>Per page, per rect, the last id that mark carried — a clear's undo re-marks them all (#429).</summary>
    private readonly IReadOnlyDictionary<int, List<int?>> _named;

    private IReadOnlyList<RedactionMarkRename> _renames = [];

    /// <param name="pageIndex">The page the UI treats as this operation's own. A clear spans pages.</param>
    private ClearRedactionMarksOperation(IPdfDocument document, int pageIndex,
                                         IReadOnlyDictionary<int, IReadOnlyList<PdfRect>> marksByPage,
                                         IReadOnlyDictionary<int, List<int?>> named)
    {
        Document = document;
        PageIndex = pageIndex;
        _marksByPage = marksByPage;
        _named = named;
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
        var named = new Dictionary<int, List<int?>>();
        for (var i = 0; i < document.PageCount; i++)
        {
            using var page = document.GetPage(i);
            // Ids as well as rectangles: the undo re-marks the rectangles, and the ids are what
            // its fresh marks replace in the rest of the history (#429).
            var marks = page.GetRedactionMarks().ToList();
            if (marks.Count == 0)
                continue;
            marksByPage[i] = [.. marks.Select(m => m.Bounds)];
            named[i] = [.. marks.Select(m => (int?)m.MarkId)];
        }
        return marksByPage.Count == 0
            ? null
            : new ClearRedactionMarksOperation(document, pageIndex, marksByPage, named);
    }

    public IReadOnlyList<RedactionMarkRename> LastRenames => _renames;

    /// <summary>The pages this clear emptied, so the view knows which overlays to redraw.</summary>
    public IReadOnlyCollection<int> Pages => [.. _marksByPage.Keys];

    public void Apply()
    {
        Document.ClearRedactionMarks();
        _renames = [];
    }

    public void Revert()
    {
        var renames = new List<RedactionMarkRename>();
        foreach (var (pageIndex, rects) in _marksByPage)
        {
            using var page = Document.GetPage(pageIndex);
            var named = _named[pageIndex];
            for (var i = 0; i < rects.Count; i++)
            {
                var id = page.MarkForRedaction(rects[i]);
                if (id < 0)
                    continue;
                if (named[i] is { } was && was != id)
                    renames.Add(new RedactionMarkRename(pageIndex, was, id));
                named[i] = id;
            }
        }
        _renames = renames;
    }

    public void Rebind(IReadOnlyList<RedactionMarkRename> renames)
    {
        foreach (var rename in renames)
        {
            if (_named.TryGetValue(rename.PageIndex, out var ids))
                RedactionMarkIds.Rebind(ids, rename);
        }
    }

    public JournalEntry ToJournalEntry(bool inverse) =>
        new RedactionMarkClearEntry(PageIndex,
            [.. _marksByPage.SelectMany(p => p.Value.Select(r => new MarkRect(p.Key, r.X, r.Y, r.Width, r.Height)))]);
}

/// <summary>
/// Removes a redaction mark — clicking one selects it, ✕ or Delete removes it. Undo puts it
/// back, which is safe for exactly the reason marking is: nothing has been removed yet.
/// </summary>
public sealed class RemoveRedactionMarkOperation(IPdfDocument document, int pageIndex, int markId, PdfRect bounds)
    : IPageEditOperation, IRedactionMarkEdit
{
    /// <summary>The id the core has for the mark now, or null while the removal stands.</summary>
    private int? _live = markId;

    /// <summary>The last id the mark carried: what the rest of the history is holding (#429).</summary>
    private int _markId = markId;

    private IReadOnlyList<RedactionMarkRename> _renames = [];

    public int PageIndex { get; } = pageIndex;

    public string Description => "remove redaction mark";

    /// <summary>Removing a mark takes nothing out of the file — nothing was ever in it.</summary>
    public bool ChangesTheFile => false;

    public IReadOnlyList<RedactionMarkRename> LastRenames => _renames;

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        if (_live is { } id)
            page.RemoveRedactionMark(id);
        // _markId keeps the id, so the undo below can say which one its fresh mark replaces.
        _live = null;
        _renames = [];
    }

    public void Revert()
    {
        using var page = document.GetPage(PageIndex);
        var fresh = page.MarkForRedaction(bounds);
        if (fresh < 0)
        {
            _renames = [];
            return;
        }
        // The mark is back, under an id nothing else in the history knows (#429): this is the
        // undo that used to leave a move recorded before it naming a mark that no longer exists.
        _renames = fresh == _markId ? [] : [new RedactionMarkRename(PageIndex, _markId, fresh)];
        _live = fresh;
        _markId = fresh;
    }

    public void Rebind(IReadOnlyList<RedactionMarkRename> renames)
    {
        foreach (var rename in renames)
        {
            if (rename.PageIndex != PageIndex)
                continue;
            if (_markId == rename.OldId)
                _markId = rename.NewId;
            if (_live == rename.OldId)
                _live = rename.NewId;
        }
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new RedactionMarkAddEntry(PageIndex, bounds.X, bounds.Y, bounds.Width, bounds.Height)
        : new RedactionMarkRemoveEntry(PageIndex, bounds.X, bounds.Y, bounds.Width, bounds.Height);
}

/// <summary>
/// Moves or resizes a mark. Like the others here it changes nothing in the document.
///
/// The core moves an id in place, so undo and redo keep the same mark. The *id* is not as
/// durable as the mark: a removal or a clear undone between this operation and its turn puts
/// the mark back under a new one, and <see cref="Rebind"/> is how this follows it (#429).
/// </summary>
public sealed class MoveRedactionMarkOperation(IPdfDocument document, int pageIndex, int markId, PdfRect from,
                                               PdfRect to) : IPageEditOperation, IRedactionMarkEdit
{
    private int _markId = markId;

    public int PageIndex { get; } = pageIndex;

    public string Description => "move redaction mark";

    /// <summary>A mark can be dragged and resized without the file noticing.</summary>
    public bool ChangesTheFile => false;

    /// <summary>A move never re-marks, so it has nothing to report; it only follows.</summary>
    public IReadOnlyList<RedactionMarkRename> LastRenames => [];

    public void Apply() => Move(to);

    public void Revert() => Move(from);

    private void Move(PdfRect bounds)
    {
        using var page = document.GetPage(PageIndex);
        // False means the id is not on the page. Dropping it is how #429 stayed invisible: the
        // Undo was pressed, the mark did not move, and nothing said so. A history that cannot
        // do what it says fails out loud instead.
        if (!page.MoveRedactionMark(_markId, bounds))
            throw new InvalidOperationException($"redaction mark {_markId} is not on page {PageIndex}");
    }

    public void Rebind(IReadOnlyList<RedactionMarkRename> renames)
    {
        foreach (var rename in renames)
        {
            if (rename.PageIndex == PageIndex && rename.OldId == _markId)
                _markId = rename.NewId;
        }
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new RedactionMarkMoveEntry(PageIndex, _markId, to.X, to.Y, to.Width, to.Height,
                                     from.X, from.Y, from.Width, from.Height)
        : new RedactionMarkMoveEntry(PageIndex, _markId, from.X, from.Y, from.Width, from.Height,
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
