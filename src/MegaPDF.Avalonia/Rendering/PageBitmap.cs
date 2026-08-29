using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MegaPDF.Core.Engine;

namespace MegaPDF.Avalonia.Rendering;

/// <summary>
/// Bridges the engine's output to an Avalonia surface.
///
/// Proven by the ADR-002 spike on both platforms: page 1 of stamped.pdf rendered
/// to 816x1056 px / 3,446,784 BGRA bytes / 9 distinct pixel values identically on
/// macos-latest and windows-latest.
/// </summary>
internal static class PageBitmap
{
    /// <summary>PDF points are 1/72 inch; Avalonia's device-independent pixel is 1/96.</summary>
    internal const double PointsToPixels = 96.0 / 72.0;

    /// <summary>
    /// Core hands back plain BGRA bytes (<see cref="RenderedPage"/>), which is
    /// exactly Avalonia's <see cref="PixelFormat.Bgra8888"/> — so this is a
    /// straight row-by-row copy with no per-pixel conversion. That the engine's
    /// output format needs no adaptation is itself an ADR-002 finding.
    ///
    /// Unpremul, not Premul: pdfium's FPDFBitmap_BGRA is straight
    /// (non-premultiplied) alpha. A rendered page is opaque so the distinction
    /// cannot change what you see here, but getting it wrong would quietly
    /// misrepresent any future transparent overlay.
    /// </summary>
    internal static WriteableBitmap FromRenderedPage(RenderedPage page, double dpiScale = 1.0)
    {
        var dpi = 96.0 * dpiScale;
        var bitmap = new WriteableBitmap(
            new PixelSize(page.PixelWidth, page.PixelHeight),
            new Vector(dpi, dpi),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using var buffer = bitmap.Lock();
        var rowBytes = page.PixelWidth * 4;

        // Copy per row rather than in one shot: the locked framebuffer's stride
        // is not guaranteed to equal width*4.
        for (var y = 0; y < page.PixelHeight; y++)
        {
            Marshal.Copy(page.Bgra, y * rowBytes, buffer.Address + (y * buffer.RowBytes), rowBytes);
        }

        return bitmap;
    }

    /// <summary>
    /// Renders one page at a zoom factor, in the pixel grid the display actually
    /// uses — so a retina Mac and a 100% Windows monitor both get a sharp page
    /// rather than an upscaled one.
    /// </summary>
    internal static WriteableBitmap Render(IPdfPage page, double zoom, double dpiScale)
    {
        var scale = PointsToPixels * zoom * dpiScale;
        var pixelWidth = Math.Max(1, (int)Math.Round(page.Width * scale));
        var pixelHeight = Math.Max(1, (int)Math.Round(page.Height * scale));
        return FromRenderedPage(page.Render(pixelWidth, pixelHeight), dpiScale);
    }
}
