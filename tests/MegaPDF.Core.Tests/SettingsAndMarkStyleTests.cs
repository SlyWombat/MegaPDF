using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

public class SettingsAndMarkStyleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-settings-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Settings_DefaultsMatchStakeholderDecisions()
    {
        var settings = new AppSettings(Path.Combine(_dir, "settings.json"));
        Assert.Equal(CheckMarkStyle.Cross, settings.MarkStyle); // Appendix B #3: X default
        Assert.Equal("", settings.Theme);
        Assert.False(settings.ReopenLastFile);
    }

    [Fact]
    public void Settings_PersistAcrossInstances()
    {
        var path = Path.Combine(_dir, "settings.json");
        var settings = new AppSettings(path);
        settings.MarkStyle = CheckMarkStyle.FilledSquare;
        settings.Theme = "Dark";
        settings.ReopenLastFile = true;

        var reloaded = new AppSettings(path);
        Assert.Equal(CheckMarkStyle.FilledSquare, reloaded.MarkStyle);
        Assert.Equal("Dark", reloaded.Theme);
        Assert.True(reloaded.ReopenLastFile);
    }

    [Theory]
    [InlineData(CheckMarkStyle.Cross)]
    [InlineData(CheckMarkStyle.Check)]
    [InlineData(CheckMarkStyle.FilledSquare)]
    public void EveryMarkStyle_RendersInk_AndPersists(CheckMarkStyle style)
    {
        var docPath = Path.Combine(_dir, $"{style}.pdf");
        File.WriteAllBytes(docPath, SamplePdf.BuildWithDrawnSquares());
        var savedPath = Path.Combine(_dir, $"{style}-saved.pdf");
        var square = new PdfRect(100, 274, 18, 18);

        using (var doc = _engine.Open(docPath))
        {
            using (var page = doc.GetPage(0))
            {
                page.AddCheckMarkStamp(square, null, style);
                var rendered = page.Render(612, 792);
                Assert.True(RegionHasInk(rendered, 101, 275, 16, 16), $"{style} should draw ink in the square");
            }
            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        using var reopenedPage = reopened.GetPage(0);
        Assert.Equal(PageHitKind.StampAnnotation, reopenedPage.HitTest(square.Center).Kind);
    }

    private static bool RegionHasInk(RenderedPage rendered, int x, int y, int w, int h)
    {
        for (var row = y; row < y + h; row++)
        {
            for (var col = x; col < x + w; col++)
            {
                var i = (row * rendered.PixelWidth + col) * 4;
                if (rendered.Bgra[i] < 0x80 && rendered.Bgra[i + 1] < 0x80 && rendered.Bgra[i + 2] < 0x80)
                    return true;
            }
        }
        return false;
    }
}
