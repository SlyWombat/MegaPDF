using System.Text;
using MegaPDF.Avalonia.Rendering;
using MegaPDF.Avalonia.ViewModels;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Viewing;

namespace MegaPDF.Avalonia;

/// <summary>
/// Find on scanned pages (#676) through the real view model, which is the half the Core tests
/// cannot reach: the searcher built with the document, the cache following a page move, the
/// highlights marked as read and the note under the find bar. Run by <c>--self-test</c>.
///
/// Two parts. The first scripts the recogniser, so it runs the same on Linux, where there is none.
/// The second is a genuine scan: the fixture's own first page rendered to pixels and wrapped in a
/// PDF with no text layer, read by the real Vision on a Mac, with the hit checked against where the
/// engine's own search finds the same word on the original. It prints SKIP, and passes, anywhere
/// that has no recogniser.
/// </summary>
internal static class ScanSearchCheck
{
    /// <summary>"scan page N" for the Nth read, which is page N-1 on a first search, because Find walks the pages in order.</summary>
    private sealed class Scripted : IPageRecogniser
    {
        public int Reads;
        public int MaxPixelsPerSide => 10_000;

        public OcrPage? Read(RenderedPage image, double pageWidth, double pageHeight)
        {
            var n = Interlocked.Increment(ref Reads);
            var text = $"scan page {n}";
            return new OcrPage([new OcrLine(text, [
                new OcrWord(0, 4, new PdfRect(72, 100, 40, 20)),
                new OcrWord(5, 9, new PdfRect(116, 100, 40, 20)),
                new OcrWord(10, text.Length, new PdfRect(160, 100, 12, 20))])]);
        }
    }

    public static void Run(string dir, Func<DocumentViewModel> newDocument, Action<string, bool> check)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "megapdf-selftest-scan-search-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        var original = DocumentViewModel.RecogniserFactory;
        try
        {
            RunScripted(scratch, newDocument, check);
            RunReal(Path.Combine(dir, "fixture.pdf"), "fixture", scratch, newDocument, check);
            // The demo agreement, which was written to say "rental" three times on its first page:
            // a real scan of it must give back as many hits as the original has.
            if (File.Exists(Path.Combine(dir, "demo.pdf")))
                RunReal(Path.Combine(dir, "demo.pdf"), DemoContent.SearchTerm, scratch, newDocument, check);
        }
        finally
        {
            DocumentViewModel.RecogniserFactory = original;
            try { Directory.Delete(scratch, recursive: true); } catch { /* best-effort scratch cleanup */ }
        }
    }

    private static void RunScripted(string scratch, Func<DocumentViewModel> newDocument, Action<string, bool> check)
    {
        var script = new Scripted();
        DocumentViewModel.RecogniserFactory = () => script;   // before the open: the searcher is made with the document
        var path = Path.Combine(scratch, "scan3.pdf");
        File.WriteAllBytes(path, ScanPdf(Enumerable.Range(0, 3).Select(_ => Marked(306, 396)).ToList(), 612, 792));
        using var vm = newDocument();
        vm.Open(path);
        check("  a three-page scan opened", vm.IsDocumentOpen && vm.Pages.Count == 3);

        vm.Search("scan page");
        check("  Find reads every page of the scan and finds a hit on each", vm.MatchCount == 3);
        check("    the recogniser was asked once per page", script.Reads == 3);
        check("    the note under the find bar is up while the current hit was read from a scan", vm.ScanNoteVisible);
        check("    and the page draws that hit as one the recogniser read",
              vm.Pages[0].Highlights.Count == 1 && vm.Pages[0].Highlights[0].FromScan);

        vm.Search("scan page 2");
        check("  a narrower term is found from what was read", vm.MatchCount == 1);
        check("    without reading anything again", script.Reads == 3);

        vm.MovePage(0, 2);
        vm.Search("scan page 1");
        check("  after a move, the text went with its page",
              vm.MatchCount == 1 && vm.Pages[2].Highlights.Count == 1 && vm.Pages[0].Highlights.Count == 0);
        check("    and a move reads nothing again", script.Reads == 3);

        vm.CloseFind();
        check("  the note is gone when the find bar is closed", !vm.ScanNoteVisible);
        vm.DiscardChanges();
    }

    private static void RunReal(string fixturePath, string term, string scratch, Func<DocumentViewModel> newDocument, Action<string, bool> check)
    {
        DocumentViewModel.RecogniserFactory = () => OperatingSystem.IsMacOS() ? Platform.MacVisionRecogniser.TryCreate() : null;
        if (DocumentViewModel.RecogniserFactory() is null)
        {
            Console.WriteLine("  SKIP: the real recogniser — this platform has none");
            return;
        }

        // A genuine scan: the fixture's own first page as pixels, with no text layer behind it.
        using var source = new PdfiumEngine().Open(fixturePath);
        RenderedPage render;
        IReadOnlyList<PdfSearchMatch> originals;
        double pageWidth, pageHeight;
        using (var page = source.GetPage(0))
        {
            pageWidth = page.Width;
            pageHeight = page.Height;
            render = page.Render((int)(page.Width * 150 / 72), (int)(page.Height * 150 / 72));
            originals = page.FindText(term);
        }
        var original = originals.FirstOrDefault()?.Rects.FirstOrDefault();
        var name = Path.GetFileName(fixturePath);
        check($"  {name}: the document's own search finds \"{term}\" on its first page", original is not null);
        if (original is null)
            return;

        var gray = new byte[render.PixelWidth * render.PixelHeight];
        for (var i = 0; i < gray.Length; i++)
            gray[i] = (byte)((render.Bgra[i * 4] + render.Bgra[i * 4 + 1] + render.Bgra[i * 4 + 2]) / 3);
        var path = Path.Combine(scratch, "scan-of-" + name);
        File.WriteAllBytes(path, ScanPdf([(render.PixelWidth, render.PixelHeight, gray)], pageWidth, pageHeight));

        using (var scan = new PdfiumEngine().Open(path))
        using (var page = scan.GetPage(0))
            check("    a scan of it has nothing for the engine's own search to find", page.FindText(term).Count == 0);

        using var vm = newDocument();
        vm.Open(path);
        vm.Search(term);
        check($"    Find reads the scan with the real recogniser and finds {originals.Count} hit(s), as the original has",
              vm.MatchCount == originals.Count);
        check("    marked as read, not as the document's own text", vm.ScanNoteVisible);
        if (vm.Pages[0].Highlights.FirstOrDefault(h => h.FromScan) is { } hit)
        {
            var toPoints = 1.0 / (PageBitmap.PointsToPixels * vm.Pages[0].Zoom);
            var centre = new PdfPoint((hit.X + hit.Width / 2) * toPoints, (hit.Y + hit.Height / 2) * toPoints);
            var expected = original.Value.Center;
            check($"    and the first lands where the original has the word (within 25 pt): {centre} vs {expected}",
                  Math.Abs(centre.X - expected.X) < 25 && Math.Abs(centre.Y - expected.Y) < 25);
        }
        else
        {
            check("    and the page draws a highlight for it", false);
        }
        // For a photograph of the result: a copy of the scan, left where the caller asked.
        if (Environment.GetEnvironmentVariable("MEGAPDF_SCAN_SEARCH_KEEP") is { Length: > 0 } keep)
        {
            Directory.CreateDirectory(keep);
            File.Copy(path, Path.Combine(keep, "scan-of-" + name), overwrite: true);
        }
        vm.DiscardChanges();
    }

    /// <summary>A grey page image with a black block on it, so it is not a blank sheet.</summary>
    private static (int W, int H, byte[] Gray) Marked(int w, int h)
    {
        var gray = new byte[w * h];
        Array.Fill(gray, (byte)255);
        for (var y = 80; y < 120; y++)
            for (var x = 60; x < 250; x++)
                gray[y * w + x] = 0;
        return (w, h, gray);
    }

    /// <summary>
    /// A PDF whose every page is only a picture: one raw 8-bit grey image stretched over the page,
    /// no text object anywhere. The shape of a scan.
    /// </summary>
    private static byte[] ScanPdf(IReadOnlyList<(int W, int H, byte[] Gray)> images, double pageWidth, double pageHeight)
    {
        var ms = new MemoryStream();
        var offsets = new List<int>();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void Begin() { offsets.Add((int)ms.Length); Write($"{offsets.Count} 0 obj\n"); }
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        Write("%PDF-1.4\n");
        Begin(); Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var kids = string.Join(" ", images.Select((_, i) => $"{3 + i * 3} 0 R"));
        Begin(); Write($"<< /Type /Pages /Kids [{kids}] /Count {images.Count} >>\nendobj\n");
        for (var i = 0; i < images.Count; i++)
        {
            var (w, h, gray) = images[i];
            var page = 3 + i * 3;
            Begin();
            Write(string.Create(inv, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth} {pageHeight}] "
                + $"/Resources << /XObject << /Im0 {page + 2} 0 R >> >> /Contents {page + 1} 0 R >>\nendobj\n"));
            var content = string.Create(inv, $"q {pageWidth} 0 0 {pageHeight} 0 0 cm /Im0 Do Q");
            Begin(); Write($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
            Begin();
            Write($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceGray "
                + $"/BitsPerComponent 8 /Length {gray.Length} >>\nstream\n");
            ms.Write(gray);
            Write("\nendstream\nendobj\n");
        }
        var xref = (int)ms.Length;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            Write($"{o:D10} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
