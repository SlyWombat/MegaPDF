using Avalonia;
using Avalonia.Controls;   // ResourceNodeExtensions.TryFindResource
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using System.Reflection;
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
    /// The arguments that mean "a rig is driving this, not a person". Only the ones that
    /// reach a window: --brand-check shuts down before there is one, and --render-check,
    /// --print-check, --language-check and --self-test never start Avalonia at all.
    /// </summary>
    private static bool IsAutomationArgument(string argument) =>
        argument is "--screenshot" or "--screenshot-state" or "--story" or "--desktop-check";

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
    private static bool ApplyScreenshotState(MainViewModel viewModel, MainWindow? window, string state, string? signaturePng)
    {
        switch (state)
        {
            // The signature library (#100): white cards with the ink, an overflow per
            // card, and the two ways in. Needs at least one signature to show a card,
            // so an empty library is seeded from --signature (or the repo's demo
            // signature) through the same import path a person uses.
            case "sign":
                if (window is null)
                {
                    Console.Error.WriteLine("::error::--screenshot-state sign has no window to open the flyout on.");
                    return false;
                }
                if (viewModel.Signatures.Count == 0)
                {
                    var seed = signaturePng ?? FindDemoSignature();
                    if (seed is null || !File.Exists(seed))
                    {
                        Console.Error.WriteLine(
                            "::error::--screenshot-state sign: the library is empty and no signature image "
                            + "was found to seed it (pass --signature <png|jpg>, or run from the repo so "
                            + "tools/assets/megawoman-sig.jpg is reachable).");
                        return false;
                    }
                    try
                    {
                        viewModel.AddSignatureFromImage("Mega W.", Rendering.SignatureImages.LoadBgra(seed),
                                                        Rendering.SignatureImages.EncodePng);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"::error::--screenshot-state sign: seeding the library failed: {ex.Message}");
                        return false;
                    }
                }
                Console.WriteLine($"screenshot-state sign: {viewModel.Signatures.Count} signature(s) in the library");
                window.ShowSignaturesFlyout();
                return true;

            // Search hits: cyan for every match, brand blue for the one you are on.
            //
            // The term follows the capture language, like everything else the demo puts
            // into the document (#146 §3). It used to be the literal "equipment", which
            // is not on the French agreement at all — so the French store set could not
            // be taken: the capture failed and left no file behind it.
            case "find":
                viewModel.IsFindOpen = true;
                // Into the box as well as into the view model. The find field is a plain
                // TextBox that the typing path fills; searching the view model directly
                // left the capture showing a watermark — an empty-looking field beside
                // "1 of 3", which is the sort of thing a store screenshot must not say.
                if (window is not null)
                    window.FindBox.Text = DemoContent.SearchTerm;
                viewModel.Search(DemoContent.SearchTerm);
                if (viewModel.MatchCount == 0)
                {
                    Console.Error.WriteLine(
                        $"::error::--screenshot-state find matched nothing. The fixture no "
                        + $"longer contains \"{DemoContent.SearchTerm}\", so the capture would be an "
                        + "ordinary document view under a name that claims otherwise.");
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

            // The More menu held open (#144): Save As, Password…, Print, Shrink and
            // Options, under whatever the window's width has overflowed.
            case "more":
                if (window is null)
                {
                    Console.Error.WriteLine("::error::--screenshot-state more has no window to open the menu on.");
                    return false;
                }
                window.ShowMoreMenuForScreenshot();
                return true;

            // An added text box, selected (#144): the font and size pickers join the
            // toolbar row for it. The mode state shows them for Add text armed.
            // With a window the add goes through the #139 page check first, off the UI
            // thread, so the box is selected a moment later; the capture log's toolbar
            // line says pickers=shown when it worked.
            // Placed where --story prints the name: under the demo agreement's signature
            // line (tools/gen_test_fixtures.py demo.pdf). On another document it may land on text.
            // The name follows the capture language (#146 §3), so a French shot is
            // French all the way through: DemoContent.
            case "textbox":
                viewModel.AddTextBox(0, new PdfPoint(72, 405), DemoContent.PrintedName);
                DispatcherTimer.RunOnce(() =>
                {
                    if (viewModel.BoxesOn(0).LastOrDefault() is not { } box)
                    {
                        Console.Error.WriteLine("::error::--screenshot-state textbox: the text box was not added.");
                        return;
                    }
                    viewModel.HandlePageClick(0, new PdfPoint(box.Bounds.X + (box.Bounds.Width / 2),
                                                              box.Bounds.Y + (box.Bounds.Height / 2)));
                    if (!viewModel.IsTextStyleContext)
                        Console.Error.WriteLine("::error::--screenshot-state textbox: selecting the box did not bring the pickers.");
                }, TimeSpan.FromSeconds(1));
                return true;

            // The same added text, with nothing selected: the store slot (#146 §3).
            //
            // `textbox` selects the box on purpose, to bring the pickers onto the toolbar
            // for a #144 review shot. A listing image is a different job: the selection
            // border is drawn tight to the object's own bounds, so on a French name it
            // runs straight through the acute and grave marks — which in a French store
            // screenshot reads as the accent bug this project has already been bitten by
            // (#160), even though the glyphs underneath are perfect. Windows deselects
            // for the same reason. So this one places the text and lets go of it.
            case "text":
                viewModel.AddTextBox(0, new PdfPoint(72, 405), DemoContent.PrintedName);
                DispatcherTimer.RunOnce(() =>
                {
                    if (viewModel.BoxesOn(0).LastOrDefault() is null)
                    {
                        Console.Error.WriteLine("::error::--screenshot-state text: the text box was not added.");
                        return;
                    }
                    viewModel.ClearSelection();
                    if (viewModel.Selection is not null)
                        Console.Error.WriteLine(
                            "::error::--screenshot-state text: the box is still selected, so its "
                            + "border will cut across the name's accents.");
                }, TimeSpan.FromSeconds(1));
                return true;

            // Redact (#173): a line of the demo document marked and selected, which is the
            // state the feature has to be legible in — a translucent box you can still read
            // through, over text you are about to remove, with the chrome that takes it off
            // (#329). The mark is placed through the same call a drag makes, so the capture
            // is of the real thing.
            case "redact":
                if (!viewModel.IsDocumentOpen)
                {
                    Console.Error.WriteLine("::error::--screenshot-state redact needs a document.");
                    return false;
                }
                viewModel.ToggleRedactCommand.Execute(null);
                if (!viewModel.IsRedactMode)
                {
                    Console.Error.WriteLine("::error::--screenshot-state redact: the tool did not arm.");
                    return false;
                }
                // A run of the document's own text, found by what it says rather than by a
                // rectangle that has to be right: a fixed one landed on the signature line,
                // and a capture is supposed to show the feature doing its job on a sentence.
                // The word when the document has it, and otherwise the longest line on the
                // page: a capture run may be posed on the English demo with a French window,
                // and a shot with nothing marked would say nothing at all.
                var lines = viewModel.LinesOn(0);
                var markedLine = lines
                    .FirstOrDefault(l => l.Text.Contains(DemoContent.RedactedWord, StringComparison.Ordinal))
                    ?? lines.OrderByDescending(l => l.Text.Length).FirstOrDefault();
                if (markedLine is null)
                {
                    Console.Error.WriteLine(
                        "::error::--screenshot-state redact: the document has no line to mark.");
                    return false;
                }
                // AddRedactionMarkNow, not AddRedactionMark: with a window the latter starts the
                // work and returns (#145), so the check below read the marks before they were
                // placed and this state never wrote an image (#329's capture pass).
                viewModel.AddRedactionMarkNow(0, markedLine.Bounds);
                if (!viewModel.HasRedactionMarks)
                {
                    Console.Error.WriteLine(
                        "::error::--screenshot-state redact: nothing was marked for "
                        + $"({markedLine.Bounds.X:0.#},{markedLine.Bounds.Y:0.#} "
                        + $"{markedLine.Bounds.Width:0.#}x{markedLine.Bounds.Height:0.#}).");
                    return false;
                }
                // Selected, not armed (#329), which is what 2.1's copy is about: a mark you can
                // take back, so the shot has to carry the chrome that takes it off — the box
                // and its ✕. 2.0's pose armed the tool again instead, and marking already
                // leaves the tool (PlaceRedactionMarkAsync sets PageMode.Select), so there is
                // nothing to disarm. The phones' pose moved to the selected mark for 2.1, and
                // a set where only one platform shows the affordance is worse than either.
                if (!viewModel.SelectRedactionMarkAt(0, markedLine.Bounds.Center))
                {
                    Console.Error.WriteLine(
                        "::error::--screenshot-state redact: the mark was placed but did not select, "
                        + "so the shot would show a mark with no chrome.");
                    return false;
                }
                return true;

            // The busy strip under the toolbar (#145), as it shows 0.5 s into a slow save's
            // read-back. The operation is held for the life of the capture run.
            case "busy":
                if (!viewModel.IsDocumentOpen)
                {
                    Console.Error.WriteLine("::error::--screenshot-state busy needs a document to be busy with.");
                    return false;
                }
                ScreenshotBusy = viewModel.Busy.Begin(Strings.BusyCheckingSavedFile);
                return true;

            // The page-level spinner (#145): a change waiting on its page's #139 check.
            case "busy-page":
                if (!viewModel.IsDocumentOpen)
                {
                    Console.Error.WriteLine("::error::--screenshot-state busy-page needs a document.");
                    return false;
                }
                ScreenshotBusy = viewModel.Busy.Begin(Strings.BusyCheckingPage, scope: MegaPDF.Core.Services.BusyScope.Page, pageIndex: 0);
                DispatcherTimer.RunOnce(() =>
                {
                    if (window?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PageBusyBadge") is { } badge)
                        Console.WriteLine($"busy-page badge: visible={badge.IsEffectivelyVisible} bounds={badge.Bounds} "
                                          + $"in window at {badge.TranslatePoint(new Point(0, 0), window)} desired={badge.DesiredSize} "
                                          + $"parent={badge.GetVisualParent()?.GetType().Name} parentBounds={(badge.GetVisualParent() as Visual)?.Bounds}");
                    else
                        Console.Error.WriteLine("::error::--screenshot-state busy-page: no page badge in the visual tree.");
                }, TimeSpan.FromSeconds(1));
                return true;

            // The spinner under a line (#145): the text-edit check before the editor opens.
            case "busy-line":
                if (!viewModel.IsDocumentOpen || viewModel.LinesOn(0).FirstOrDefault() is not { } line)
                {
                    Console.Error.WriteLine("::error::--screenshot-state busy-line needs a document with a line of text.");
                    return false;
                }
                ScreenshotBusy = viewModel.Busy.Begin(Strings.BusyCheckingPage, scope: MegaPDF.Core.Services.BusyScope.Page,
                                                      pageIndex: 0, area: line.Bounds);
                return true;

            // The unsaved-changes question (#145, D1), after a tick on the demo agreement's
            // first box. Rendered beside the window to <out>-dialog.png.
            case "unsaved":
                if (window is null || !viewModel.IsDocumentOpen)
                {
                    Console.Error.WriteLine("::error::--screenshot-state unsaved needs a window and a document.");
                    return false;
                }
                viewModel.HandlePageClick(0, new PdfPoint(78.5, 201.5));
                DispatcherTimer.RunOnce(() =>
                {
                    if (!viewModel.IsDirty)
                        Console.Error.WriteLine("::error::--screenshot-state unsaved: the tick did not make the document dirty.");
                    ScreenshotDialog = window.ShowUnsavedChangesForScreenshot();
                }, TimeSpan.FromSeconds(1));
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

            // About MegaPDF and the licences it leads to (#176), each rendered beside
            // the window to <out>-dialog.png. The app menu that leads to About is
            // macOS's own NSMenu, which no window renderer can reach; that half is
            // checked on a real screen instead.
            case "about":
            case "notices":
                DispatcherTimer.RunOnce(() =>
                {
                    var about = ShowAbout();
                    if (state == "about")
                    {
                        ScreenshotDialog = about;
                        return;
                    }
                    var notices = ShowNotices(about);
                    // Synchronously, so the capture cannot photograph the spinner.
                    notices.LoadNow();
                    ScreenshotDialog = notices;
                }, TimeSpan.FromSeconds(1));
                return true;

            // The home screen with a recents list (#146 §3). The machine's own list is
            // whatever it last opened — on the capture Mac, a path through the sandbox
            // container — so the rows come from DemoContent, as iOS's do.
            case "home":
                if (viewModel.IsDocumentOpen)
                {
                    Console.Error.WriteLine(
                        "::error::--screenshot-state home was given a document to open. The home "
                        + "shot is the empty window; pass no .pdf.");
                    return false;
                }
                viewModel.ShowDemoRecents(DemoContent.Recents);
                if (!viewModel.HasRecents)
                {
                    Console.Error.WriteLine("::error::--screenshot-state home: the recents list is empty.");
                    return false;
                }
                Console.WriteLine($"screenshot-state home: {viewModel.Recents.Count} recent row(s)");
                return true;

            default:
                Console.Error.WriteLine($"::error::unknown --screenshot-state '{state}'");
                return false;
        }
    }

    /// <summary>The repo's demo signature (tools/assets/megawoman-sig.jpg), found by walking up from the binary; null outside a checkout.</summary>
    private static string? FindDemoSignature()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "assets", "megawoman-sig.jpg");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Waits for the window to lay out and its first page to raster, renders it to
    /// a PNG, and exits. The delay is a wait for real work — page rasterisation is
    /// asynchronous with respect to layout — not a guess at a frame rate.
    /// </summary>
    /// <summary>The busy operation a `busy` capture holds open (#145).</summary>
    private static IDisposable? ScreenshotBusy;

    /// <summary>A dialog a capture state opened beside the window, rendered to its own file.</summary>
    private static Window? ScreenshotDialog;

    private static void CaptureAndExit(IClassicDesktopStyleApplicationLifetime desktop, string outPath, double seconds)
    {
        DispatcherTimer.RunOnce(() =>
        {
            if (desktop.MainWindow is { } window)
            {
                // The toolbar's step and the menu bar audit (#144), for whoever reads the log.
                if (window is MainWindow main)
                {
                    Console.WriteLine(main.DescribeToolbar());
                    // And the find bar's, when there is one on screen: it folds on the
                    // same principle and at 480 it is the row that used to overflow (#237).
                    if (main.DataContext is MainViewModel { IsFindOpen: true })
                        Console.WriteLine(main.DescribeFindBar());
                    Console.WriteLine(main.DescribeMenuBar());
                }
                if (RenderScale != 1 && OverlaysOnThePage(desktop.MainWindow?.DataContext as MainViewModel))
                {
                    Console.Error.WriteLine(
                        "::error::--scale 2 draws page overlays in the wrong place, so this capture "
                        + "would be a wrong image rather than a sharp one. Measured on the find state "
                        + "(#146 §3): every search highlight and redaction mark lands at exactly twice "
                        + "its offset and twice its size, while the toolbar, the page and the text are "
                        + "identical — so the highlights float in the grey beside the page. Nothing in "
                        + "the window is positioned differently; only the offscreen 2x render is. "
                        + "Capture at 1x, or use a display that is actually Retina.");
                    desktop.Shutdown(1);
                    return;
                }
                RenderWindow(window, outPath);
            }
            if (ScreenshotDialog is { } dialog)
                RenderWindow(dialog, Path.Combine(Path.GetDirectoryName(outPath) ?? ".",
                                                  Path.GetFileNameWithoutExtension(outPath) + "-dialog.png"));
            // A capture run quits whatever it changed; nobody is there to answer a question.
            if (desktop.MainWindow is MainWindow closing)
                closing.SkipCloseConfirmation();
            ScreenshotBusy?.Dispose();
            desktop.Shutdown();
        }, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// --scale 2: render at twice the window's DIP size (192 DPI), the way a Retina
    /// screen draws it, for review shots (#144). One otherwise, as before.
    ///
    /// Only usable for states with nothing drawn over the page — see the refusal in
    /// <see cref="CaptureAndExit"/> and <see cref="OverlaysOnThePage"/>.
    /// </summary>
    private static double RenderScale = 1;

    /// <summary>
    /// Whether anything is drawn over the page by margin: a search highlight, a mark,
    /// or the per-line busy bar. These are what the 2x render misplaces (#146 §3).
    ///
    /// Asked of the state that is actually on screen rather than of the state name, so
    /// a capture posed some other way is covered too.
    /// </summary>
    private static bool OverlaysOnThePage(MainViewModel? viewModel) =>
        viewModel is not null
        && viewModel.Pages.Any(page => page.Highlights.Count > 0
                                       || page.RedactionMarks.Count > 0
                                       || page.ShowsBusyOnLine);

    /// <summary>Renders the window as it stands to a PNG; never throws.</summary>
    private static void RenderWindow(TopLevel window, string outPath)
    {
        try
        {
            var size = new PixelSize(
                Math.Max(1, (int)(window.Bounds.Width * RenderScale)),
                Math.Max(1, (int)(window.Bounds.Height * RenderScale)));
            using var target = new RenderTargetBitmap(size, new Vector(96 * RenderScale, 96 * RenderScale));
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
            ("printed-name", () => viewModel.AddTextBox(0, new PdfPoint(72, 405), DemoContent.PrintedName)),
            ("find", () => { viewModel.IsFindOpen = true; viewModel.Search(DemoContent.SearchTerm); }),
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

        // Which typeface the app actually ends up in (#160). Worth printing,
        // because the answer was a surprise: the comment in Program.cs says macOS
        // gets San Francisco, and it never did — Avalonia resolves something else,
        // and that something else drops the accent off every capital.
        var resolved = global::Avalonia.Media.FontManager.Current.DefaultFontFamily.Name;
        Console.WriteLine($"default font family: {resolved}");

        Console.WriteLine(failures == 0
            ? "brand-check: PASS"
            : $"::error::brand-check: {failures} token(s) unresolved");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// --desktop-check: what the running window actually got from the desktop
    /// (#158) — which windowing backend, and which file-dialog implementation.
    ///
    /// The file dialog is the reason this exists. Avalonia picks between the XDG
    /// desktop portal and its own fallback at runtime, silently, by asking D-Bus:
    /// inside a Flatpak sandbox the portal is the only route that can hand back a
    /// file the sandbox will let the app read, and outside one it is what gives
    /// GNOME and KDE their native dialog instead of a toolkit-drawn stand-in.
    /// Which it chose is invisible from the outside — both open a dialog and both
    /// return a file — so nothing short of asking the TopLevel can tell a portal
    /// run from a fallback run, and a sandboxed build that quietly fell back would
    /// look fine until a user picked a file it could not open.
    ///
    /// It reports rather than demands a particular answer: a CI runner has no
    /// portal and must legitimately fall back. What it does assert is that there
    /// is a storage provider at all, and that it can be asked for a path.
    /// </summary>
    private static async Task<int> DesktopCheckAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var failures = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
            if (!ok) failures++;
        }

        var window = desktop.MainWindow;
        // X11 or Wayland, and which desktop — both change which portal answers and
        // how scaling is reported, so a result is only readable beside them.
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "(unset)";
        var desktopName = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "(unset)";
        Console.WriteLine($"session: {session}, desktop: {desktopName}");

        // Avalonia 11 has no Wayland backend — Avalonia.X11 is the only Linux one
        // in the published output — so a Wayland session runs this app through
        // XWayland, and a fractional scale there is XWayland's upscale of an
        // integer-scaled surface rather than a crisp render (#158). Worth printing
        // beside the scaling, because the two together explain what a blurry
        // screenshot from a QA pass actually is.
        if (OperatingSystem.IsLinux() && string.Equals(session, "wayland", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("  Wayland session: this app has no Wayland backend, so it is running through XWayland");
        var font = global::Avalonia.Media.FontManager.Current.DefaultFontFamily.Name;
        Console.WriteLine($"default font family: {font}");

        Check("a window was created", window is not null);
        if (window is null)
        {
            Console.WriteLine("::error::desktop-check: no window, so nothing below could be asked");
            return 1;
        }

        var storage = window.StorageProvider;
        Check("the window has a file-dialog provider", storage is not null);

        // The type name alone is not the answer. On Linux Avalonia hands back a
        // FallbackStorageProvider — a chain, not an implementation — and the portal
        // client sits inside it ahead of the managed dialog when a portal answered
        // on D-Bus. Reading only the outer name reports "no portal" on a machine
        // that has one, which is precisely the mistake this check exists to avoid,
        // so the chain is walked.
        Console.WriteLine($"file dialogs: {storage?.GetType().FullName ?? "(none)"}");

        // Which dialog actually opens — the XDG desktop portal or Avalonia's own —
        // is NOT knowable from here, and the honest thing is to say so rather than
        // to guess from a type name. On Linux Avalonia hands back a chain of
        // factories and calls them in turn only when a dialog is opened; the portal
        // factory returns null if no portal answers on D-Bus, and the next one is
        // used instead. Nothing observable differs until a dialog is actually
        // shown, which needs either a person or a portal that answers OpenFile —
        // so confirming the portal route belongs to the QA pass on a real GNOME or
        // KDE session, and to a Flatpak build, where it is the only route that can
        // return a file the sandbox will let the app read (#158).
        Console.WriteLine("  the portal-or-fallback choice is made when a dialog opens, not now — "
                          + "confirm it on a real desktop session");
        Console.WriteLine($"  D-Bus session bus: {(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is { Length: > 0 } ? "present" : "absent")}");

        if (storage is not null)
        {
            Check("it can open files", storage.CanOpen);
            Check("it can save files", storage.CanSave);
            // A provider that cannot resolve a path it was just given is one that
            // will not be able to reopen a recent document either.
            var probe = Path.GetTempFileName();
            try
            {
                // Awaited, never .Result: the portal implementation completes on the
                // dispatcher, so blocking the UI thread for it deadlocks — which is
                // exactly what the first version of this check did.
                var file = await storage.TryGetFileFromPathAsync(probe);
                Check("and it resolves a path to a file it can hand back",
                      file is not null && file.TryGetLocalPath() == probe);
            }
            catch (Exception ex)
            {
                Check($"and it resolves a path to a file it can hand back ({ex.GetType().Name}: {ex.Message})", false);
            }
            finally
            {
                try { File.Delete(probe); } catch (IOException) { }
            }
        }

        // The face a typed signature would be drawn in. None of the four the app
        // asks for ships with a Linux distribution, and the fallback is the body
        // face in bold italic — legible, and not a signature (#158). Reported, not
        // asserted: which script faces a machine has is the machine's business.
        using (var script = Views.TypeSignatureWindow.ResolveScriptTypeface())
            Console.WriteLine($"typed signatures would be drawn in: {script.FamilyName}"
                              + $" ({(Views.TypeSignatureWindow.HasScriptFace() ? "a script face" : "NOT a script face — the body font in italic")})");

        // Where a save stages its verified copy before the destination is touched.
        // On Linux this must not be a tmpfs: staging a 2.5 GB document in RAM is the
        // cost #147 took out of opening one, and on Fedora /tmp is a tmpfs by
        // default (#193).
        var temp = Path.GetTempPath();
        Console.WriteLine($"temporary files: {temp}");
        if (OperatingSystem.IsLinux())
        {
            var backing = BackingFilesystem(temp);
            Console.WriteLine($"  backing: {backing}");
            Check("saves are not staged on a RAM-backed filesystem",
                  !backing.StartsWith("tmpfs", StringComparison.Ordinal)
                  && !backing.StartsWith("ramfs", StringComparison.Ordinal));
        }

        // Fractional scaling (#158, and the #146 150% trap). The capture path
        // renders the window itself at its DIP size, so a screenshot is the same
        // pixels whatever the desktop is scaled to — which is the point. What the
        // scale does change is what a person sees, so it is reported.
        Console.WriteLine($"display scaling: {window.RenderScaling:0.###}x "
                          + $"(the window is {window.Bounds.Width:0}x{window.Bounds.Height:0} DIP)");
        foreach (var screen in window.Screens?.All ?? [])
            Console.WriteLine($"  screen {screen.Bounds.Width}x{screen.Bounds.Height} px at {screen.Scaling:0.###}x");

        // What the desktop reads to group the window under its launcher. The
        // .desktop file states StartupWMClass=MegaPDF, and if the window ever
        // stopped calling itself that, the taskbar entry would quietly split in two.
        Console.WriteLine($"window title: {window.Title}");

        Console.WriteLine(failures == 0 ? "desktop-check: PASS" : $"::error::desktop-check: {failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The filesystem type a path sits on, from /proc/self/mounts — the longest
    /// mount point that is a prefix of it wins, which is how the kernel resolves it.
    /// "(unknown)" rather than an exception on anything that cannot be read.
    /// </summary>
    private static string BackingFilesystem(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('/');
            var best = "(unknown)";
            var bestLength = -1;
            foreach (var line in File.ReadLines("/proc/self/mounts"))
            {
                var parts = line.Split(' ');
                if (parts.Length < 3)
                    continue;
                var point = parts[1].TrimEnd('/');
                var contains = point.Length == 0
                    || full == point
                    || full.StartsWith(point + "/", StringComparison.Ordinal);
                if (contains && point.Length > bestLength)
                {
                    bestLength = point.Length;
                    best = $"{parts[2]} on {(point.Length == 0 ? "/" : point)}";
                }
            }
            return best;
        }
        catch (Exception)
        {
            return "(unknown)";
        }
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
        BuildAppMenu();
    }

    // --- The app menu, and the windows it opens (#176) ---

    private static AboutWindow? _aboutWindow;
    private static ThirdPartyNoticesWindow? _noticesWindow;

    /// <summary>The About window while it is up, for the self-test to look at after choosing the menu item.</summary>
    internal static AboutWindow? CurrentAbout => _aboutWindow;

    /// <summary>The notices window while it is up, likewise.</summary>
    internal static ThirdPartyNoticesWindow? CurrentNotices => _noticesWindow;

    /// <summary>
    /// The whole macOS application menu: About MegaPDF, Services, Hide, Hide
    /// Others, Show All and Quit (#176, #191).
    ///
    /// Avalonia's macOS exporter builds its own app menu — one item, opening a panel
    /// about the framework — only when the Application carries none, and it appends
    /// Services, Hide, Show All and Quit to whichever menu it finds. The exporter runs
    /// from AppBuilder's AfterSetup, which is after Initialize, so setting the menu
    /// here replaces About Avalonia and keeps everything the system puts below it.
    /// Setting it any later would find the exporter already built, and the standard
    /// items would not be added a second time.
    ///
    /// Since #191 the six below are all ours. Avalonia adds its own Services, Hide,
    /// Hide Others, Show All and Quit to whatever menu it finds, with English titles
    /// compiled into the framework — so a French run read "À propos de MegaPDF"
    /// above "Hide MegaPDF", and nothing could be done about the four English words
    /// from outside. `MacOSPlatformOptions.DisableDefaultApplicationMenuItems` in
    /// Program.BuildAvaloniaApp stops it adding them; these replace them, with the
    /// wording macOS itself uses (Strings.MenuHideApp and friends) and Apple's
    /// shortcuts.
    ///
    /// Harmless off macOS: no platform but this one exports an application menu.
    /// </summary>
    internal NativeMenu BuildAppMenu()
    {
        if (NativeMenu.GetMenu(this) is { } existing)
            return existing;

        var appName = Name ?? "MegaPDF";
        var menu = new NativeMenu();

        var about = new NativeMenuItem(Strings.AboutMegaPDF);
        about.Click += (_, _) => ShowAbout();
        menu.Add(about);

        if (ServicesItem() is { } services)
        {
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(services);
        }

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(AppCommand(Strings.MenuHideApp(appName), new KeyGesture(Key.H, KeyModifiers.Meta),
                            Platform.MacApplication.Hide));
        // ⌥⌘H, which is what macOS binds Hide Others to. Avalonia's own item had
        // ⌥⌘Q — the shortcut for Quit and Keep Windows — which is one more reason
        // these four are ours now (#191).
        menu.Add(AppCommand(Strings.MenuHideOthers, new KeyGesture(Key.H, KeyModifiers.Meta | KeyModifiers.Alt),
                            Platform.MacApplication.HideOthers));
        menu.Add(AppCommand(Strings.MenuShowAll, null, Platform.MacApplication.ShowAll));

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(AppCommand(Strings.MenuQuitApp(appName), new KeyGesture(Key.Q, KeyModifiers.Meta), () =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.TryShutdown();
        }));

        NativeMenu.SetMenu(this, menu);
        return menu;
    }

    /// <summary>
    /// The Services submenu, which the system fills in and we only have to mark.
    ///
    /// This one thing is reached by reflection, and it is worth saying why. Avalonia
    /// marks the Services submenu with an attached property declared on
    /// MacOSNativeMenuCommands, and that class — along with the
    /// INativeApplicationCommands it implements, which would otherwise give a handle
    /// on it through the locator — is internal to the framework. There is no public
    /// route to either. Asking the property registry for it by name finds nothing at
    /// Application.Initialize, because nothing has touched the declaring type yet;
    /// reading the field is also what runs its static constructor and registers it.
    ///
    /// Guarded, and worth the guard: if a later Avalonia renames or moves it, the item
    /// is left out rather than added, because a Services menu that opens on nothing is
    /// worse than no Services menu at all. The alternative was to drop Services from a
    /// Mac app's first menu, which is not a trade this should make quietly.
    /// </summary>
    private static NativeMenuItem? ServicesItem()
    {
        var marker = Type.GetType("Avalonia.Native.MacOSNativeMenuCommands, Avalonia.Native")?
            .GetField("IsServicesSubmenuProperty", BindingFlags.Public | BindingFlags.Static)?
            .GetValue(null) as AvaloniaProperty;
        if (marker is null)
        {
            Console.Error.WriteLine(
                "::warning::Avalonia no longer declares MacOSNativeMenuCommands.IsServicesSubmenuProperty, "
                + "so the app menu has no Services item (#191).");
            return null;
        }
        var submenu = new NativeMenu();
        submenu.SetValue(marker, true);
        return new NativeMenuItem(Strings.MenuServices) { Menu = submenu };
    }

    /// <summary>
    /// An app-menu item that reaches AppKit. Nothing here runs off macOS: the menu
    /// is only ever exported there, and the interop would have nothing to talk to.
    /// </summary>
    private static NativeMenuItem AppCommand(string header, KeyGesture? gesture, Action invoke)
    {
        var item = new NativeMenuItem(header) { Gesture = gesture };
        item.Click += (_, _) =>
        {
            if (OperatingSystem.IsMacOS())
                invoke();
        };
        return item;
    }

    /// <summary>About MegaPDF, raised rather than opened twice.</summary>
    internal static AboutWindow ShowAbout()
    {
        if (_aboutWindow is { } already)
        {
            already.Activate();
            return already;
        }

        var window = new AboutWindow();
        _aboutWindow = window;
        window.Closed += (_, _) => _aboutWindow = null;
        Present(window);
        return window;
    }

    /// <summary>
    /// The third-party notices. Tracked here rather than on the About window because
    /// the Help menu reaches them without one, and two routes to the same licences
    /// should not put two windows of them on the screen.
    /// </summary>
    internal static ThirdPartyNoticesWindow ShowNotices(Window? owner = null)
    {
        if (_noticesWindow is { } already)
        {
            already.Activate();
            return already;
        }

        var window = new ThirdPartyNoticesWindow();
        _noticesWindow = window;
        window.Closed += (_, _) => _noticesWindow = null;
        Present(window, owner);
        return window;
    }

    /// <summary>Shows a window over the main one when there is one up, else on its own.</summary>
    private static void Present(Window window, Window? owner = null)
    {
        owner ??= (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is { IsVisible: true })
            window.Show(owner);
        else
            window.Show();
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

            // The sign screenshot seeds a signature into the library; that must land in
            // a throwaway directory, never in the person's real one (#100).
            var viewModel = ArgumentAfter(desktop.Args, "--screenshot-state") == "sign"
                ? new MainViewModel(Directory.CreateTempSubdirectory("megapdf-shot-sign-").FullName)
                : new MainViewModel();
            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;

            // A capture or diagnostic run is nobody's session, so it is never offered
            // crash recovery (#145): there is nobody to answer, and the journal the offer
            // would name was left by an earlier run of the rig. #153 watched that happen —
            // a `--story` run's journal prompting on every later launch. Said out loud
            // now, because the offer comes before the launched document rather than after
            // it, so it is no longer stood down by a document already being open.
            window.SkipRecoveryOffer = desktop.Args?.Any(IsAutomationArgument) == true;

            // Opening a PDF from Finder (#143): a double-click, a drop on the Dock
            // icon, Open With. macOS sends these as an Apple Event, not as arguments,
            // to a running app and to one it is launching alike, and Avalonia raises
            // them here. Subscribed before the run loop starts, because Avalonia does
            // not hold on to an event nobody was listening for — and a cold launch's
            // file arrives as the run loop starts. The window queues it until it is open.
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e is FileActivatedEventArgs { Files: var items }
                        && items.OfType<IStorageFile>().FirstOrDefault() is { } file)
                        window.OpenFromSystem(file);
                };
            }

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

            // Cmd+Q with unsaved changes asks Save, Don't Save or Cancel first (D1, #145): it used
            // to quit at once and delete the recovery journal with the edits. Otherwise the
            // engine's native handles go on the way out rather than at finalisation.
            desktop.ShutdownRequested += (_, e) =>
            {
                if (window.NeedsConfirmationBeforeClose)
                {
                    e.Cancel = true;
                    _ = window.ConfirmThenQuitAsync(desktop);
                    return;
                }
                viewModel.Dispose();
            };

            // A PDF passed on the command line (the Windows file association, the
            // capture scripts, `open --args`) opens as soon as the window does. Finder
            // does not pass files this way; see the activation handler above.
            var path = desktop.Args?.FirstOrDefault(a =>
                a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
            if (path is not null)
                window.OpenFromSystem(path);

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

            // --desktop-check needs a real window — the file-dialog provider is a
            // feature of the TopLevel, not of the application — so unlike
            // --brand-check it runs after the window has been shown.
            if (desktop.Args?.Contains("--desktop-check") == true)
            {
                window.Opened += (_, _) => DispatcherTimer.RunOnce(async () =>
                {
                    int code;
                    try
                    {
                        code = await DesktopCheckAsync(desktop);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"::error::desktop-check: {ex.GetType().Name}: {ex.Message}");
                        code = 1;
                    }
                    desktop.Shutdown(code);
                }, TimeSpan.FromSeconds(1));
                base.OnFrameworkInitializationCompleted();
                return;
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

                if (ArgumentAfter(desktop.Args, "--scale") is "2")
                    RenderScale = 2;

                // A state that silently does not fire is worse than no state at
                // all: the workflow still writes 06-mode-banner.png, and the next
                // person compares three innocuous screenshots and concludes the
                // colours are fine. Each case now asserts it actually reached the
                // state, and a miss exits non-zero.
                if (ArgumentAfter(desktop.Args, "--screenshot-state") is { } state)
                {
                    DispatcherTimer.RunOnce(() =>
                    {
                        if (!ApplyScreenshotState(viewModel, desktop.MainWindow as MainWindow, state,
                                                  ArgumentAfter(desktop.Args, "--signature")))
                            desktop.Shutdown(1);
                    }, TimeSpan.FromSeconds(2));
                }
                // --hold <seconds>: stay up longer before rendering and exiting, so a
                // screen capture of a popup the window cannot render (the `more` state's
                // menu) can be taken from outside. Four seconds otherwise.
                var hold = double.TryParse(ArgumentAfter(desktop.Args, "--hold"),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                    out var holdSeconds) && holdSeconds > 4 ? holdSeconds : 4;
                CaptureAndExit(desktop, shot, hold);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
