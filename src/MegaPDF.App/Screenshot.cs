using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    /// <summary>The busy operation a `busy` capture holds open until the process exits (#145).</summary>
    private static IDisposable? _heldBusy;

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
        // --screenshot always runs its own process, standalone, against the one tab it
        // opened (#348 phase 1, plan §6.10) — never redirected, never more than one tab.
        if (window.Shell.Active is not { } vm)
        {
            Console.Error.WriteLine($"--screenshot-state {state}: no document is open.");
            return false;
        }
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

            // #144: an added text box selected, which brings the font and size pickers
            // onto the toolbar row. The mode state shows them for Add text armed.
            case "textbox":
                if (!await window.SelectNewTextBoxForScreenshotAsync("Jane Whitfield"))
                {
                    Console.Error.WriteLine(
                        "--screenshot-state textbox: no box was selected, or the pickers did not appear.");
                    return false;
                }
                return true;

            // #144: the toolbar's "…" open. RenderTargetBitmap renders neither the popup
            // layer nor a popup's content on its own (both tried: the content throws
            // ArgumentException), so the menu itself is for a screen capture taken
            // during --hold; the rendered PNG shows the window behind it.
            case "more":
                window.OpenToolbarOverflow();
                return true;

            // The warning before Save overwrites a signed original (#476, #481). A
            // ContentDialog is a popup too, so like `more` this is for a screen capture
            // taken during --hold; the rendered PNG shows the window behind it. Needs a
            // signed document already open (signed-approval.pdf or signed-certified.pdf,
            // tools/gen_signature_fixtures.py).
            case "signed-save":
                if (!vm.IsSigned)
                {
                    Console.Error.WriteLine(
                        "--screenshot-state signed-save: the open document is not signed.");
                    return false;
                }
                vm.ShowSignedSaveWarningForScreenshot();
                return true;

            case "sign":
                return await OpenSignatureLibraryAsync(window);

            // #402: a library entry whose image has gone is listed and says so, and the
            // others still show their thumbnails. Needs a document; the exit code is the test.
            case "sign-missing":
                return await ShowMissingSignatureAsync(window);

            // #145: the busy strip under the toolbar, and the page-level spinner, each held
            // open for the capture as they show 0.5 s into slow work.
            case "busy":
            case "busy-page":
                if (!vm.IsDocumentOpen)
                {
                    Console.Error.WriteLine($"--screenshot-state {state} needs a document to be busy with.");
                    return false;
                }
                _heldBusy = state == "busy"
                    ? vm.Busy.Begin(Strings.BusyCheckingSavedFile)
                    : vm.Busy.Begin(Strings.BusyCheckingPage, scope: MegaPDF.Core.Services.BusyScope.Page, pageIndex: 0);
                await Task.Delay(900);
                return state == "busy" ? vm.Busy.ShowsStrip : vm.Busy.ShowsPageSpinner;

            // #169: keyboard focus on a toolbar button must not land on Undo when the button
            // is disabled by work on the page, and the toolbar is off while a dialog is up.
            // Needs a document; the capture is incidental, the exit code is the test.
            case "focus":
                return await CheckToolbarFocusAsync(window);

            // #401: a plain click on a text line opens the inline editor and a click on a
            // drawn box ticks it — the route a tap, Enter and Space share, which 2.1.1 lost
            // without a crash or a dialog. Needs a document; the exit code is the test.
            case "click":
                return await window.ClickFirstRegionsForTest();

            // #504/#510: reading mode, in a real window. The nearest thing this app has
            // to the Avalonia leg's --self-test block, and for the same reasons (#462
            // is open precisely because WinUI has no headless platform and a CI runner
            // has no desktop session). Needs a document; the exit code is the test.
            case "reading":
                return await CheckReadingModeAsync(window);

            default:
                Console.Error.WriteLine($"unknown --screenshot-state '{state}'");
                return false;
        }
    }

    /// <summary>
    /// Reading mode, end to end (#504 tier 1, #510 tier 2), in the real window this
    /// process already has open.
    ///
    /// Why a window and not a view model: every rule worth breaking here is the
    /// window's. Whether the toolbar is *out of the tab order* rather than merely
    /// invisible, whether Escape steps back one level and which one, whether the
    /// floating bar stops fading when a screen reader is on — a view-model check can
    /// see none of those, and each of them is how a screen-reader user would meet the
    /// feature.
    ///
    /// Two honest limits, stated here rather than buried:
    ///
    /// * A WinUI process cannot synthesise its own key presses, so this drives
    ///   <c>StepBackFromReadingMode</c> (the ladder itself) rather than the Escape key
    ///   that calls it, and calls <c>ToggleReadingMode</c> rather than pressing Ctrl+H.
    ///   That the accelerators are on <c>RootGrid</c> is checked by reading the grid's
    ///   accelerator list; that Windows delivers those keys is not checkable from here.
    /// * A screen reader cannot be turned on and off around a test, so
    ///   <see cref="ScreenReader.OverrideForTest"/> stands in for the answer while the
    ///   production guard — timer, tick and all — is what runs.
    ///
    /// The app's own settings.json is written by the page-colour checks and is put back
    /// exactly as it was found before this returns.
    /// </summary>
    private static async Task<bool> CheckReadingModeAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } vm || window.PageScroller is not { } scroll)
        {
            Console.Error.WriteLine("--screenshot-state reading needs a document.");
            return false;
        }

        var ok = true;
        void Check(string what, bool passed)
        {
            Console.Error.WriteLine($"{(passed ? "PASS" : "FAIL")}: {what}");
            ok &= passed;
        }

        var settings = window.Shell.Settings;
        var colours = settings.PageColours;
        var openInReading = settings.OpenInReadingMode;
        try
        {
            // A throw anywhere below is a FAIL with a stack, not a window left up: an
            // exception escaping here is posted to the dispatcher and the process hangs
            // with nothing said, which is exactly how a check becomes worse than none
            // (measured on the first run of this one — a Brand token misspelt by a
            // suffix left the app sitting on Dave's desktop).
            await RunReadingChecksAsync(window, vm, scroll, settings, Check);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: the reading-mode check threw: {ex}");
            ok = false;
        }
        finally
        {
            // Left as found (the app writes to the real per-user settings.json).
            settings.PageColours = colours;
            settings.OpenInReadingMode = openInReading;
            window.ApplyPageColours();
        }
        return ok;
    }

    private static async Task RunReadingChecksAsync(
        MainWindow window, DocumentViewModel vm, ScrollViewer scroll,
        MegaPDF.Core.Services.AppSettings settings, Action<string, bool> Check)
    {
        {
            settings.PageTint = MegaPDF.Core.Engine.PageTint.Normal;
            window.ApplyPageColours();
            await Task.Delay(400);

            // --- 1. The two entry points and the accelerators exist -------------
            var accelerators = window.ReadingAcceleratorsForTest();
            Check($"Ctrl+H is on the accelerator grid ({string.Join(", ", accelerators)})",
                  accelerators.Contains("Control+H"));
            Check("F11 is too", accelerators.Contains("None+F11"));
            Check("Reading mode is in the zoom flyout and in the More menu",
                  window.ReadingModeEntryPointsForTest().Count == 2);

            // --- 2. Entering and leaving ---------------------------------------
            var before = window.FocusableControlIds();
            Check($"before: the toolbar is in the tab order ({before.Count} focusable controls)",
                  before.Contains("OpenButton"));

            var zoom = vm.ZoomPercent;
            var page = vm.CurrentPage;
            var offset = scroll.VerticalOffset;

            window.ToggleReadingMode();
            await Task.Delay(500);
            Check($"Ctrl+H's command turns reading mode on ({window.DescribeReadingMode()})", window.IsReadingMode);
            Check("  the page host is untouched", scroll.Visibility == Visibility.Visible);
            Check("  the floating bar is up", window.Shell.Active?.View?.IsReadingBarShown == true);
            Check($"  and the change is announced (\"{window.Shell.Active?.View?.LastAnnouncement}\")",
                  window.Shell.Active?.View?.LastAnnouncement == Strings.ReadingModeOn);
            Check($"  same zoom ({vm.ZoomPercent}%), same page ({vm.CurrentPage}), same scroll offset",
                  vm.ZoomPercent == zoom && vm.CurrentPage == page
                  && Math.Abs(scroll.VerticalOffset - offset) < 1);

            // --- 3. The tab order ----------------------------------------------
            var inside = window.FocusableControlIds();
            var trapped = inside.Where(id => id is "OpenButton" or "SaveButton" or "SignaturesButton"
                                                or "AddTextButton" or "WhiteoutButton" or "RedactButton"
                                                or "UndoButton" or "RedoButton" or "ZoomInButton"
                                                or "ZoomOutButton" or "ZoomMenuButton").ToList();
            Check($"no hidden chrome is left in the tab order ({inside.Count} focusable controls)",
                  trapped.Count == 0);
            if (trapped.Count > 0)
                Console.Error.WriteLine($"  still focusable: {string.Join(", ", trapped)}");
            Check("  and the bar's Exit is, so the way out is reachable from the keyboard",
                  inside.Contains("ReadingExitButton"));

            // --- 4. Escape steps back exactly one level -------------------------
            window.ToggleFullScreen();
            window.Shell.Active?.View?.ShowFindBar();
            await Task.Delay(400);
            Check("set up: reading mode, full screen and the find bar, all on",
                  window.IsReadingMode && window.IsFullScreen
                  && window.Shell.Active?.View?.IsFindBarOpen == true);

            var step = window.StepBackFromReadingMode();
            Check($"Escape 1 closes the find bar and nothing else ({step})",
                  step == MegaPDF.Core.Viewing.ReadingModeStep.CloseFind
                  && window.Shell.Active?.View?.IsFindBarOpen == false
                  && window.IsFullScreen && window.IsReadingMode);

            step = window.StepBackFromReadingMode();
            await Task.Delay(300);
            Check($"Escape 2 leaves full screen and stays in reading mode ({step})",
                  step == MegaPDF.Core.Viewing.ReadingModeStep.LeaveFullScreen
                  && !window.IsFullScreen && window.IsReadingMode);

            step = window.StepBackFromReadingMode();
            await Task.Delay(300);
            Check($"Escape 3 leaves reading mode ({step})",
                  step == MegaPDF.Core.Viewing.ReadingModeStep.LeaveReadingMode && !window.IsReadingMode);
            Check("  every host is back, and the tab order with them",
                  window.FocusableControlIds().Contains("OpenButton"));
            Check($"  and that is announced too (\"{window.Shell.Active?.View?.LastAnnouncement}\")",
                  window.Shell.Active?.View?.LastAnnouncement == Strings.ReadingModeOff);

            step = window.StepBackFromReadingMode();
            Check($"Escape 4 is not ours — it still means whatever it meant before ({step})",
                  step == MegaPDF.Core.Viewing.ReadingModeStep.Nothing);

            // Full screen is offered only inside the mode (plan §2).
            window.ToggleFullScreen();
            await Task.Delay(300);
            Check("F11 outside reading mode does nothing", !window.IsFullScreen);

            // --- 5. Armed tools, and editing off --------------------------------
            vm.StartWhiteoutMode();
            Check("set up: a tool is armed", vm.IsWhiteoutMode);
            window.ToggleReadingMode();
            await Task.Delay(400);
            Check("entering reading mode disarms an armed tool", !vm.IsWhiteoutMode);
            window.ToggleReadingMode();
            await Task.Delay(400);
            Check("  and leaving does not put it back", !vm.IsWhiteoutMode);

            var view = window.Shell.Active?.View;
            var dirtyBefore = vm.HasUnsavedChanges;
            Check("the click about to be made would change the document",
                  !dirtyBefore && vm.Pages.Count > 0
                  && vm.Pages[0].Regions.Any(r => r.Kind == MegaPDF.Core.Engine.PageHitKind.DrawnCheckbox));

            window.ToggleReadingMode();
            await Task.Delay(400);
            var aimed = view is not null
                        && await view.ActivateFirstRegionForTest(MegaPDF.Core.Engine.PageHitKind.DrawnCheckbox);
            // Long enough that a change on its way would have arrived: "nothing
            // happened" has to be given the same chance to be wrong as "something
            // happened" is given below.
            await Task.Delay(900);
            Check("a click on the page in reading mode does nothing at all",
                  aimed && !vm.HasUnsavedChanges && !vm.UndoCommand.CanExecute(null));

            window.ToggleReadingMode();
            await Task.Delay(400);
            if (view is not null)
                await view.ActivateFirstRegionForTest(MegaPDF.Core.Engine.PageHitKind.DrawnCheckbox);
            await Task.Delay(1200);
            Check("  and the same click works again once the mode is off", vm.HasUnsavedChanges);
            while (vm.UndoCommand.CanExecute(null))
            {
                vm.UndoCommand.Execute(null);
                await Task.Delay(400);
            }

            // --- 6. The bar, and the screen-reader rule -------------------------
            window.ToggleReadingMode();
            await Task.Delay(400);
            view = window.Shell.Active?.View;
            try
            {
                ScreenReader.OverrideForTest = () => true;
                view!.ShowReadingBar();
                await Task.Delay(200);
                Check("with a screen reader running, the bar is up", view.IsReadingBarShown);
                Check("  and no idle countdown was ever started", !view.ReadingBarIsCountingDown);
                // Forced anyway: the guard has to be in the tick and not only in
                // whether the timer was started, or a later change that restarts it
                // from somewhere else could take the bar away.
                view.FadeReadingBarNowForTest();
                Check("  and forcing the idle tick still leaves it up — it never fades",
                      view.IsReadingBarShown);

                ScreenReader.OverrideForTest = () => false;
                window.ClickPagesForTest();   // focus off the bar
                view.ShowReadingBar();
                await Task.Delay(200);
                Check("with no screen reader, the bar is up and counting down",
                      view.IsReadingBarShown && view.ReadingBarIsCountingDown);
                view.FadeReadingBarNowForTest();
                Check("  and the idle tick fades it", !view.IsReadingBarShown);
                view.ShowReadingBar();
                Check("  movement brings it back", view.IsReadingBarShown);

                view.ReadingExitControl.Focus(FocusState.Keyboard);
                await Task.Delay(300);
                Check("keyboard focus in the bar stops the countdown", !view.ReadingBarIsCountingDown);
                view.FadeReadingBarNowForTest();
                Check("  and holds it open through an idle tick", view.IsReadingBarShown);
            }
            finally
            {
                ScreenReader.OverrideForTest = null;
            }

            // --- 7. Page colours: the engine, against real pixels ---------------
            Check("the engine tints the page it is asked to", CheckTintedPixels(vm, Check));

            // --- 8. Page colours: the app, the cache and the settings file ------
            var firstPage = vm.Pages[0];
            settings.PageTint = MegaPDF.Core.Engine.PageTint.Night;
            window.ApplyPageColours();
            await Task.Delay(1500);
            Check("choosing Night reaches every tab and every page",
                  vm.Tint == MegaPDF.Core.Engine.PageTint.Night);
            Check("  the visible page re-rendered in it",
                  !ReferenceEquals(vm.Pages[0], firstPage)
                  && vm.Pages[0].Tint == MegaPDF.Core.Engine.PageTint.Night);
            var faraway = vm.Pages.Skip(6).FirstOrDefault();
            Check("  and a page nobody is looking at did not",
                  faraway is null || faraway.Source is null);
            Check("  the gutter retints to match", scroll.Background is not null);

            settings.OpenInReadingMode = true;
            var onDisk = new MegaPDF.Core.Services.AppSettings();
            Check($"Page colours persists to the shared settings.json (\"{onDisk.PageColours}\")",
                  onDisk.PageColours == "Night");
            Check("Open documents in reading mode persists beside it", onDisk.OpenInReadingMode);

            settings.PageTint = MegaPDF.Core.Engine.PageTint.Normal;
            window.ApplyPageColours();
            await Task.Delay(800);
            Check("  and Normal hands the gutter back to the theme rather than pinning a colour",
                  scroll.ReadLocalValue(Control.BackgroundProperty) == DependencyProperty.UnsetValue);

            window.ExitReadingMode();
            await Task.Delay(300);

            // --- 9. "Open documents in reading mode", and tabs -------------------
            //
            // The setting's whole job is to be honoured when a document arrives
            // (#168 decision 2), and reading mode is the *window's*, so a second tab
            // is also the only way to see the bar name the file it is showing.
            var second = Path.Combine(
                Path.GetDirectoryName(vm.DocumentPath ?? "") ?? "", "demo.pdf");
            if (File.Exists(second))
            {
                settings.OpenInReadingMode = true;
                await window.Shell.OpenInTabAsync(second);
                await Task.Delay(2000);
                Check("with the setting on, a document opens straight into reading mode",
                      window.IsReadingMode);
                Check("  and the chrome went with it",
                      !window.FocusableControlIds().Contains("OpenButton"));
                Console.Error.WriteLine(
                    $"  (tabs={window.Shell.Documents.Count}, active={window.Shell.Active?.OpenDocumentName}, "
                    + $"view={(window.Shell.Active?.View is null ? "none" : "attached")})");
                Check($"  with two tabs open, the bar names the file "
                      + $"(\"{window.Shell.Active?.View?.ReadingFileNameText}\")",
                      window.Shell.Active?.View?.ReadingFileNameText == Path.GetFileName(second));
                Check("  and both tabs are in the mode — it is the window's, not a document's",
                      window.Shell.Documents.All(d => d.IsReadingMode));

                window.ExitReadingMode();
                await Task.Delay(400);
                Console.Error.WriteLine(
                    $"  (after leaving: view={(window.Shell.Active?.View is null ? "none" : "attached")}, "
                    + $"bar name=\"{window.Shell.Active?.View?.ReadingFileNameText}\")");
                Check("leaving puts the chrome back for both tabs",
                      window.FocusableControlIds().Contains("OpenButton")
                      && window.Shell.Documents.All(d => !d.IsReadingMode));
            }
            else
            {
                Console.Error.WriteLine($"note: {second} is not there, so the "
                    + "open-in-reading-mode and two-tab checks were skipped.");
            }
        }
    }

    /// <summary>
    /// The engine half of page colours (#509 consumed, not merely passed): the same
    /// page rendered three ways, asserted on the pixels of a sheet of white paper.
    /// "The caller ORs a bit" is not evidence that a sepia page is sepia.
    /// </summary>
    private static bool CheckTintedPixels(DocumentViewModel vm, Action<string, bool> check)
    {
        if (vm.CurrentDocument is not { } document)
        {
            check("  a document to render", false);
            return false;
        }
        using var page = document.GetPage(0);
        var normal = page.Render(200, 260).Bgra;
        var sepia = page.Render(200, 260, MegaPDF.Core.Engine.PageTint.Sepia).Bgra;
        var night = page.Render(200, 260, MegaPDF.Core.Engine.PageTint.Night).Bgra;

        var white = -1;
        for (var i = 0; i + 3 < normal.Length; i += 4)
        {
            if (normal[i] == 0xFF && normal[i + 1] == 0xFF && normal[i + 2] == 0xFF)
            {
                white = i;
                break;
            }
        }
        check("  the page has white paper to tint", white >= 0);
        if (white < 0)
            return false;

        var sepiaOk = sepia[white] == 0xD8 && sepia[white + 1] == 0xEC && sepia[white + 2] == 0xF4;
        check($"  sepia turns it into the core's paper white (#{sepia[white + 2]:X2}{sepia[white + 1]:X2}{sepia[white]:X2})", sepiaOk);
        var nightOk = night[white] <= 0x40 && night[white + 1] <= 0x40 && night[white + 2] <= 0x40;
        check($"  night turns it dark (#{night[white + 2]:X2}{night[white + 1]:X2}{night[white]:X2})", nightOk);
        var differ = !normal.SequenceEqual(sepia) && !normal.SequenceEqual(night);
        check("  and Normal is the page as the document draws it", differ);
        return sepiaOk && nightOk && differ;
    }

    private static async Task<bool> CheckToolbarFocusAsync(MainWindow window)
    {
        var vm = window.Shell.Active;
        if (vm is null || !vm.IsDocumentOpen)
        {
            Console.Error.WriteLine("--screenshot-state focus needs a document.");
            return false;
        }
        var ok = true;
        void Check(string what, bool passed)
        {
            Console.Error.WriteLine($"{(passed ? "PASS" : "FAIL")}: {what}");
            ok &= passed;
        }

        // Something to undo, so Undo is enabled and is where focus would fall.
        await vm.AddWhiteoutAsync(0, new MegaPDF.Core.Engine.PdfRect(40, 40, 30, 12));
        await Task.Delay(500);

        // A change applied while a toolbar button has keyboard focus: the busy state disables
        // it. Whiteout for preference, but a narrow window has moved it into the overflow,
        // where nothing can be focused -- take whatever is on the bar instead (#222).
        var focused = window.FocusAnyToolbarButtonForTest("WhiteoutButton", "SignaturesButton", "OpenButton");
        await Task.Delay(300);
        var before = window.FocusedAutomationId();
        await vm.AddWhiteoutAsync(0, new MegaPDF.Core.Engine.PdfRect(40, 80, 30, 12));
        await Task.Delay(800);
        var after = window.FocusedAutomationId();
        Check($"focus on {before} during a change does not move to Undo (now {after})",
              before == focused && focused is not null && after != "UndoButton");
        Check($"  it goes to the pages (now {after})", after == "PagesScroll");

        // Work held busy for longer than the strip's delay, with a toolbar button focused.
        window.FocusAnyToolbarButtonForTest("SignaturesButton", "OpenButton");
        await Task.Delay(300);
        using (vm.Busy.Begin(Strings.BusyApplying))
            await Task.Delay(900);
        await Task.Delay(500);
        after = window.FocusedAutomationId();
        Check($"focus on Signatures while busy does not move to another toolbar button (now {after})",
              after is not ("UndoButton" or "RedoButton" or "OpenButton" or "SaveButton"));

        // A dialog: the toolbar is off while it shows, back on after.
        window.FocusAnyToolbarButtonForTest("OpenButton");
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "focus check",
            CloseButtonText = Strings.OK,
            XamlRoot = window.Content.XamlRoot,
        };
        var showing = dialog.ShowOneAtATimeAsync();
        await Task.Delay(800);
        Check("the toolbar is disabled while a dialog shows", !window.IsToolbarEnabled);
        dialog.Hide();
        await showing;
        await Task.Delay(300);
        Check("  and enabled again once it closes", window.IsToolbarEnabled);

        while (vm.UndoCommand.CanExecute(null))
        {
            vm.UndoCommand.Execute(null);
            await Task.Delay(300);
        }
        return ok;
    }

    /// <summary>
    /// The signature library with one card in it (#100). An empty library is seeded
    /// from tools/assets/megawoman-sig.jpg through the same cleanup the Add-from-photo
    /// button uses, so the shot shows what a user with a signature sees. The seed lands
    /// in the real per-user library, exactly as the store rig's
    /// Add-SignatureToLibrary.ps1 did by hand.
    /// </summary>
    private static async Task<bool> OpenSignatureLibraryAsync(MainWindow window)
    {
        if (window.Shell.Active is not { } vm)
        {
            Console.Error.WriteLine("--screenshot-state sign: no document is open.");
            return false;
        }
        if (vm.Signatures.Count == 0)
        {
            var seed = FindUpwards(Path.Combine("tools", "assets", "megawoman-sig.jpg"));
            if (seed is null)
            {
                Console.Error.WriteLine(
                    "--screenshot-state sign: the library is empty and tools/assets/megawoman-sig.jpg "
                    + "was not found above the executable, so there is no card to photograph.");
                return false;
            }
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(seed);
            var image = await SignatureImageProcessor.LoadAndCleanAsync(file);
            await vm.AddSignatureFromImageAsync(image, "Mega W.");
            if (vm.Signatures.Count == 0)
            {
                Console.Error.WriteLine("--screenshot-state sign: seeding the library failed.");
                return false;
            }
        }

        // Not the real flyout: RenderTargetBitmap renders the popup layer as nothing
        // (0×0 for the presenter, verified), PrintWindow returns white for a
        // composition window, and a screen BitBlt needs the window in front, which a
        // process started from a terminal cannot arrange. The window keeps an in-tree
        // copy of the same panel for exactly this shot.
        window.ShowSignatureLibraryForScreenshot();
        // Thumbnails are decoded from bytes after the cards appear (#402): every card must
        // end up with one, or a shot of white boxes would pass for the library.
        if (!await WaitForThumbnailsAsync(vm))
        {
            Console.Error.WriteLine("--screenshot-state sign: a card is still without its thumbnail, or its image is reported missing.");
            return false;
        }
        Console.WriteLine($"signature library shown with {vm.Signatures.Count} signature(s)");
        return true;
    }

    /// <summary>
    /// True once every card has either its thumbnail or its "image not found" state; false
    /// when one is still blank after two seconds, or when <paramref name="expectMissing"/>
    /// is not how many cards say the image is gone.
    /// </summary>
    private static async Task<bool> WaitForThumbnailsAsync(DocumentViewModel vm, int expectMissing = 0)
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(100);
            if (vm.Signatures.All(s => s.Thumbnail is not null || s.IsMissing))
                break;
        }
        await Task.Delay(700); // layout, for the capture
        var missing = vm.Signatures.Count(s => s.IsMissing);
        var blank = vm.Signatures.Count(s => s.Thumbnail is null && !s.IsMissing);
        Console.WriteLine($"signature cards: {vm.Signatures.Count}, thumbnails: {vm.Signatures.Count - missing - blank}, missing: {missing}, blank: {blank}");
        return blank == 0 && missing == expectMissing;
    }

    /// <summary>
    /// The #402 pose: adds a signature of its own, deletes its image behind the library's
    /// back, and reloads — the card must come back saying the image is gone, beside the
    /// others with their thumbnails intact. The entry it added is removed again at the
    /// end, so the per-user library is left as it was found.
    /// </summary>
    private static async Task<bool> ShowMissingSignatureAsync(MainWindow window)
    {
        if (window.Shell.Active is not { } vm)
        {
            Console.Error.WriteLine("--screenshot-state sign-missing: no document is open.");
            return false;
        }
        var seed = FindUpwards(Path.Combine("tools", "assets", "megawoman-sig.jpg"));
        if (seed is null)
        {
            Console.Error.WriteLine("--screenshot-state sign-missing: tools/assets/megawoman-sig.jpg was not found above the executable.");
            return false;
        }
        var before = vm.Signatures.Count;
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(seed);
        var image = await SignatureImageProcessor.LoadAndCleanAsync(file);
        await vm.AddSignatureFromImageAsync(image, "Gone W.");
        var added = vm.Signatures.LastOrDefault();
        if (added is null || vm.Signatures.Count != before + 1)
        {
            Console.Error.WriteLine("--screenshot-state sign-missing: adding the test signature failed.");
            return false;
        }
        // The card has to stay for the capture, which happens after this returns, so the
        // entry is written out of the index as the process exits instead (Environment.Exit
        // runs ProcessExit). Plain file I/O on the library, nothing of XAML's.
        var library = window.Shell.SignatureLibrary;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { library.Remove(added.Id); }
            catch (Exception) { /* already gone: nothing to leave behind */ }
        };

        File.Delete(added.PngPath);
        vm.LoadSignatures(); // what a tab activation does: the library re-read from disk
        window.ShowSignatureLibraryForScreenshot();
        if (!await WaitForThumbnailsAsync(vm, expectMissing: 1))
        {
            Console.Error.WriteLine("--screenshot-state sign-missing: expected exactly one card to say its image is gone, with every other thumbnail present.");
            return false;
        }
        var gone = vm.Signatures.Single(s => s.IsMissing);
        if (gone.Id != added.Id || gone.Thumbnail is not null)
        {
            Console.Error.WriteLine("--screenshot-state sign-missing: the wrong card is the missing one, or it still has a thumbnail.");
            return false;
        }
        Console.WriteLine($"signature library shown with {vm.Signatures.Count} card(s), '{gone.Name}' reported missing");
        return true;
    }

    /// <summary>A repo-relative file, found by walking up from the executable (a dev build runs from bin/).</summary>
    private static string? FindUpwards(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
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
        if (window.Shell.Active is not { } vm || window.PageScroller is not { } scroll)
        {
            Console.Error.WriteLine("--screenshot-state find-zoomed: no document is open.");
            return false;
        }

        // Awaited, not fire-and-forget: ZoomIn is async, so Execute in a loop
        // races its own CanExecute and stops short of maximum by a step or two.
        while (vm.ZoomPercent < DocumentViewModel.MaxZoom)
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
            //
            // --scale 2 (#144): twice the logical size in pixels whatever the display
            // scale, so review shots match between machines at 150% and 200%.
            if (content is FrameworkElement element)
            {
                element.UpdateLayout();
                var factor = ArgumentAfter("--scale") is "2" && element.XamlRoot is { RasterizationScale: > 0 } root
                    ? 2 / root.RasterizationScale
                    : 1;
                var w = (int)Math.Round(element.ActualWidth * factor);
                var h = (int)Math.Round(element.ActualHeight * factor);
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
