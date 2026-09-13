using MegaPDF.Core.Viewing;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>The raster size clamp both viewers apply before asking the engine to render (#93, #94).</summary>
public class RenderLimitsTests
{
    [Fact]
    public void OrdinaryPages_AreNotTouched()
    {
        // Letter at 300% on a 2x display: 4896 x 6336, well inside both limits.
        Assert.Equal((4896, 6336), RenderLimits.Fit(4896, 6336));
        Assert.False(RenderLimits.IsCapped(4896, 6336));
        Assert.Equal((1, 1), RenderLimits.Fit(0, 0));
    }

    [Fact]
    public void TheCorpusBanner_ComesDownToTheMegapixelBudget()
    {
        // 4176 x 2801 pt at 300% on a 1.5x display (issue #93's repro): 33408 x 22408.
        var (w, h) = RenderLimits.Fit(33408, 22408);
        Assert.True(RenderLimits.IsCapped(33408, 22408));
        Assert.True((long)w * h <= RenderLimits.MaxPixels, $"{w}x{h} exceeds the budget");
        Assert.True(w <= RenderLimits.MaxSidePixels && h <= RenderLimits.MaxSidePixels);
        Assert.Equal(33408.0 / 22408.0, (double)w / h, 2);
    }

    [Fact]
    public void AVeryWideStrip_IsBoundByTheSideLimit()
    {
        // 8368 x 1191 pt at 300% x 2: 66944 x 9528. The side clamp alone would still be
        // 38 MP, so the pixel budget takes it a little further down.
        var (w, h) = RenderLimits.Fit(66944, 9528);
        Assert.True(RenderLimits.IsCapped(66944, 9528));
        Assert.True(w <= RenderLimits.MaxSidePixels && h <= RenderLimits.MaxSidePixels, $"{w}x{h}");
        Assert.True((long)w * h <= RenderLimits.MaxPixels, $"{w}x{h}");
        Assert.InRange(w, 14000, RenderLimits.MaxSidePixels);
        Assert.Equal(66944.0 / 9528.0, (double)w / h, 2);
    }

    [Fact]
    public void CappedSize_IsTheSameForEveryZoomPastTheCap()
    {
        // This is what lets a viewer reuse one raster across zoom steps on a huge page.
        var at200 = RenderLimits.Fit(22272, 14939);
        var at300 = RenderLimits.Fit(33408, 22408);
        Assert.InRange(Math.Abs(at200.Width - at300.Width), 0, 1);
        Assert.InRange(Math.Abs(at200.Height - at300.Height), 0, 1);
    }
}
