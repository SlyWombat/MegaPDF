using System.Text;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Viewing;
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

    /// <summary>
    /// The refusal the viewers rely on: anything past <see cref="RenderLimits"/> is rejected
    /// rather than attempted. It used to be checked only by accident, as three stress fixtures
    /// that came back "partial" in every run because the harness asked for their natural size
    /// (#209). The harness fits its renders now, so the refusal needs a test of its own.
    /// </summary>
    [Fact]
    public void Render_PastTheLimits_IsRefused()
    {
        using var doc = _engine.Open(WriteSamplePdf());
        using var page = doc.GetPage(0);

        var tooWide = RenderLimits.MaxSidePixels + 1;
        var ex = Assert.Throws<InvalidOperationException>(() => page.Render(tooWide, 1000));
        Assert.Contains("RenderLimits", ex.Message);

        // And the size RenderLimits hands back for the same request is one the engine takes.
        var (w, h) = RenderLimits.Fit(tooWide, 1000);
        Assert.Equal(w * h * 4, page.Render(w, h).Bgra.Length);
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

    // --- Save (#97): a full rewrite, and one that must not balloon the file ---

    [Fact]
    public void Save_Unmodified_DoesNotAppendACopyOfTheDocument()
    {
        // Guards against switching to FPDF_INCREMENTAL by reflex: PDFium's incremental
        // mode appends every loaded object, so an unmodified save came back as the
        // original plus a whole rewrite (1.97x at the median over the corpus, #97).
        // A rewrite of this sample is smaller than the sample itself plus the sample.
        var path = WriteSamplePdf();
        var original = File.ReadAllBytes(path);

        using var saved = new MemoryStream();
        using (var doc = _engine.Open(path))
        {
            using (var page = doc.GetPage(0))
                _ = page.Width; // the apps' open-time size pass loads every page
            doc.Save(saved);
        }

        var bytes = saved.ToArray();
        Assert.True(bytes.Length < original.Length * 2, $"save grew the {original.Length}-byte sample to {bytes.Length} bytes");
        Assert.False(bytes.Length > original.Length && bytes.AsSpan(0, original.Length).SequenceEqual(original),
            "the output is the original with a copy of the document appended — the incremental trap");

        var reopenedPath = Path.Combine(_dir, "resaved.pdf");
        File.WriteAllBytes(reopenedPath, bytes);
        using var reopened = _engine.Open(reopenedPath);
        Assert.Equal(1, reopened.PageCount);
    }

    [Fact]
    public void Save_AfterAnEdit_ReopensWithTheEdit()
    {
        var path = WriteSamplePdf();
        var savedPath = Path.Combine(_dir, "edited.pdf");

        using (var doc = _engine.Open(path))
        {
            using (var page = doc.GetPage(0))
                page.AppendTextBox("added later", 12, new PdfPoint(72, 100));
            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        using var reopenedPage = reopened.GetPage(0);
        var box = Assert.Single(reopenedPage.GetTextBoxes());
        Assert.Equal("added later", box.Text);
    }

    [Fact]
    public void Save_ToANonSeekableStream_StillWrites()
    {
        var path = WriteSamplePdf();
        using var sink = new MemoryStream();
        using var forwardOnly = new ForwardOnlyStream(sink);
        using (var doc = _engine.Open(path))
            doc.Save(forwardOnly);
        Assert.True(sink.Length > 0);
    }

    /// <summary>A write-only, non-seekable wrapper — what a sandboxed host stream can look like.</summary>
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
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
