using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using MegaPDF.Core.Imaging;

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
    /// <remarks>
    /// The arithmetic lives in <see cref="MegaPDF.Core.Imaging.SignatureCleanup"/>,
    /// not here. It is SDD §6.2 contract 3, so a second copy of it in a WinUI project
    /// that no test can reach is exactly the drift the contract exists to prevent.
    /// Decoding stays here — that is the genuinely platform-specific half.
    /// </remarks>
    public static SignatureImage Clean(SignatureImage image)
    {
        var cleaned = SignatureCleanup.Clean(new SignatureBitmap(image.Bgra, image.Width, image.Height));
        return new SignatureImage(cleaned.Bgra, cleaned.Width, cleaned.Height);
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

    /// <summary>JPEG (no alpha) at the given quality — used by shrink-for-email.</summary>
    public static async Task<byte[]> EncodeJpegAsync(SignatureImage image, double quality)
    {
        using var stream = new InMemoryRandomAccessStream();
        var properties = new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) },
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, properties);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)image.Width, (uint)image.Height, 96, 96, image.Bgra);
        await encoder.FlushAsync();

        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        return bytes;
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
}
