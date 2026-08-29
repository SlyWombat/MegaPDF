namespace MegaPDF.Core.Imaging;

/// <summary>BGRA pixels of a signature image (B, G, R, A per pixel, top-down rows).</summary>
public sealed record SignatureBitmap(byte[] Bgra, int Width, int Height);

/// <summary>
/// SDD §6.2 **contract 3** — the signature cleanup pixel math, as a shared reference
/// every platform is measured against.
///
/// This lived only in <c>src/MegaPDF.App/SignatureImageProcessor.cs</c>, a WinUI
/// project that cannot be referenced from anywhere else and has no test project, so
/// the contract that "a change on any platform is a breaking change everywhere" had
/// no .NET test behind it at all — Android's SignatureImageProcessorTest.kt was the
/// only executable statement of it. ADR-002 flagged that as the contract most
/// exposed to silent drift, precisely because there is no cross-platform fixture PDF
/// that would catch a divergence later. This is that gap closed: the math moves
/// here, both desktop UIs call it, and the tests assert the same cases Android's do.
///
/// Only the arithmetic lives here. Decoding a PNG or JPEG stays with each UI, which
/// is the part that genuinely differs per platform.
/// </summary>
public static class SignatureCleanup
{
    /// <summary>Above this luminance a pixel is background, not ink.</summary>
    public const double WhiteLuminanceCutoff = 235;

    /// <summary>At or below this alpha a pixel does not count as visible ink.</summary>
    public const int InkAlphaCutoff = 16;

    /// <summary>Pixels of breathing room kept around the ink when trimming.</summary>
    public const int TrimMargin = 4;

    /// <summary>
    /// Whether the image already carries meaningful transparency, in which case it was
    /// drawn rather than photographed and the white-removal step would only damage it.
    /// </summary>
    public static bool HasTransparency(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
            if (bgra[i] < 250)
                return true;
        return false;
    }

    /// <summary>Photographed or scanned signatures: near-white becomes transparent.</summary>
    /// <remarks>
    /// Luminance = 0.114·B + 0.587·G + 0.299·R. Note the weights are applied to the
    /// channels in that order, so pure green (149.7) is kept as ink while a 240 grey
    /// is removed — the case Android's test pins, and the easiest one to get backwards
    /// by assuming RGB order.
    /// </remarks>
    public static void RemoveWhiteBackground(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var luminance = (0.114 * bgra[i]) + (0.587 * bgra[i + 1]) + (0.299 * bgra[i + 2]);
            if (luminance > WhiteLuminanceCutoff)
                bgra[i + 3] = 0;
        }
    }

    /// <summary>
    /// Crops to the bounding box of visible ink plus <see cref="TrimMargin"/>, clamped
    /// to the image. Returns the input unchanged when nothing is visible, so an empty
    /// capture does not collapse to a zero-sized bitmap.
    /// </summary>
    public static SignatureBitmap TrimToInk(byte[] bgra, int width, int height)
    {
        int minX = width, minY = height, maxX = -1, maxY = -1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (bgra[((y * width) + x) * 4 + 3] <= InkAlphaCutoff)
                    continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0)
            return new SignatureBitmap(bgra, width, height);

        minX = Math.Max(0, minX - TrimMargin);
        minY = Math.Max(0, minY - TrimMargin);
        maxX = Math.Min(width - 1, maxX + TrimMargin);
        maxY = Math.Min(height - 1, maxY + TrimMargin);

        int w = maxX - minX + 1, h = maxY - minY + 1;
        var cropped = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            Buffer.BlockCopy(bgra, (((minY + y) * width) + minX) * 4, cropped, y * w * 4, w * 4);

        return new SignatureBitmap(cropped, w, h);
    }

    /// <summary>White background to transparent, then trim — the whole contract in order.</summary>
    public static SignatureBitmap Clean(SignatureBitmap image)
    {
        RemoveWhiteBackground(image.Bgra);
        return TrimToInk(image.Bgra, image.Width, image.Height);
    }
}
