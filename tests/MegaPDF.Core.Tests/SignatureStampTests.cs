using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

public class SignatureStampTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-sig-stamp-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly PdfRect PlacementBounds = new(100, 400, 180, 60);

    /// <summary>An opaque blue 24x8 test "signature" with transparent top-left quadrant.</summary>
    private static (byte[] Bgra, int W, int H) TestImage()
    {
        const int w = 24, h = 8;
        var bgra = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                var transparent = x < w / 2 && y < h / 2;
                bgra[i] = 0xCC;                                   // B
                bgra[i + 1] = 0x33;                               // G
                bgra[i + 2] = 0x11;                               // R
                bgra[i + 3] = (byte)(transparent ? 0x00 : 0xFF);  // A
            }
        }
        return (bgra, w, h);
    }

    [Fact]
    public void AddImageStamp_HitTests_Renders_AndPersists()
    {
        var savedPath = Path.Combine(_dir, "signed.pdf");
        string id;
        var (bgra, w, h) = TestImage();

        using (var doc = _engine.Open(WritePdf()))
        {
            using (var page = doc.GetPage(0))
            {
                id = page.AddImageStamp(bgra, w, h, PlacementBounds);
                Assert.StartsWith("sig:", id);

                var hit = page.HitTest(PlacementBounds.Center);
                Assert.Equal(PageHitKind.StampAnnotation, hit.Kind);
                Assert.Equal(id, hit.AnnotationId);

                // Render full-size; the opaque half of the image must show color.
                var rendered = page.Render(612, 792);
                Assert.True(HasBluePixel(rendered, 100, 430, 180, 30), "signature pixels should render");
            }
            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        using var reopenedPage = reopened.GetPage(0);
        Assert.Equal(PageHitKind.StampAnnotation, reopenedPage.HitTest(PlacementBounds.Center).Kind);
    }

    [Fact]
    public void GetStampImage_ReadsBackPlacedPixels()
    {
        var (bgra, w, h) = TestImage();
        using var doc = _engine.Open(WritePdf());
        using var page = doc.GetPage(0);
        var id = page.AddImageStamp(bgra, w, h, PlacementBounds);

        var image = page.GetStampImage(id);

        // The rendered read-back may be scaled by the placement matrix; verify content,
        // not exact dimensions: transparent top-left region, opaque blue bottom-right.
        Assert.NotNull(image);
        Assert.True(image.PixelWidth > 0 && image.PixelHeight > 0);

        var topLeft = (image.PixelHeight / 8 * image.PixelWidth + image.PixelWidth / 8) * 4;
        Assert.Equal(0, image.Bgra[topLeft + 3]);

        var bottomRight = ((image.PixelHeight * 7 / 8) * image.PixelWidth + image.PixelWidth * 7 / 8) * 4;
        Assert.True(image.Bgra[bottomRight] > 0xA0, "expected blue channel");
        Assert.Equal(0xFF, image.Bgra[bottomRight + 3]);
    }

    [Fact]
    public void SignatureOperations_UndoRedo_PlaceAndRemove()
    {
        var (bgra, w, h) = TestImage();
        using var doc = _engine.Open(WritePdf());
        var stack = new UndoStack();

        var place = new AddSignatureOperation(doc, 0, bgra, w, h, PlacementBounds);
        stack.Do(place);
        using (var page = doc.GetPage(0))
            Assert.Equal(PageHitKind.StampAnnotation, page.HitTest(PlacementBounds.Center).Kind);

        var remove = new RemoveSignatureOperation(doc, 0, place.CurrentId!, PlacementBounds);
        stack.Do(remove);
        using (var page = doc.GetPage(0))
            Assert.Equal(PageHitKind.None, page.HitTest(PlacementBounds.Center).Kind);

        stack.Undo(); // signature back
        using (var page = doc.GetPage(0))
            Assert.Equal(PageHitKind.StampAnnotation, page.HitTest(PlacementBounds.Center).Kind);

        stack.Undo(); // placement undone
        using (var page = doc.GetPage(0))
            Assert.Equal(PageHitKind.None, page.HitTest(PlacementBounds.Center).Kind);
    }

    private static bool HasBluePixel(RenderedPage rendered, int x, int y, int w, int h)
    {
        for (var row = y; row < y + h; row++)
        {
            for (var col = x; col < x + w; col++)
            {
                var i = (row * rendered.PixelWidth + col) * 4;
                if (rendered.Bgra[i] > 0xA0 && rendered.Bgra[i + 2] < 0x60)
                    return true;
            }
        }
        return false;
    }

    private string WritePdf()
    {
        var path = Path.Combine(_dir, $"sig-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.Build());
        return path;
    }
}
