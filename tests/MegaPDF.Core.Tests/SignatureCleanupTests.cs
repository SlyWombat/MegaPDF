using MegaPDF.Core.Imaging;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// SDD §6.2 contract 3. These cases are deliberately the same ones Android's
/// SignatureImageProcessorTest.kt asserts, with the same numbers — that test was
/// the only executable statement of this contract anywhere, so matching it is how
/// the two platforms are held together. A change here is a breaking change on
/// Android and iOS too.
/// </summary>
public class SignatureCleanupTests
{
    private static byte[] Bgra(params (byte A, byte R, byte G, byte B)[] pixels)
    {
        var bytes = new byte[pixels.Length * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            bytes[i * 4] = pixels[i].B;
            bytes[i * 4 + 1] = pixels[i].G;
            bytes[i * 4 + 2] = pixels[i].R;
            bytes[i * 4 + 3] = pixels[i].A;
        }
        return bytes;
    }

    [Fact]
    public void NearWhite_BecomesTransparent_InkStays()
    {
        // luminance ~250 > 235 removed; dark blue ~40 kept.
        var pixels = Bgra((255, 250, 250, 250), (255, 30, 30, 120));

        SignatureCleanup.RemoveWhiteBackground(pixels);

        Assert.Equal(0, pixels[3]);
        Assert.Equal(255, pixels[7]);
    }

    [Fact]
    public void LuminanceCutoff_UsesBgrWeights_NotRgb()
    {
        // The case that catches a channel-order mistake: pure green is 0.587*255 =
        // 149.7 and must survive, while a flat 240 grey must not.
        var pixels = Bgra((255, 0, 255, 0), (255, 240, 240, 240));

        SignatureCleanup.RemoveWhiteBackground(pixels);

        Assert.Equal(255, pixels[3]);
        Assert.Equal(0, pixels[7]);
    }

    [Fact]
    public void Trim_CropsToInkBoundingBox_PlusFourPixelMargin()
    {
        const int width = 30, height = 20;
        var pixels = new byte[width * height * 4];      // all transparent
        var dot = ((10 * width) + 12) * 4;              // one ink dot at (12,10)
        pixels[dot + 3] = 255;

        var trimmed = SignatureCleanup.TrimToInk(pixels, width, height);

        Assert.Equal(9, trimmed.Width);                 // x 8..16
        Assert.Equal(9, trimmed.Height);                // y 6..14
        Assert.Equal(255, trimmed.Bgra[((4 * 9) + 4) * 4 + 3]);   // dot now centred
    }

    [Fact]
    public void Trim_MarginClampsAtTheEdges()
    {
        const int width = 10, height = 10;
        var pixels = new byte[width * height * 4];
        pixels[3] = 255;                                // ink in the top-left corner

        var trimmed = SignatureCleanup.TrimToInk(pixels, width, height);

        Assert.Equal(5, trimmed.Width);                 // 0..4, not -4..4
        Assert.Equal(5, trimmed.Height);
    }

    [Fact]
    public void Trim_WhenNothingIsVisible_KeepsTheImageUnchanged()
    {
        const int width = 5, height = 5;
        var pixels = new byte[width * height * 4];
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 10;                             // alpha 10, at or below the cutoff

        var trimmed = SignatureCleanup.TrimToInk(pixels, width, height);

        Assert.Equal(5, trimmed.Width);
        Assert.Equal(5, trimmed.Height);
    }

    [Fact]
    public void HasTransparency_DistinguishesDrawnFromPhotographed()
    {
        Assert.True(SignatureCleanup.HasTransparency(Bgra((0, 0, 0, 0))));
        Assert.False(SignatureCleanup.HasTransparency(Bgra((255, 10, 10, 10), (252, 200, 200, 200))));
    }

    [Fact]
    public void Clean_RemovesBackgroundThenTrims()
    {
        // A 3x3 white field with one dark pixel in the middle: after cleanup the
        // white is transparent and the trim clamps to the whole image (margin 4 on a
        // 3x3), which is the realistic end-to-end shape of a tiny scan.
        const int width = 3, height = 3;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }
        var centre = ((1 * width) + 1) * 4;
        pixels[centre] = pixels[centre + 1] = pixels[centre + 2] = 20;

        var cleaned = SignatureCleanup.Clean(new SignatureBitmap(pixels, width, height));

        Assert.Equal(3, cleaned.Width);
        Assert.Equal(3, cleaned.Height);
        Assert.Equal(0, cleaned.Bgra[3]);                          // corner: background
        Assert.Equal(255, cleaned.Bgra[((1 * 3) + 1) * 4 + 3]);    // centre: ink
    }
}
