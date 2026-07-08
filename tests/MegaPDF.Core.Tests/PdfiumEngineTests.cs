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

    private string WriteSamplePdf()
    {
        var path = Path.Combine(_dir, $"sample-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildSamplePdf());
        return path;
    }

    /// <summary>One-page US-Letter PDF with Helvetica text, correct xref offsets.</summary>
    private static byte[] BuildSamplePdf()
    {
        var content = "BT /F1 36 Tf 72 700 Td (Hello MegaPDF) Tj ET\n";
        string[] objects =
        [
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        ];

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new long[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(objects[i]);
        }

        var xrefOffset = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
