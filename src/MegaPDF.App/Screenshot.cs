using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MegaPDF.App;

/// <summary>
/// --screenshot: render the window to a PNG and quit (#84).
///
/// The other three apps can photograph themselves; Windows could not, which is
/// why its half of the brand-token work (#76) was the only one applied without
/// anyone seeing the result. `tools/screenshots-windows/` drives the installed
/// package with UI Automation and synthetic input — the right tool for Store
/// screenshots and the wrong one for checking a colour, because it needs a real
/// desktop session and a human to watch it.
///
/// This renders the XAML tree instead, so it needs no input, no focus, and no
/// desktop to itself.
/// </summary>
internal static class Screenshot
{
    /// <summary>The value after <paramref name="flag"/> on the command line.</summary>
    public static string? ArgumentAfter(string flag)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Drives the window into a state worth photographing.
    ///
    /// A state that silently does not fire is worse than no state: the caller
    /// still gets a file named after it, and the next person compares
    /// innocuous-looking screenshots and concludes the colours are fine. Each
    /// case asserts it arrived, and the caller exits non-zero on false.
    /// </summary>
    public static async Task<bool> ApplyStateAsync(MainWindow window, string state)
    {
        var vm = window.ViewModel;
        switch (state)
        {
            case "find":
                await vm.SearchAsync("equipment");
                if (vm.SearchMatchCount == 0)
                {
                    Console.Error.WriteLine(
                        "--screenshot-state find matched nothing: the fixture no longer "
                        + "contains \"equipment\", so this would be an ordinary document view.");
                    return false;
                }
                return true;

            case "mode":
                vm.StartTextBoxMode();
                if (!vm.IsTextBoxMode)
                {
                    Console.Error.WriteLine(
                        "--screenshot-state mode left no mode active, so there is no banner.");
                    return false;
                }
                return true;

            default:
                Console.Error.WriteLine($"unknown --screenshot-state '{state}'");
                return false;
        }
    }

    /// <summary>Renders <paramref name="window"/>'s content to a PNG at <paramref name="path"/>.</summary>
    public static async Task<bool> CaptureAsync(Window window, string path)
    {
        try
        {
            if (window.Content is not UIElement content)
            {
                Console.Error.WriteLine("window has no content to render");
                return false;
            }

            var bitmap = new RenderTargetBitmap();

            // Explicit dimensions, in the element's own logical units. Left to
            // itself RenderAsync came back 1358x559 for a window whose content
            // measured 687x439.5 at scale 2 — the width right and the height cut
            // to a third. Asking for the size removes the guess.
            if (content is FrameworkElement element)
            {
                element.UpdateLayout();
                var w = (int)Math.Round(element.ActualWidth);
                var h = (int)Math.Round(element.ActualHeight);
                await bitmap.RenderAsync(content, w, h);
            }
            else
            {
                await bitmap.RenderAsync(content);
            }
            var pixels = await bitmap.GetPixelsAsync();

            // IBuffer.ToArray lived in System.Runtime.InteropServices.WindowsRuntime,
            // which .NET 5 dropped. DataReader is the supported way across.
            var bytes = new byte[pixels.Length];
            using (var reader = DataReader.FromBuffer(pixels))
                reader.ReadBytes(bytes);

            // RenderTargetBitmap renders the XAML tree and nothing else — the
            // Mica backdrop behind it is not XAML, so wherever the app is
            // transparent the bitmap is too. Encoded as-is that reads as white,
            // which looks right in light theme by accident and produces white
            // icons on white in dark. Composite over the theme's own base fill.
            var behind = (Windows.UI.Color)Application.Current.Resources["SolidBackgroundFillColorBase"];
            for (var i = 0; i + 3 < bytes.Length; i += 4)
            {
                var alpha = bytes[i + 3];
                if (alpha == 255)
                    continue;
                var gap = (255 - alpha) / 255.0;   // premultiplied: just add the ground back
                bytes[i] = (byte)Math.Min(255, bytes[i] + behind.B * gap);
                bytes[i + 1] = (byte)Math.Min(255, bytes[i + 1] + behind.G * gap);
                bytes[i + 2] = (byte)Math.Min(255, bytes[i + 2] + behind.R * gap);
                bytes[i + 3] = 255;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var stream = File.Create(path);
            var encoder = await BitmapEncoder.CreateAsync(
                BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight,
                96, 96, bytes);
            await encoder.FlushAsync();

            Console.WriteLine($"screenshot: {path} ({bitmap.PixelWidth}x{bitmap.PixelHeight})");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"screenshot failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
