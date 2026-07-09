using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

public class FlattenTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-flatten-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Flatten_BakesMarkAndSignature_InkStaysAnnotationsGo()
    {
        var docPath = Path.Combine(_dir, "doc.pdf");
        File.WriteAllBytes(docPath, SamplePdf.BuildWithDrawnSquares());
        var savedPath = Path.Combine(_dir, "flat.pdf");

        var sigBounds = new PdfRect(300, 500, 90, 30);
        var square = new PdfRect(100, 274, 18, 18);
        var bgra = new byte[8 * 4 * 4];
        for (var i = 0; i < bgra.Length; i += 4) { bgra[i] = 0xCC; bgra[i + 3] = 0xFF; }

        using (var doc = _engine.Open(docPath))
        {
            using (var page = doc.GetPage(0))
            {
                page.AddCheckMarkStamp(square);
                page.AddImageStamp(bgra, 8, 4, sigBounds);
            }

            doc.FlattenAllPages();

            using (var page = doc.GetPage(0))
            {
                // No interactive stamps remain…
                Assert.Empty(page.GetStamps());
                Assert.Equal(PageHitKind.None, page.HitTest(sigBounds.Center).Kind);
                // …but the ink is baked into the page.
                var rendered = page.Render(612, 792);
                Assert.True(RegionHasInk(rendered, 101, 275, 16, 16), "mark ink should remain");
                Assert.True(HasBluePixel(rendered, 300, 505, 90, 20), "signature pixels should remain");
            }

            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        using var reopenedPage = reopened.GetPage(0);
        Assert.Empty(reopenedPage.GetStamps());
        var r = reopenedPage.Render(612, 792);
        Assert.True(RegionHasInk(r, 101, 275, 16, 16), "mark survives save/reopen");
        Assert.True(HasBluePixel(r, 300, 505, 90, 20), "signature survives save/reopen");
    }

    private static bool RegionHasInk(RenderedPage rendered, int x, int y, int w, int h)
    {
        for (var row = y; row < y + h; row++)
            for (var col = x; col < x + w; col++)
            {
                var i = (row * rendered.PixelWidth + col) * 4;
                if (rendered.Bgra[i] < 0x80 && rendered.Bgra[i + 1] < 0x80 && rendered.Bgra[i + 2] < 0x80)
                    return true;
            }
        return false;
    }

    private static bool HasBluePixel(RenderedPage rendered, int x, int y, int w, int h)
    {
        for (var row = y; row < y + h; row++)
            for (var col = x; col < x + w; col++)
            {
                var i = (row * rendered.PixelWidth + col) * 4;
                if (rendered.Bgra[i] > 0xA0 && rendered.Bgra[i + 2] < 0x60)
                    return true;
            }
        return false;
    }
}
