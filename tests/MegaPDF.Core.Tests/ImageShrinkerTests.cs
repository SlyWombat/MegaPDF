using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Imaging;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// Shrink's progress and cancellation (#145).
///
/// Shrink is the longest operation either desktop has. Measured against the synthetic large
/// fixtures <c>tools/gen_large_fixtures.py</c> builds, with a stub encoder so the figures are
/// floors rather than estimates: 7.1 s over a 280 MB scan's 100 images, 12.5 s over the 400
/// images of a 1 GB document and 39.7 s over the 1,000 images of a 2.5 GB one. Nothing else
/// measured came within a factor of eight of that. So it is the operation that most needs to say
/// where it has got to and to be stoppable — and it is also the one where stopping costs nothing,
/// because every caller runs it on a copy of the file made for the purpose.
///
/// These tests use a real two-image document rather than a stand-in, because what is being
/// asserted is that the loop reports and gives up *per image*, and a fake with one image cannot
/// tell "stopped after the first" from "never started".
/// </summary>
public sealed class ImageShrinkerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-shrink-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A document with two oversized images: the one-image sample, with its own page imported after it.</summary>
    private string WriteTwoImagePdf()
    {
        var one = Path.Combine(_dir, $"one-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(one, SamplePdf.BuildWithOversampledImage());
        var two = Path.Combine(_dir, $"two-{Guid.NewGuid():N}.pdf");
        using (var doc = _engine.Open(one))
        {
            doc.ImportPages(one, null, null, 1);
            using var file = File.Create(two);
            doc.Save(file);
        }
        return two;
    }

    /// <summary>Bytes in, smaller bytes out. What it encodes does not matter here; how far it got does.</summary>
    private static byte[] StubEncode(byte[] bgra, int width, int height, double quality)
        => new byte[Math.Max(1, bgra.Length / 20)];

    [Fact]
    public void Shrink_ReportsEveryImageAgainstTheTotal()
    {
        using var doc = _engine.Open(WriteTwoImagePdf());
        Assert.Equal(2, doc.GetImages().Count);

        var reports = new List<(int Done, int Total)>();
        var result = ImageShrinker.Shrink(doc, StubEncode, progress: (done, total) => reports.Add((done, total)));

        Assert.Equal(2, result.ImagesReplaced);
        // The total is known before the first image is touched, so the first report is the one
        // that turns the bar determinate rather than leaving it indeterminate for the whole of
        // the first image — which on a 300 dpi scan is a second or more on its own.
        Assert.Equal((0, 2), reports[0]);
        Assert.Equal([(0, 2), (1, 2), (2, 2)], reports);
    }

    [Fact]
    public void Shrink_CountsImagesConsidered_NotImagesReplaced()
    {
        // A document whose image is not worth re-encoding: 100 pixels across a 200pt box is under
        // fifty dpi already, so the loop skips it by `continue` — and the bar must still move. A
        // bar that only advanced on the paths that did work would stall on a document full of
        // pictures already the right size, which is a common document rather than an odd one.
        var path = Path.Combine(_dir, "already-small.pdf");
        File.WriteAllBytes(path, SamplePdf.BuildWithLargeImage());
        using var doc = _engine.Open(path);

        var reports = new List<(int Done, int Total)>();
        var result = ImageShrinker.Shrink(doc, StubEncode, progress: (done, total) => reports.Add((done, total)));

        Assert.Equal(0, result.ImagesReplaced);
        Assert.Equal(doc.GetImages().Count, reports[^1].Done);
        Assert.Equal(doc.GetImages().Count, reports[^1].Total);
    }

    [Fact]
    public void Shrink_StopsWhenAsked_AtTheImageItIsOn()
    {
        using var doc = _engine.Open(WriteTwoImagePdf());
        using var cancel = new CancellationTokenSource();

        var reports = new List<(int Done, int Total)>();
        // Cancelled from inside the first image's own report: deterministic, and not a wait. The
        // second image must then never be touched.
        var thrown = Assert.Throws<OperationCanceledException>(() => ImageShrinker.Shrink(
            doc, StubEncode,
            progress: (done, total) =>
            {
                reports.Add((done, total));
                if (done == 1)
                    cancel.Cancel();
            },
            cancellationToken: cancel.Token));

        Assert.Equal(cancel.Token, thrown.CancellationToken);
        Assert.Equal([(0, 2), (1, 2)], reports);
    }

    [Fact]
    public void Shrink_AskedToStopBeforeItBegins_TouchesNothing()
    {
        using var doc = _engine.Open(WriteTwoImagePdf());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ImageShrinker.Shrink(doc, StubEncode, cancellationToken: cancel.Token));

        // Still saveable and still readable: a cancel is not a poisoning. (The desktops throw the
        // copy away rather than saving it, but the document itself must be left in one piece,
        // because the copy is opened from the same file the open document reads.)
        var after = Path.Combine(_dir, "after-cancel.pdf");
        using (var file = File.Create(after))
            doc.Save(file);
        using var reopened = _engine.Open(after);
        Assert.Equal(2, reopened.PageCount);
    }

    [Fact]
    public void Shrink_WithNoProgressAndNoToken_BehavesAsItAlwaysDid()
    {
        using var doc = _engine.Open(WriteTwoImagePdf());
        Assert.Equal(2, ImageShrinker.Shrink(doc, StubEncode).ImagesReplaced);
    }
}
