using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MegaPDF.Core.Imaging;

namespace MegaPDF.Avalonia.Rendering;

/// <summary>
/// PNG ↔ BGRA, the platform half of the signature pipeline.
///
/// The cleanup arithmetic deliberately is NOT here — that is SDD §6.2 contract 3
/// and lives in <see cref="SignatureCleanup"/> so every platform shares one copy.
/// Decoding and encoding is the part that genuinely differs, and this is Avalonia's.
/// </summary>
internal static class SignatureImages
{
    /// <summary>Decodes a PNG to straight BGRA bytes the engine can stamp.</summary>
    internal static SignatureBitmap LoadBgra(string pngPath)
    {
        using var stream = File.OpenRead(pngPath);
        return LoadBgra(stream);
    }

    internal static SignatureBitmap LoadBgra(Stream stream)
    {
        using var decoded = WriteableBitmap.Decode(stream);
        var size = decoded.PixelSize;
        var bytes = new byte[size.Width * size.Height * 4];

        using (var locked = decoded.Lock())
        {
            var rowBytes = size.Width * 4;
            for (var y = 0; y < size.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    locked.Address + (y * locked.RowBytes), bytes, y * rowBytes, rowBytes);
            }
        }

        return new SignatureBitmap(bytes, size.Width, size.Height);
    }

    /// <summary>Encodes straight BGRA as PNG — what the signature library stores.</summary>
    internal static byte[] EncodePng(SignatureBitmap image)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using (var locked = bitmap.Lock())
        {
            var rowBytes = image.Width * 4;
            for (var y = 0; y < image.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    image.Bgra, y * rowBytes, locked.Address + (y * locked.RowBytes), rowBytes);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A thumbnail for the signature list, straight from the stored PNG.</summary>
    internal static Bitmap? LoadThumbnail(string pngPath)
    {
        try
        {
            return new Bitmap(pngPath);
        }
        catch (Exception)
        {
            // A signature whose PNG has been corrupted should cost a missing thumbnail,
            // not a crash on opening the library.
            return null;
        }
    }
}
