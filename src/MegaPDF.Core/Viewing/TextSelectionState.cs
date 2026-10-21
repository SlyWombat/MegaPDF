using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Viewing;

/// <summary>
/// Each page's selectable text (#710, contract 11), loaded once and kept until the page changes:
/// the snapshot a drag queries on every pointer move. One per open document, on both desktops, so
/// the two cannot disagree about when a snapshot goes stale.
///
/// A load takes the core lock and can take about 16 ms on a dense page, so <see cref="LoadAsync"/>
/// runs it off the UI thread and a view asks for it as a page is drawn; <see cref="Get"/> loads in
/// place for the rare press that arrives first. The queries on a loaded snapshot are lock-free.
///
/// Stale is the same three events <see cref="ScanSearcher"/> already follows: an edit to a page
/// (<see cref="PageChanged"/>), a page operation that renumbers them (<see cref="Renumbered"/>),
/// and a redaction, a replay or the document closing (<see cref="Clear"/>). A snapshot that goes
/// stale is disposed, and any selection on it means nothing in the next one, so
/// <see cref="Invalidated"/> tells the view to drop it.
/// </summary>
public sealed class SelectableTextCache : IDisposable
{
    private readonly IPdfDocument _document;
    private readonly object _gate = new();
    private readonly Dictionary<int, PdfSelectableText> _pages = [];
    private int _generation;
    private bool _disposed;

    public SelectableTextCache(IPdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _document = document;
    }

    /// <summary>
    /// Raised, on whatever thread made the change, when snapshots were dropped: the page index,
    /// or null for every page.
    /// </summary>
    public event Action<int?>? Invalidated;

    /// <summary>The page's snapshot if it is already loaded; never loads.</summary>
    public PdfSelectableText? Peek(int pageIndex)
    {
        lock (_gate)
            return _pages.GetValueOrDefault(pageIndex);
    }

    /// <summary>
    /// The page's snapshot, loading it here if need be. Throws <see cref="ObjectDisposedException"/>
    /// once the document has closed.
    /// </summary>
    public PdfSelectableText Get(int pageIndex)
    {
        while (true)
        {
            int generation;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_pages.TryGetValue(pageIndex, out var loaded))
                    return loaded;
                generation = _generation;
            }

            PdfSelectableText fresh;
            using (var page = _document.GetPage(pageIndex))
                fresh = page.LoadSelectableText();

            lock (_gate)
            {
                if (!_disposed && generation == _generation)
                {
                    // Two loads of the same page can race; the first one in is kept.
                    if (_pages.TryGetValue(pageIndex, out var winner))
                    {
                        fresh.Dispose();
                        return winner;
                    }
                    _pages[pageIndex] = fresh;
                    return fresh;
                }
            }
            // The document changed while this page was being read: what was read may already
            // be wrong. Read it again rather than hand back a snapshot of a page that is gone.
            fresh.Dispose();
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    /// <summary>
    /// Loads the page's snapshot off the calling thread. Null when the document closed first or
    /// the page could not be read: a page with no snapshot simply selects nothing.
    /// </summary>
    public Task<PdfSelectableText?> LoadAsync(int pageIndex) => Task.Run(() =>
    {
        try
        {
            return (PdfSelectableText?)Get(pageIndex);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    });

    /// <summary>The page's content changed: its snapshot is stale.</summary>
    public void PageChanged(int pageIndex)
    {
        PdfSelectableText? stale;
        lock (_gate)
        {
            _generation++;
            _pages.Remove(pageIndex, out stale);
        }
        stale?.Dispose();
        Invalidated?.Invoke(pageIndex);
    }

    /// <summary>
    /// Pages moved, arrived or left (#174). Every snapshot is dropped rather than renumbered: they
    /// are a cache, and one on the wrong index selects the wrong page's text.
    /// </summary>
    public void Renumbered() => Clear();

    /// <summary>Everything is stale: a redaction, a replay, a flatten.</summary>
    public void Clear()
    {
        List<PdfSelectableText> stale;
        lock (_gate)
        {
            _generation++;
            stale = [.. _pages.Values];
            _pages.Clear();
        }
        foreach (var text in stale)
            text.Dispose();
        Invalidated?.Invoke(null);
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        Clear();
    }
}

/// <summary>
/// The text selection on one document (#710): an anchor caret where it began and a focus caret
/// where it ends, either order, possibly on different pages. What both desktops hold, so a drag,
/// Shift+click, Shift+arrow, Select All and Copy mean the same on each.
///
/// It holds positions only. Drawing it is each platform's: <see cref="RangeOn"/> says what part
/// of a page to highlight, and the platform asks that page's snapshot for the rectangles.
/// </summary>
public sealed class TextSelectionState
{
    /// <summary>Where the selection began; null when there is none.</summary>
    public PdfTextPosition? Anchor { get; private set; }

    /// <summary>Where it ends — the end that moves; null when there is none.</summary>
    public PdfTextPosition? Focus { get; private set; }

    /// <summary>Raised after any change: the pages the selection covered before and after, inclusive.</summary>
    public event Action<(int First, int Last)?, (int First, int Last)?>? Changed;

    /// <summary>Whether anything is selected: a selection whose two ends meet is nothing.</summary>
    public bool IsEmpty => Anchor is not { } a || Focus is not { } f || a.CompareTo(f) == 0;

    /// <summary>The first and last page the selection touches, or null.</summary>
    public (int First, int Last)? Pages =>
        Anchor is { } a && Focus is { } f
            ? (Math.Min(a.PageIndex, f.PageIndex), Math.Max(a.PageIndex, f.PageIndex))
            : null;

    /// <summary>Starts a selection at one caret; nothing is selected until it is extended.</summary>
    public void Begin(PdfTextPosition at) => Set(at, at);

    /// <summary>Moves the focus end, keeping the anchor; begins one if there is none.</summary>
    public void ExtendTo(PdfTextPosition focus) => Set(Anchor ?? focus, focus);

    /// <summary>Selects exactly this, as a double-click on a word does.</summary>
    public void Select(PdfTextPosition anchor, PdfTextPosition focus) => Set(anchor, focus);

    /// <summary>
    /// Every page of the document, first caret to last (Ctrl/Cmd+A). The last page's end is
    /// <see cref="int.MaxValue"/> until <see cref="Step"/> needs a real number: every range is
    /// clamped to its page, so the whole document is selected without loading every page.
    /// </summary>
    public void SelectAll(int pageCount)
    {
        if (pageCount <= 0)
            Clear();
        else
            Set(new PdfTextPosition(0, 0), new PdfTextPosition(pageCount - 1, int.MaxValue));
    }

    public void Clear()
    {
        if (Anchor is null && Focus is null)
            return;
        Set(null, null);
    }

    /// <summary>
    /// Moves the focus end by <paramref name="delta"/> characters (Shift+Left, Shift+Right), on
    /// over a page break. A page's end and the next page's start are one place, so a step never
    /// lands on a break and does nothing visible. <paramref name="pageLength"/> reads a page's
    /// character count; a page with none is stepped over.
    /// </summary>
    public void Step(int delta, int pageCount, Func<int, int> pageLength)
    {
        ArgumentNullException.ThrowIfNull(pageLength);
        if (Focus is not { } focus || pageCount <= 0 || delta == 0)
            return;
        var page = Math.Clamp(focus.PageIndex, 0, pageCount - 1);
        var caret = Math.Clamp(focus.Caret, 0, pageLength(page)) + delta;
        while (caret < 0 && page > 0)
        {
            page--;
            caret += pageLength(page);
        }
        while (caret > pageLength(page) && page < pageCount - 1)
        {
            caret -= pageLength(page);
            page++;
        }
        caret = Math.Clamp(caret, 0, pageLength(page));
        Set(Anchor ?? focus, new PdfTextPosition(page, caret));
    }

    /// <summary>What of page <paramref name="pageIndex"/> is selected, or null.</summary>
    public PdfTextRange? RangeOn(int pageIndex, int pageLength) =>
        Anchor is { } a && Focus is { } f ? PdfTextSelection.RangeOnPage(a, f, pageIndex, pageLength) : null;

    /// <summary>
    /// The selected text as the core gives it — words joined by one space, <c>\n</c> where a line
    /// ends, pages joined by one <c>\n</c> — read through <paramref name="text"/>, which may load
    /// pages that are not on screen. Empty when nothing is selected.
    /// </summary>
    public string GetText(SelectableTextCache text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return IsEmpty ? "" : PdfTextSelection.GetText(Anchor!.Value, Focus!.Value, text.Get);
    }

    private void Set(PdfTextPosition? anchor, PdfTextPosition? focus)
    {
        var before = Pages;
        Anchor = anchor;
        Focus = focus;
        Changed?.Invoke(before, Pages);
    }
}
