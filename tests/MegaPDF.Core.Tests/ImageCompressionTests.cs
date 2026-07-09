using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

public class ImageCompressionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-imgcompress-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WritePdf()
    {
        var path = Path.Combine(_dir, $"big-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.BuildWithLargeImage());
        return path;
    }

    [Fact]
    public void GetImages_ReportsPixelDisplayAndStoredSizes()
    {
        using var doc = _engine.Open(WritePdf());
        var image = Assert.Single(doc.GetImages());

        Assert.Equal(0, image.PageIndex);
        Assert.Equal(100, image.PixelWidth);
        Assert.Equal(100, image.PixelHeight);
        Assert.Equal(200, image.DisplayWidthPoints, 1);
        Assert.Equal(200, image.DisplayHeightPoints, 1);
        Assert.Equal(30_000, image.StoredByteLength);
    }

    [Fact]
    public void RenderImageAt_DownsamplesToRequestedSize()
    {
        using var doc = _engine.Open(WritePdf());
        var image = doc.GetImages()[0];

        var pixels = doc.RenderImageAt(image, 25, 25);

        Assert.Equal(25, pixels.PixelWidth);
        Assert.Equal(25, pixels.PixelHeight);
        // Source bytes are all 0x40 → downsampled pixels stay mid-gray.
        Assert.InRange(pixels.Bgra[0], 0x30, 0x50);
    }

    [Fact]
    public void ReplaceImageWithJpeg_ShrinksTheFile_AndStillRenders()
    {
        var sourcePath = WritePdf();
        var savedPath = Path.Combine(_dir, "smaller.pdf");
        var originalSize = new FileInfo(sourcePath).Length;

        using (var doc = _engine.Open(sourcePath))
        {
            var image = doc.GetImages()[0];
            var pixels = doc.RenderImageAt(image, 25, 25);
            var jpeg = EncodeJpeg(pixels.Bgra, pixels.PixelWidth, pixels.PixelHeight, quality: 75);
            Assert.True(jpeg.Length < image.StoredByteLength / 4, "jpeg should be much smaller than raw");

            doc.ReplaceImageWithJpeg(image, jpeg);

            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        var newSize = new FileInfo(savedPath).Length;
        Assert.True(newSize < originalSize / 2, $"expected < half of {originalSize}, got {newSize}");

        // Reopen: image now stores the smaller resolution and still renders gray.
        using var reopened = _engine.Open(savedPath);
        var replaced = Assert.Single(reopened.GetImages());
        Assert.Equal(25, replaced.PixelWidth);
        Assert.Equal(200, replaced.DisplayWidthPoints, 1); // display size unchanged

        using var page = reopened.GetPage(0);
        var rendered = page.Render(612, 792);
        // Image area (PDF 100,400 200x200 → top-left y = 792-600 = 192): mid-gray, not blank.
        var i = ((250 * 612) + 200) * 4;
        Assert.InRange(rendered.Bgra[i], 0x28, 0x58);
    }

    private static byte[] EncodeJpeg(byte[] bgra, int width, int height, long quality)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        bitmap.UnlockBits(data);

        var encoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
        parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, quality);
        using var output = new MemoryStream();
        bitmap.Save(output, encoder, parameters);
        return output.ToArray();
    }
}
