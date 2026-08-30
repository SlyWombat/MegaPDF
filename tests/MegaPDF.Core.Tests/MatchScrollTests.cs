using MegaPDF.Core.Engine;
using MegaPDF.Core.Viewing;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #32 — the reasoning behind "scroll the hit into view", pinned.
///
/// The visual half genuinely needs a window and a person. This covers the part
/// that does not: which axis moves, and when. That is where both the original bug
/// (#28: horizontal never moved, so a hit on a zoomed page was revealed off
/// screen) and its opposite (jolting the view for a hit already visible) live.
/// </summary>
public class MatchScrollTests
{
    // A 1000x2000 content in an 800x600 viewport: overflows both axes.
    private const double ViewportW = 800, ViewportH = 600, ExtentW = 1000;

    private static ScrollDecision Reveal(PdfRect target, double offsetX = 0, double offsetY = 0,
                                         double extentWidth = ExtentW) =>
        MatchScroll.Reveal(target, offsetX, offsetY, ViewportW, ViewportH, extentWidth);

    [Fact]
    public void AHitAlreadyComfortablyInView_MovesNothing()
    {
        var decision = Reveal(new PdfRect(100, 200, 60, 12));

        Assert.Null(decision.Horizontal);
        Assert.Null(decision.Vertical);
        Assert.False(decision.MovesAnything);
    }

    [Fact]
    public void AHitBelowTheFold_MovesOnlyVertically()
    {
        var decision = Reveal(new PdfRect(100, 1500, 60, 12));

        Assert.Null(decision.Horizontal);
        // Lands a third down rather than at the very top.
        Assert.Equal(1500 - (ViewportH / 3), decision.Vertical!.Value, 3);
    }

    [Fact]
    public void AHitOffToTheSide_MovesBothAxes()
    {
        // This is #28: before the fix the horizontal axis never moved, so the hit
        // was highlighted where the user could not see it.
        var decision = Reveal(new PdfRect(950, 1500, 40, 12));

        Assert.NotNull(decision.Horizontal);
        Assert.NotNull(decision.Vertical);
        // Centred horizontally on the hit.
        Assert.Equal(950 + 20 - (ViewportW / 2), decision.Horizontal!.Value, 3);
    }

    [Fact]
    public void WhenTheContentDoesNotOverflowHorizontally_TheHorizontalAxisIsLeftAlone()
    {
        // Below overflow the panel centres its content, so content coordinates do
        // not line up with the scroll offset and moving would land somewhere wrong.
        var decision = Reveal(new PdfRect(700, 1500, 60, 12), extentWidth: ViewportW);

        Assert.Null(decision.Horizontal);
        Assert.NotNull(decision.Vertical);
    }

    [Fact]
    public void AHitInsideTheMarginCountsAsOutside()
    {
        // Hard against the top edge is visible but unreadable in context.
        var decision = Reveal(new PdfRect(100, 210, 60, 12), offsetY: 200);

        Assert.NotNull(decision.Vertical);
    }

    [Fact]
    public void RevealingNeverScrollsPastTheStart()
    {
        var decision = Reveal(new PdfRect(10, 5, 60, 12), offsetY: 400);

        Assert.Equal(0, decision.Vertical!.Value);
    }

    [Fact]
    public void AWideHitWrappingAcrossLines_IsJudgedByItsFullExtent()
    {
        // Search matches arrive as the union of their rects, so a hit that wraps is
        // one wide rectangle; its right edge is what decides whether it fits.
        var decision = Reveal(new PdfRect(600, 200, 380, 12));

        Assert.NotNull(decision.Horizontal);
    }
}
