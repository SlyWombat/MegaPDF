using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Viewing;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// Find on scanned pages (#676), the half both desktops share: what was read and how a hit is
/// found in it, how the cache follows page tools, when a page is read at all, and what is done
/// with a text box laid over a scan. The recognisers themselves are the platforms' own
/// (<c>Windows.Media.Ocr</c>, Vision); here the recogniser is scripted.
/// </summary>
public class ScanSearchTests
{
    private static OcrWord W(int start, int end, double left, double right) =>
        new(start, end, new PdfRect(left, 100, right - left, 20));

    /// <summary>"Total due 1,000", a word each.</summary>
    private static readonly OcrPage Invoice = new([
        new OcrLine("Total due 1,000", [W(0, 5, 10, 40), W(6, 9, 50, 80), W(10, 15, 90, 150)])]);

    // --- the matcher ---

    [Fact]
    public void AMatchIsCaseInsensitiveAndLandsOnItsWord()
    {
        var hits = Invoice.Matches("DUE");
        Assert.Single(hits);
        Assert.Equal([new PdfRect(50, 100, 30, 20)], hits[0].Rects);
    }

    [Fact]
    public void AMatchInsideAWordHighlightsTheWholeWordNotAGuessedSlice() =>
        Assert.Equal([new PdfRect(10, 100, 30, 20)], Invoice.Matches("otal")[0].Rects);

    [Fact]
    public void AMatchAcrossWordsIsOneRectangleOverBoth()
    {
        var hits = Invoice.Matches("due 1,0");
        Assert.Single(hits);
        Assert.Equal([new PdfRect(50, 100, 100, 20)], hits[0].Rects);
    }

    [Fact]
    public void ARepeatedTermIsOneHitEachWithoutOverlap() =>
        Assert.Equal(2, new OcrPage([new OcrLine("aaaa", [W(0, 4, 0, 40)])]).Matches("aa").Count);

    [Fact]
    public void NoMatchAndAnEmptyTermAreEmpty()
    {
        Assert.Empty(Invoice.Matches("zebra"));
        Assert.Empty(Invoice.Matches(""));
    }

    [Fact]
    public void AReadHitOnTopOfOneTheDocumentAlreadyFoundIsNotShownTwice()
    {
        var tight = new PdfRect(52, 102, 26, 16);
        Assert.Empty(Invoice.Matches("due", [tight]));
        Assert.Single(Invoice.Matches("due", [new PdfRect(500, 500, 20, 20)]));
    }

    // --- the cache follows the pages ---

    private static OcrCache Cache()
    {
        var cache = new OcrCache();
        for (var i = 0; i < 4; i++)
            cache.Set(i, new OcrPage([new OcrLine($"page {i}", [W(0, 6, 0, 1)])]));
        return cache;
    }

    private static string?[] Texts(OcrCache cache) =>
        [.. Enumerable.Range(0, 4).Select(i => cache.Get(i)?.Lines[0].Text)];

    [Fact]
    public void ADeleteDropsThatPagesTextAndRenumbersTheRest() =>
        Assert.Equal(new string?[] { "page 0", "page 2", "page 3", null }, Texts(Cache().Shifted([PageShift.Removed(1)])));

    [Fact]
    public void AMoveCarriesTheTextWithThePage() =>
        Assert.Equal(new string?[] { "page 1", "page 2", "page 0", "page 3" }, Texts(Cache().Shifted([PageShift.Moved(0, 2)])));

    [Fact]
    public void AnInsertLeavesTheNewPageUnreadNotEmpty()
    {
        var shifted = Cache().Shifted([PageShift.Inserted(1)]);
        Assert.Null(shifted.Get(1));
        Assert.Equal("page 1", shifted.Get(2)?.Lines[0].Text);
    }

    [Fact]
    public void PagesFoundToHaveTextFollowToo()
    {
        var cache = new OcrCache();
        cache.NoteText(2);
        Assert.True(cache.Shifted([PageShift.Removed(0)]).HasText(1));
        Assert.False(cache.Shifted([PageShift.Removed(2)]).HasText(2));
    }

    // --- the searcher, with the recogniser scripted ---

    private sealed class Script(params int[] ownText)
    {
        public readonly List<int> Reads = [];
        public bool HasText(int page) => ownText.Contains(page);
        public OcrPage? Read(int page)
        {
            lock (Reads) Reads.Add(page);
            return new OcrPage([new OcrLine($"invoice {page + 1}", [W(0, 7, 10, 70), W(8, 9, 80, 90)])]);
        }
    }

    private static ScanSearcher Searcher(Script script) => new(script.HasText, script.Read);

    [Fact]
    public void AScanIsReadOnceAndFoundFromWhatWasReadAfterwards()
    {
        var script = new Script();
        var searcher = Searcher(script);
        for (var p = 0; p < 4; p++)
            Assert.Single(searcher.Hits(p, "invoice", []));
        Assert.Equal([0, 1, 2, 3], script.Reads);
        Assert.Single(searcher.Hits(2, "invoice 3", []));
        Assert.Equal(4, script.Reads.Count);
    }

    [Fact]
    public void APageWithTextOfItsOwnIsNeverRead()
    {
        var script = new Script(0, 1);
        var searcher = Searcher(script);
        Assert.Empty(searcher.Hits(0, "invoice", []));
        Assert.Empty(searcher.Hits(0, "invoice", []));
        Assert.Empty(script.Reads);
    }

    [Fact]
    public void WhatWasReadFollowsAMoveAndIsNotReadAgain()
    {
        var script = new Script();
        var searcher = Searcher(script);
        for (var p = 0; p < 4; p++)
            searcher.Hits(p, "invoice", []);
        searcher.Renumbered([PageShift.Moved(0, 2)]);
        Assert.Single(searcher.Hits(2, "invoice 1", []));
        Assert.Empty(searcher.Hits(0, "invoice 1", []));
        Assert.Equal(4, script.Reads.Count);
    }

    [Fact]
    public void ADeletedPageIsGoneAndTheRestAreNotReadAgain()
    {
        var script = new Script();
        var searcher = Searcher(script);
        for (var p = 0; p < 4; p++)
            searcher.Hits(p, "invoice", []);
        searcher.Renumbered([PageShift.Removed(1)]);
        Assert.Empty(searcher.Hits(1, "invoice 2", []));
        Assert.Single(searcher.Hits(1, "invoice 3", []));
        Assert.Equal(4, script.Reads.Count);
    }

    [Fact]
    public void AChangedPageIsReadAgainAndClearForgetsEverything()
    {
        var script = new Script();
        var searcher = Searcher(script);
        for (var p = 0; p < 3; p++)
            searcher.Hits(p, "invoice", []);
        searcher.PageChanged(1);
        searcher.Hits(1, "invoice", []);
        Assert.Equal([0, 1, 2, 1], script.Reads);
        searcher.Clear();
        Assert.True(searcher.CacheIsEmpty);
        searcher.Hits(0, "invoice", []);
        Assert.Equal(5, script.Reads.Count);
    }

    [Fact]
    public void AReadThatAnEditOvertookIsUsedOnceAndNotKept()
    {
        ScanSearcher? searcher = null;
        searcher = new ScanSearcher(_ => false, page =>
        {
            searcher!.PageChanged(page); // the edit lands while the page is being read
            return new OcrPage([new OcrLine("invoice", [W(0, 7, 10, 70)])]);
        });
        Assert.Empty(searcher.Hits(0, "invoice", []));
        Assert.Null(searcher.Cached(0));
    }

    [Fact]
    public void AReadHitOverAnOwnHitIsDroppedSoTheBoxIsNotShownTwice()
    {
        var searcher = Searcher(new Script());
        Assert.Empty(searcher.Hits(0, "invoice", [new PdfRect(15, 102, 50, 16)]));
    }

    // --- the real engine: when is a page read, and what is sent to the recogniser ---

    private static string Fixture(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);

    private sealed class Spy : IPageRecogniser
    {
        public int MaxPixelsPerSide { get; init; } = 10_000;
        public readonly List<(int W, int H, double PageW, double PageH)> Seen = [];
        public OcrPage? Read(RenderedPage image, double pageWidth, double pageHeight)
        {
            Seen.Add((image.PixelWidth, image.PixelHeight, pageWidth, pageHeight));
            return new OcrPage([]);
        }
    }

    [Fact]
    public void AnImageOnlyPageHasNoTextOfItsOwnAndATextPageDoes()
    {
        using var doc = new PdfiumEngine().Open(Fixture("structure", "scan.pdf"));
        Assert.False(ScanReader.HasOwnText(doc, 0));
        using var text = new PdfiumEngine().Open(Fixture("structure", "headings.pdf"));
        Assert.True(ScanReader.HasOwnText(text, 0));
    }

    [Fact]
    public void OnlyAPageWithAPictureAndNoTextNeedsReading()
    {
        // #687: the hint's trigger. A scan does; a text page does not; a blank page has nothing to read.
        using var scan = new PdfiumEngine().Open(Fixture("structure", "scan.pdf"));
        Assert.True(ScanReader.NeedsReading(scan, 0, ScanReader.PagesWithPictures(scan)));
        using var text = new PdfiumEngine().Open(Fixture("structure", "headings.pdf"));
        Assert.False(ScanReader.NeedsReading(text, 0, ScanReader.PagesWithPictures(text)));
        using var empty = new PdfiumEngine().Open(Fixture("structure", "empty-page.pdf"));
        Assert.False(ScanReader.NeedsReading(empty, 0, ScanReader.PagesWithPictures(empty)));
        using var layered = new PdfiumEngine().Open(Fixture("structure", "tiny-font-size.pdf"));
        Assert.False(ScanReader.NeedsReading(layered, 0, ScanReader.PagesWithPictures(layered)), "an invisible text layer: Find already works on it");
    }

    [Fact]
    public void AScanWithAnInvisibleTextLayerAlreadyHasTextAndIsNotRead()
    {
        using var doc = new PdfiumEngine().Open(Fixture("structure", "tiny-font-size.pdf"));
        Assert.True(ScanReader.HasOwnText(doc, 0), "an image under an invisible OCR layer: Find already works on it");
    }

    [Fact]
    public void ATextBoxLaidOverAScanIsNotATextLayer()
    {
        using var doc = new PdfiumEngine().Open(Fixture("structure", "scan.pdf"));
        using (var page = doc.GetPage(0))
            page.AppendTextBox("Invoice addendum", 24, new PdfPoint(72, 120));
        Assert.False(ScanReader.HasOwnText(doc, 0), "the scan under the box still needs reading");
        using var again = doc.GetPage(0);
        Assert.Single(again.FindText("addendum"));
    }

    [Fact]
    public void ARenderAtTwoHundredDpiIsWhatTheRecogniserSees()
    {
        using var doc = new PdfiumEngine().Open(Fixture("structure", "scan.pdf"));
        var spy = new Spy();
        using (var page = doc.GetPage(0))
        {
            Assert.NotNull(ScanReader.Read(doc, 0, spy));
            var (w, h, pw, ph) = Assert.Single(spy.Seen);
            Assert.Equal(page.Width, pw);
            Assert.Equal(pw * 200 / 72, w, 2);
            Assert.Equal(ph * 200 / 72, h, 2);
        }
    }

    [Fact]
    public void TheRenderIsCappedByWhatTheRecogniserAccepts()
    {
        using var doc = new PdfiumEngine().Open(Fixture("structure", "scan.pdf"));
        var spy = new Spy { MaxPixelsPerSide = 400 };
        Assert.NotNull(ScanReader.Read(doc, 0, spy));
        var (w, h, _, _) = Assert.Single(spy.Seen);
        Assert.True(Math.Max(w, h) < 400, $"{w} x {h} is past the recogniser's 400 px limit");
    }

    [Fact]
    public void ABlankSheetIsReadAsEmptyWithoutBeingSentToTheRecogniser()
    {
        using var doc = new PdfiumEngine().Open(Fixture("structure", "empty-page.pdf"));
        var spy = new Spy();
        var read = ScanReader.Read(doc, 0, spy);
        Assert.NotNull(read);
        Assert.Empty(read!.Lines);
        Assert.Empty(spy.Seen);
    }

    [Fact]
    public void ASheetWithMarksOnItIsNotBlank()
    {
        var w = 200;
        var h = 200;
        var white = new byte[w * h * 4];
        Array.Fill(white, (byte)255);
        Assert.True(ScanReader.Blank.IsBlank(new RenderedPage(w, h, white)));
        for (var y = 80; y < 120; y++)
            for (var x = 20; x < 180; x++)
                Array.Clear(white, (y * w + x) * 4, 3);
        Assert.False(ScanReader.Blank.IsBlank(new RenderedPage(w, h, white)));
    }
}
