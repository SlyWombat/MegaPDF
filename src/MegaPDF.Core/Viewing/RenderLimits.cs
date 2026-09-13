using MegaPDF.Core.Engine.Core;

namespace MegaPDF.Core.Viewing;

/// <summary>
/// How large a page raster the viewers will ask the engine for (#93, #94).
///
/// A poster-sized scan at 300% on a retina display works out to hundreds of
/// megapixels: PDFium refuses the bitmap, GPUs cannot texture anything past
/// 16,384 px on a side, and the process balloons past 10 GB on the way there.
/// Nothing on screen needs that many pixels, so the ideal size is clamped and the
/// view scales the bitmap up over the remaining distance.
///
/// Since #111 the clamp is the shared engine core's (<c>megapdf_render_size</c>),
/// so every platform — the phones included — behaves the same on the same
/// document; this class is the desktop binding of it.
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
        CoreNative.megapdf_render_size(idealWidth, idealHeight, out var width, out var height);
        return (width, height);
    }

    /// <summary>True when <see cref="Fit"/> would shrink this request, i.e. the page is
    /// being rendered below the size the view will show it at.</summary>
    public static bool IsCapped(double idealWidth, double idealHeight) =>
        CoreNative.megapdf_render_is_capped(idealWidth, idealHeight) != 0;
}
