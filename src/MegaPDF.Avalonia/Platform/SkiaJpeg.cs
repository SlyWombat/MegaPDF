using System.Runtime.InteropServices;
using SkiaSharp;

namespace MegaPDF.Avalonia.Platform;

/// <summary>
/// JPEG encoding for shrink-for-email, via SkiaSharp.
///
/// No new native dependency: Avalonia already renders through Skia, so
/// libSkiaSharp is in the bundle either way. The package reference is pinned to the
/// version Avalonia resolves — two SkiaSharp versions wanting different libSkiaSharp
/// ABIs inside one app fails at runtime on macOS, not at build.
/// </summary>
internal static class SkiaJpeg
{
    /// <summary>Encodes straight BGRA as JPEG. Quality is 0..1, as SDD §3.7 states it.</summary>
    internal static byte[] Encode(byte[] bgra, int width, int height, double quality)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);

        var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            bitmap.InstallPixels(info, pin.AddrOfPinnedObject(), info.RowBytes);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, (int)Math.Round(quality * 100));
            return encoded.ToArray();
        }
        finally
        {
            pin.Free();
        }
    }
}
