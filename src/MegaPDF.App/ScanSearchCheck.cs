using System.Text;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Viewing;
using Microsoft.UI.Xaml;

namespace MegaPDF.App;

/// <summary>
/// <c>--screenshot-state scan-search</c> (#676): Find on scanned pages through the real view
/// model and window, which is the half the Core tests cannot reach — the searcher built with the
/// document, the cache following a page move, the dashed highlight and the note under the find
/// bar. The exit code is the test.
///
/// Two parts. The first scripts the recogniser, so it runs the same on a hosted runner that has no
/// OCR language installed. The second is a genuine scan: the fixture's own first page rendered to
/// pixels and wrapped in a PDF with no text layer, read by the real <c>Windows.Media.Ocr</c>, with
/// the hit checked against where the engine's own search finds the same word on the original.
/// It is skipped, and says so, when the machine has no OCR language.
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

    public static async Task<bool> RunAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } fixtureTab
            || fixtureTab.DocumentPath is not { } fixturePath)
        {
            Console.Error.WriteLine("--screenshot-state scan-search needs a document.");
            return false;
        }

        var passed = 0;
        var failed = 0;
        void Check(string what, bool ok)
        {
            Console.Error.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (ok) passed++; else failed++;
        }

        var scratch = Path.Combine(Path.GetTempPath(), "megapdf-selftest-scan-search");
        Directory.CreateDirectory(scratch);
        var original = DocumentViewModel.RecogniserFactory;
        // For a photograph of the result: leaves the real scan open, with the find bar up, and its
        // scratch directory for the caller to remove once the process has exited. Off by default.
        var keep = Environment.GetEnvironmentVariable("MEGAPDF_SCAN_SEARCH_KEEP") == "1";
        try
        {
            await ScriptedAsync(window, scratch, Check);
            await RealAsync(window, fixturePath, scratch, Check);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: the scan-search check threw: {ex}");
            failed++;
        }
        finally
        {
            DocumentViewModel.RecogniserFactory = original;
            if (keep)
            {
                window.Shell.Active?.View?.ShowFindBar();
                await Task.Delay(800);
                Console.Error.WriteLine($"kept: {scratch}");
            }
            else
            {
                foreach (var tab in window.Shell.Documents.Where(t => !ReferenceEquals(t, fixtureTab)).ToList())
                {
                    tab.HasUnsavedChanges = false;
                    await window.CloseTabAsync(tab);
                }
                try { Directory.Delete(scratch, recursive: true); } catch { /* best-effort scratch cleanup */ }
            }
        }

        Console.Error.WriteLine($"scan-search: {passed} passed, {failed} failed ({passed + failed} checks)");
        return failed == 0;
    }

    private static async Task ScriptedAsync(MainWindow window, string scratch, Action<string, bool> Check)
    {
        var script = new Scripted();
        DocumentViewModel.RecogniserFactory = () => script;   // before the open: the searcher is made with the document
        var path = Path.Combine(scratch, "scan3.pdf");
        File.WriteAllBytes(path, ScanPdf(Enumerable.Range(0, 3).Select(_ => Marked(306, 396)).ToList(), 612, 792));
        var tab = await window.Shell.OpenInTabAsync(path);
        await Task.Delay(1500);
        Check("a three-page scan opened", tab.IsDocumentOpen && tab.Pages.Count == 3);

        await tab.SearchAsync("scan page");
        Check("Find reads every page of the scan and finds a hit on each", tab.SearchMatchCount == 3);
        Check("  the recogniser was asked once per page", script.Reads == 3);
        Check("  the note under the find bar is up while the current hit was read from a scan",
              tab.ScanNoteVisibility == Visibility.Visible);
        Check("  and the page draws that hit with a dashed edge",
              tab.Pages[0].Highlights.Count == 1 && tab.Pages[0].Highlights[0].FromScan
              && tab.Pages[0].Highlights[0].Dashes.Count > 0);

        await tab.SearchAsync("scan page 2");
        Check("a narrower term is found from what was read", tab.SearchMatchCount == 1);
        Check("  without reading anything again", script.Reads == 3);

        await tab.MovePageAsync(0, 2);
        await Task.Delay(500);
        await tab.SearchAsync("scan page 1");
        Check("after a move, the text went with its page", tab.SearchMatchCount == 1
              && tab.Pages[2].Highlights.Count == 1 && tab.Pages[0].Highlights.Count == 0);
        Check("  and a move reads nothing again", script.Reads == 3);

        tab.ClearSearch();
        Check("the note is gone when the search is cleared", tab.ScanNoteVisibility == Visibility.Collapsed);
    }

    private static async Task RealAsync(MainWindow window, string fixturePath, string scratch, Action<string, bool> Check)
    {
        DocumentViewModel.RecogniserFactory = WindowsOcrRecogniser.TryCreate;
        if (WindowsOcrRecogniser.TryCreate() is null)
        {
            Console.Error.WriteLine("SKIP: the real recogniser — this machine has no OCR language installed");
            return;
        }

        // A genuine scan: the fixture's own first page as pixels, with no text layer behind it.
        using var engine = new PdfiumEngine().Open(fixturePath);
        RenderedPage render;
        PdfRect? original;
        double pageWidth, pageHeight;
        using (var page = engine.GetPage(0))
        {
            pageWidth = page.Width;
            pageHeight = page.Height;
            render = page.Render((int)(page.Width * 150 / 72), (int)(page.Height * 150 / 72));
            original = page.FindText("fixture").FirstOrDefault()?.Rects.FirstOrDefault();
        }
        Check("the fixture's own search finds \"fixture\" on its first page", original is not null);
        if (original is null)
            return;

        var gray = new byte[render.PixelWidth * render.PixelHeight];
        for (var i = 0; i < gray.Length; i++)
            gray[i] = (byte)((render.Bgra[i * 4] + render.Bgra[i * 4 + 1] + render.Bgra[i * 4 + 2]) / 3);
        var path = Path.Combine(scratch, "real-scan.pdf");
        File.WriteAllBytes(path, ScanPdf([(render.PixelWidth, render.PixelHeight, gray)], pageWidth, pageHeight));

        var tab = await window.Shell.OpenInTabAsync(path);
        await Task.Delay(1500);
        Check("a real scan opened", tab.IsDocumentOpen && tab.Pages.Count == 1);
        using (var scan = new PdfiumEngine().Open(path))
        using (var page = scan.GetPage(0))
            Check("  and the engine's own search finds nothing on it", page.FindText("fixture").Count == 0);

        await tab.SearchAsync("fixture");
        Check("Find reads the scan with the real recogniser and finds \"fixture\"", tab.SearchMatchCount >= 1);
        Check("  marked as read, not as the document's own text", tab.ScanNoteVisibility == Visibility.Visible);
        if (tab.Pages[0].Highlights.FirstOrDefault(h => h.FromScan) is { } hit)
        {
            var toPoints = 72.0 / 96 / tab.ZoomFactor;
            var centre = new PdfPoint((hit.X + hit.Width / 2) * toPoints, (hit.Y + hit.Height / 2) * toPoints);
            var expected = original.Value.Center;
            Check($"  and it lands where the original has the word (within 25 pt): {centre} vs {expected}",
                  Math.Abs(centre.X - expected.X) < 25 && Math.Abs(centre.Y - expected.Y) < 25);
        }
        else
        {
            Check("  and the page draws a highlight for it", false);
        }
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
    /// A PDF whose every page is only a picture: one raw 8-bit grey image stretched over the
    /// page, no text object anywhere. The shape of a scan.
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
