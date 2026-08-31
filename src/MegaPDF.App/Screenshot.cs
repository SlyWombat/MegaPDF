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

            // #32: find scroll-to-hit on a zoomed page. The fix shipped in 1.6.2
            // and was never watched working — three attempts to drive it by UI
            // automation were lost to a crash-recovery prompt, a picker timeout
            // and unrelated collateral. It needs a real window, which is what
            // this is.
            case "find-zoomed":
                return await FindOnAZoomedPageAsync(window);

            default:
                Console.Error.WriteLine($"unknown --screenshot-state '{state}'");
                return false;
        }
    }

    /// <summary>
    /// Zooms until the page overflows both axes, scrolls the hit off screen in
    /// both directions, then searches for it — and reports whether the view
    /// actually came back to it.
    ///
    /// The bug this reproduces (#28, Windows half in #32) was that
    /// ChangeView(null, offset, null) never set the horizontal axis, so a match
    /// off to the side was highlighted where nobody could see it and pressing
    /// next appeared to do nothing.
    /// </summary>
    private static async Task<bool> FindOnAZoomedPageAsync(MainWindow window)
    {
        var vm = window.ViewModel;
        var scroll = window.PageScroller;

        // Awaited, not fire-and-forget: ZoomIn is async, so Execute in a loop
        // races its own CanExecute and stops short of maximum by a step or two.
        while (vm.ZoomPercent < MainViewModel.MaxZoom)
        {
            await vm.ZoomInCommand.ExecuteAsync(null);
        }
        await Task.Delay(1200);
        Console.WriteLine($"zoom {vm.ZoomLabel}, extent {scroll.ExtentWidth:F0}x{scroll.ExtentHeight:F0}, "
                          + $"viewport {scroll.ViewportWidth:F0}x{scroll.ViewportHeight:F0}");

        if (scroll.ExtentWidth <= scroll.ViewportWidth)
        {
            Console.Error.WriteLine(
                "the page does not overflow horizontally even at maximum zoom, so this "
                + "does not exercise the axis #32 is about. A wider window or a wider fixture is needed.");
            return false;
        }

        // Away from the hit on both axes: the whole point is that both have to move.
        scroll.ChangeView(scroll.ExtentWidth, scroll.ExtentHeight, null, true);
        await Task.Delay(900);
        var (h0, v0) = (scroll.HorizontalOffset, scroll.VerticalOffset);
        Console.WriteLine($"before find: h={h0:F0} v={v0:F0}");

        await vm.SearchAsync("Equipment");
        if (vm.SearchMatchCount == 0)
        {
            Console.Error.WriteLine("no match for \"Equipment\" — the fixture changed.");
            return false;
        }
        await Task.Delay(1200);

        var (h1, v1) = (scroll.HorizontalOffset, scroll.VerticalOffset);
        Console.WriteLine($"after find:  h={h1:F0} v={v1:F0}  (moved h={h1 - h0:F0} v={v1 - v0:F0})");

        var ok = true;
        if (Math.Abs(h1 - h0) < 1)
        {
            Console.Error.WriteLine("the horizontal axis did not move — this is the #28 bug.");
            ok = false;
        }
        if (Math.Abs(v1 - v0) < 1)
        {
            Console.Error.WriteLine("the vertical axis did not move.");
            ok = false;
        }

        // Pressing next through hits already on screen must not jolt the view.
        var (h2, v2) = (scroll.HorizontalOffset, scroll.VerticalOffset);
        vm.MoveToMatch(1);
        await Task.Delay(700);
        Console.WriteLine($"after next:  h={scroll.HorizontalOffset:F0} v={scroll.VerticalOffset:F0}");

        // Back to the first hit, so the screenshot shows what the search landed on
        // rather than wherever "next" went.
        vm.MoveToMatch(-1);
        await Task.Delay(900);
        Console.WriteLine($"back to first: h={scroll.HorizontalOffset:F0} v={scroll.VerticalOffset:F0} "
                          + $"(was h={h2:F0} v={v2:F0})");
        return ok;
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
