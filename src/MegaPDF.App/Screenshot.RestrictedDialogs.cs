using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MegaPDF.App;

internal static partial class Screenshot
{
    /// <summary>
    /// The two restricted-document ContentDialogs, photographed (#800): the one the Unlock
    /// button raises, and the redaction refusal. RenderTargetBitmap does not draw a
    /// ContentDialog (it is a popup, #144), so this asks Windows for the window's own pixels
    /// with PrintWindow, which needs a real desktop session; it is a capture rig, not a CI
    /// check (the `restricted` state is the check). Writes &lt;png&gt;-unlock.png and
    /// &lt;png&gt;-redact.png, closes each dialog the way Cancel does, and fails only if a
    /// dialog never opened or a frame could not be cut. Needs owner-only.pdf as the document.
    /// </summary>
    private static async Task<bool> CaptureRestrictedDialogsAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } tab || !tab.Capabilities.IsRestricted
            || ArgumentAfter("--screenshot") is not { Length: > 0 } png)
        {
            Console.Error.WriteLine("--screenshot-state restricted-dialogs needs owner-only.pdf and --screenshot <png>.");
            return false;
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(png))!;
        var stem = Path.GetFileNameWithoutExtension(png);
        var ok = true;

        async Task<bool> PhotographAsync(Task showing, string suffix)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!DialogGate.IsShowing && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            if (DialogGate.Current is not { } dialog)
            {
                Console.Error.WriteLine($"FAIL: no dialog opened for {suffix}");
                return false;
            }
            await Task.Delay(700);   // let it lay out and fade in
            var cut = await PrintWindowToPngAsync(window, Path.Combine(folder, $"{stem}-{suffix}.png"));
            dialog.Hide();
            await Task.WhenAny(showing, Task.Delay(TimeSpan.FromSeconds(5)));
            await Task.Delay(300);
            return cut;
        }

        // The Unlock button's own command.
        ok &= await PhotographAsync(tab.UnlockAsync(), "unlock");
        // The redaction refusal as the app shows it: a titled message dialog.
        ok &= await PhotographAsync(tab.ShowErrorAsync(Strings.RedactRefusedTitle, Strings.RedactNeedsPermission), "redact");
        return ok;
    }

    private static async Task<bool> PrintWindowToPngAsync(MainWindow window, string path)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (!GetWindowRect(hwnd, out var r))
                return false;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            var screen = GetDC(IntPtr.Zero);
            var mem = CreateCompatibleDC(screen);
            var bmp = CreateCompatibleBitmap(screen, w, h);
            var old = SelectObject(mem, bmp);
            try
            {
                if (!PrintWindow(hwnd, mem, 2 /* PW_RENDERFULLCONTENT */))
                    return false;
                var info = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = w, Height = -h, Planes = 1, BitCount = 32,
                };
                var bytes = new byte[w * h * 4];
                if (GetDIBits(mem, bmp, 0, (uint)h, bytes, ref info, 0) == 0)
                    return false;
                for (var i = 3; i < bytes.Length; i += 4)
                    bytes[i] = 255;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = File.Create(path);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, bytes);
                await encoder.FlushAsync();
                Console.WriteLine($"screenshot: {path} ({w}x{h})");
                return true;
            }
            finally
            {
                SelectObject(mem, old);
                DeleteObject(bmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PrintWindow capture failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectI { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectI rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
}
