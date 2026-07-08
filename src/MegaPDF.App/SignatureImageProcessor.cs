using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MegaPDF.App;

/// <summary>BGRA pixels of a signature image.</summary>
public sealed record SignatureImage(byte[] Bgra, int Width, int Height);

/// <summary>
/// Signature image pipeline (SDD §3.3): decode, remove the white scan background
/// (luminance → alpha), and trim whitespace margins.
/// </summary>
public static class SignatureImageProcessor
{
    public static async Task<SignatureImage> LoadAndCleanAsync(StorageFile file)
    {
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var pixels = data.DetachPixelData();
        return Clean(new SignatureImage(pixels, (int)decoder.PixelWidth, (int)decoder.PixelHeight));
    }

    /// <summary>White background → transparent, then trim to the ink's bounding box.</summary>
    public static SignatureImage Clean(SignatureImage image)
    {
        RemoveWhiteBackground(image.Bgra);
        return Trim(image.Bgra, image.Width, image.Height);
    }

    public static async Task<SignatureImage> LoadPngAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return new SignatureImage(data.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    public static async Task<byte[]> EncodePngAsync(SignatureImage image)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            (uint)image.Width, (uint)image.Height, 96, 96, image.Bgra);
        await encoder.FlushAsync();

        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        return bytes;
    }

    /// <summary>Scanned-signature cleanup: near-white pixels become transparent (SDD §3.3).</summary>
    private static void RemoveWhiteBackground(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var luminance = 0.114 * bgra[i] + 0.587 * bgra[i + 1] + 0.299 * bgra[i + 2];
            if (luminance > 235)
                bgra[i + 3] = 0;
        }
    }

    /// <summary>Crops to the ink's bounding box plus a small margin.</summary>
    private static SignatureImage Trim(byte[] bgra, int width, int height)
    {
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (bgra[(y * width + x) * 4 + 3] <= 16)
                    continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0)
            return new SignatureImage(bgra, width, height); // nothing visible — keep as-is

        const int margin = 4;
        minX = Math.Max(0, minX - margin);
        minY = Math.Max(0, minY - margin);
        maxX = Math.Min(width - 1, maxX + margin);
        maxY = Math.Min(height - 1, maxY + margin);

        int w = maxX - minX + 1, h = maxY - minY + 1;
        var cropped = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            System.Buffer.BlockCopy(bgra, ((minY + y) * width + minX) * 4, cropped, y * w * 4, w * 4);
        return new SignatureImage(cropped, w, h);
    }
}
