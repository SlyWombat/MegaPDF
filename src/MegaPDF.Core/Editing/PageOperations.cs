using MegaPDF.Core.Engine;
using MegaPDF.Core.Recovery;

namespace MegaPDF.Core.Editing;

/// <summary>
/// An edit that changes which pages a document has, their order, or the way up one is shown
/// (#174, core contract 10). Shared by both desktops: the operations are here, in Core, so
/// WinUI and Avalonia undo a page change the same way and write the same journal.
///
/// Two things every page operation owes its view model beyond <see cref="IEditOperation"/>:
///
/// <see cref="Shifts"/> — how the document's pages were renumbered, which is what the app
/// applies to its own index-keyed state (the page list, the render caches, the thumbnails,
/// the settled #139 pages, the selection, the search hits). Read *after* Apply: an import
/// does not know how many pages arrived until it has asked for them.
///
/// <see cref="ChangedPages"/> — which pages need drawing again, in the numbering that holds
/// after the operation. A rotation renumbers nothing and changes everything about how its
/// pages look, so the two are genuinely separate questions.
/// </summary>
public interface IPageStructureOperation : IPageEditOperation
{
    /// <summary>How the pages were renumbered, in the order the shifts happened. Read after Apply/Revert.</summary>
    IReadOnlyList<PageShift> Shifts { get; }

    /// <summary>Pages whose raster is now wrong, numbered as the document is numbered now.</summary>
    IReadOnlyList<int> ChangedPages { get; }
}

/// <summary>
/// Turns pages by quarter turns clockwise (#174). Sets each page's <c>/Rotate</c> and rewrites
/// no content, so a rotation can lose nothing — and so the #118 layout guard never judges it.
/// Its own inverse, with the turns negated, which is exactly what contract 10 promises.
/// </summary>
public sealed class RotatePagesOperation : IPageStructureOperation
{
    private readonly IPdfDocument _document;
    private readonly int[] _pages;
    private readonly int _quarterTurns;

    public RotatePagesOperation(IPdfDocument document, IReadOnlyList<int> pages, int quarterTurns)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pages);
        _document = document;
        _pages = pages.Distinct().Order().ToArray();
        _quarterTurns = quarterTurns;
        if (_pages.Length == 0)
            throw new ArgumentException("A rotation needs at least one page.", nameof(pages));
    }

    public int PageIndex => _pages[0];

    /// <summary>Nothing is renumbered, so the shift is in-place for each page turned.</summary>
    public IReadOnlyList<PageShift> Shifts => _pages.Select(PageShift.InPlace).ToList();

    public IReadOnlyList<int> ChangedPages => _pages;

    public string Description => _quarterTurns > 0 ? "rotate pages right" : "rotate pages left";

    public void Apply() => Turn(_quarterTurns);

    public void Revert() => Turn(-_quarterTurns);

    private void Turn(int quarterTurns)
    {
        foreach (var page in _pages)
            _document.RotatePage(page, quarterTurns);
    }

    public JournalEntry ToJournalEntry(bool inverse) =>
        new PagesRotateEntry(PageIndex, _pages, inverse ? -_quarterTurns : _quarterTurns);
}

/// <summary>
/// Takes pages off the document (#174), keeping each one alive so the undo puts back exactly
/// what was deleted — which is what <c>megapdf_page_restore</c> exists for.
///
/// Highest index first, so the indices still to delete do not move under the delete; and back
/// lowest first, so each page lands at the index it came from. A selection need not be
/// contiguous, which is why <see cref="Shifts"/> is a list and not one shift.
/// </summary>
public sealed class DeletePagesOperation : IPageStructureOperation
{
    private readonly IPdfDocument _document;

    /// <summary>Ascending: the indices the pages had, and the indices they go back to.</summary>
    private readonly int[] _pages;

    private readonly List<RemovedPage> _removed = [];

    public DeletePagesOperation(IPdfDocument document, IReadOnlyList<int> pages)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pages);
        _document = document;
        _pages = pages.Distinct().Order().ToArray();
        if (_pages.Length == 0)
            throw new ArgumentException("A delete needs at least one page.", nameof(pages));
    }

    public int PageIndex => _pages[0];

    /// <summary>One removal per page, highest index first — the order Apply performed them in.</summary>
    public IReadOnlyList<PageShift> Shifts =>
        _pages.OrderDescending().Select(p => PageShift.Removed(p)).ToList();

    /// <summary>Nothing is redrawn: the pages that are left are the pages they were.</summary>
    public IReadOnlyList<int> ChangedPages => [];

    /// <summary>The pages this deleted, ascending, as the document was numbered before it ran.</summary>
    public IReadOnlyList<int> Pages => _pages;

    public string Description => _pages.Length == 1 ? "delete a page" : "delete pages";

    public void Apply()
    {
        _removed.Clear();
        // Descending, so the next index to delete has not moved.
        foreach (var page in _pages.OrderDescending())
            _removed.Add(_document.DeletePage(page));
        // Kept ascending, to match the order Revert puts them back in.
        _removed.Reverse();
    }

    public void Revert()
    {
        // Ascending, so each page lands at the index it was taken from: putting the
        // lowest back first re-opens the gap the next one goes into.
        for (var i = 0; i < _pages.Length; i++)
            _document.RestorePage(_removed[i], _pages[i]);
        _removed.Clear();
    }

    /// <summary>
    /// Lets go of the pages this is still holding — for an operation dropped for good (the undo
    /// stack cleared, a redaction applied). Harmless to call twice; closing the document does
    /// the same for anything still held.
    /// </summary>
    public void DiscardHeldPages()
    {
        foreach (var page in _removed)
            _document.DiscardRemovedPage(page);
        _removed.Clear();
    }

    /// <summary>
    /// Forward, the delete itself. Inverse, "import those pages of the file on disk, back where
    /// they were" — because a journal cannot carry a page (contract 10). Best effort in the
    /// sense <see cref="TextRestoreEntry"/> is: edits made to a page before it was deleted are
    /// replayed by their own entries only if they precede the delete.
    /// </summary>
    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new PagesRestoreEntry(PageIndex, _pages)
        : new PagesDeleteEntry(PageIndex, _pages);
}

/// <summary>
/// Moves one page so it stands at another index (#174): the drag in the thumbnail strip, and
/// the Move Up/Move Down that does the same from the keyboard. Its own inverse with the two
/// indices swapped. The page dictionary is untouched, so its fields, annotations, marks and
/// everything else travel with it.
/// </summary>
public sealed class MovePageOperation(IPdfDocument document, int from, int to) : IPageStructureOperation
{
    public int PageIndex => from;

    public int From => from;
    public int To => to;

    public IReadOnlyList<PageShift> Shifts => [PageShift.Moved(from, to)];

    public IReadOnlyList<int> ChangedPages => [];

    public string Description => "move a page";

    public void Apply() => document.MovePage(from, to);

    public void Revert() => document.MovePage(to, from);

    public JournalEntry ToJournalEntry(bool inverse) =>
        inverse ? new PageMoveEntry(to, from) : new PageMoveEntry(from, to);
}

/// <summary>
/// Adds an empty page (#174). Undone by deleting it again, and the page is not kept for a
/// further undo: an empty page can be made again from the same two numbers, so holding it
/// would be holding a page nothing needs.
/// </summary>
public sealed class InsertBlankPageOperation(IPdfDocument document, int at, double widthPoints, double heightPoints)
    : IPageStructureOperation
{
    public int PageIndex => at;

    public IReadOnlyList<PageShift> Shifts => [PageShift.Inserted(at)];

    public IReadOnlyList<int> ChangedPages => [];

    public string Description => "insert a blank page";

    public void Apply() => document.InsertBlankPage(at, widthPoints, heightPoints);

    public void Revert()
    {
        var removed = document.DeletePage(at);
        document.DiscardRemovedPage(removed);
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new PagesDeleteEntry(at, [at])
        : new PageInsertBlankEntry(at, widthPoints, heightPoints);
}

/// <summary>
/// Combine (#174): pages of another file inserted into this one. Undone by taking exactly the
/// pages that arrived back off — the inverse contract 10 states, "delete(at) n times" — and the
/// count is known only once the import has run, which is why <see cref="Shifts"/> is read after
/// Apply rather than built in the constructor.
///
/// The password, when the other file needed one, is held here and never journalled: a recovery
/// journal must not carry a password (ADR-004 §7). A replay of this entry therefore cannot open
/// a protected source and is skipped, which the replayer already does for an entry that no
/// longer resolves.
/// </summary>
public sealed class ImportPagesOperation(
    IPdfDocument document, string otherPath, string? password, IReadOnlyList<int>? pages, int insertAt)
    : IPageStructureOperation
{
    private int _imported;

    public int PageIndex => insertAt;

    /// <summary>How many pages arrived; 0 until <see cref="Apply"/> has run.</summary>
    public int Imported => _imported;

    public string SourcePath => otherPath;

    public IReadOnlyList<PageShift> Shifts =>
        _imported > 0 ? [PageShift.Inserted(insertAt, _imported)] : [];

    public IReadOnlyList<int> ChangedPages => [];

    public string Description => "insert pages from a file";

    public void Apply() => _imported = document.ImportPages(otherPath, password, pages, insertAt);

    public void Revert()
    {
        // Highest first, so the next index has not moved; discarded rather than held, because
        // a redo imports them again from the file they came from.
        for (var i = _imported - 1; i >= 0; i--)
        {
            var removed = document.DeletePage(insertAt + i);
            document.DiscardRemovedPage(removed);
        }
        // _imported is deliberately left standing: the view model reads Shifts after Revert
        // (inverted) and the journal entry needs the count too. A redo re-runs Apply, which
        // sets it again from the import that actually happened.
    }

    public JournalEntry ToJournalEntry(bool inverse) => inverse
        ? new PagesDeleteEntry(insertAt, Enumerable.Range(insertAt, _imported).ToArray())
        : new PagesImportEntry(insertAt, otherPath, pages?.ToArray() ?? []);
}
