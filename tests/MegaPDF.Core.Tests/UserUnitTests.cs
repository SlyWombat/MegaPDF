using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// A page drawn in units larger than a point (#150). Fixtures/userunit.pdf is a 306 x 396
/// MediaBox with CropBox [0 50 306 350] and /UserUnit 2 (tools/gen_test_fixtures.py), so
/// it measures 612 x 600 pt. A viewer that ignores /UserUnit shows it at half size and
/// puts every tap, mark and text box at half its distance from the corner.
/// </summary>
public class UserUnitTests
{
    private readonly PdfiumEngine _engine = new();

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "userunit.pdf");

    [Fact]
    public void Page_MeasuresInPoints_AndReportsContentWhereItIsDrawn()
    {
        using var doc = _engine.Open(Fixture);
        using var page = doc.GetPage(0);

        Assert.Equal(612, page.Width, 1);
        Assert.Equal(600, page.Height, 1);

        // The 10 pt square at crop (100,400)-(110,410): top-left y = 600 - 410 = 190.
        var square = Assert.Single(page.DetectCheckboxSquares());
        // The square's bounds take in its 1 pt stroke.
        Assert.InRange(square.X, 99, 101);
        Assert.InRange(square.Y, 189, 191);

        // The text field at crop (100,300)-(300,320).
        var field = Assert.Single(page.GetFormFields());
        Assert.Equal(new PdfRect(100, 280, 200, 20), Round(field.Bounds));

        // "Hello MegaPDF" in 36 pt with its baseline 50 pt below the top.
        var run = Assert.Single(page.GetTextRuns());
        Assert.Equal(36, run.FontSize, 2);
        Assert.InRange(run.Bounds.Bottom, 50, 60);
        var hit = Assert.Single(Assert.Single(page.FindText("megapdf")).Rects);
        Assert.InRange(hit.Y, 20, 60);

        // Shrink-for-email sizes its target from the placed size in points.
        var image = Assert.Single(doc.GetImages());
        Assert.Equal(100, image.DisplayWidthPoints, 2);
        Assert.Equal(50, image.DisplayHeightPoints, 2);
    }

    [Fact]
    public void Placements_LandWhereTheViewAskedForThem()
    {
        using var doc = _engine.Open(Fixture);
        using var page = doc.GetPage(0);

        var cover = new PdfRect(20, 20, 60, 30);
        page.AppendWhiteout(cover);
        Assert.Equal(cover, Round(Assert.Single(page.GetWhiteouts()).Bounds));

        var box = page.AppendTextBox("Unit", 12, new PdfPoint(40, 120));
        var boxes = page.GetTextBoxes();
        var written = Assert.Single(boxes);
        Assert.Equal(12, written.FontSize, 2);
        Assert.InRange(written.Bounds.Height, 8, 16);
        Assert.InRange(written.Bounds.X, 39, 43);
        page.MoveTextBox(box, written.Bounds with { X = 200, Y = 220 });
        var moved = Assert.Single(page.GetTextBoxes());
        Assert.InRange(moved.Bounds.X, 199.9, 200.1);
        Assert.InRange(moved.Bounds.Y, 219.9, 220.1);

        var bgra = Enumerable.Range(0, 4 * 4 * 4).Select(i => i % 4 == 3 ? (byte)0xFF : (byte)0x40).ToArray();
        var placed = new PdfRect(350, 100, 120, 60);
        var id = page.AddImageStamp(bgra, 4, 4, placed);
        Assert.Equal(placed, Round(Assert.Single(page.GetStamps(), s => s.Id == id).Bounds));

        // Rendered at 1 px per point, the signature and the whiteout are where they were put.
        var rendered = page.Render(612, 600);
        Assert.True(IsDark(rendered, 410, 130), "the signature draws inside its bounds");
        Assert.True(IsWhite(rendered, 340, 130) && IsWhite(rendered, 480, 130), "and not beside them");
        Assert.True(IsWhite(rendered, 50, 35), "the whiteout is white");
    }

    private static PdfRect Round(PdfRect r) =>
        new(Math.Round(r.X, 1), Math.Round(r.Y, 1), Math.Round(r.Width, 1), Math.Round(r.Height, 1));

    private static bool IsWhite(RenderedPage p, int x, int y) => Channels(p, x, y).All(c => c == 0xFF);

    private static bool IsDark(RenderedPage p, int x, int y) => Channels(p, x, y).All(c => c < 0xA0);

    private static byte[] Channels(RenderedPage p, int x, int y)
    {
        var i = (y * p.PixelWidth + x) * 4;
        return [p.Bgra[i], p.Bgra[i + 1], p.Bgra[i + 2]];
    }
}
