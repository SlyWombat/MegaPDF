using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Imaging;

/// <summary>
/// Shrink-for-email (SDD §3.7): re-encodes oversized page images at screen-ish
/// resolution so a scanned document becomes small enough to send.
///
/// The decision rules live here rather than in either UI, because "which images are
/// worth re-encoding, and at what size" is behaviour that should not differ between
/// Windows and macOS. Only the JPEG encoder is injected — that genuinely differs
/// per platform (Windows.Graphics.Imaging on WinUI, SkiaSharp on Avalonia).
/// </summary>
public static class ImageShrinker
{
    /// <summary>Images are re-encoded to roughly this resolution on the page.</summary>
    public const double TargetDpi = 150;

    /// <summary>JPEG quality for the re-encode — visually fine for scans, much smaller.</summary>
    public const double JpegQuality = 0.75;

    /// <summary>Smallest edge worth re-encoding, and the floor the target clamps to.</summary>
    public const int MinTargetPixels = 8;

    /// <summary>Encodes BGRA pixels as JPEG at the given quality (0..1).</summary>
    public delegate byte[] JpegEncoder(byte[] bgra, int width, int height, double quality);

    public sealed record Result(int ImagesReplaced);

    /// <summary>
    /// Re-encodes what is worth re-encoding, in place, on the supplied document.
    /// Callers work on a copy: this degrades image quality by design.
    /// </summary>
    public static Result Shrink(IPdfDocument document, JpegEncoder encodeJpeg)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(encodeJpeg);

        var replaced = 0;

        foreach (var image in document.GetImages())
        {
            var targetWidth = (int)Math.Round(image.DisplayWidthPoints / 72 * TargetDpi);
            var targetHeight = (int)Math.Round(image.DisplayHeightPoints / 72 * TargetDpi);

            // Skip what is not worth touching: an image that is already about the
            // right resolution AND under 100 KB, or anything tiny. Re-encoding those
            // costs quality and saves nothing.
            var oversized = image.PixelWidth > targetWidth * 1.2;
            if ((!oversized && image.StoredByteLength < 100_000) || image.StoredByteLength < 8_000)
                continue;

            // Nothing this small is worth re-encoding, and clamping it would throw:
            // Math.Clamp requires min <= max, so Clamp(n, 8, 4) is an
            // ArgumentException that escapes the loop and abandons every remaining
            // image in the document (#64). A 4x4 divider carrying a fat ICC profile
            // is enough to reach here.
            if (image.PixelWidth < MinTargetPixels || image.PixelHeight < MinTargetPixels)
                continue;

            targetWidth = Math.Clamp(targetWidth, MinTargetPixels, image.PixelWidth);
            targetHeight = Math.Clamp(targetHeight, MinTargetPixels, image.PixelHeight);

            var pixels = document.RenderImageAt(image, targetWidth, targetHeight);
            var jpeg = encodeJpeg(pixels.Bgra, pixels.PixelWidth, pixels.PixelHeight, JpegQuality);

            // A re-encode that saves less than 10% is not worth the quality loss.
            if (jpeg.Length >= image.StoredByteLength * 0.9)
                continue;

            document.ReplaceImageWithJpeg(image, jpeg);
            replaced++;
        }

        return new Result(replaced);
    }
}
