using MegaPDF.Core.Engine;
using MegaPDF.Core.Recovery;

namespace MegaPDF.Core.Editing;

/// <summary>
/// Marks an area for redaction (#173). Nothing is removed and nothing on the page changes:
/// the mark is the core's own and is never written to the file, so this is undoable for
/// nothing, and the recovery journal records a rectangle rather than any content.
/// </summary>
public sealed class MarkForRedactionOperation(IPdfDocument document, int pageIndex, PdfRect bounds)
    : IPageEditOperation
{
    private int _markId = -1;

    public int PageIndex { get; } = pageIndex;

    public string Description => "redaction mark";

    /// <summary>The mark's id once it has been placed, so the view can select it.</summary>
    public int MarkId => _markId;

    public void Apply()
    {
        using var page = document.GetPage(PageIndex);
        _markId = page.MarkForRedaction(bounds);
    }

    public void Revert()
    {
        if (_markId < 0)
            return;
        using var page = document.GetPage(PageIndex);
        page.RemoveRedactionMark(_markId);
        _markId = -1;
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new RedactionMarkRemoveEntry(PageIndex, bounds.X, bounds.Y, bounds.Width, bounds.Height)
        : new RedactionMarkAddEntry(PageIndex, bounds.X, bounds.Y, bounds.Width, bounds.Height);
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
