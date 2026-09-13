namespace MegaPDF.Core.Viewing;

/// <summary>
/// How large a page raster the viewers will ask the engine for (#93, #94).
///
/// A poster-sized scan at 300% on a retina display works out to hundreds of
/// megapixels: PDFium refuses the bitmap (an <see cref="OutOfMemoryException"/>
/// out of <c>Render</c>), GPUs cannot texture anything past 16,384 px on a side,
/// and the process balloons past 10 GB on the way there. Nothing on screen needs
/// that many pixels, so the ideal size is clamped here and the view scales the
/// bitmap up over the remaining distance.
///
/// The clamp is shared by both desktop apps so the same document behaves the same
/// on each, and it is a pure function so it can be tested without a graphics stack.
/// </summary>
public static class RenderLimits
{
    /// <summary>Longest side any page raster may have, in pixels (the common GPU texture limit).</summary>
    public const int MaxSidePixels = 16_384;

    /// <summary>Most pixels any page raster may have: 32 MP is 128 MB of BGRA, enough for
    /// a tabloid page at 300% on a 2× display with room to spare.</summary>
    public const long MaxPixels = 32_000_000;

    /// <summary>
    /// The pixel size to render at for a page that would ideally be
    /// <paramref name="idealWidth"/> × <paramref name="idealHeight"/> pixels. Aspect
    /// ratio is preserved; the result is never smaller than 1 × 1.
    /// </summary>
    public static (int Width, int Height) Fit(double idealWidth, double idealHeight)
    {
        var w = Math.Max(1.0, idealWidth);
        var h = Math.Max(1.0, idealHeight);

        var scale = 1.0;
        if (w > MaxSidePixels)
            scale = Math.Min(scale, MaxSidePixels / w);
        if (h > MaxSidePixels)
            scale = Math.Min(scale, MaxSidePixels / h);
        if (w * h * scale * scale > MaxPixels)
            scale = Math.Min(scale, Math.Sqrt(MaxPixels / (w * h)));

        return (Math.Max(1, (int)Math.Floor(w * scale)), Math.Max(1, (int)Math.Floor(h * scale)));
    }

    /// <summary>True when <see cref="Fit"/> would shrink this request, i.e. the page is
    /// being rendered below the size the view will show it at.</summary>
    public static bool IsCapped(double idealWidth, double idealHeight)
    {
        var (w, h) = Fit(idealWidth, idealHeight);
        return w < (int)Math.Floor(Math.Max(1.0, idealWidth)) || h < (int)Math.Floor(Math.Max(1.0, idealHeight));
    }
}
