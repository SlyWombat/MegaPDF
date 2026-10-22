using MegaPDF.Core.Engine.Core;

namespace MegaPDF.Core.Engine;

/// <summary>
/// A page's text as contract 11 selects it (#710, <c>core/megapdf_core.h</c>): its characters in
/// reading order, numbered 0 … <see cref="Length"/> − 1, and the carets between them,
/// 0 … <see cref="Length"/>. A selection is an anchor caret and a focus caret in either order;
/// every call here orders them itself.
///
/// The reading order is the core's — contract 9's words, lines and XY-cut, so across two
/// columns a selection runs down the first and then the second — and so is what a copy
/// contains: one space between words, <c>\n</c> where a line ends, ligatures expanded, line-end
/// hyphens kept and not joined. Points and rectangles are view space, top-left origin, like
/// every other <see cref="IPdfPage"/> answer.
///
/// <para><b>Lifetime.</b> A snapshot of the page as it was when <see cref="IPdfPage.LoadSelectableText"/>
/// ran: load it once per shown page and query it on every pointer move. After any change to the
/// page's content or rotation, dispose it, load a new one and drop the selection — carets index
/// one snapshot and mean nothing in the next. It holds no PDFium state, so it outlives its page
/// and its document.</para>
///
/// <para><b>Threading.</b> Every query reads only the snapshot and takes no lock, so it may run on
/// the UI thread while the engine renders on another. Dispose may race a query safely: the
/// native snapshot is freed when the last query in flight returns.</para>
///
/// <para><b>Copy.</b> Gate the Copy command on <see cref="Services.DocumentCapabilities.CanCopyText"/>:
/// a document that forbids copying still selects and highlights, and only Copy is disabled.</para>
/// </summary>
public sealed class PdfSelectableText : IDisposable
{
    private readonly CoreNative.SelectableHandle _handle;
    private readonly double _pageHeight;

    internal PdfSelectableText(CoreNative.SelectableHandle handle, int pageIndex, double pageHeight)
    {
        _handle = handle;
        _pageHeight = pageHeight;
        PageIndex = pageIndex;
        Length = CoreNative.megapdf_selectable_length(handle);
    }

    /// <summary>The page's index when the snapshot was loaded.</summary>
    public int PageIndex { get; }

    /// <summary>How many characters there are to select; 0 for a blank page or a scan.</summary>
    public int Length { get; }

    /// <summary>
    /// The caret nearest <paramref name="point"/>, or null when there is no text within
    /// <paramref name="tolerance"/> points of it. Null tolerance is no limit: what a drag asks on
    /// every move, so a pointer in a margin still extends the selection.
    /// </summary>
    public int? CaretAt(PdfPoint point, double? tolerance = null)
    {
        var caret = CoreNative.megapdf_selectable_caret_at(_handle, point.X, _pageHeight - point.Y, Native(tolerance));
        return caret < 0 ? null : caret;
    }

    /// <summary>The character nearest <paramref name="point"/>, resolved as <see cref="CaretAt"/>; null as it is.</summary>
    public int? CharAt(PdfPoint point, double? tolerance = null)
    {
        var index = CoreNative.megapdf_selectable_char_at(_handle, point.X, _pageHeight - point.Y, Native(tolerance));
        return index < 0 ? null : index;
    }

    /// <summary>
    /// The word at <paramref name="point"/> (a double-click, a long-press), without the
    /// punctuation at its ends unless the point is on it; null when there is none within
    /// <paramref name="tolerance"/>.
    /// </summary>
    public PdfTextRange? WordAt(PdfPoint point, double? tolerance = null)
    {
        var n = CoreNative.megapdf_selectable_word_at(_handle, point.X, _pageHeight - point.Y, Native(tolerance),
            out var start, out var end);
        return n > 0 ? new PdfTextRange(start, end) : null;
    }

    /// <summary>The visual line at <paramref name="point"/> (a triple-click); null as <see cref="WordAt"/>.</summary>
    public PdfTextRange? LineAt(PdfPoint point, double? tolerance = null)
    {
        var n = CoreNative.megapdf_selectable_line_at(_handle, point.X, _pageHeight - point.Y, Native(tolerance),
            out var start, out var end);
        return n > 0 ? new PdfTextRange(start, end) : null;
    }

    /// <summary>The text between two carets, in either order, as a copy puts it on the clipboard.</summary>
    public string GetText(int anchor, int focus)
    {
        var n = (int)CoreNative.megapdf_selectable_text(_handle, anchor, focus, null, 0);
        if (n == 0)
            return "";
        var units = new ushort[n];
        n = (int)CoreNative.megapdf_selectable_text(_handle, anchor, focus, units, (nuint)n);
        return new string(System.Runtime.InteropServices.MemoryMarshal.Cast<ushort, char>(units.AsSpan(0, Math.Min(n, units.Length))));
    }

    /// <inheritdoc cref="GetText(int, int)"/>
    public string GetText(PdfTextRange range) => GetText(range.Start, range.End);

    /// <summary>
    /// The rectangles to highlight between two carets, in either order: one per line touched, in
    /// reading order, each the line's full height, in view space.
    /// </summary>
    public IReadOnlyList<PdfRect> GetRects(int anchor, int focus)
    {
        var n = (int)CoreNative.megapdf_selectable_rects(_handle, anchor, focus, null, 0);
        if (n == 0)
            return [];
        var rects = new CoreNative.Rect[n];
        n = (int)CoreNative.megapdf_selectable_rects(_handle, anchor, focus, rects, (nuint)n);
        var result = new List<PdfRect>(n);
        for (var i = 0; i < Math.Min(n, rects.Length); i++)
        {
            var r = rects[i];
            result.Add(new PdfRect(r.Left, _pageHeight - r.Top, r.Right - r.Left, r.Top - r.Bottom));
        }
        return result;
    }

    /// <inheritdoc cref="GetRects(int, int)"/>
    public IReadOnlyList<PdfRect> GetRects(PdfTextRange range) => GetRects(range.Start, range.End);

    public void Dispose() => _handle.Dispose();

    private static double Native(double? tolerance) =>
        tolerance is { } t && t >= 0 && !double.IsNaN(t) ? t : CoreNative.SelectAnywhere;
}

/// <summary>Two carets on one page, <see cref="Start"/> ≤ <see cref="End"/>.</summary>
public readonly record struct PdfTextRange(int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>A caret on a page: where a selection that may cross pages starts or ends.</summary>
public readonly record struct PdfTextPosition(int PageIndex, int Caret) : IComparable<PdfTextPosition>
{
    public int CompareTo(PdfTextPosition other) =>
        PageIndex != other.PageIndex ? PageIndex.CompareTo(other.PageIndex) : Caret.CompareTo(other.Caret);
}

/// <summary>
/// A selection across pages (#710), stitched the one way contract 11 specifies, so the two
/// desktops cannot stitch it two ways: each page's own range, the non-empty pieces joined with
/// one <c>\n</c> — a page break is a line break — and a page with no text adding nothing.
/// </summary>
public static class PdfTextSelection
{
    /// <summary>
    /// The carets <paramref name="pageIndex"/> contributes to the selection from
    /// <paramref name="anchor"/> to <paramref name="focus"/> (either order), or null when the page
    /// is outside it: the first page from its caret to its end, the last from its start to its
    /// caret, every page between in full.
    /// </summary>
    public static PdfTextRange? RangeOnPage(PdfTextPosition anchor, PdfTextPosition focus, int pageIndex, int pageLength)
    {
        var (first, last) = anchor.CompareTo(focus) <= 0 ? (anchor, focus) : (focus, anchor);
        if (pageIndex < first.PageIndex || pageIndex > last.PageIndex)
            return null;
        var start = pageIndex == first.PageIndex ? Math.Clamp(first.Caret, 0, pageLength) : 0;
        var end = pageIndex == last.PageIndex ? Math.Clamp(last.Caret, 0, pageLength) : pageLength;
        return start < end ? new PdfTextRange(start, end) : null;
    }

    /// <summary>
    /// The text of the selection from <paramref name="anchor"/> to <paramref name="focus"/>, in
    /// either order. <paramref name="page"/> hands over each page's snapshot, in page order, and
    /// keeps ownership of it: the app's own cache, which loads the pages between when the
    /// selection spans pages that are not on screen.
    /// </summary>
    public static string GetText(PdfTextPosition anchor, PdfTextPosition focus, Func<int, PdfSelectableText> page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var (first, last) = anchor.CompareTo(focus) <= 0 ? (anchor, focus) : (focus, anchor);
        var pieces = new List<string>();
        for (var index = first.PageIndex; index <= last.PageIndex; index++)
        {
            var text = page(index);
            var range = RangeOnPage(first, last, index, text.Length);
            if (range is not { } r)
                continue;
            var piece = text.GetText(r);
            if (piece.Length > 0)
                pieces.Add(piece);
        }
        return string.Join('\n', pieces);
    }

    /// <summary>How many characters of a selection a screen reader is told (#710).</summary>
    public const int SpokenLimit = 80;

    /// <summary>
    /// The start of a selection as a screen reader should say it (#710): line ends and runs of
    /// white space made single spaces, trimmed, and cut to <paramref name="limit"/> characters
    /// with an ellipsis when there was more. A cut never splits a surrogate pair. Empty when the
    /// text is only white space.
    /// </summary>
    public static string Spoken(string text, int limit = SpokenLimit)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var collapsed = Collapse(text);
        if (collapsed.Length <= limit)
            return collapsed;
        var cut = char.IsHighSurrogate(collapsed[limit - 1]) ? limit - 1 : limit;
        return collapsed[..cut].TrimEnd() + "\u2026";
    }

    /// <summary>
    /// <see cref="Spoken"/> for the selection from <paramref name="anchor"/> to
    /// <paramref name="focus"/>, reading only as many pages as it takes to fill
    /// <paramref name="limit"/> characters: Select All on a long document must not read every
    /// page to say eighty characters.
    /// </summary>
    public static string Preview(PdfTextPosition anchor, PdfTextPosition focus, Func<int, PdfSelectableText> page,
                                 int limit = SpokenLimit)
    {
        ArgumentNullException.ThrowIfNull(page);
        var (first, last) = anchor.CompareTo(focus) <= 0 ? (anchor, focus) : (focus, anchor);
        var gathered = new System.Text.StringBuilder();
        for (var index = first.PageIndex; index <= last.PageIndex; index++)
        {
            var text = page(index);
            if (RangeOnPage(first, last, index, text.Length) is not { } range)
                continue;
            var piece = text.GetText(range);
            if (piece.Length == 0)
                continue;
            if (gathered.Length > 0)
                gathered.Append('\n');
            gathered.Append(piece);
            if (Collapse(gathered.ToString()).Length > limit)
                break;
        }
        return Spoken(gathered.ToString(), limit);
    }

    private static string Collapse(string text)
    {
        var result = new System.Text.StringBuilder(text.Length);
        var gap = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                gap = result.Length > 0;
                continue;
            }
            if (gap)
                result.Append(' ');
            gap = false;
            result.Append(c);
        }
        return result.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> with its line ends as a platform's clipboard wants them. The core
    /// ends every line with <c>\n</c>; Windows expects CR LF (<c>"\r\n"</c>) on the clipboard, and
    /// macOS and Linux LF. Any CR LF or lone CR already in the text is normalised first, so the
    /// conversion never doubles one.
    /// </summary>
    public static string WithLineEnds(string text, string newline)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(newline);
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return newline == "\n" ? lf : lf.Replace("\n", newline, StringComparison.Ordinal);
    }
}
