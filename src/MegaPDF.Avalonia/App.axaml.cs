using Avalonia;
using Avalonia.Controls;   // ResourceNodeExtensions.TryFindResource
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using MegaPDF.Avalonia.ViewModels;
using MegaPDF.Core.Engine;
using MegaPDF.Avalonia.Views;

namespace MegaPDF.Avalonia;

public partial class App : Application
{
    private static string? ArgumentAfter(IReadOnlyList<string>? args, string flag)
    {
        if (args is null)
            return null;
        var index = args.ToList().IndexOf(flag);
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    /// <summary>
    /// Drives the app into a state worth photographing, for --screenshot.
    ///
    /// The three states captured before this — empty, document open, form — have
    /// nothing selected and no search running, so not one pixel of the brand
    /// accent appeared in any of them. Every colour token except the page card
    /// shadow was invisible to review (#80). iOS and Android have had states like
    /// these since their store screenshots were first captured; macOS is catching
    /// up with them.
    /// </summary>
    private static bool ApplyScreenshotState(MainViewModel viewModel, string state)
    {
        switch (state)
        {
            // Search hits: cyan for every match, brand blue for the one you are on.
            case "find":
                viewModel.IsFindOpen = true;
                viewModel.Search("equipment");
                if (viewModel.MatchCount == 0)
                {
                    Console.Error.WriteLine(
                        "::error::--screenshot-state find matched nothing. The fixture no "
                        + "longer contains \"equipment\", so the capture would be an ordinary "
                        + "document view under a name that claims otherwise.");
                    return false;
                }
                return true;

            // The keyboard focus ring (#2): brand accent-pressed stroke over an
            // accent-subtle fill.
            case "focus":
                viewModel.MoveFocus(forward: true);
                if (viewModel.PageFocus is null)
                {
                    Console.Error.WriteLine(
                        "::error::--screenshot-state focus reached no region. The fixture has "
                        + "nothing focusable, so no ring would be drawn.");
                    return false;
                }
                return true;

            // The mode banner — the largest area of brand accent in the app, and
            // the only place BrandAccentOn is used.
            case "mode":
                viewModel.ToggleAddTextCommand.Execute(null);
                if (!viewModel.IsModeActive)
                {
                    Console.Error.WriteLine(
                        "::error::--screenshot-state mode left no mode active, so there is no "
                        + "banner to photograph.");
                    return false;
                }
                return true;

            default:
                Console.Error.WriteLine($"::error::unknown --screenshot-state '{state}'");
                return false;
        }
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
            if (desktop.MainWindow is { } window)
                RenderWindow(window, outPath);
            desktop.Shutdown();
        }, TimeSpan.FromSeconds(4));
    }

    /// <summary>Renders the window as it stands to a PNG; never throws.</summary>
    private static void RenderWindow(Window window, string outPath)
    {
        try
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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::screenshot failed: {ex.Message}");
        }
    }

    /// <summary>
    /// --story &lt;dir&gt; --signature &lt;png&gt;: the frames of the Mac preview
    /// video. The app opens the unfilled demo agreement (a .pdf argument) and
    /// then does, on a timer, what the iOS choreography does with taps: ticks
    /// two boxes, places the signature, prints the name under the line, finds
    /// every "rental" and steps through the matches, rendering the window after
    /// each step as NN-name.png. tools/macos-demo-video.sh turns the frames into
    /// a clip.
    ///
    /// Frames rather than a screen recording because screencapture and synthetic
    /// input both need privacy permissions an SSH session on the capture Mac
    /// cannot be granted; the window renders itself exactly as --screenshot does.
    /// The points are the demo agreement's layout (tools/gen_test_fixtures.py,
    /// 612x792, here from the top-left as the view model counts).
    /// </summary>
    private static void RunStory(IClassicDesktopStyleApplicationLifetime desktop,
                                 MainViewModel viewModel, string outDir, string? signaturePng)
    {
        var signature = signaturePng is not null && File.Exists(signaturePng)
            ? Rendering.SignatureImages.LoadBgra(signaturePng)
            : null;
        if (signature is null)
            Console.Error.WriteLine("::warning::--story without a readable --signature png; the signature step is skipped");

        var steps = new (string Name, Action Act)[]
        {
            ("opened", () => { }),
            ("box-1", () => viewModel.HandlePageClick(0, new PdfPoint(78.5, 201.5))),
            ("box-2", () => viewModel.HandlePageClick(0, new PdfPoint(78.5, 227.5))),
            ("signed", () =>
            {
                if (signature is not null)
                    viewModel.PlaceSignature(0, new PdfPoint(196, 355), signature, "Mega W.");
            }),
            ("printed-name", () => viewModel.AddTextBox(0, new PdfPoint(72, 405), "Jane Whitfield")),
            ("find", () => { viewModel.IsFindOpen = true; viewModel.Search("rental"); }),
            ("find-next", () => viewModel.FindNextCommand.Execute(null)),
            ("find-next-2", () => viewModel.FindNextCommand.Execute(null)),
            ("done", () => viewModel.CloseFind()),
        };

        var step = 0;
        void Next()
        {
            if (step >= steps.Length)
            {
                desktop.Shutdown();
                return;
            }
            var (name, act) = steps[step];
            try { act(); }
            catch (Exception ex) { Console.Error.WriteLine($"::error::story step {name}: {ex.Message}"); }
            var index = step++;
            // Page raster and layout are asynchronous; give them a moment, then
            // render and move on.
            DispatcherTimer.RunOnce(() =>
            {
                if (desktop.MainWindow is { } window)
                    RenderWindow(window, Path.Combine(outDir, $"{index:00}-{name}.png"));
                DispatcherTimer.RunOnce(Next, TimeSpan.FromMilliseconds(300));
            }, TimeSpan.FromSeconds(1.5));
        }

        DispatcherTimer.RunOnce(Next, TimeSpan.FromSeconds(4));
    }

    /// <summary>
    /// Every token <c>Brand.axaml</c> is expected to define, and the type each
    /// one must come back as. Ordered as docs/design-tokens.md lists them.
    /// </summary>
    private static readonly (string Key, Type Type)[] BrandTokens =
    [
        ("BrandAccent", typeof(IBrush)),
        ("BrandAccentPressed", typeof(IBrush)),
        ("BrandAccentSubtle", typeof(IBrush)),
        ("BrandAccentOn", typeof(IBrush)),
        ("BrandFindMatch", typeof(IBrush)),
        ("BrandFindMatchCurrent", typeof(IBrush)),
        ("BrandDanger", typeof(IBrush)),
        ("BrandInk", typeof(IBrush)),
        ("BrandRule", typeof(IBrush)),
        ("BrandCardShadow", typeof(BoxShadows)),
        // Both ends of SyncFluentAccent. The Brand* keys are what Brand.axaml
        // declares; the plain ones are what Fluent actually reads, and they
        // exist only because the sync ran. Checking the source alone would let a
        // silently no-op sync pass here and fail only in a pixel comparison.
        ("SystemAccentColor", typeof(Color)),
        ("SystemAccentColorLight1", typeof(Color)),
        ("SystemAccentColorLight2", typeof(Color)),
        ("SystemAccentColorLight3", typeof(Color)),
        ("SystemAccentColorDark1", typeof(Color)),
        ("SystemAccentColorDark2", typeof(Color)),
        ("SystemAccentColorDark3", typeof(Color)),
        ("BrandSystemAccentColor", typeof(Color)),
        ("BrandSystemAccentColorLight1", typeof(Color)),
        ("BrandSystemAccentColorLight2", typeof(Color)),
        ("BrandSystemAccentColorLight3", typeof(Color)),
        ("BrandSystemAccentColorDark1", typeof(Color)),
        ("BrandSystemAccentColorDark2", typeof(Color)),
        ("BrandSystemAccentColorDark3", typeof(Color)),
        ("TypeCaption", typeof(double)),
        ("TypeBody", typeof(double)),
        ("TypeSubtitle", typeof(double)),
        ("TypeTitle", typeof(double)),
        ("SpaceXs", typeof(double)),
        ("SpaceS", typeof(double)),
        ("SpaceM", typeof(double)),
        ("SpaceL", typeof(double)),
        ("SpaceXl", typeof(double)),
        ("SpaceXxl", typeof(double)),
    ];

    /// <summary>
    /// --brand-check: resolve every design token in both theme variants and
    /// report.
    ///
    /// This exists because the failure it catches is silent. Brand.Brush returns
    /// a transparent brush for a key it cannot find, so a renamed or mistyped
    /// token does not throw — the selection border, the resize handles and the
    /// focus ring simply stop being drawn, and every other check still passes. A
    /// build proves Brand.axaml parses; only a lookup proves the keys are
    /// reachable, and nothing else in CI performs one.
    ///
    /// Both variants, because the theme dictionaries are separate: a token added
    /// to Light and forgotten in Dark is invisible until someone switches
    /// appearance.
    /// </summary>
    private static int BrandCheck()
    {
        var failures = 0;
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            Console.WriteLine($"{variant} theme:");
            foreach (var (key, type) in BrandTokens)
            {
                var found = Current is { } app
                    && app.TryFindResource(key, variant, out var value)
                    && value is not null
                    && type.IsInstanceOfType(value);
                Console.WriteLine($"  [{(found ? "PASS" : "FAIL")}] {key} resolves as {type.Name}");
                if (!found) failures++;
            }
        }

        Console.WriteLine(failures == 0
            ? "brand-check: PASS"
            : $"::error::brand-check: {failures} token(s) unresolved");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The seven colours FluentTheme builds its control accents from.</summary>
    private static readonly string[] FluentAccentKeys =
    [
        "SystemAccentColor",
        "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
    ];

    /// <summary>
    /// Copies the brand accent ramp for the current theme into the resources
    /// Fluent reads, so its controls match the chrome the markup paints.
    ///
    /// It has to be done in code. Fluent defines SystemAccentColor in its own
    /// theme dictionaries; for a key it owns, its theme dictionary beats ours,
    /// while a direct entry in Application.Resources beats everything. So a
    /// theme-scoped override never wins and a shared one cannot vary by theme —
    /// which is why the dark screenshot showed a #4F9BEA banner beside a
    /// #0E6FD8 button. Brand.axaml holds the values under Brand-prefixed keys,
    /// where nothing competes, and this writes the right ones across.
    /// </summary>
    private void SyncFluentAccent()
    {
        foreach (var key in FluentAccentKeys)
        {
            if (this.TryFindResource("Brand" + key, ActualThemeVariant, out var value)
                && value is Color colour)
            {
                Resources[key] = colour;
            }
        }
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        SyncFluentAccent();
        // macOS switches appearance under a running app, so this is not one-shot.
        ActualThemeVariantChanged += (_, _) => SyncFluentAccent();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // No window for this one: the resources are loaded by Initialize, so
            // the check has everything it needs before anything is shown.
            if (desktop.Args?.Contains("--brand-check") == true)
            {
                DispatcherTimer.RunOnce(() => desktop.Shutdown(BrandCheck()), TimeSpan.Zero);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // --window 1440x900: the size the window opens at, for captures. The
            // default 1000x800 is right for a design-review shot and wrong for a
            // Mac App Store one, where the listing sizes start at 1280x800 and
            // the toolbar's last button is clipped at anything under ~1100.
            if (ArgumentAfter(desktop.Args, "--window") is { } windowSize
                && windowSize.Split('x') is [var w, var h]
                && double.TryParse(w, out var width) && double.TryParse(h, out var height))
            {
                desktop.MainWindow.Width = width;
                desktop.MainWindow.Height = height;
            }

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
            if (ArgumentAfter(desktop.Args, "--story") is { } storyDir)
            {
                if (ArgumentAfter(desktop.Args, "--theme") is "dark")
                    RequestedThemeVariant = ThemeVariant.Dark;
                RunStory(desktop, viewModel, storyDir, ArgumentAfter(desktop.Args, "--signature"));
            }

            var shot = ArgumentAfter(desktop.Args, "--screenshot");
            if (shot is not null)
            {
                // After layout, not here. The focus ring is built by the view
                // against a realised page container, and at this point the window
                // has not been shown — OnPageFocusChanged finds no container and
                // draws nothing, silently. The find and mode states happen to
                // survive being set early because they are bound view-model data,
                // which is exactly why the difference is easy to miss.
                // --theme dark forces the variant rather than asking the runner to
                // switch appearance. A CI machine's Appearance setting does not
                // reliably reach an already-launched process, and the dark half of
                // the token file is exactly where a value can be wrong without
                // anyone noticing — the key-set parity test proves both themes
                // define a token, not that the dark one is right.
                if (ArgumentAfter(desktop.Args, "--theme") is "dark")
                    RequestedThemeVariant = ThemeVariant.Dark;

                // A state that silently does not fire is worse than no state at
                // all: the workflow still writes 06-mode-banner.png, and the next
                // person compares three innocuous screenshots and concludes the
                // colours are fine. Each case now asserts it actually reached the
                // state, and a miss exits non-zero.
                if (ArgumentAfter(desktop.Args, "--screenshot-state") is { } state)
                {
                    DispatcherTimer.RunOnce(() =>
                    {
                        if (!ApplyScreenshotState(viewModel, state))
                            desktop.Shutdown(1);
                    }, TimeSpan.FromSeconds(2));
                }
                CaptureAndExit(desktop, shot);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
