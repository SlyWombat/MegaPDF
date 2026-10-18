using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Viewing;

// Find on scanned pages (#676), the part both desktops share. The recogniser is the one
// platform-specific piece (Windows.Media.Ocr on Windows, Vision on the Mac, nothing on Linux), so
// what was read, how a hit is found in it, how the cache follows page tools and when a page is
// read at all are decided here once, as the iOS and Android builds do for their own stacks.
//
// Nothing here is written to the document or to disk: it is a transient, per-open-document cache,
// not a text layer (that is #175).

/// <summary>One recognised word: where it stands in its line's text (Start inclusive, End exclusive, UTF-16 units) and where it sits on the page (top-left origin, PDF points).</summary>
public readonly record struct OcrWord(int Start, int End, PdfRect Rect);

/// <summary>One recognised line of text, with the words that make it up.</summary>
public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words);

/// <summary>Everything the recogniser read on one page, in the order it read it.</summary>
public sealed record OcrPage(IReadOnlyList<OcrLine> Lines)
{
    /// <summary>
    /// Case-insensitive literal matches of <paramref name="term"/>, in reading order — the same
    /// contract as <see cref="IPdfPage.FindText"/>, so the caller merges the two without caring
    /// which produced a hit.
    ///
    /// A match is within one recognised line (the engine's own search can wrap across lines; this
    /// does not, because recognised lines are not guaranteed to be one paragraph's consecutive
    /// lines). Its rectangle is the union of the <b>words</b> it touches: a highlight that is a
    /// word too wide is honest about what the recogniser told us, where one that interpolates
    /// inside a word would claim a precision nobody measured.
    ///
    /// <paramref name="besides"/> are rectangles the document's own text already matched: a read
    /// hit that mostly lies on one is the same words seen twice (a text box rendered into the
    /// picture being read) and is left out.
    /// </summary>
    public IReadOnlyList<PdfSearchMatch> Matches(string term, IReadOnlyList<PdfRect>? besides = null)
    {
        if (term.Length == 0)
            return [];
        var found = new List<PdfSearchMatch>();
        foreach (var line in Lines)
        {
            var from = 0;
            while (from <= line.Text.Length)
            {
                var at = line.Text.IndexOf(term, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    break;
                var end = at + term.Length;
                var touched = line.Words.Where(w => w.Start < end && w.End > at).ToList();
                if (touched.Count > 0)
                {
                    var rect = touched.Skip(1).Aggregate(touched[0].Rect, (a, w) => Union(a, w.Rect));
                    if (besides is null || !besides.Any(b => MostlyLiesOn(rect, b)))
                        found.Add(new PdfSearchMatch([rect]));
                }
                // Past the match, not past one character: "aa" in "aaaa" is two hits, as in the engine.
                from = end;
            }
        }
        return found;
    }

    internal static PdfRect Union(PdfRect a, PdfRect b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new PdfRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    /// <summary>
    /// Whether the two overlap by at least half of the smaller one's area. Symmetric on purpose: a
    /// recognised word box is wider than the glyphs the engine matched inside it, and either may be
    /// the larger.
    /// </summary>
    internal static bool MostlyLiesOn(PdfRect a, PdfRect b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        var area = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return w > 0 && h > 0 && area > 0 && w * h / area >= 0.5;
    }
}

/// <summary>
/// The recognised pages of one open document, by page index.
///
/// <b>A missing entry means "not read yet", never "read and empty"</b>: a page that was read and
/// had no words is stored as an empty <see cref="OcrPage"/>, so a scan is not redone for every
/// search.
/// </summary>
public sealed class OcrCache
{
    private readonly Dictionary<int, OcrPage> _pages;
    /// <summary>Pages found to carry text of their own, so a text document is not asked again on every keystroke.</summary>
    private readonly HashSet<int> _textPages;

    public OcrCache() : this([], []) { }

    private OcrCache(Dictionary<int, OcrPage> pages, HashSet<int> textPages)
    {
        _pages = pages;
        _textPages = textPages;
    }

    public bool IsEmpty => _pages.Count == 0 && _textPages.Count == 0;

    public OcrPage? Get(int page) => _pages.GetValueOrDefault(page);
    public void Set(int page, OcrPage read) => _pages[page] = read;
    public bool HasText(int page) => _textPages.Contains(page);
    public void NoteText(int page) => _textPages.Add(page);

    /// <summary>
    /// Carried across a page operation, exactly like the rendered pages (#174): recognised text
    /// that stayed on the wrong page after a move would be a silent wrong answer.
    /// </summary>
    public OcrCache Shifted(IReadOnlyList<PageShift> shifts)
    {
        var pages = new Dictionary<int, OcrPage>();
        foreach (var (index, read) in _pages)
            if (PageShift.Map(shifts, index) is { } now)
                pages[now] = read;
        var text = new HashSet<int>();
        foreach (var index in _textPages)
            if (PageShift.Map(shifts, index) is { } now)
                text.Add(now);
        return new OcrCache(pages, text);
    }

    public void Remove(int page)
    {
        _pages.Remove(page);
        _textPages.Remove(page);
    }

    public void Clear()
    {
        _pages.Clear();
        _textPages.Clear();
    }
}

/// <summary>
/// Reads a rendered page. The one platform-specific piece of Find on scans: an implementation
/// answers null when it could not read the page, and an empty <see cref="OcrPage"/> when it read
/// it and found nothing. Called off the UI thread.
/// </summary>
public interface IPageRecogniser
{
    /// <summary>The longest side, in pixels, the recogniser will accept (Windows' is 10,000 and it throws past it).</summary>
    int MaxPixelsPerSide { get; }

    /// <summary>Reads <paramref name="image"/>, a render of a page that is <paramref name="pageWidth"/> × <paramref name="pageHeight"/> points.</summary>
    OcrPage? Read(RenderedPage image, double pageWidth, double pageHeight);
}

/// <summary>Render a page, decide whether it is worth reading, read it.</summary>
public static class ScanReader
{
    /// <summary>
    /// 200, not phase 0's 150 (#676, Dave 2026-10-18): on a real small-print scan, 150 missed 9 of 29
    /// hits on Windows and 5 on Android, and 200 found all 29. Vision is flat across both.
    /// </summary>
    public const double Dpi = 200;

    /// <summary>
    /// Whether the page carries text of its own — real, or an invisible OCR layer — <b>not
    /// counting boxes the person added in MegaPDF</b>. The trigger for reading is "no text layer",
    /// never "has an image": a scan that has already been through OCR has both and Find already
    /// works on it. A box laid over a scan is not a text layer: the scan under it still needs
    /// reading, and Find searches the box as ordinary text. When in doubt, true: it leaves the
    /// page to the engine's own search.
    /// </summary>
    public static bool HasOwnText(IPdfDocument document, int index)
    {
        try
        {
            using var page = document.GetPage(index);
            // GetTextRuns() does not say which runs are boxes (TextBoxId is only filled in by
            // GetTextBoxes()), so the boxes are subtracted by object index.
            var boxes = page.GetTextBoxes().Select(b => b.ObjectIndex).ToHashSet();
            return page.GetTextRuns().Any(r => !boxes.Contains(r.ObjectIndex) && !string.IsNullOrWhiteSpace(r.Text));
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Renders the page at <see cref="Dpi"/> (capped by what the recogniser accepts) and reads it.
    /// A uniform blank sheet is read as empty without being sent to the recogniser. Null when the
    /// page could not be rendered or read.
    /// </summary>
    public static OcrPage? Read(IPdfDocument document, int index, IPageRecogniser recogniser)
    {
        try
        {
            using var page = document.GetPage(index);
            var scale = Math.Min(Dpi / 72.0, (recogniser.MaxPixelsPerSide - 1) / Math.Max(page.Width, page.Height));
            var image = page.Render(
                Math.Max(1, (int)(page.Width * scale)), Math.Max(1, (int)(page.Height * scale)));
            return Blank.IsBlank(image) ? new OcrPage([]) : recogniser.Read(image, page.Width, page.Height);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"couldn't read page {index + 1} for Find: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Whether a rendered page is a uniform sheet: a sampled grid in which almost nothing differs
    /// from the corner pixel. A few specks of scanner noise stay under the line; a ruled form or a
    /// drawing goes over it and is read, as is any real scan.
    /// </summary>
    public static class Blank
    {
        public static bool IsBlank(RenderedPage image)
        {
            var w = image.PixelWidth;
            var h = image.PixelHeight;
            var px = image.Bgra;
            if (w <= 0 || h <= 0 || px.Length < w * h * 4)
                return false;
            int b0 = px[0], g0 = px[1], r0 = px[2];
            var sampled = 0;
            var different = 0;
            for (var y = 0; y < h; y += 4)
            {
                for (var x = 0; x < w; x += 4)
                {
                    sampled++;
                    var i = (y * w + x) * 4;
                    if (Math.Abs(px[i] - b0) + Math.Abs(px[i + 1] - g0) + Math.Abs(px[i + 2] - r0) > 60)
                        different++;
                }
            }
            return sampled > 0 && (double)different / sampled < 0.0002;
        }
    }
}

/// <summary>
/// Find on a scanned page: reads a page that has no text of its own, once per open document, and
/// answers searches from what was read. Thread-safe, because the search walks pages on a
/// background thread while page tools and edits arrive on the UI thread.
/// </summary>
public sealed class ScanSearcher
{
    private readonly Func<int, bool> _hasOwnText;
    private readonly Func<int, OcrPage?> _read;
    private readonly object _gate = new();
    private OcrCache _cache = new();
    /// <summary>
    /// Bumped by everything that can make a read in flight wrong — a page edit, a renumbering, a
    /// close — so a recognition that finishes afterwards is used once and not kept.
    /// </summary>
    private int _generation;

    public ScanSearcher(Func<int, bool> hasOwnText, Func<int, OcrPage?> read)
    {
        _hasOwnText = hasOwnText;
        _read = read;
    }

    public bool CacheIsEmpty
    {
        get { lock (_gate) return _cache.IsEmpty; }
    }

    public OcrPage? Cached(int page)
    {
        lock (_gate)
            return _cache.Get(page);
    }

    /// <summary>
    /// Hits found in what the recogniser reads of <paramref name="page"/> when it has no text of its
    /// own. Empty for a page with text, for one that could not be read, and for a read that an edit
    /// overtook. <paramref name="own"/> are the rectangles the document's own text already found on
    /// the page: a read hit that lands on one is the same words seen twice and is dropped.
    /// </summary>
    public IReadOnlyList<PdfSearchMatch> Hits(int page, string term, IReadOnlyList<PdfRect> own)
    {
        OcrPage? scanned;
        int started;
        lock (_gate)
        {
            if (_cache.HasText(page))
                return [];
            scanned = _cache.Get(page);
            started = _generation;
        }
        if (scanned is null)
        {
            if (_hasOwnText(page))
            {
                lock (_gate)
                    if (started == _generation)
                        _cache.NoteText(page);
                return [];
            }
            var fresh = _read(page);
            if (fresh is null)
                return [];
            lock (_gate)
            {
                if (started != _generation)
                    return [];
                _cache.Set(page, fresh);
            }
            scanned = fresh;
        }
        return scanned.Matches(term, own);
    }

    /// <summary>A page operation renumbered the document: what was read follows its page.</summary>
    public void Renumbered(IReadOnlyList<PageShift> shifts)
    {
        lock (_gate)
        {
            _generation++;
            _cache = _cache.Shifted(shifts);
        }
    }

    /// <summary>A page's content changed (an edit, a rotation): what was read from it is stale.</summary>
    public void PageChanged(int page)
    {
        lock (_gate)
        {
            _generation++;
            _cache.Remove(page);
        }
    }

    /// <summary>Everything is stale: a redaction was applied, or the document closed.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _cache.Clear();
        }
    }
}
