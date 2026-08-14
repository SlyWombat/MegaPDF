using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// Search rects on a page whose CropBox is offset from the MediaBox (#28). The
/// viewer draws the CropBox and scales highlights by the page size it was given,
/// so a rect reported in raw user space is drawn at the wrong height — right
/// column, wrong line.
/// </summary>
public class SearchCropBoxTests
{
    private readonly PdfiumEngine _engine = new();

    [Fact]
    public void FindText_OnOffsetCropBox_ReportsRectInsideTheVisiblePage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"crop-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.BuildWithOffsetCropBox());
        try
        {
            using var doc = _engine.Open(path);
            using var page = doc.GetPage(0);

            // CropBox [0 100 612 700] -> the visible page is 612 x 600.
            Assert.Equal(612, page.Width, 1);
            Assert.Equal(600, page.Height, 1);

            var rect = Assert.Single(Assert.Single(page.FindText("megapdf")).Rects);

            // Text baseline is at user-space y=650, which is 50pt below the crop top,
            // so in the viewer's top-left space the glyphs sit just above y=50.
            Assert.InRange(rect.Y, 0, 60);
            Assert.InRange(rect.Y + rect.Height, 0, page.Height);
            Assert.InRange(rect.X, 100, 300);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
