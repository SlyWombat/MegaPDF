using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// Search counts on a real-world document, asserted exactly (#98).
///
/// During the corpus stress run (#92) the micro:bit V2.2.1 schematic returned 13
/// matches for "the" once on macOS and 12 on every other attempt on both
/// platforms — 84 re-runs never reproduced it. The same assertion lives in the
/// iOS and Android engine tests, so every CI run is another sample. A failure
/// here means the engine's text search is not deterministic for this document;
/// capture the per-page counts and the rects before touching anything else.
/// </summary>
public class SearchParityTests
{
    private static readonly int[] ExpectedPerPage = [4, 6, 2];

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void MicrobitSchematic_The_HasExactlyTwelveMatches_FourSixTwo()
    {
        var engine = new PdfiumEngine();
        using var doc = engine.Open(Fixture("microbit-v2-schematic.pdf"));
        Assert.Equal(ExpectedPerPage.Length, doc.PageCount);

        var perPage = new int[doc.PageCount];
        for (var i = 0; i < doc.PageCount; i++)
        {
            using var page = doc.GetPage(i);
            var matches = page.FindText("the");
            perPage[i] = matches.Count;

            foreach (var match in matches)
            {
                Assert.NotEmpty(match.Rects);
                foreach (var r in match.Rects)
                {
                    Assert.True(r.Width > 0 && r.Height > 0, $"page {i + 1}: degenerate rect {r}");
                    Assert.True(r.X >= -1 && r.Y >= -1
                                && r.X + r.Width <= page.Width + 1 && r.Y + r.Height <= page.Height + 1,
                        $"page {i + 1}: rect {r} lies outside the {page.Width}x{page.Height} page");
                }
            }
        }

        Assert.Equal(ExpectedPerPage, perPage);
        Assert.Equal(12, perPage.Sum());
    }

    [Fact]
    public void MicrobitSchematic_Seaman_HasNoMatches()
    {
        var engine = new PdfiumEngine();
        using var doc = engine.Open(Fixture("microbit-v2-schematic.pdf"));
        for (var i = 0; i < doc.PageCount; i++)
        {
            using var page = doc.GetPage(i);
            Assert.Empty(page.FindText("Seaman"));
        }
    }
}
