using System.Text;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

public class FontSubstitutionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-fontsub-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("ABCDEF+SegoeUI", true)]
    [InlineData("BCDFGH+Times-Roman", true)]
    [InlineData("Helvetica", false)]
    [InlineData("Arial-BoldMT", false)]
    [InlineData("abcdef+lower", false)]
    public void IsSubsetFontName_DetectsSubsetPrefix(string name, bool expected) =>
        Assert.Equal(expected, PdfiumPage.IsSubsetFontName(name));

    [Theory]
    [InlineData("SegoeUI", "Helvetica")]
    [InlineData("Arial-BoldMT", "Helvetica-Bold")]
    [InlineData("Calibri-Italic", "Helvetica-Oblique")]
    [InlineData("TimesNewRomanPSMT", "Times-Roman")]
    [InlineData("Times-BoldItalic", "Times-BoldItalic")]
    [InlineData("Georgia", "Helvetica")]
    [InlineData("LiberationSerif-Bold", "Times-Bold")]
    [InlineData("CourierNewPSMT", "Courier")]
    [InlineData("Consolas-Bold", "Helvetica-Bold")]
    [InlineData("RobotoMono-Italic", "Courier-Oblique")]
    public void MapToStandardFont_PicksClosestStandardFace(string original, string expected) =>
        Assert.Equal(expected, PdfiumPage.MapToStandardFont(original));

    [Fact]
    public void StandardFontEdit_StaysTier1()
    {
        using var doc = _engine.Open(WriteSamplePdf());
        using var page = doc.GetPage(0);
        var run = page.GetTextRuns()[0];

        // Helvetica is a full standard font, not a subset — no substitution needed.
        var outcome = page.SetTextRunText(run, "Symbols beyond original: XYZQ!?");

        Assert.Equal(TextEditOutcome.EditedInPlace, outcome);
    }

    [Fact]
    public void SubstituteTextObject_PreservesIndex_AndSurvivesSaveRoundTrip()
    {
        var savedPath = Path.Combine(_dir, "substituted.pdf");
        using (var doc = _engine.Open(WriteSamplePdf()))
        {
            using (var page = (PdfiumPage)doc.GetPage(0))
            {
                var run = page.GetTextRuns()[0];
                page.ForceSubstituteForTest(run, "Swapped to a standard face");

                var runs = page.GetTextRuns();
                var newRun = Assert.Single(runs);
                Assert.Equal(run.ObjectIndex, newRun.ObjectIndex);
                Assert.Equal("Swapped to a standard face", newRun.Text);
                // The replacement keeps the original's position (same baseline area).
                Assert.InRange(newRun.Bounds.Y, run.Bounds.Y - 10, run.Bounds.Y + 10);
                Assert.InRange(newRun.Bounds.X, run.Bounds.X - 5, run.Bounds.X + 5);
            }
            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        using var reopenedPage = reopened.GetPage(0);
        Assert.Equal("Swapped to a standard face", Assert.Single(reopenedPage.GetTextRuns()).Text);

        var rendered = reopenedPage.Render(306, 396);
        var hasInk = false;
        for (var i = 0; i < rendered.Bgra.Length && !hasInk; i += 4)
            hasInk = rendered.Bgra[i] < 0x80 && rendered.Bgra[i + 1] < 0x80 && rendered.Bgra[i + 2] < 0x80;
        Assert.True(hasInk, "substituted text should rasterize as ink");
    }

    private string WriteSamplePdf()
    {
        var path = Path.Combine(_dir, $"sample-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.Build());
        return path;
    }
}
