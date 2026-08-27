using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;

namespace MegaPDF.Spike.MacShell;

/// <summary>
/// ADR-002 spike job 2: does MegaPDF.Core's renderer feed an Avalonia surface,
/// and does the resulting app bundle run on macOS?
///
/// Two independent assertions, deliberately ordered cheapest-and-most-important
/// first so a wobble in Avalonia's headless API can't cost us the engine answer:
///
///   1. ENGINE — PdfiumEngine opens the PDF and renders page 1 to BGRA. No UI
///      framework involved. On macOS this is the whole ADR-002 Option B thesis
///      in one call: Core + libpdfium.dylib, unmodified.
///   2. SURFACE — that BGRA becomes an Avalonia WriteableBitmap, gets composited
///      in a real window, and the frame is captured. Wrapped in try/catch: a
///      failure here is a finding to iterate on, not a reason to lose (1).
/// </summary>
internal static class Program
{
    internal static string PdfPath = "";

    [STAThread]
    public static int Main(string[] args)
    {
        var pdf = args.FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf is null || !File.Exists(pdf))
        {
            Console.Error.WriteLine("usage: MegaPDF.MacShellSpike <file.pdf> [--verify [out.png]]");
            return 2;
        }

        PdfPath = pdf;
        Console.WriteLine($"runtime: {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}");

        if (!args.Contains("--verify"))
            return BuildDesktopApp().StartWithClassicDesktopLifetime(args);

        var outPng = args.FirstOrDefault(a => a.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                     ?? "spike-frame.png";
        return Verify(pdf, outPng);
    }

    private static int Verify(string pdf, string outPng)
    {
        // ---- Assertion 1: the engine, with no UI framework in the picture ----
        RenderedPage rendered;
        double ptWidth, ptHeight;
        try
        {
            using var engine = new PdfiumEngine();
            using var doc = engine.Open(pdf);
            Console.WriteLine($"[engine] opened {Path.GetFileName(pdf)}, {doc.PageCount} page(s)");
            using var page = doc.GetPage(0);
            ptWidth = page.Width;
            ptHeight = page.Height;
            // 96 DPI against PDF's 72 pt/inch — the same 1.333 scale the desktop
            // app uses for its default zoom.
            var px = (int)Math.Round(ptWidth * 96.0 / 72.0);
            var py = (int)Math.Round(ptHeight * 96.0 / 72.0);
            rendered = page.Render(px, py);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::[engine] FAILED — {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine("This is the load-bearing failure: Core could not render through");
            Console.Error.WriteLine("libpdfium.dylib. ADR-002 Option B's premise does not hold as written.");
            return 1;
        }

        var distinct = CountDistinctPixels(rendered.Bgra);
        Console.WriteLine($"[engine] page 1: {ptWidth:F1}x{ptHeight:F1} pt -> "
                          + $"{rendered.PixelWidth}x{rendered.PixelHeight} px, "
                          + $"{rendered.Bgra.Length} BGRA bytes, {distinct} distinct pixel values");
        if (distinct < 2)
        {
            Console.Error.WriteLine("::error::[engine] the render is blank — one uniform colour. "
                                    + "pdfium loaded but drew nothing.");
            return 1;
        }
        Console.WriteLine("[engine] PASS — Core rendered a non-blank page through the platform's pdfium.");

        // ---- Assertion 2: does that reach an Avalonia surface? ----
        try
        {
            AppBuilder.Configure<SpikeApp>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .SetupWithoutStarting();

            var bitmap = ToAvaloniaBitmap(rendered);
            Console.WriteLine($"[surface] WriteableBitmap {bitmap.PixelSize.Width}x{bitmap.PixelSize.Height} built from engine BGRA");

            var window = new Window
            {
                Width = 800,
                Height = 1000,
                Content = new Image { Source = bitmap, Stretch = Stretch.Uniform },
            };
            window.Show();

            var frame = window.CaptureRenderedFrame();
            if (frame is null)
            {
                Console.Error.WriteLine("::warning::[surface] CaptureRenderedFrame returned null — "
                                        + "composited but not capturable. Engine answer stands.");
                return 0;
            }

            frame.Save(outPng);
            Console.WriteLine($"[surface] PASS — composited frame captured, wrote {outPng}");
            return 0;
        }
        catch (Exception ex)
        {
            // Deliberately non-fatal. Avalonia's headless capture API is the part
            // of this spike most likely to need a version tweak, and that must not
            // erase assertion 1's result.
            Console.Error.WriteLine($"::warning::[surface] FAILED — {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine("::warning::Iterate on this half; the engine half above is the ADR-002 answer.");
            return 0;
        }
    }

    /// <summary>
    /// Core hands back plain BGRA bytes (RenderedPage), which is exactly
    /// Avalonia's Bgra8888 — so this is a straight copy, no per-pixel conversion.
    /// That is itself a finding: the engine's output format needs no adaptation.
    /// Unpremul, not Premul: pdfium's FPDFBitmap_BGRA is straight (non-premultiplied)
    /// alpha. A rendered page is opaque so it cannot change pass/fail, but it would
    /// mislead anyone eyeballing the artifact PNG to judge whether the render looks right.
    /// </summary>
    internal static WriteableBitmap ToAvaloniaBitmap(RenderedPage page)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(page.PixelWidth, page.PixelHeight),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using var buffer = bitmap.Lock();
        for (var y = 0; y < page.PixelHeight; y++)
        {
            Marshal.Copy(
                page.Bgra,
                y * page.PixelWidth * 4,
                buffer.Address + (y * buffer.RowBytes),
                page.PixelWidth * 4);
        }
        return bitmap;
    }

    private static int CountDistinctPixels(byte[] bgra)
    {
        var seen = new HashSet<uint>();
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            seen.Add(BitConverter.ToUInt32(bgra, i));
            if (seen.Count > 8)
                break;
        }
        return seen.Count;
    }

    private static AppBuilder BuildDesktopApp() =>
        AppBuilder.Configure<SpikeApp>().UsePlatformDetect().LogToTrace();
}

/// <summary>Code-only Application — no App.axaml, see the csproj comment.</summary>
internal sealed class SpikeApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            using var engine = new PdfiumEngine();
            using var doc = engine.Open(Program.PdfPath);
            using var page = doc.GetPage(0);
            var rendered = page.Render(
                (int)Math.Round(page.Width * 96.0 / 72.0),
                (int)Math.Round(page.Height * 96.0 / 72.0));

            desktop.MainWindow = new Window
            {
                Title = "MegaPDF — ADR-002 macOS shell spike",
                Width = 800,
                Height = 1000,
                Content = new ScrollViewer
                {
                    Content = new Image
                    {
                        Source = Program.ToAvaloniaBitmap(rendered),
                        Stretch = Stretch.Uniform,
                    },
                },
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
