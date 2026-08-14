using System.Text;
using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

public class PdfiumEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-engine-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Open_ValidPdf_ReportsPageCount()
    {
        using var doc = _engine.Open(WriteSamplePdf());
        Assert.Equal(1, doc.PageCount);
    }

    [Fact]
    public void GetPage_ReportsLetterSizeInPoints()
    {
        using var doc = _engine.Open(WriteSamplePdf());
        using var page = doc.GetPage(0);
        Assert.Equal(612, page.Width, 1);
        Assert.Equal(792, page.Height, 1);
    }

    [Fact]
    public void Render_ProducesInkOnPaper()
    {
        using var doc = _engine.Open(WriteSamplePdf());
        using var page = doc.GetPage(0);

        var rendered = page.Render(306, 396);

        Assert.Equal(306 * 396 * 4, rendered.Bgra.Length);
        var pixels = rendered.Bgra;
        var hasWhite = false;
        var hasInk = false;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] == 0xFF && pixels[i + 1] == 0xFF && pixels[i + 2] == 0xFF)
                hasWhite = true;
            else if (pixels[i] < 0x80 && pixels[i + 1] < 0x80 && pixels[i + 2] < 0x80)
                hasInk = true;
            if (hasWhite && hasInk)
                break;
        }
        Assert.True(hasWhite, "expected white background pixels");
        Assert.True(hasInk, "expected dark text pixels");
    }

    [Fact]
    public void Save_RoundTrips_AndReopens()
    {
        var savedPath = Path.Combine(_dir, "saved.pdf");
        using (var doc = _engine.Open(WriteSamplePdf()))
        using (var stream = File.Create(savedPath))
        {
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        Assert.Equal(1, reopened.PageCount);
        using var page = reopened.GetPage(0);
        Assert.Equal(612, page.Width, 1);
    }

    [Fact]
    public void Open_DoesNotLockTheFile()
    {
        var path = WriteSamplePdf();
        using var doc = _engine.Open(path);
        // The atomic-save protocol (SDD §3.4) replaces the original file, so the
        // engine must not hold it open. This delete would throw if it did.
        File.Delete(path);
    }

    [Fact]
    public void Open_NotAPdf_ThrowsFormatError()
    {
        var path = Path.Combine(_dir, "junk.pdf");
        File.WriteAllText(path, "this is not a pdf");
        var ex = Assert.Throws<PdfLoadException>(() => _engine.Open(path));
        Assert.Equal(PdfiumNative.FPDF_ERR_FORMAT, ex.ErrorCode);
    }

    // --- Text search (issue #26: case-insensitive substring, rects in top-left page space) ---

    [Fact]
    public void FindText_MatchesCaseInsensitively()
    {
        using var doc = _engine.Open(WriteSamplePdf()); // draws "Hello MegaPDF"
        using var page = doc.GetPage(0);

        var matches = page.FindText("megapdf");

        var match = Assert.Single(matches);
        var rect = Assert.Single(match.Rects);
        // 36pt Helvetica at 72,700 (PDF bottom-left): "MegaPDF" starts after "Hello ",
        // and 792-700=92 puts the baseline — so the glyph top — near y≈60 in our space.
        Assert.InRange(rect.X, 100, 300);
        Assert.InRange(rect.Y, 40, 92);
        Assert.InRange(rect.Width, 50, 300);
        Assert.InRange(rect.Height, 10, 45);
    }

    [Fact]
    public void FindText_ReturnsEveryOccurrence_InReadingOrder()
    {
        var path = Path.Combine(_dir, "fish.pdf");
        File.WriteAllBytes(path, SamplePdf.Build("one fish two fish red fish"));
        using var doc = _engine.Open(path);
        using var page = doc.GetPage(0);

        var matches = page.FindText("FISH");

        Assert.Equal(3, matches.Count);
        var xs = matches.Select(m => m.Rects[0].X).ToList();
        Assert.True(xs[0] < xs[1] && xs[1] < xs[2], "matches should come back left to right");
    }

    [Fact]
    public void FindText_SpansTextObjectBoundaries()
    {
        var path = Path.Combine(_dir, "multirun.pdf");
        File.WriteAllBytes(path, SamplePdf.BuildMultiRun()); // "Hello " + "cruel " + "world"
        using var doc = _engine.Open(path);
        using var page = doc.GetPage(0);

        var matches = page.FindText("hello cruel");

        var match = Assert.Single(matches);
        Assert.NotEmpty(match.Rects);
    }

    [Fact]
    public void FindText_NoMatch_ReturnsEmpty()
    {
        using var doc = _engine.Open(WriteSamplePdf());
        using var page = doc.GetPage(0);
        Assert.Empty(page.FindText("zebra"));
    }

    [Fact]
    public void FindText_EmptyTerm_ReturnsEmpty()
    {
        using var doc = _engine.Open(WriteSamplePdf());
        using var page = doc.GetPage(0);
        Assert.Empty(page.FindText(""));
    }

    private string WriteSamplePdf()
    {
        var path = Path.Combine(_dir, $"sample-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.Build());
        return path;
    }
}
