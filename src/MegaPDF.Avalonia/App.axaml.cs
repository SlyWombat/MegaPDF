using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using MegaPDF.Avalonia.ViewModels;
using MegaPDF.Avalonia.Views;

namespace MegaPDF.Avalonia;

public partial class App : Application
{
    private static string? ScreenshotArgument(IReadOnlyList<string>? args)
    {
        if (args is null)
            return null;
        var index = args.ToList().IndexOf("--screenshot");
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    /// <summary>
    /// Waits for the window to lay out and its first page to raster, renders it to
    /// a PNG, and exits. The delay is a wait for real work — page rasterisation is
    /// asynchronous with respect to layout — not a guess at a frame rate.
    /// </summary>
    private static void CaptureAndExit(IClassicDesktopStyleApplicationLifetime desktop, string outPath)
    {
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                if (desktop.MainWindow is { } window)
                {
                    var size = new PixelSize(
                        Math.Max(1, (int)window.Bounds.Width),
                        Math.Max(1, (int)window.Bounds.Height));
                    using var target = new RenderTargetBitmap(size, new Vector(96, 96));
                    target.Render(window);
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                    target.Save(outPath);
                    Console.WriteLine($"screenshot: {outPath} ({size.Width}x{size.Height})");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"::error::screenshot failed: {ex.Message}");
            }
            desktop.Shutdown();
        }, TimeSpan.FromSeconds(4));
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // The engine holds a native document handle and a pinned byte[]; let
            // it go on the way out rather than at finalisation.
            desktop.ShutdownRequested += (_, _) => viewModel.Dispose();

            // A PDF passed on the command line (Finder "Open With", or the
            // Windows file association) opens straight away.
            var path = desktop.Args?.FirstOrDefault(a =>
                a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
            if (path is not null)
                viewModel.Open(path);

            // --screenshot <out.png>: render the window to a file and quit.
            //
            // The app renders itself rather than the OS capturing the screen. A
            // desktop capture needs Screen Recording permission, which on a CI
            // runner means a system prompt that lands ON TOP of the thing being
            // photographed — which is exactly what happened to the first set. This
            // also yields the window alone, with no desktop or dock around it,
            // which is what a design review wants.
            var shot = ScreenshotArgument(desktop.Args);
            if (shot is not null)
                CaptureAndExit(desktop, shot);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
