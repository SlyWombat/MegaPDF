using System.Globalization;
using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Recovery;
using MegaPDF.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
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
        // #617: the one state that needs *no* document — the opposite of every other one
        // below — so it runs before the "needs a document" guard, not through it.
        // App.xaml.cs's RunScreenshotAsync already knows not to open the command line's
        // fixture for this state.
        if (state == "empty")
            return CheckEmptyState(window);

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

            // #528: zoom anchoring, in a real window, for the same reason "reading" is —
            // CI cannot raise this (#462), so this is the by-hand gate. Needs a document;
            // the exit code is the test.
            case "zoom-anchor":
                return await CheckZoomAnchorAsync(window);

            // #543: closing a tab, replacing a tab's document, and the window closing with
            // tabs still in it must all dispose the document each one held. Needs a
            // document; the exit code is the test.
            case "close-tabs":
                return await CheckCloseDisposesDocumentsAsync(window);

            // #174: the page tools — the Pages pane, rotate, delete, reorder, insert, combine and
            // extract — in a real window, because half of what can break is the window's (the
            // toolbar's Pages control, the accelerator grid, the tab order, the grid's own
            // selection). Needs a document; the exit code is the test.
            case "pages":
                return await CheckPageToolsAsync(window);

            // #3/#4: a whiteout's move/resize chrome, and the inline text editor's
            // persona-simple S/M/L sizes and Shift+Enter multi-line — in a real window, for
            // the same reason `pages` needs one. Needs a document; the exit code is the test.
            case "whiteout-text":
                return await CheckWhiteoutAndTextAsync(window);

            // #145: progress and Stop on the operations measured slow enough to deserve them —
            // search and shrink, both cancellable and counting themselves — and extract, which
            // gets a Stop with no honest count to give. Needs a document; the exit code is the
            // test.
            case "progress":
                return await CheckProgressAndCancelAsync(window);

            // #590: a real AcroForm text field filled in, and a real AcroForm checkbox
            // ticked — not the drawn square `click` already covers (fixture.pdf has no
            // AcroForm fields at all). Needs formtext.pdf and forms.pdf beside the
            // document on the command line (tools/gen_test_fixtures.py writes both next
            // to fixture.pdf, so CI's "Generate fixtures" step already has them there).
            // The exit code is the test.
            case "fill":
                return await CheckFillFormAsync(window);

            // #590: a redaction mark drawn, selected, moved, resized, removed, cleared and
            // undone — the #329 mark lifecycle, with no Windows self-test state to watch
            // it. Needs a document; the exit code is the test.
            case "redact":
                return await CheckRedactMarkAsync(window);

            // #590: pressing Save for real — the file on disk changes and
            // HasUnsavedChanges clears. Needs a document; the exit code is the test.
            case "save":
                return await CheckSaveAsync(window);

            // #590: the Password command raises the real Set Password dialog with its
            // real fields, and the write path behind it (ApplySecurityAsync) actually
            // encrypts the saved file. Needs a document; the exit code is the test.
            case "security":
                return await CheckSecurityDialogAsync(window);

            // #590: About's version text and the third-party notices dialog — #571 was
            // this exact shape on Linux (a route and a window, nothing leading to
            // either). Needs a document (ApplyStateAsync's own guard); the exit code is
            // the test.
            case "about":
                return await CheckAboutAsync(window);

            default:
                Console.Error.WriteLine($"unknown --screenshot-state '{state}'");
                return false;
        }
    }

    /// <summary>
    /// A freshly launched window, zero tabs (#617) — the first Windows store capture of this
    /// screen ever taken found three things wrong at once, all one cause: with no tab, <c>
    /// Shell.Active</c> is null, and the <c>x:Bind</c>s that used to reach through it fell back
    /// to their *target* property's own default rather than a safe one — <see
    /// cref="Visibility.Visible"/> for the busy strip and both halves of Stop, "stay however I
    /// was" (enabled) for Undo/Redo and every other command bound by <c>Command</c> alone. See
    /// <c>ShellViewModel.Active</c>'s remarks for the fix: every one of those now reaches
    /// through a <c>Shell</c>-level proxy with a real non-null fallback instead.
    ///
    /// Needs *no* document — the opposite of every other state in this file — which is why it
    /// is handled before <see cref="ApplyStateAsync"/>'s "needs a document" guard rather than
    /// through it, and why <c>App.xaml.cs</c> skips opening the command line's fixture for it.
    /// </summary>
    private static bool CheckEmptyState(MainWindow window)
    {
        if (window.Shell.HasDocuments)
        {
            Console.Error.WriteLine("--screenshot-state empty needs zero tabs open, but one is.");
            return false;
        }

        var passed = 0;
        var failed = 0;
        void Check(string what, bool ok)
        {
            Console.Error.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (ok)
                passed++;
            else
                failed++;
        }

        Check("the busy strip is not up with no work running", !window.BusyStripIsVisibleForTest);
        // Not "not both at once" — neither is drawn at all. Mutual exclusion would still pass
        // with both collapsed, which is exactly the bug: #617's screenshot showed both visible,
        // painted over each other, not one incorrectly chosen over the other.
        Check("Stop is not drawn", !window.BusyCancelButtonIsVisibleForTest);
        Check("\"Stopping…\" is not drawn either", !window.BusyCancellingLabelIsVisibleForTest);

        foreach (var (name, enabled) in window.DocumentCommandEnabledStatesForTest())
            Check($"{name} is disabled", !enabled);

        Check("Open, New window and Settings stay enabled with no document",
              window.DocumentIndependentCommandsEnabledForTest);

        if (failed > 0)
            Console.Error.WriteLine($"empty: {failed} of {passed + failed} checks failed.");
        return failed == 0;
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
    /// Zoom anchoring (#528), in the real window this process already has open. Every
    /// entry point used to grow the page from <c>PagesScroll</c>'s own origin (its own
    /// top-left corner), so whatever you were looking at slid out from under the
    /// pointer or the click that asked for the zoom. Mirrors <see cref="CheckReadingModeAsync"/>'s
    /// own shape and the Avalonia leg's <c>CheckZoomAnchor</c> (#534) — the nearest
    /// thing this app has to a self-test for a real window, for the same reason
    /// reading mode needed one (#462: no headless platform, no CI desktop session).
    /// Needs a document; the exit code is the test.
    /// </summary>
    private static async Task<bool> CheckZoomAnchorAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } vm
            || vm.View is not { } view
            || window.PageScroller is not { } scroll)
        {
            Console.Error.WriteLine("--screenshot-state zoom-anchor needs a document.");
            return false;
        }

        var ok = true;
        void Check(string what, bool passed)
        {
            Console.Error.WriteLine($"{(passed ? "PASS" : "FAIL")}: {what}");
            ok &= passed;
        }

        try
        {
            await RunZoomAnchorChecksAsync(vm, view, scroll, Check);
        }
        catch (Exception ex)
        {
            // Same reasoning as CheckReadingModeAsync's own try/catch: an exception left
            // to escape here is posted to the dispatcher and the process just hangs, with
            // nothing said — worse than no check at all.
            Console.Error.WriteLine($"FAIL: the zoom-anchor check threw: {ex}");
            ok = false;
        }
        finally
        {
            // Left at a known zoom, not whatever the last check happened to leave it at —
            // --window is the caller's to pick, this app's own settings are not touched.
            await vm.SetZoomPercentAsync(100);
        }
        return ok;
    }

    /// <summary>
    /// The checks themselves (#528). Each one reads the content point under an anchor
    /// before a zoom step, computes independently — not by calling
    /// <see cref="MegaPDF.App.ZoomAnchor.Reanchor"/> itself, which would only prove the
    /// production code agrees with itself — where that point should land given the
    /// zoom <see cref="DocumentViewModel"/> actually committed to, and asserts the
    /// offset landed there rather than wherever an unfixed zoom would have left it (the
    /// corner, unmoved, no matter the anchor).
    ///
    /// Run with a narrower window than the page at every zoom level used below, on
    /// purpose (<c>--screenshot --window 900x700</c> in the by-hand recipe): at 100%,
    /// with fit-to-window never triggered (a fresh fixture with no remembered view
    /// opens at 100%, see <see cref="DocumentViewModel.SetZoomAsync"/>), there is
    /// nothing yet to correct, and it is also the one place a check could not tell
    /// "anchored correctly" from "never moved at all" — the corner an unfixed zoom
    /// already sits at. Every check here first zooms past 100% and moves the offset off
    /// (0,0) before it measures anything.
    /// </summary>
    private static async Task RunZoomAnchorChecksAsync(
        DocumentViewModel vm, DocumentView view, ScrollViewer scroll, Action<string, bool> check)
    {
        // Layout rounding leaves a fraction of a DIP of slack; a real drift from the
        // corner-anchoring bug is tens to hundreds of DIP, not this.
        const double Epsilon = 1.5;

        async Task SetOffsetAsync(double x, double y)
        {
            scroll.ChangeView(x, y, null, disableAnimation: true);
            await Task.Delay(200);
        }

        // --- Menu, toolbar and keyboard: anchored on the viewport's centre ---
        //
        // vm.ZoomInCommand.ExecuteAsync(null) is exactly what the toolbar's zoom-in
        // button (Command-bound in MainWindow.xaml), the keyboard accelerators
        // (MainWindow.Toolbar.cs) and the reading-mode bar's own zoom button
        // (DocumentView.ReadingMode.cs) all run.
        await vm.SetZoomPercentAsync(200);
        await Task.Delay(300);
        await SetOffsetAsync(37, 210);

        var viewport = new Point(scroll.ViewportWidth, scroll.ViewportHeight);
        var centre = new Point(viewport.X / 2, viewport.Y / 2);
        var centreBeforeOffset = new Point(scroll.HorizontalOffset, scroll.VerticalOffset);
        var centreBeforeZoom = vm.ZoomFactor;
        var contentAtCentreBefore = new Point(centreBeforeOffset.X + centre.X, centreBeforeOffset.Y + centre.Y);

        await vm.ZoomInCommand.ExecuteAsync(null);
        await Task.Delay(300);

        var centreRatio = vm.ZoomFactor / centreBeforeZoom;
        var expectedAtCentre = new Point(contentAtCentreBefore.X * centreRatio, contentAtCentreBefore.Y * centreRatio);
        var actualAtCentre = new Point(scroll.HorizontalOffset + centre.X, scroll.VerticalOffset + centre.Y);
        check($"menu/keyboard zoom in ({centreBeforeZoom * 100:F0}% -> {vm.ZoomPercent}%) keeps the viewport "
              + $"centre's content under it (expected {Describe(expectedAtCentre)}, got {Describe(actualAtCentre)})",
              Math.Abs(actualAtCentre.X - expectedAtCentre.X) < Epsilon && Math.Abs(actualAtCentre.Y - expectedAtCentre.Y) < Epsilon);

        // --- The clamp: correct by the ratio actually committed, not the ratio asked for ---
        //
        // ZoomInCommand asks for +25 (ZoomStep) every time; at 290% that asks for 315%,
        // which SetZoomAsync clamps to MaxZoom (300%). The ratio actually applied is
        // 300/290, not 315/290 — correcting by the requested ratio would land the
        // anchor point somewhere the committed zoom does not put it.
        await vm.SetZoomPercentAsync(290);
        await Task.Delay(300);
        await SetOffsetAsync(15, 90);

        var clampBeforeOffset = new Point(scroll.HorizontalOffset, scroll.VerticalOffset);
        var clampBeforeZoom = vm.ZoomFactor; // 2.90
        var clampAnchor = new Point(viewport.X / 2, viewport.Y / 2);
        var contentAtClampAnchorBefore = new Point(clampBeforeOffset.X + clampAnchor.X, clampBeforeOffset.Y + clampAnchor.Y);

        await vm.ZoomInCommand.ExecuteAsync(null);
        await Task.Delay(300);

        check($"zooming in from 290% clamps to {vm.ZoomPercent}% (MaxZoom), not 315%", vm.ZoomPercent == 300);
        var committedRatio = vm.ZoomFactor / clampBeforeZoom; // 300/290
        var requestedRatio = 3.15 / clampBeforeZoom;          // 315/290 — what a naive correction would use
        var expectedByCommitted = new Point(contentAtClampAnchorBefore.X * committedRatio, contentAtClampAnchorBefore.Y * committedRatio);
        var expectedByRequested = new Point(contentAtClampAnchorBefore.X * requestedRatio, contentAtClampAnchorBefore.Y * requestedRatio);
        var actualAtClampAnchor = new Point(scroll.HorizontalOffset + clampAnchor.X, scroll.VerticalOffset + clampAnchor.Y);
        check($"  the correction uses the clamped ratio (expected {Describe(expectedByCommitted)}, got {Describe(actualAtClampAnchor)})",
              Math.Abs(actualAtClampAnchor.X - expectedByCommitted.X) < Epsilon && Math.Abs(actualAtClampAnchor.Y - expectedByCommitted.Y) < Epsilon);
        check("  not the requested one (would have landed at "
              + $"{Describe(expectedByRequested)}, further off than {Epsilon} DIP from where it actually landed)",
              Math.Abs(actualAtClampAnchor.X - expectedByRequested.X) >= Epsilon || Math.Abs(actualAtClampAnchor.Y - expectedByRequested.Y) >= Epsilon);

        // --- Ctrl+wheel: anchored on the pointer ---
        await vm.SetZoomPercentAsync(200);
        await Task.Delay(300);
        await SetOffsetAsync(20, 150);

        var wheelAnchor = new Point(60, 40);
        var wheelBeforeOffset = new Point(scroll.HorizontalOffset, scroll.VerticalOffset);
        var wheelBeforeZoom = vm.ZoomFactor;
        var contentUnderWheelBefore = new Point(wheelBeforeOffset.X + wheelAnchor.X, wheelBeforeOffset.Y + wheelAnchor.Y);

        // ApplyWheelZoomAsync is exactly what OnPagesPointerWheel runs once it has read
        // the pointer's position and the wheel's sign off a real PointerRoutedEventArgs
        // — see that method's own remark on why this test cannot build one of those.
        await view.ApplyWheelZoomAsync(wheelAnchor, wheelDelta: 120);
        await Task.Delay(300);

        check($"Ctrl+wheel zooms ({wheelBeforeZoom * 100:F0}% -> {vm.ZoomPercent}%)", vm.ZoomFactor > wheelBeforeZoom);
        var wheelRatio = vm.ZoomFactor / wheelBeforeZoom;
        var expectedUnderWheel = new Point(contentUnderWheelBefore.X * wheelRatio, contentUnderWheelBefore.Y * wheelRatio);
        var actualUnderWheel = new Point(scroll.HorizontalOffset + wheelAnchor.X, scroll.VerticalOffset + wheelAnchor.Y);
        check($"  anchored on the pointer, not the corner (expected {Describe(expectedUnderWheel)}, got {Describe(actualUnderWheel)})",
              Math.Abs(actualUnderWheel.X - expectedUnderWheel.X) < Epsilon && Math.Abs(actualUnderWheel.Y - expectedUnderWheel.Y) < Epsilon);

        // --- The pure arithmetic, independent of any window at all ---
        var pureReanchor = MegaPDF.App.ZoomAnchor.Reanchor(new Point(100, 200), 1.0, 0.5, new Point(50, 50));
        check($"ZoomAnchor.Reanchor at 0.5x from (100,200) around (50,50) is (25,75) (got {Describe(pureReanchor)})",
              Math.Abs(pureReanchor.X - 25) < 0.0001 && Math.Abs(pureReanchor.Y - 75) < 0.0001);
        var pureReanchorAtClamp = MegaPDF.App.ZoomAnchor.Reanchor(new Point(100, 200), 4.0, 4.0, new Point(50, 50));
        check($"ZoomAnchor.Reanchor with no ratio change leaves the offset alone (got {Describe(pureReanchorAtClamp)})",
              pureReanchorAtClamp.X == 100 && pureReanchorAtClamp.Y == 200);

        static string Describe(Point p) => $"({p.X:F1}, {p.Y:F1})";
    }

    /// <summary>
    /// #543: closing a tab, replacing a tab's document with another, and the window
    /// closing with tabs still open must all dispose the document each one held — the
    /// PDFium document, its form environment, every page still loaded, and the core's
    /// file source, none of which anything else reclaims.
    ///
    /// Proven by asking a captured <see cref="MegaPDF.Core.Engine.IPdfDocument"/> for a
    /// page afterward: <c>PdfiumDocument</c>'s own guard throws <see
    /// cref="ObjectDisposedException"/> once <c>megapdf_close</c> has actually run (#542)
    /// — a stronger signal than "the tab left the strip", which the pre-fix code already
    /// got right on its own and would make a check that only looked at
    /// <see cref="ShellViewModel.Documents"/> pass for the wrong reason.
    ///
    /// The window-close path is exercised through <see
    /// cref="MainWindow.DisposeAllDocumentsForTest"/> — the exact body <see
    /// cref="MainWindow"/>'s own <c>Closed</c> handler runs — rather than by really
    /// closing the window, which would end this process before this check's result
    /// could be reported; the same reason <see cref="CheckReadingModeAsync"/> drives
    /// <c>StepBackFromReadingMode</c> instead of a real Escape key. That WinUI raises
    /// <c>Closed</c> when the window goes is reasoned from here, not driven.
    ///
    /// Needs a document (the fixture --screenshot already opened); the exit code is the
    /// test. Leaves the fixture's own tab as it found it — the extra tabs this check
    /// opens (on copies of the fixture under another name, since opening the same path
    /// twice activates the existing tab rather than opening a second one) are all closed
    /// or disposed before it returns.
    /// </summary>
    private static async Task<bool> CheckCloseDisposesDocumentsAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } tabA || tabA.DocumentPath is not { } fixturePath)
        {
            Console.Error.WriteLine("--screenshot-state close-tabs needs a document.");
            return false;
        }

        var ok = true;
        void Check(string what, bool passed)
        {
            Console.Error.WriteLine($"{(passed ? "PASS" : "FAIL")}: {what}");
            ok &= passed;
        }

        static bool ThrowsDisposed(MegaPDF.Core.Engine.IPdfDocument doc)
        {
            try
            {
                doc.GetPage(0);
                return false; // still alive — the bug this check exists to catch
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "megapdf-selftest-close-tabs");
        Directory.CreateDirectory(tempDir);
        var copyB = Path.Combine(tempDir, "b.pdf");
        var copyC = Path.Combine(tempDir, "c.pdf");
        var copyD = Path.Combine(tempDir, "d.pdf");
        File.Copy(fixturePath, copyB, overwrite: true);
        File.Copy(fixturePath, copyC, overwrite: true);
        File.Copy(fixturePath, copyD, overwrite: true);

        try
        {
            // --- Closing a tab disposes its document (ShellViewModel.RemoveDocument) ---
            var tabB = await window.Shell.OpenInTabAsync(copyB);
            var docB = tabB.CurrentDocument;
            Check("opening a second tab gives it its own document",
                  docB is not null && !ReferenceEquals(docB, tabA.CurrentDocument));
            if (docB is not null)
            {
                await window.CloseTabAsync(tabB);
                Check("closing that tab removes it from the shell", !window.Shell.Documents.Contains(tabB));
                Check("  and disposes the document it held (GetPage now throws ObjectDisposedException)",
                      ThrowsDisposed(docB));
            }

            // --- Replacing a tab's document disposes the one it replaced (AdoptDocumentAsync) ---
            var tabC = await window.Shell.OpenInTabAsync(copyC);
            var docC = tabC.CurrentDocument;
            Check("a third tab also gets its own document", docC is not null && !ReferenceEquals(docC, tabA.CurrentDocument));
            if (docC is not null)
            {
                await tabC.OpenDocumentAsync(copyD); // a different file into the same tab: a replacement, not a close
                Check("opening a new file into that same tab disposes the document it replaced",
                      ThrowsDisposed(docC));
                await window.CloseTabAsync(tabC); // leave the window as this check found it
            }

            // --- The window closing (with tabs still open) disposes every one of them,
            // the active tab included — tabF, opened last, is Shell.Active for as long
            // as nothing else changes it, which nothing here does. This is also what
            // stands in for the last tab closing the window (#543's other uncovered
            // path): CloseTabAsync's last-tab branch never calls RemoveDocument either,
            // so the mechanism it needs is exactly this one, not a second one. tabA (the
            // fixture tab this check must leave alone) is a third, background tab
            // throughout this block and is never Active, so it is never touched. ---
            var tabE = await window.Shell.OpenInTabAsync(copyB);
            var tabF = await window.Shell.OpenInTabAsync(copyC);
            var docE = tabE.CurrentDocument;
            var docF = tabF.CurrentDocument;
            Check("two more tabs — one of them (the last opened) active, one a background tab — each get their own document",
                  docE is not null && docF is not null && !ReferenceEquals(docE, docF) && window.Shell.Active == tabF);
            if (docE is not null && docF is not null)
            {
                window.DisposeAllDocumentsForTest();
                Check("the window-close path disposes every remaining tab's document, active and background alike",
                      ThrowsDisposed(docE) && ThrowsDisposed(docF));
                // Both documents are gone; leave the tabs out of the strip too, so a
                // screenshot taken after this check does not try to render them — and
                // so Active falls back to tabA, the one tab this check must leave alone.
                window.Shell.RemoveDocument(tabE);
                window.Shell.RemoveDocument(tabF);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort scratch cleanup */ }
        }

        return ok;
    }

    /// <summary>
    /// Page tools (#174), end to end, in the real window this process already has open — the
    /// Windows counterpart of the Avalonia leg's `page tools` self-test block.
    ///
    /// Why a window and not a view model: half of what is worth breaking here is the window's.
    /// Whether the Pages control is still on the toolbar, whether F4 is still on the accelerator
    /// grid, whether the pane leaves the tab order when reading mode hides it, and whether the
    /// grid's own selection and the view model's stay in step through a renumbering — a
    /// view-model check can see none of those, and each is how a keyboard or screen-reader user
    /// meets the feature.
    ///
    /// Three things in here are assertions rather than assumptions, because each is a rule a
    /// later change can break while the window still looks perfectly right:
    ///
    ///   1. The undo of a delete puts back the *page*, not a blank one — proved by hit-testing
    ///      the drawn square that only the original first page of fixture.pdf carries.
    ///   2. The app's own index-keyed state follows the renumbering, which contract 10 says in so
    ///      many words is the app's job: the page list, the tiles, the page sizes, the search
    ///      hits, the pane's selection and the page indicator.
    ///   3. The engine's two deliberate refusals reach the person as sentences that say what
    ///      happened and what still works, not as "the change failed".
    ///
    /// Three honest limits, stated here rather than discovered later:
    ///
    ///   * A WinUI process cannot synthesise its own key presses, so this reads the accelerator
    ///     grid to prove F4 and the two rotate chords are registered, and drives the commands
    ///     they invoke. That Windows delivers those keys is not checkable from here.
    ///   * Nor can it synthesise a drag, so the drop is driven through the same
    ///     gap-to-index step a real drop runs (<c>DropPageForTest</c>) rather than through a
    ///     pointer. The pane's own hit testing of a drop point is the by-hand part.
    ///   * Which behaviour this build's PDFium has for a form-field hierarchy is fixed at compile
    ///     time and cannot be asked for, so both branches are accepted and the log says which one
    ///     ran. The refusal itself is asserted where it is certain — a hierarchy whose name is
    ///     already taken, which no build can rename.
    ///
    /// Everything this writes lives in its own scratch directory and in extra tabs, and both are
    /// gone before it returns: the tab the fixture was opened in is left exactly as found.
    /// </summary>
    /// <summary>
    /// Everything a document's <see cref="BusyState"/> published while it was watched (#145):
    /// used to prove a page operation reports where it says it does, and not on a page that may
    /// not be there by the time anything could draw on it.
    ///
    /// Windows' own <see cref="MainWindow"/> and every page command run on the UI thread until
    /// their one background hop, and <see cref="BusyState.Begin"/>/<c>End</c> publish immediately
    /// when called from the thread that built the state — so a frame recorded here is not a race:
    /// by the time an awaited command has returned, every frame it published has already arrived.
    /// </summary>
    private sealed class BusyRecorder : IDisposable
    {
        private readonly BusyState _busy;
        private readonly List<Frame> _frames = [];
        private Func<Frame, bool>? _stopWhen;
        private bool _stopped;

        internal readonly record struct Frame(BusyScope Scope, string Label, int PageIndex, bool HasProgress,
                                              int Done, int Total, string Text, bool CanCancel, bool IsCancelling);

        internal BusyRecorder(BusyState busy)
        {
            _busy = busy;
            _busy.PropertyChanged += OnChanged;
        }

        internal IReadOnlyList<Frame> Frames => _frames;

        /// <summary>
        /// Calls <c>RequestCancel</c> the first time a published frame matches (#145) — from
        /// inside the work when <paramref name="busy"/> was built with no captured
        /// <see cref="SynchronizationContext"/> (see <see cref="WatchableDocument"/>), which is
        /// the only way this is a deterministic stop rather than a race against how fast the
        /// work finishes.
        /// </summary>
        internal void StopWhen(Func<Frame, bool> predicate)
        {
            _stopWhen = predicate;
            _stopped = false;
        }

        private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            var frame = new Frame(_busy.Scope, _busy.Label, _busy.PageIndex, _busy.HasProgress,
                                  _busy.ProgressDone, _busy.ProgressTotal, _busy.ProgressText,
                                  _busy.CanCancel, _busy.IsCancelling);
            _frames.Add(frame);
            // Once only: RequestCancel publishes IsCancelling, which re-enters this handler.
            if (_stopped || _stopWhen is not { } stop || !stop(frame))
                return;
            _stopped = true;
            _busy.RequestCancel();
        }

        internal void Clear()
        {
            _frames.Clear();
            _stopWhen = null;
            _stopped = false;
        }

        internal bool Saw(Func<Frame, bool> predicate) => _frames.Any(predicate);

        public void Dispose() => _busy.PropertyChanged -= OnChanged;
    }

    /// <summary>
    /// A document view model whose busy state raises its changes on whichever thread changes
    /// them, not on the UI dispatcher (#145).
    ///
    /// The loops this exists to watch — <c>SearchAsync</c>'s page walk,
    /// <c>ReplaceOversizedImagesAsync</c>'s picture walk, and the one call behind an extract —
    /// call <c>Report</c> and end their operation from a thread-pool thread by way of
    /// <c>Task.Run</c>. <see cref="BusyState"/> posts a change to the UI dispatcher whenever the
    /// calling thread differs from the one the state was built on, so a real tab's busy state —
    /// built on the UI thread, watched from the UI thread — only lets a <see cref="BusyRecorder"/>
    /// ask for a stop *after* the work has already gone as far as it is going to: racing how fast
    /// a two-page search or a two-picture shrink finishes, not proving anything about Stop. That
    /// race is exactly what a real `windows-ui-selftest` run hit for search's fixture-sized work.
    ///
    /// A document view model built with no captured <see cref="SynchronizationContext"/> instead
    /// publishes on whichever thread calls it, so <see cref="BusyRecorder.StopWhen"/>'s
    /// <c>RequestCancel</c> runs on the very thread running the loop, from inside its own call
    /// stack — a stop asked for from inside the work, not a wait or a margin.
    /// </summary>
    private static DocumentViewModel WatchableDocument(MainWindow window, ShellViewModel shell)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            return new DocumentViewModel(window, shell.Settings, shell.RecentFiles, shell.SignatureLibrary);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static async Task<bool> CheckPageToolsAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } fixtureTab
            || fixtureTab.DocumentPath is not { } fixturePath)
        {
            Console.Error.WriteLine("--screenshot-state pages needs a document.");
            return false;
        }

        var passed = 0;
        var failed = 0;
        void Check(string what, bool ok)
        {
            Console.Error.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (ok)
                passed++;
            else
                failed++;
        }

        // #569: a fixed name here, reused by every run on the same machine, let this check poison
        // itself. RecentFiles (SDD §3.4) remembers a scroll position per *path*, which is right for
        // a real document someone reopens — but this check always rewrites work.pdf's bytes from
        // fixture.pdf and expects to meet a document that has never been seen before. A run that
        // left the view scrolled down (ordinary: by the time this check reaches its later sections
        // the document is two pages and has been scrolled) wrote that position back under this same
        // path, and the *next* run restored it on open — so "the page you are looking at" was
        // already page 2 before "with nothing selected, Rotate Left turns the page you are looking
        // at" ever ran, and Rotate Left turned the page the test was not looking at.
        //
        // Measured directly (not guessed): with a %LOCALAPPDATA%\MegaPDF carrying one stale entry
        // for this path, the check failed 8/8 runs, every time on this exact line, and a debug
        // build that printed TargetPages at the moment of failure showed the fallback page was
        // wrong, not late — nothing races here, something remembers. Cleared before every run, the
        // same build passed 30/30, and with a GUID per run below it held 60/60 even starting from
        // the worst-case polluted profile and never cleaning between runs. A GUID per run makes
        // that history impossible to meet again.
        var scratch = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-pages-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            await RunPageToolChecksAsync(window, fixtureTab, fixturePath, scratch, Check);
        }
        catch (Exception ex)
        {
            // A throw here is a FAIL with a stack, not a window left standing: an exception
            // escaping into the dispatcher hangs the process with nothing said, which is how a
            // check becomes worse than no check at all (the reading-mode state learnt this).
            Console.Error.WriteLine($"FAIL: the page-tools check threw: {ex}");
            failed++;
        }
        finally
        {
            // Left as found: every tab but the fixture's, and the scratch directory.
            foreach (var tab in window.Shell.Documents.Where(t => !ReferenceEquals(t, fixtureTab)).ToList())
            {
                tab.HasUnsavedChanges = false;   // never ask about scratch (#145 D5's dialog)
                await window.CloseTabAsync(tab);
            }
            fixtureTab.IsPagesPaneOpen = false;
            fixtureTab.SelectedPageIndices = [];
            try { Directory.Delete(scratch, recursive: true); } catch { /* best-effort scratch cleanup */ }
        }

        Console.Error.WriteLine($"pages: {passed} passed, {failed} failed ({passed + failed} checks)");
        return failed == 0;
    }

    private static async Task RunPageToolChecksAsync(
        MainWindow window, DocumentViewModel fixtureTab, string fixturePath, string scratch,
        Action<string, bool> Check)
    {
        // The centre of the 12x12 pt square stroked on fixture.pdf's first page, in the top-left
        // page space the core reports and reads (#439). Page 2 has no square, and neither has a
        // blank page — which is what makes "the page that came back is the page that went" an
        // assertion rather than a page count.
        var drawnCentre = new PdfPoint(78, 186);

        // A copy per document opened, because opening a path a tab already holds activates that
        // tab instead of opening another (the same reason `close-tabs` copies).
        var work = Path.Combine(scratch, "work.pdf");
        var other = Path.Combine(scratch, "other.pdf");
        var form = Path.Combine(scratch, "form.pdf");
        var hierarchy = Path.Combine(scratch, "hierarchy.pdf");
        File.Copy(fixturePath, work, overwrite: true);
        File.Copy(fixturePath, other, overwrite: true);
        // Built here rather than generated into the fixtures directory, so this check needs no
        // change to CI — the same choice the Avalonia leg made for the same two documents.
        File.WriteAllBytes(form, FlatPersonFieldPdf());
        File.WriteAllBytes(hierarchy, ParentFieldsPdf());

        var tab = await window.Shell.OpenInTabAsync(work);
        await Task.Delay(1500);
        var view = tab.View;

        // A fresh two-page document in the working tab, between sections. The unsaved flag is
        // cleared first because replacing a document that has unsaved changes asks about them in a
        // ContentDialog (#145 D5) — and a self-test that opens a modal has hung rather than failed,
        // with nothing said, which is the one failure mode worse than no check at all.
        // What Narrator was last told. The view model raises its announcement and the view posts it
        // to the dispatcher (DocumentView.Keyboard.cs), so reading it in the same turn as the
        // command that caused it can see the one before — this yields a turn first.
        async Task<string> SaidAsync()
        {
            await Task.Delay(120);
            return view!.LastAnnouncement;
        }

        async Task StartOverAsync(string path)
        {
            tab.SelectedPageIndices = [];
            tab.IsPageToolNoticeOpen = false;
            tab.HasUnsavedChanges = false;
            await tab.OpenDocumentAsync(path);
            await Task.Delay(1200);
        }
        Check("a working tab opened on a two-page document", tab.Pages.Count == 2 && view is not null);
        if (view is null)
            return;

        static bool HasDrawnSquare(DocumentViewModel vm, int pageIndex, PdfPoint point)
        {
            if (vm.CurrentDocument is not { } document || pageIndex < 0 || pageIndex >= document.PageCount)
                return false;
            using var page = document.GetPage(pageIndex);
            return page.HitTest(point).Kind == PageHitKind.DrawnCheckbox;
        }

        static PdfFormField? FieldAt(DocumentViewModel vm, int pageIndex, PdfPoint point)
        {
            if (vm.CurrentDocument is not { } document || pageIndex < 0 || pageIndex >= document.PageCount)
                return null;
            using var page = document.GetPage(pageIndex);
            return page.HitTest(point).Field;
        }

        // --- 1. Where the affordance is, and the keys ---------------------------
        {
            var entries = window.PageToolEntryPointsForTest();
            Check($"the page tools are reachable from the toolbar and from “…” ({string.Join(", ", entries)})",
                  entries.Contains("Toolbar/PagesMenuButton")
                  && entries.Contains("PagesMenu/PagesPaneItem")
                  && entries.Contains("More/PagesPaneButton"));

            var accelerators = window.PageAcceleratorsForTest();
            Check($"F4 shows the pane, the way Acrobat's navigation pane does on Windows ({accelerators.Count} accelerators on the window)",
                  accelerators.Contains("None+F4"));
            Check("Ctrl+R turns the page right, as it does in Windows Photos",
                  accelerators.Contains("Control+R"));
            Check("and Ctrl+Shift+R turns it left",
                  accelerators.Contains("Control, Shift+R") || accelerators.Contains("Control,Shift+R"));

            var items = window.PagesMenuItemsForTest();
            Check($"every operation has a home in the Pages menu ({string.Join(", ", items)})",
                  items.SequenceEqual(new[]
                  {
                      "PagesPaneItem", "RotateRightItem", "RotateLeftItem", "DeletePagesItem",
                      "MovePageEarlierItem", "MovePageLaterItem", "InsertBlankPageItem",
                      "InsertPagesFromFileItem", "ExtractPagesItem",
                  }));
        }

        // --- 2. The pane itself -------------------------------------------------
        {
            Check("the Pages pane starts shut", !tab.IsPagesPaneOpen && !view.IsPagesPaneShown);
            Check("  and nothing in it is in the tab order while it is",
                  !window.FocusableControlIds().Contains("PageTile"));

            window.TogglePagesPane();
            await Task.Delay(1200);
            Check("Pages opens it", tab.IsPagesPaneOpen && view.IsPagesPaneShown);
            Check($"  and its tiles join the tab order ({window.FocusableControlIds().Count(id => id == "PageTile")} of them)",
                  window.FocusableControlIds().Contains("PageTile"));
            window.OpenPagesMenuForTest();
            Check("  the menu item is ticked while it is open", window.PagesPaneItemIsCheckedForTest);
            Check("  every page has a tile, numbered from one",
                  tab.Thumbnails.Select(t => t.PageNumberLabel).SequenceEqual(["1", "2"]));
            Check($"  and a tile says which page it is out loud (“{tab.Thumbnails[1].AccessibleName}”)",
                  tab.Thumbnails[1].AccessibleName == Strings.PageThumbnailName(2)
                  && view.TileAccessibleNameForTest(1) == Strings.PageThumbnailName(2));
            Check($"  the tiles are drawn ({tab.Thumbnails.Count(t => t.Source is not null)} of {tab.Thumbnails.Count})",
                  tab.Thumbnails.All(t => t.Source is not null));

            // Reading mode hides the chrome, and the pane is chrome (#504).
            window.ToggleReadingMode();
            await Task.Delay(800);
            Check("reading mode hides the pane, chrome and all",
                  window.IsReadingMode && !view.IsPagesPaneShown && tab.IsPagesPaneOpen);
            Check("  so nothing of it is left in the tab order either",
                  !window.FocusableControlIds().Contains("PageTile"));
            window.ToggleReadingMode();
            await Task.Delay(800);
            Check("and leaving puts it back, because it was never closed",
                  !window.IsReadingMode && view.IsPagesPaneShown);

            tab.SelectedPageIndices = [0];
            Check($"selecting one tile says which page (“{tab.PagesPaneHeading}”)",
                  tab.PagesPaneHeading == Strings.PageSelected(1));
            Check("  and the grid's own selection follows the view model's",
                  view.PageTilesForTest.SelectedItems.OfType<PageThumbnail>().Select(t => t.Index).SequenceEqual([0]));
            tab.SelectedPageIndices = [0, 1];
            Check($"selecting both says how many (“{tab.PagesPaneHeading}”)",
                  tab.PagesPaneHeading == Strings.PagesSelected(2));
            tab.SelectedPageIndices = [];
        }

        // --- 3. Rotate ----------------------------------------------------------
        {
            var portrait = (tab.Pages[0].PointsWidth, tab.Pages[0].PointsHeight);
            Check("the first page arrives unrotated", tab.PageRotation(0) == 0);

            tab.SelectedPageIndices = [0];
            await tab.RotatePagesRightCommand.ExecuteAsync(null);
            Check("Rotate Right turns it a quarter turn clockwise", tab.PageRotation(0) == 1);
            Check("  the page's width and height swap with it",
                  Math.Abs(tab.Pages[0].PointsWidth - portrait.PointsHeight) < 0.5
                  && Math.Abs(tab.Pages[0].PointsHeight - portrait.PointsWidth) < 0.5);
            Check("  and so does its tile's, so the pane is not showing a portrait box",
                  Math.Abs(tab.Thumbnails[0].PointsWidth - portrait.PointsHeight) < 0.5);
            Check("  it makes the document unsaved", tab.HasUnsavedChanges);
            var said = await SaidAsync();
            Check($"  said out loud for Narrator (“{said}”)", said == Strings.PageTurnedRight);

            await tab.UndoCommand.ExecuteAsync(null);
            Check("undo turns it back", tab.PageRotation(0) == 0);
            Check("  and the page is its own size again",
                  Math.Abs(tab.Pages[0].PointsWidth - portrait.PointsWidth) < 0.5);
            await tab.RedoCommand.ExecuteAsync(null);
            Check("redo turns it again", tab.PageRotation(0) == 1);
            await tab.UndoCommand.ExecuteAsync(null);

            tab.SelectedPageIndices = [];
            await tab.RotatePagesLeftCommand.ExecuteAsync(null);
            Check("with nothing selected, Rotate Left turns the page you are looking at",
                  tab.PageRotation(0) == 3);
            await tab.UndoCommand.ExecuteAsync(null);

            tab.SelectedPageIndices = [0, 1];
            await tab.RotatePagesRightCommand.ExecuteAsync(null);
            Check("a selection turns together", tab.PageRotation(0) == 1 && tab.PageRotation(1) == 1);
            Check($"  and says how many ({await SaidAsync()})",
                  await SaidAsync() == Strings.PagesTurnedRight(2));
            await tab.UndoCommand.ExecuteAsync(null);
            Check("  and comes back together, in one step",
                  tab.PageRotation(0) == 0 && tab.PageRotation(1) == 0 && !tab.UndoCommand.CanExecute(null));
            tab.SelectedPageIndices = [];
        }

        // --- 4. A rotation survives a save and a reopen -------------------------
        {
            var saved = Path.Combine(scratch, "rotated.pdf");
            tab.SelectedPageIndices = [1];
            await tab.RotatePagesRightCommand.ExecuteAsync(null);
            await tab.SaveToPathForTestAsync(saved);
            tab.SelectedPageIndices = [];
            using var engine = new PdfiumEngine();
            using var reopened = engine.Open(saved);
            Check("a rotation is in the saved file", reopened.GetPageRotation(1) == 1);
            Check("  and only on the page that was turned", reopened.GetPageRotation(0) == 0);
        }

        // --- 5. Delete, and the undo that puts the page itself back -------------
        {
            await StartOverAsync(work);
            Check("the drawn square is on the first page", HasDrawnSquare(tab, 0, drawnCentre));

            tab.SelectedPageIndices = [0];
            await tab.DeletePagesCommand.ExecuteAsync(null);
            Check("deleting a page leaves the other one", tab.Pages.Count == 1 && tab.Thumbnails.Count == 1);
            Check("  the tiles are renumbered from one", tab.Thumbnails[0].PageNumberLabel == "1");
            Check("  the page indicator follows", tab.PageCount == 1 && tab.CurrentPage == 1);
            Check("  nothing is selected in the pane any more", !tab.HasPageSelection);
            Check("  the grid agrees, so a following command cannot act on a page that is gone",
                  view.PageTilesForTest.SelectedItems.Count == 0);
            Check($"  and it says Undo puts it back (“{await SaidAsync()}”)",
                  await SaidAsync() == Strings.PageDeleted);

            await tab.UndoCommand.ExecuteAsync(null);
            Check("undo puts the page back", tab.Pages.Count == 2 && tab.Thumbnails.Count == 2);
            // The whole point of megapdf_page_restore: what comes back is the page that went, not
            // a blank sheet of the same size.
            Check("  and it is the page that went, not a blank one", HasDrawnSquare(tab, 0, drawnCentre));
            await tab.RedoCommand.ExecuteAsync(null);
            Check("redo takes it off again", tab.Pages.Count == 1);

            // A PDF must have a page. The command is off rather than failing, and the rule is
            // still said out loud when the key is pressed on it.
            tab.SelectedPageIndices = [0];
            Check("Delete Page is off for the last page a document has",
                  !tab.DeletePagesCommand.CanExecute(null));
            tab.ShowLastPageRefusal();
            Check($"  with the rule said out loud, and the way round it (“{Shorten(tab.PageToolNotice)}”)",
                  tab.IsPageToolNoticeOpen && tab.PageToolNotice == Strings.CannotDeleteLastPage);
            Check("  which is the same sentence the engine's own refusal gets",
                  DocumentViewModel.DescribePageToolFailure(
                      new PageToolException(PageToolFailure.LastPage, "x")) == Strings.CannotDeleteLastPage);
            tab.IsPageToolNoticeOpen = false;
        }

        // --- 6. Reorder ---------------------------------------------------------
        {
            await StartOverAsync(work);
            // The first page is marked by turning it, so "the page moved" is an assertion about
            // that page rather than about a page count.
            tab.SelectedPageIndices = [0];
            await tab.RotatePagesRightCommand.ExecuteAsync(null);
            await tab.MovePageLaterCommand.ExecuteAsync(null);
            Check("Move Page Later moves the page, rotation and all",
                  tab.PageRotation(1) == 1 && tab.PageRotation(0) == 0);
            Check("  the pane's selection follows the page, not the index", tab.SelectedPageIndices is [1]);
            // Not the drawn square: the page was turned a moment ago, and every rectangle on a
            // turned page is reported in the rotated crop space (#439), so the square is no longer
            // where it was. What the tile owes is the turned page's shape.
            Check("  and its tile went with it, landscape and all",
                  Math.Abs(tab.Thumbnails[1].PointsWidth - tab.Pages[1].PointsWidth) < 0.5
                  && tab.Thumbnails[1].PointsWidth > tab.Thumbnails[1].PointsHeight
                  && tab.Thumbnails[0].PointsWidth < tab.Thumbnails[0].PointsHeight);
            Check($"  said out loud, with where it landed (“{await SaidAsync()}”)",
                  await SaidAsync() == Strings.PageMoved(1, 2));
            await tab.UndoCommand.ExecuteAsync(null);
            Check("undo moves it back", tab.PageRotation(0) == 1 && tab.SelectedPageIndices is [0]);
            Check("Move Page Earlier is off for the first page", !tab.MovePageEarlierCommand.CanExecute(null));
            tab.SelectedPageIndices = [1];
            Check("  and Move Page Later is off for the last", !tab.MovePageLaterCommand.CanExecute(null));

            // The drag's own arithmetic: a page dropped into the gap past the last tile lands
            // last, because it leaves its own place before it arrives.
            tab.SelectedPageIndices = [];
            await view.DropPageForTest(0, 2);
            Check("a drop past the last tile puts the page at the end", tab.PageRotation(1) == 1);
            await tab.UndoCommand.ExecuteAsync(null);
            Check("  and it is one undo step like any other reorder", tab.PageRotation(0) == 1);
        }

        // --- 7. A blank page ----------------------------------------------------
        {
            await StartOverAsync(work);
            var size = (tab.Pages[0].PointsWidth, tab.Pages[0].PointsHeight);
            tab.SelectedPageIndices = [0];
            await tab.InsertBlankPageCommand.ExecuteAsync(null);
            Check("a blank page goes in after the page you are on",
                  tab.Pages.Count == 3 && tab.Thumbnails.Count == 3);
            Check("  it is the size of the page it follows",
                  Math.Abs(tab.Pages[1].PointsWidth - size.PointsWidth) < 0.5
                  && Math.Abs(tab.Pages[1].PointsHeight - size.PointsHeight) < 0.5);
            Check("  and it is blank: the square is still on page 1 only",
                  HasDrawnSquare(tab, 0, drawnCentre) && !HasDrawnSquare(tab, 1, drawnCentre));
            Check($"  said out loud (“{await SaidAsync()}”)",
                  await SaidAsync() == Strings.BlankPageInserted(2));
            await tab.UndoCommand.ExecuteAsync(null);
            Check("undo takes it out again", tab.Pages.Count == 2 && tab.Thumbnails.Count == 2);
        }

        // --- 8. Combine ---------------------------------------------------------
        {
            await StartOverAsync(work);
            var added = await tab.ImportPagesAsync(other);
            Check("pages from another file are inserted", added && tab.Pages.Count == 4);
            Check("  with a tile each", tab.Thumbnails.Count == 4);
            Check($"  said out loud, naming the file (“{Shorten(await SaidAsync())}”)",
                  await SaidAsync() == Strings.PagesInsertedFromFile(Path.GetFileName(other)));
            Check("  and it is one undo step", tab.UndoCommand.CanExecute(null));
            await tab.UndoCommand.ExecuteAsync(null);
            Check("undo takes exactly the imported pages off", tab.Pages.Count == 2 && tab.Thumbnails.Count == 2);
            Check("  and leaves this document's own pages alone", HasDrawnSquare(tab, 0, drawnCentre));

            // A form document combined with itself: the field names clash, which is the case the
            // engine renames rather than letting two fields merge into one.
            await StartOverAsync(form);
            var centre = new PdfPoint(200, 792 - 610);
            var source = FieldAt(tab, 0, centre);
            Check($"the form document has a field to bring ({source?.Name})", source?.Name == "person");
            Check("a form document combined with itself doubles its pages",
                  await tab.ImportPagesAsync(form) && tab.Pages.Count == 2);
            Check("  the imported page came with its form field", FieldAt(tab, 1, centre) is not null);
            Check($"  renamed so the two never merge into one field ({FieldAt(tab, 1, centre)?.Name})",
                  FieldAt(tab, 1, centre)?.Name == "person_2");
            Check("  while this document's own field keeps its name", FieldAt(tab, 0, centre)?.Name == "person");
            await tab.UndoCommand.ExecuteAsync(null);
            Check("  and undo leaves one page with one field",
                  tab.Pages.Count == 1 && FieldAt(tab, 0, centre)?.Name == "person");
        }

        // --- 9. The refusal the engine makes on purpose (MEGAPDF_ERR_FIELDS) ----
        //
        // A page whose form fields hang off a /Parent field cannot be copied by a PDFium without
        // patch 0033: the copy would name an object that is not the widget's parent, and the form
        // would quietly lose its field names. About 0.8% of a real corpus, and refused rather
        // than corrupted on purpose. What is asserted unconditionally is what the interface owes:
        // when it is refused, the document is untouched and the person is told what happened and
        // what still works.
        {
            await StartOverAsync(work);
            var imported = await tab.ImportPagesAsync(hierarchy);
            Check($"a hierarchy with no name clash {(imported ? "imports" : "is refused")} on this build's PDFium",
                  imported
                      ? tab.Pages.Count == 3
                      : tab.Pages.Count == 2 && !tab.HasUnsavedChanges && !tab.UndoCommand.CanExecute(null));
            if (imported)
                await tab.UndoCommand.ExecuteAsync(null);

            // Certain on either build: a widget whose name lives on its /Parent has no /T of its
            // own to rename, so a hierarchy whose top-level name is already taken here is refused
            // whole and nothing is changed.
            await StartOverAsync(form);
            var refused = !await tab.ImportPagesAsync(hierarchy);
            Check("a form field the copy cannot carry refuses the whole import", refused);
            Check("  and leaves the document exactly as it was",
                  tab.Pages.Count == 1 && tab.Thumbnails.Count == 1
                  && !tab.HasUnsavedChanges && !tab.UndoCommand.CanExecute(null));
            Check($"  saying what happened and what still works (“{Shorten(tab.PageToolNotice)}”)",
                  tab.IsPageToolNoticeOpen && tab.PageToolNotice == Strings.PagesRefusedFormFields);
            Check("  out loud as well, because a bar at the foot of the page is easy to miss",
                  await SaidAsync() == Strings.PagesRefusedFormFields);
            tab.IsPageToolNoticeOpen = false;

            // Deterministic whatever this build's PDFium does: the sentence a person would read.
            Check("the field-hierarchy refusal has its own wording",
                  DocumentViewModel.DescribePageToolFailure(
                      new PageToolException(PageToolFailure.FieldHierarchy, "x")) == Strings.PagesRefusedFormFields);
            Check("  and it is not the generic could-not-edit line",
                  Strings.PagesRefusedFormFields != Strings.CouldNotEditTitle);
            Check("a restricted document's page tools say the owner password would unlock them",
                  DocumentViewModel.DescribePageToolFailure(
                      new PageToolException(PageToolFailure.Restricted, "x")) == Strings.PageToolsRestricted);
            Check("and a file that could not be read says so rather than naming an errno",
                  DocumentViewModel.DescribePageToolFailure(
                      new PageToolException(PageToolFailure.File, "x")) == Strings.PagesFileProblem);
        }

        // --- 10. There is no layout refusal in this path at all -----------------
        //
        // #556 established it and this holds it: no page operation rewrites a content stream — a
        // rotation is /Rotate and nothing else — so the #118 guard is never consulted and
        // MEGAPDF_ERR_LAYOUT cannot be the answer. Which is why there is no dialog for it here,
        // and why inventing one would be a dialog for a state that cannot happen.
        {
            using var engine = new PdfiumEngine();
            using var document = engine.Open(work);
            IPageEditOperation[] operations =
            [
                new RotatePagesOperation(document, [0], 1),
                new DeletePagesOperation(document, [1]),
                new MovePageOperation(document, 0, 1),
                new InsertBlankPageOperation(document, 1, 300, 400),
                new ImportPagesOperation(document, other, null, null, 0),
            ];
            Check("no page operation asks the #118 layout guard anything, so none can be refused by it",
                  operations.All(op => !PageRegenerationWarnings.RegeneratesUnjudged(op)));
            Check("  and every one of them is a page-structure operation the assemble bit gates",
                  operations.All(op => op is IPageStructureOperation)
                  && operations.All(MegaPDF.Core.Services.DocumentCapabilities.Unprotected.Allows));
        }

        // --- 11. Extract --------------------------------------------------------
        {
            await StartOverAsync(work);
            var extracted = Path.Combine(scratch, "extracted.pdf");
            tab.SelectedPageIndices = [1];
            var wrote = await tab.ExtractPagesToPathAsync(extracted);
            Check("the selected page is written to a new file", wrote && File.Exists(extracted));
            Check("  the document itself is untouched",
                  tab.Pages.Count == 2 && !tab.HasUnsavedChanges);
            Check("  and there is nothing to undo, because nothing was changed",
                  !tab.UndoCommand.CanExecute(null));
            Check($"  said out loud (“{Shorten(await SaidAsync())}”)",
                  await SaidAsync() == Strings.PageSavedAs(Path.GetFileName(extracted)));
            using (var engine = new PdfiumEngine())
            using (var copy = engine.Open(extracted))
                Check("  the new file holds exactly the pages that were selected", copy.PageCount == 1);
            tab.SelectedPageIndices = [];

            Check("a run of pages is suggested as a range",
                  DocumentViewModel.SuggestExtractedFileName("Form.pdf", [1, 2])
                  == $"Form ({Strings.ExtractedPageRangeName(2, 3)}).pdf");
            Check("one page is suggested by its number",
                  DocumentViewModel.SuggestExtractedFileName("Form.pdf", [4])
                  == $"Form ({Strings.ExtractedOnePageName(5)}).pdf");
            Check("and a scattered selection by how many there are",
                  DocumentViewModel.SuggestExtractedFileName("Form.pdf", [0, 2])
                  == $"Form ({Strings.ExtractedPageCountName(2)}).pdf");
        }

        // --- 12. The app's index-keyed state follows the renumbering ------------
        {
            await StartOverAsync(work);
            await tab.ImportPagesAsync(other, insertAt: 0);
            Check("a combine can insert before the first page", tab.Pages.Count == 4);
            await tab.SearchAsync("Page 2");
            var found = tab.SearchMatchCount;
            Check($"there is something to find in the document ({found} hit(s))", found > 0);
            tab.SelectedPageIndices = [0];
            await tab.DeletePagesCommand.ExecuteAsync(null);
            Check("deleting a page keeps the hits that are still there", tab.SearchMatchCount == found);
            Check("  and the page indicator counts the pages that are left",
                  tab.PageCount == 3 && tab.CurrentPage <= 3);
            tab.ClearSearch();
            tab.SelectedPageIndices = [];
        }

        // --- 13. The recovery journal replays every page operation --------------
        //
        // Front to back, each entry carrying the page index the document had when it was made,
        // which is why no entry needs a renumbering term. The insert entry is the one that catches
        // a replayer written the obvious way: its index may be the page count itself, which is not
        // a page to load.
        {
            using var engine = new PdfiumEngine();
            using var replayed = engine.Open(work);
            JournalEntry[] entries =
            [
                new PagesRotateEntry(0, [0], 1),
                new PageInsertBlankEntry(2, 300, 400),   // appends: the index is the page count
                new PageMoveEntry(2, 0),
                new PagesDeleteEntry(0, [0]),
            ];
            var applied = JournalReplayer.Replay(replayed, entries, work);
            Check($"every page-operation journal entry replays ({applied} of {entries.Length})",
                  applied == entries.Length);
            Check("  and lands the document where it was left", replayed.PageCount == 2);
            Check("  with the rotation the first entry made", replayed.GetPageRotation(0) == 1);

            // The undo of a delete: a journal cannot carry a page, so contract 10 replays it by
            // importing that page back out of the file on disk.
            JournalEntry[] restore =
            [
                new PagesDeleteEntry(0, [0]),
                new PagesRestoreEntry(0, [0]),
            ];
            using var fresh = engine.Open(work);
            Check("a delete and its undo both replay", JournalReplayer.Replay(fresh, restore, work) == 2);
            Check("  leaving the page count it started with", fresh.PageCount == 2);
            using var noFile = engine.Open(work);
            Check("with no file to read the page back out of, the restore is skipped, not fatal",
                  JournalReplayer.Replay(noFile, restore, null) == 1);
        }

        // --- 14. Every operation is one undo step, all the way back -------------
        {
            await StartOverAsync(work);
            tab.SelectedPageIndices = [0];
            await tab.RotatePagesRightCommand.ExecuteAsync(null);
            await tab.InsertBlankPageCommand.ExecuteAsync(null);
            tab.SelectedPageIndices = [2];
            await tab.MovePageEarlierCommand.ExecuteAsync(null);
            await tab.ImportPagesAsync(other);
            tab.SelectedPageIndices = [0];
            await tab.DeletePagesCommand.ExecuteAsync(null);
            Check($"five page operations in a row ({tab.Pages.Count} pages)", tab.Pages.Count == 4);
            var steps = 0;
            while (tab.UndoCommand.CanExecute(null) && steps < 10)
            {
                await tab.UndoCommand.ExecuteAsync(null);
                steps++;
            }
            Check($"each is one undo step ({steps})", steps == 5);
            Check("and undoing them all is the document it opened",
                  tab.Pages.Count == 2 && tab.Thumbnails.Count == 2 && tab.PageRotation(0) == 0
                  && HasDrawnSquare(tab, 0, drawnCentre));
            Check("  with the pane and the page list still agreeing about how many pages there are",
                  tab.Thumbnails.Count == tab.Pages.Count
                  && tab.Thumbnails.Select(t => t.Index).SequenceEqual(Enumerable.Range(0, tab.Pages.Count)));
        }

        // --- 15. A structure operation reports in the strip, not on a page (#145) ----
        //
        // DoEditAsync used to report every page operation with a page-scoped spinner. A structure
        // operation's own PageIndex is only the first page it touched — this method already knows
        // that, which is why it hands ApplyPageShiftsAsync's renumbering -1 rather than that index
        // — so the spinner sat where a combine's insertion point usually is (off-screen) or, for a
        // delete, on the very PageViewModel ApplyPageShifts is about to dispose. It reported
        // nowhere at all: the same defect #563 found and fixed on the Mac and Linux leg. It now
        // reports in the strip under the toolbar, named for what it is doing.
        {
            await StartOverAsync(work);
            using var recorder = new BusyRecorder(tab.Busy);

            tab.SelectedPageIndices = [1];
            await tab.RotatePagesRightCommand.ExecuteAsync(null);
            Check("a rotate reports in the strip, named for turning pages",
                  recorder.Saw(f => f.Scope == BusyScope.Document && f.Label == Strings.BusyTurningPages));
            Check("  and never as a spinner on a page",
                  !recorder.Saw(f => f.Scope == BusyScope.Page));
            await tab.UndoCommand.ExecuteAsync(null);

            recorder.Clear();
            tab.SelectedPageIndices = [1];
            await tab.MovePageEarlierCommand.ExecuteAsync(null);
            Check("a move reports in the strip, named for moving the page",
                  recorder.Saw(f => f.Scope == BusyScope.Document && f.Label == Strings.BusyMovingPage));
            await tab.UndoCommand.ExecuteAsync(null);

            recorder.Clear();
            tab.SelectedPageIndices = [0];
            await tab.InsertBlankPageCommand.ExecuteAsync(null);
            Check("an insert reports in the strip, named for inserting a page",
                  recorder.Saw(f => f.Scope == BusyScope.Document && f.Label == Strings.BusyInsertingPage));
            await tab.UndoCommand.ExecuteAsync(null);

            // The one that reported nowhere at all, rather than merely out of sight: a delete's
            // own page index is a page the delete removes, so the old page-scoped spinner would
            // have sat on a tile that is gone by the time anything could draw it.
            recorder.Clear();
            tab.SelectedPageIndices = [1];
            await tab.DeletePagesCommand.ExecuteAsync(null);
            Check("a delete reports in the strip, named for deleting pages",
                  recorder.Saw(f => f.Scope == BusyScope.Document && f.Label == Strings.BusyDeletingPages));
            Check("  and not on the page it is deleting",
                  !recorder.Saw(f => f.Scope == BusyScope.Page && f.PageIndex == 1));
            await tab.UndoCommand.ExecuteAsync(null);

            recorder.Clear();
            await tab.ImportPagesAsync(other);
            Check("a combine reports in the strip, named for combining pages",
                  recorder.Saw(f => f.Scope == BusyScope.Document && f.Label == Strings.BusyCombiningPages));
            await tab.UndoCommand.ExecuteAsync(null);

            // The contrast that keeps the fix honest: an ordinary field edit changes one page and
            // still reports on that page, exactly as before.
            var centre = new PdfPoint(200, 792 - 610);
            await StartOverAsync(form);
            using var recorderOnForm = new BusyRecorder(tab.Busy);
            var field = FieldAt(tab, 0, centre);
            Check("the form field this contrast needs is there", field is not null);
            if (field is { } textField)
            {
                await tab.ApplyFormTextAsync(0, textField, "Changed");
                Check("while an ordinary field edit still reports on that page",
                      recorderOnForm.Saw(f => f.Scope == BusyScope.Page && f.PageIndex == 0));
                Check("  and never in the document strip",
                      !recorderOnForm.Saw(f => f.Scope == BusyScope.Document));
                await tab.UndoCommand.ExecuteAsync(null);
            }
            await StartOverAsync(work);
        }
    }

    /// <summary>
    /// Whiteout move/resize chrome (#3) and text box ergonomics — persona-simple sizes and
    /// Shift+Enter multi-line (#4) — in a real window, for the same reason `pages` needs one
    /// (#462: no headless platform, no CI desktop session).
    ///
    /// Two things proved here are assertions rather than assumptions, because each is a rule
    /// that can regress while the window still looks perfectly right:
    ///
    ///   1. A whiteout's object index changes on every move (there is no native "move in
    ///      place" for page content — <c>MoveWhiteoutOperation</c> detaches and re-appends).
    ///      Deleting one right after moving it is the sharpest check of the chrome's own
    ///      re-anchoring: get it wrong and the wrong rectangle disappears, or the delete
    ///      throws reaching for an index that no longer exists.
    ///   2. A two-line note is two independently selectable objects, and undoing it is one
    ///      step that removes both — not "undo, undo".
    ///
    /// Two honest limits, stated here rather than discovered later:
    ///
    ///   * WinUI cannot synthesize a pointer press or a manipulation gesture, so selecting is
    ///     driven through <c>RoutePageActivationAsync</c> (via <c>ActivatePageForTest</c>) —
    ///     the same route a tap, Enter or Space takes — and a completed drag is driven by
    ///     setting the chrome's on-screen rect the way one would have left it
    ///     (<c>DragSelectionForTest</c>), the same honest limit <c>DropPageForTest</c>
    ///     documents for a page reorder's drag. Neither can exercise the resize handle's own
    ///     live aspect-lock math, which only runs mid-gesture against a real manipulation
    ///     event — free-form vs. proportional resize is a by-hand gate.
    ///   * Nor can it synthesize a key press, so Shift+Enter's own effect (inserting a literal
    ///     newline at the caret) is not driven through a key event either — the note's text is
    ///     set directly on the open editor with the newline already in it
    ///     (<c>ActiveEditorTextForTest</c>), proving what committing a such a string does
    ///     (<c>AddTextBoxesOperation</c>, one object per line) rather than the key binding
    ///     that produces the string in the first place.
    ///
    /// Everything this writes lives in its own scratch directory and its own tab, and both are
    /// gone before it returns: the tab the fixture was opened in is left exactly as found.
    /// </summary>
    private static async Task<bool> CheckWhiteoutAndTextAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } fixtureTab
            || fixtureTab.DocumentPath is not { } fixturePath)
        {
            Console.Error.WriteLine("--screenshot-state whiteout-text needs a document.");
            return false;
        }

        var passed = 0;
        var failed = 0;
        void Check(string what, bool ok)
        {
            Console.Error.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (ok)
                passed++;
            else
                failed++;
        }

        var scratch = Path.Combine(Path.GetTempPath(), "megapdf-selftest-whiteout-text");
        Directory.CreateDirectory(scratch);
        try
        {
            await RunWhiteoutAndTextChecksAsync(window, fixtureTab, fixturePath, scratch, Check);
        }
        catch (Exception ex)
        {
            // A throw here is a FAIL with a stack, not a window left standing (see
            // CheckPageToolsAsync's own remark — the same failure mode either check learnt).
            Console.Error.WriteLine($"FAIL: the whiteout/text check threw: {ex}");
            failed++;
        }
        finally
        {
            foreach (var tab in window.Shell.Documents.Where(t => !ReferenceEquals(t, fixtureTab)).ToList())
            {
                tab.HasUnsavedChanges = false;   // never ask about scratch (#145 D5's dialog)
                await window.CloseTabAsync(tab);
            }
            try { Directory.Delete(scratch, recursive: true); } catch { /* best-effort scratch cleanup */ }
        }

        Console.Error.WriteLine($"whiteout-text: {passed} passed, {failed} failed ({passed + failed} checks)");
        return failed == 0;
    }

    private static async Task RunWhiteoutAndTextChecksAsync(
        MainWindow window, DocumentViewModel fixtureTab, string fixturePath, string scratch,
        Action<string, bool> Check)
    {
        // A copy, not the fixture path itself — opening a path a tab already holds activates
        // that tab instead of opening another (the same reason `pages` and `close-tabs` copy).
        var work = Path.Combine(scratch, "work.pdf");
        File.Copy(fixturePath, work, overwrite: true);

        var tab = await window.Shell.OpenInTabAsync(work);
        await Task.Delay(1500);
        var view = tab.View;
        Check("a working tab opened on the fixture", tab.IsDocumentOpen && view is not null);
        if (view is null)
            return;

        static bool Close(PdfRect a, PdfRect b, double tol = 1.5) =>
            Math.Abs(a.X - b.X) < tol && Math.Abs(a.Y - b.Y) < tol
            && Math.Abs(a.Width - b.Width) < tol && Math.Abs(a.Height - b.Height) < tol;

        // --- #3: a whiteout selects for move and resize, not remove-only -------------------

        var placedAt = new PdfRect(60, 60, 80, 40);
        await tab.AddWhiteoutAsync(0, placedAt);
        await Task.Delay(300);
        Check("a whiteout can still be placed",
              tab.HitTestPage(0, placedAt.Center).Kind == PageHitKind.Whiteout);

        Check("clicking it selects it", await view.ActivatePageForTest(0, placedAt.Center));
        Check("  offering move, not remove-only chrome", view.SelectionIsMovableForTest);
        Check("  and a resize handle too", view.SelectionIsResizableForTest);

        var movedTo = new PdfRect(placedAt.X + 90, placedAt.Y + 50, placedAt.Width + 30, placedAt.Height - 10);
        var dragged = await view.DragSelectionForTest(movedTo);
        await Task.Delay(300); // the move re-renders the page and re-selects on the new object index
        Check("dragging and resizing it in one gesture lands",
              dragged && view.SelectionBoundsForTest is { } landedBounds && Close(landedBounds, movedTo));
        Check("  the old spot is no longer covered",
              tab.HitTestPage(0, placedAt.Center).Kind != PageHitKind.Whiteout);
        Check("  and it is one undo step", tab.UndoCommand.CanExecute(null));

        await tab.UndoCommand.ExecuteAsync(null);
        await Task.Delay(300);
        Check("undo restores the prior rect, in one step",
              tab.HitTestPage(0, placedAt.Center).Kind == PageHitKind.Whiteout
              && tab.HitTestPage(0, movedTo.Center).Kind != PageHitKind.Whiteout);

        await tab.RedoCommand.ExecuteAsync(null);
        await Task.Delay(300);
        Check("redo re-applies the move",
              tab.HitTestPage(0, movedTo.Center).Kind == PageHitKind.Whiteout
              && tab.HitTestPage(0, placedAt.Center).Kind != PageHitKind.Whiteout);

        // Persistence: what a move actually wrote survives a save and a reopen.
        var savedCover = Path.Combine(scratch, "cover.pdf");
        await tab.SaveToPathForTestAsync(savedCover);
        using (var engine = new PdfiumEngine())
        using (var reopened = engine.Open(savedCover))
        {
            using var page = reopened.GetPage(0);
            Check("the moved cover is in the saved file",
                  page.GetWhiteouts().Any(w => Close(w.Bounds, movedTo)));
        }

        // The undo/redo above ran under the chrome the drag left showing — a click on an
        // already-selected item deselects it rather than reselecting it (pre-existing chrome
        // behaviour, shared by every selectable kind, not specific to a whiteout), and neither
        // Undo nor Redo itself clears a selection made before them. One throwaway click clears
        // whatever that left before the real one below actually selects.
        await view.ActivatePageForTest(0, movedTo.Center);

        // The sharpest check of the chrome's own re-anchoring (#3): a whiteout's object index
        // is not the one the selection started with any more, after a move. Deleting it right
        // here is what would go wrong first if that re-anchoring were broken.
        Check("re-selecting the moved cover", await view.ActivatePageForTest(0, movedTo.Center));
        Check("deleting it after a move takes off the right rectangle",
              await view.RemoveSelectionForTest()
              && tab.HitTestPage(0, movedTo.Center).Kind != PageHitKind.Whiteout);
        await tab.UndoCommand.ExecuteAsync(null);
        await Task.Delay(300);
        Check("  and undo puts that same one back",
              tab.HitTestPage(0, movedTo.Center).Kind == PageHitKind.Whiteout);

        // Clean up: back to nothing added, so the text checks below start from a known page.
        while (tab.UndoCommand.CanExecute(null))
            await tab.UndoCommand.ExecuteAsync(null);
        await Task.Delay(300);

        // --- #4: persona-simple size chips and Shift+Enter multi-line -----------------------

        var before = tab.TextBoxesOn(0).Count;
        var placePoint = new PdfPoint(72, 300);
        tab.StartTextBoxMode();
        Check("Add text arms the new-text editor",
              await view.ActivatePageForTest(0, placePoint) && view.HasActiveEditorForTest);
        Check("  offering the S/M/L size chips", view.SizeChipsShownForTest);

        Check("picking Large writes the same size the toolbar's own picker would",
              await view.ClickSizeChipForTest(DocumentViewModel.TextSizeLarge)
              && window.SizePickerValueForTest is { } shown && Math.Abs(shown - DocumentViewModel.TextSizeLarge) < 0.01);

        // Shift+Enter cannot be synthesized (see this method's own remark) — the text is set
        // with the newline already in it, exactly what a real Shift+Enter would have produced.
        view.ActiveEditorTextForTest = "First line\nSecond line";
        await view.CommitActiveEditorForTest();
        await Task.Delay(500);

        var boxes = tab.TextBoxesOn(0);
        Check($"a two-line note becomes two objects ({boxes.Count - before} new)", boxes.Count == before + 2);
        var added = boxes.Skip(before).ToList();
        Check("  both at the chosen (Large) size",
              added.Count == 2 && added.All(b => Math.Abs(b.FontSize - DocumentViewModel.TextSizeLarge) < 0.01));
        Check("  the two lines are not on top of each other",
              added.Count == 2 && Math.Abs(added[0].Bounds.Y - added[1].Bounds.Y) > 1);

        if (added.Count == 2)
        {
            Check("each line is individually selectable — the first",
                  await view.ActivatePageForTest(0, added[0].Bounds.Center)
                  && view.SelectionBoundsForTest is { } firstSel && Close(firstSel, added[0].Bounds));
            // A click while something is already selected deselects it first (pre-existing
            // chrome behaviour, shared by every selectable kind) — a second click on the
            // second line is what actually selects it, the same two taps a person would need.
            await view.ActivatePageForTest(0, added[1].Bounds.Center);
            Check("  and the second, separately",
                  await view.ActivatePageForTest(0, added[1].Bounds.Center)
                  && view.SelectionBoundsForTest is { } secondSel && Close(secondSel, added[1].Bounds));
        }

        Check("undo removes the whole note as one step",
              tab.UndoCommand.CanExecute(null));
        await tab.UndoCommand.ExecuteAsync(null);
        await Task.Delay(300);
        Check("  both lines gone at once", tab.TextBoxesOn(0).Count == before);

        await tab.RedoCommand.ExecuteAsync(null);
        await Task.Delay(300);
        Check("redo brings both back together", tab.TextBoxesOn(0).Count == before + 2);

        // Existing added text still restyles in place rather than growing a second line
        // (scoped out of #4 — see AddTextBoxesOperation's own remark): the chips apply to an
        // existing note's size, and nothing here should turn one line into two after the fact.
        if (tab.TextBoxesOn(0).Skip(before).FirstOrDefault() is { } existing)
        {
            var beforeEdit = tab.TextBoxesOn(0).Count;
            Check("double-clicking an existing box opens its editor",
                  await view.EditTextBoxForTest(0, existing.Bounds.Center) && view.HasActiveEditorForTest);
            Check("  still offering the size chips (#4)", view.SizeChipsShownForTest);

            await view.ClickSizeChipForTest(DocumentViewModel.TextSizeSmall);
            await view.CommitActiveEditorForTest();
            await Task.Delay(400);
            var afterEdit = tab.TextBoxesOn(0);
            Check("  restyles the one box in place at the new size, rather than growing a second line",
                  afterEdit.Count == beforeEdit
                  && afterEdit.Any(b => Math.Abs(b.FontSize - DocumentViewModel.TextSizeSmall) < 0.01));
        }

        // Clean up: undo everything this check did, so the fixture tab is left as it opened.
        while (tab.UndoCommand.CanExecute(null))
            await tab.UndoCommand.ExecuteAsync(null);
        await Task.Delay(300);
    }

    /// <summary>
    /// Progress, Stop, and the busy strip's own controls, in a real window (#145 P2).
    ///
    /// Which operation gets which was measured, not reasoned about, against the synthetic large
    /// fixtures <c>tools/gen_large_fixtures.py</c> builds (no corpus document is involved; see
    /// <c>ImageShrinker.Shrink</c>'s and <c>DocumentViewModel.SearchAsync</c>'s own remarks for
    /// the numbers). Shrink and search count themselves and can be stopped, because both are
    /// per-item loops and nothing is at stake in abandoning them. Extract gets a Stop only — the
    /// engine takes a token but there is one call, so no honest count. Save gets neither: there
    /// is no honest denominator and a half-saved file is not a thing to leave behind. Combine is
    /// covered by the `pages` state's own reporting-defect section, because it is one engine call
    /// with no interior to check a flag in — there is nothing here for it to stop.
    ///
    /// Cancelling from the outside, not from inside a progress callback: <c>Busy.Begin</c> runs
    /// synchronously before each operation's first <c>await</c>, so the newest operation is
    /// already on the busy state by the time the call that started it returns control here, and
    /// <c>RequestCancel</c> reaches it deterministically — no wait, no margin, no dependence on
    /// how many pages or pictures a loop gets through before the flag is read.
    /// </summary>
    private static async Task<bool> CheckProgressAndCancelAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } fixtureTab
            || fixtureTab.DocumentPath is not { } fixturePath)
        {
            Console.Error.WriteLine("--screenshot-state progress needs a document.");
            return false;
        }

        var passed = 0;
        var failed = 0;
        void Check(string what, bool ok)
        {
            Console.Error.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (ok)
                passed++;
            else
                failed++;
        }

        var scratch = Path.Combine(Path.GetTempPath(), "megapdf-selftest-progress");
        Directory.CreateDirectory(scratch);
        try
        {
            await RunProgressChecksAsync(window, fixtureTab, fixturePath, scratch, Check);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: the progress-and-cancel check threw: {ex}");
            failed++;
        }
        finally
        {
            foreach (var tab in window.Shell.Documents.Where(t => !ReferenceEquals(t, fixtureTab)).ToList())
            {
                tab.HasUnsavedChanges = false;   // never ask about scratch (#145 D5's dialog)
                await window.CloseTabAsync(tab);
            }
            fixtureTab.SelectedPageIndices = [];
            try { Directory.Delete(scratch, recursive: true); } catch { /* best-effort scratch cleanup */ }
        }

        Console.Error.WriteLine($"progress: {passed} passed, {failed} failed ({passed + failed} checks)");
        return failed == 0;
    }

    private static async Task RunProgressChecksAsync(
        MainWindow window, DocumentViewModel fixtureTab, string fixturePath, string scratch,
        Action<string, bool> Check)
    {
        var work = Path.Combine(scratch, "work.pdf");
        File.Copy(fixturePath, work, overwrite: true);
        var tab = await window.Shell.OpenInTabAsync(work);
        await Task.Delay(1500);
        Check("a working tab opened", tab.IsDocumentOpen && tab.Pages.Count == 2);
        if (!tab.IsDocumentOpen)
            return;

        // --- search counts the pages it has walked, and can be stopped -------------------
        {
            using var recorder = new BusyRecorder(tab.Busy);
            await tab.SearchAsync("Page 2");
            var baseline = tab.SearchMatchCount;
            Check($"a search found something to begin with ({baseline} hit(s))", baseline > 0);
            Check("it counts itself against the page count",
                  recorder.Saw(f => f.HasProgress && f.Total == tab.Pages.Count));
            Check("it reaches the last page",
                  recorder.Saw(f => f.Done == tab.Pages.Count && f.Total == tab.Pages.Count));
            Check($"and says where it is in words (\"{Strings.BusyPageOfPages(1, tab.Pages.Count)}\")",
                  recorder.Saw(f => f.Text == Strings.BusyPageOfPages(1, tab.Pages.Count)));
            Check("a search offers Stop while it runs",
                  recorder.Saw(f => f.CanCancel && f.Label == Strings.BusySearching));
            Check("and offers nothing once it is over", !tab.Busy.CanCancel);
        }

        // Stopped from inside its own page loop, at the first page it finishes: no wait, and no
        // dependence on how fast a two-page document's search finishes — a real
        // `windows-ui-selftest` run hit exactly that race the first time this used `tab.Busy`
        // directly (Stop requested from the outside, "instantly" but still after the whole
        // two-page search had already finished on a fast runner). WatchableDocument's own remark
        // says why a separate, unbound view model is what makes this deterministic.
        {
            using var watchVm = WatchableDocument(window, window.Shell);
            await watchVm.OpenDocumentAsync(work);
            using var watchRecorder = new BusyRecorder(watchVm.Busy);
            await watchVm.SearchAsync("Page 2");
            var found = watchVm.SearchMatchCount;
            Check($"a search found something to begin with, to be stopped below ({found} hit(s))", found > 0);

            watchRecorder.Clear();
            watchRecorder.StopWhen(f => f.Label == Strings.BusySearching && f.Done == 1);
            string? announced = null;
            void OnAnnounced(string said) => announced = said;
            watchVm.Announced += OnAnnounced;
            await watchVm.SearchAsync("Page 2");
            watchVm.Announced -= OnAnnounced;
            Check($"a search stopped part-way says so (\"{announced}\")", announced == Strings.SearchCancelled);
            Check("and stops where it was, not at the end",
                  !watchRecorder.Saw(f => f.Done == watchVm.Pages.Count && f.Total == watchVm.Pages.Count));
            Check("the matches it already had are still there", watchVm.SearchMatchCount == found);
            Check("nothing is left running once it is stopped", !watchVm.Busy.IsWorking && !watchVm.Busy.CanCancel);

            // A *superseded* search (a newer term) is a different thing from a stopped one: the
            // newer term owns the result from then on, so it does clear the matches.
            await watchVm.SearchAsync("thereisnosuchwordinthisdocument");
            Check("while a search for something absent does empty the matches", watchVm.SearchMatchCount == 0);

            // Not a tab, so CloseTabAsync never ends this view model's journal session for it —
            // done by hand, the same way CloseTabAsync does, so this check does not leave a
            // journal behind for work.pdf that the scratch cleanup below cannot see.
            watchVm.EndJournalSession();
        }

        // --- extract can be stopped, and leaves nothing behind when it is -----------------
        {
            var extracted = Path.Combine(scratch, "extracted.pdf");
            using var watchVm = WatchableDocument(window, window.Shell);
            await watchVm.OpenDocumentAsync(work);
            watchVm.SelectedPageIndices = [0];

            // Stopped the instant Stop is offered — deterministic for the same reason search's
            // is above: Busy.Begin publishes synchronously on whichever thread calls it when the
            // view model was built with no captured context, and this operation's very first
            // publish is the one that turns CanCancel on.
            using var watchRecorder = new BusyRecorder(watchVm.Busy);
            watchRecorder.StopWhen(f => f.Label == Strings.BusyExtractingPages && f.CanCancel);
            string? announced = null;
            void OnAnnounced(string said) => announced = said;
            watchVm.Announced += OnAnnounced;
            var saved = await watchVm.ExtractPagesToPathAsync(extracted);
            watchVm.Announced -= OnAnnounced;
            Check("extract offers Stop while it runs",
                  watchRecorder.Saw(f => f.CanCancel && f.Label == Strings.BusyExtractingPages));
            Check("an extract that was stopped does not claim to have saved", !saved);
            Check($"it says it was stopped (\"{announced}\")", announced == Strings.WorkStopped);
            // The one that matters: the engine's contract is nothing left at the destination, and
            // this is the check that holds it to it. A stopped extract that left a truncated or
            // half-written file where the pages were going would be worse than no Stop at all.
            Check("and leaves no file where the pages were going", !File.Exists(extracted));
            Check("the document itself is untouched", watchVm.Pages.Count == 2 && !watchVm.HasUnsavedChanges);

            // And the same extract, unstopped, still works — so Stop did not break the path it
            // was added to.
            watchRecorder.Clear();
            saved = await watchVm.ExtractPagesToPathAsync(extracted);
            Check("while an extract nobody stops writes its file", saved && File.Exists(extracted));

            // Not a tab, so CloseTabAsync never ends this view model's journal session for it —
            // done by hand, the same way CloseTabAsync does.
            watchVm.EndJournalSession();
        }

        // --- shrink counts the images it has considered, and can be stopped --------------
        //
        // ReplaceOversizedImagesAsync is exercised directly, on a copy of its own, the way
        // ShrinkForEmailAsync itself calls it: what is under test is the loop and the busy state
        // it drives, and a file picker cannot be driven headless.
        {
            var imagesPdf = Path.Combine(scratch, "two-images.pdf");
            File.WriteAllBytes(imagesPdf, TwoImagesPdf());
            using var engine = new PdfiumEngine();
            using var watchVm = WatchableDocument(window, window.Shell);

            using (var copy = engine.Open(imagesPdf))
            {
                using var recorder = new BusyRecorder(tab.Busy);
                Check($"there are pictures to walk ({copy.GetImages().Count})", copy.GetImages().Count == 2);
                await tab.ReplaceOversizedImagesAsync(copy);
                Check("it counts itself against the picture count",
                      recorder.Saw(f => f.HasProgress && f.Total == 2));
                Check("it reaches the last picture",
                      recorder.Saw(f => f.Done == 2 && f.Total == 2));
                Check($"and says where it is in words (\"{Strings.BusyPictureOfPictures(2, 2)}\")",
                      recorder.Saw(f => f.Text == Strings.BusyPictureOfPictures(2, 2)));
                Check("shrink offers Stop while it runs",
                      recorder.Saw(f => f.CanCancel && f.Label == Strings.BusyShrinking));
                Check("and offers nothing once it is over", !tab.Busy.CanCancel);
            }

            // Stopped from inside its own picture loop, at the first picture it finishes: the
            // same deterministic route as search, needed for the same reason — two tiny pictures
            // can finish before an outside "start it, then cancel" call ever lands.
            using (var copy = engine.Open(imagesPdf))
            {
                using var watchRecorder = new BusyRecorder(watchVm.Busy);
                watchRecorder.StopWhen(f => f.Label == Strings.BusyShrinking && f.Done == 1);
                Exception? thrown = null;
                try { await watchVm.ReplaceOversizedImagesAsync(copy); }
                catch (Exception ex) { thrown = ex; }
                Check("Stop is still offered right up to when it is used",
                      watchRecorder.Saw(f => f.CanCancel && f.Label == Strings.BusyShrinking));
                Check("a stopped shrink throws rather than pretending to finish",
                      thrown is OperationCanceledException);
                Check("and stops where it was, not at the end",
                      !watchRecorder.Saw(f => f.Done == 2 && f.Total == 2));
                Check("and nothing is left running", !watchVm.Busy.IsWorking && !watchVm.Busy.CanCancel);
            }
            // Shrink works on its own copy, opened separately above: stopping it cannot have
            // touched the document this tab has open. Asserted rather than assumed, because the
            // day that stops being true is the day Stop starts destroying pictures.
            Check("the open document is unaffected by a shrink running on an unrelated copy",
                  tab.Pages.Count == 2 && !tab.HasUnsavedChanges);
        }

        // --- Stop is offered only where it can be honoured -------------------------------
        {
            using (tab.Busy.Begin(Strings.BusySaving))
            {
                Check("a save offers no Stop", !tab.Busy.CanCancel);
                tab.Busy.RequestCancel();
                Check("and asking anyway changes nothing", !tab.Busy.IsCancelling && tab.Busy.IsBusy);
            }
            Check("with nothing running there is nothing to stop", !tab.Busy.CanCancel);
            tab.Busy.RequestCancel();
            Check("and asking then is a no-op too", !tab.Busy.IsCancelling);
        }

        // --- the strip's bar, count and Stop, on the real controls in a real window -------
        //
        // The checks above prove the view model and the busy state; this proves the bindings
        // between them and the window, which is the half that can be silently wrong while every
        // view-model check still passes. The operation is begun by hand rather than by running a
        // real shrink: what is under test is the strip, and a two-picture fixture would report
        // nothing slow enough to draw it from.
        {
            Check("the strip is not up before any work", !window.BusyStripIsVisibleForTest);

            using (var held = tab.Busy.Begin(Strings.BusyShrinking, cancellable: true,
                                             progressFormat: (done, total) => Strings.BusyPictureOfPictures(done, total)))
            {
                held.Report(250, 1000);
                // Waited for by its own condition, not by a margin: the 0.5 s before the
                // indicator appears is BusyState's documented rule, and is the thing this relies
                // on.
                var shown = await PumpUntilAsync(() => window.BusyStripIsVisibleForTest, TimeSpan.FromSeconds(5));
                Check("work that lasts brings the strip up", shown);
                Check($"under its own label (\"{window.BusyLabelTextForTest}\")",
                      window.BusyLabelTextForTest == Strings.BusyShrinking);
                Check("the bar is determinate, not a barber's pole", !window.BusyBarIsIndeterminateForTest);
                Check($"and stands a quarter of the way along ({window.BusyBarValueForTest:0.##})",
                      Math.Abs(window.BusyBarValueForTest - 0.25) < 0.001);
                Check($"the count says where it is (\"{window.BusyProgressTextForTest}\")",
                      window.BusyProgressTextIsVisibleForTest
                      && window.BusyProgressTextForTest == Strings.BusyPictureOfPictures(250, 1000));
                Check("Stop is on the strip", window.BusyCancelButtonIsVisibleForTest);
                Check("with nothing yet saying it is stopping", !window.BusyCancellingLabelIsVisibleForTest);

                window.ClickBusyCancelButtonForTest();
                Check("pressing Stop asks the work to stop", held.IsCancellationRequested && tab.Busy.IsCancelling);
                Check("the button goes, so it cannot be pressed twice", !window.BusyCancelButtonIsVisibleForTest);
                Check("and \"Stopping…\" takes its place", window.BusyCancellingLabelIsVisibleForTest);
            }

            // The work has ended; the indicator lives out its minimum with nothing left to stop.
            Check("once the work has ended there is nothing to stop",
                  !window.BusyCancelButtonIsVisibleForTest && !tab.Busy.CanCancel);
            var hidden = await PumpUntilAsync(() => !window.BusyStripIsVisibleForTest, TimeSpan.FromSeconds(5));
            Check("and the strip goes", hidden);

            // Work that cannot count itself gets the indeterminate bar and no count line — the
            // save's shape, and the one every other operation has.
            using (tab.Busy.Begin(Strings.BusySaving))
            {
                var shown = await PumpUntilAsync(() => window.BusyStripIsVisibleForTest, TimeSpan.FromSeconds(5));
                Check("work that cannot count itself gets an indeterminate bar",
                      shown && window.BusyBarIsIndeterminateForTest);
                Check("no count line", !window.BusyProgressTextIsVisibleForTest);
                Check("and no Stop", !window.BusyCancelButtonIsVisibleForTest);
            }
            await PumpUntilAsync(() => !window.BusyStripIsVisibleForTest, TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Polls <paramref name="condition"/> rather than sleeping a margin: used only for
    /// BusyState's own documented 0.5 s show-delay and 0.3 s minimum-visible window (#145).</summary>
    private static async Task<bool> PumpUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }

    /// <summary>
    /// One page with two small raster images (#145): enough for
    /// <see cref="DocumentViewModel.ReplaceOversizedImagesAsync"/> to have something to walk and
    /// count, without either one needing to be worth re-encoding — the loop reports on every
    /// picture it *considers*, not only the ones it replaces. Built here rather than added to the
    /// fixtures directory, so this check needs no change to CI.
    /// </summary>
    private static byte[] TwoImagesPdf()
    {
        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Add(string body)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        // Plain ASCII letters only: Finish() below writes the whole buffer out with
        // Encoding.ASCII, which would mangle a raw byte above 0x7F.
        static string ImageData(char fill) => new(fill, 20 * 20 * 3);

        var content = "q 20 0 0 20 20 20 cm /Im1 Do Q\nq 20 0 0 20 60 20 cm /Im2 Do Q\n";
        var image1 = ImageData('A');
        var image2 = ImageData('Z');
        Add("<< /Type /Catalog /Pages 2 0 R >>");
        Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
            + "/Contents 4 0 R /Resources << /XObject << /Im1 5 0 R /Im2 6 0 R >> >> >>");
        Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
        Add($"<< /Type /XObject /Subtype /Image /Width 20 /Height 20 /ColorSpace /DeviceRGB "
            + $"/BitsPerComponent 8 /Length {image1.Length} >>\nstream\n{image1}endstream");
        Add($"<< /Type /XObject /Subtype /Image /Width 20 /Height 20 /ColorSpace /DeviceRGB "
            + $"/BitsPerComponent 8 /Length {image2.Length} >>\nstream\n{image2}endstream");

        return Finish(pdf, offsets);
    }

    /// <summary>A notice or an announcement cut down to a log line's worth, whatever its length.</summary>
    private static string Shorten(string text) => text.Length <= 48 ? text : text[..48] + "…";

    /// <summary>
    /// A one-page form whose single text widget carries the top-level name "person" on itself —
    /// the same name <see cref="ParentFieldsPdf"/>'s parent field carries. Importing that document
    /// into this one is the clash a rename cannot reach, because a hierarchical widget has no name
    /// of its own to rename, and is therefore refused whole with MEGAPDF_ERR_FIELDS on any PDFium.
    /// The Avalonia leg builds the same two documents, for the same reason: built here rather than
    /// generated into the fixtures directory, so this check needs no change to CI.
    /// </summary>
    private static byte[] FlatPersonFieldPdf()
    {
        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Add(string body)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }

        Add("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DA (/Helv 0 Tf 0 g) "
            + "/DR << /Font << /Helv 4 0 R >> >> >> >>");
        Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> "
            + "/Annots [5 0 R] >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Add("<< /Type /Annot /Subtype /Widget /FT /Tx /T (person) /V (Grace) /DA (/Helv 12 Tf 0 g) "
            + "/Rect [100 600 300 620] /F 4 /P 3 0 R >>");

        return Finish(pdf, offsets);
    }

    /// <summary>
    /// core/tests/core_tests.cpp's <c>parent_fields_pdf()</c>: a one-page form whose two text
    /// widgets ("first", "last") are kids of a parent field "person", so the top-level name lives
    /// on the parent dictionary and not on the widgets. That is the shape a PDFium page copy could
    /// not carry before patch 0033, and the one <c>MEGAPDF_ERR_FIELDS</c> exists for.
    /// </summary>
    private static byte[] ParentFieldsPdf()
    {
        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Add(string body)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        static string Stream(string dict, string body) =>
            $"<< {dict} /Length {body.Length} >>\nstream\n{body}\nendstream";

        Add("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /DA (/Helv 0 Tf 0 g) "
            + "/DR << /Font << /Helv 4 0 R >> >> >> >>");
        Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> "
            + "/Contents 5 0 R /Annots [7 0 R 8 0 R] >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Add(Stream("", "BT /F1 14 Tf 72 720 Td (Two fields under one parent) Tj ET"));
        Add("<< /FT /Tx /T (person) /Kids [7 0 R 8 0 R] >>");
        Add("<< /Type /Annot /Subtype /Widget /Parent 6 0 R /T (first) /V (Ada) /DA (/Helv 12 Tf 0 g) "
            + "/Rect [100 600 300 620] /F 4 /P 3 0 R /AP << /N 9 0 R >> >>");
        Add("<< /Type /Annot /Subtype /Widget /Parent 6 0 R /T (last) /V (Lovelace) /DA (/Helv 12 Tf 0 g) "
            + "/Rect [100 560 300 580] /F 4 /P 3 0 R /AP << /N 10 0 R >> >>");
        Add(Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 4 0 R >> >>",
                   "0.13 G 1 w 0.5 0.5 199 19 re S BT /Helv 12 Tf 0 g 2 5 Td (Ada) Tj ET"));
        Add(Stream("/Type /XObject /Subtype /Form /BBox [0 0 200 20] /Resources << /Font << /Helv 4 0 R >> >>",
                   "0.13 G 1 w 0.5 0.5 199 19 re S BT /Helv 12 Tf 0 g 2 5 Td (Lovelace) Tj ET"));

        return Finish(pdf, offsets);
    }

    /// <summary>The xref table and trailer both builders above need, from the offsets they recorded.</summary>
    private static byte[] Finish(System.Text.StringBuilder pdf, List<int> offsets)
    {
        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(pdf.ToString());
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
    /// A real AcroForm text field filled in, and a real AcroForm checkbox ticked (#590).
    /// `click` already drives the fixture's own text line and its drawn square, but
    /// fixture.pdf has no AcroForm field of either kind — this is the row `form-fields`
    /// and `checkboxes` actually describe. formtext.pdf and forms.pdf (tools/gen_test_fixtures.py)
    /// are opened as copies beside the fixture the way `pages` opens its own extra tabs, so
    /// nothing here ever writes back into the shared fixtures directory CI generates once for
    /// every state (the #569 shape: a shared path remembers things a scratch copy must not).
    /// Needs a document; the exit code is the test.
    /// </summary>
    private static async Task<bool> CheckFillFormAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } fixtureTab
            || fixtureTab.DocumentPath is not { } fixturePath)
        {
            Console.Error.WriteLine("--screenshot-state fill needs a document.");
            return false;
        }

        var passed = 0;
        var failed = 0;
        void Check(string what, bool ok)
        {
            Console.Error.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (ok)
                passed++;
            else
                failed++;
        }

        // A GUID per run (#569): these are copies of fixtures this check never saves back
        // over, but every other state here gives itself a fresh scratch directory and this
        // one follows suit rather than being the one exception.
        var scratch = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-fill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            await RunFillFormChecksAsync(window, fixtureTab, fixturePath, scratch, Check);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: the form-fill check threw: {ex}");
            failed++;
        }
        finally
        {
            foreach (var tab in window.Shell.Documents.Where(t => !ReferenceEquals(t, fixtureTab)).ToList())
            {
                tab.HasUnsavedChanges = false;   // never ask about scratch (#145 D5's dialog)
                await window.CloseTabAsync(tab);
            }
            try { Directory.Delete(scratch, recursive: true); } catch { /* best-effort scratch cleanup */ }
        }

        Console.Error.WriteLine($"fill: {passed} passed, {failed} failed ({passed + failed} checks)");
        return failed == 0;
    }

    private static async Task RunFillFormChecksAsync(
        MainWindow window, DocumentViewModel fixtureTab, string fixturePath, string scratch,
        Action<string, bool> Check)
    {
        // Siblings of the fixture CI's "Generate fixtures" step already wrote — same
        // directory derivation `reading` uses for demo.pdf. Hard failures below, not the
        // lenient skip `reading` uses for its own optional multi-tab section: a state whose
        // whole job is proving these two widgets work has nothing to report if they are not
        // there to test.
        var fixturesDir = Path.GetDirectoryName(fixturePath) ?? "";
        var formTextSource = Path.Combine(fixturesDir, "formtext.pdf");
        var formsSource = Path.Combine(fixturesDir, "forms.pdf");
        if (!File.Exists(formTextSource) || !File.Exists(formsSource))
        {
            Check($"formtext.pdf and forms.pdf are beside {Path.GetFileName(fixturePath)}",
                  false);
            return;
        }

        // --- AcroForm text field: formtext.pdf's "fullname" widget, (100,600)-(300,620) in
        // PDF space on a 792pt-tall page => 172..192 from the top (same fixture, same
        // coordinates the Avalonia leg's own form-text-fields check uses). ---
        var formTextWork = Path.Combine(scratch, "formtext-work.pdf");
        File.Copy(formTextSource, formTextWork, overwrite: true);
        var textTab = await window.Shell.OpenInTabAsync(formTextWork);
        await Task.Delay(1000);
        Check("a working tab opened on formtext.pdf", textTab.IsDocumentOpen && textTab.View is not null);
        if (textTab.View is { } textView)
        {
            var inTheBox = new PdfPoint(200, 182);
            var hit = textTab.HitTestPage(0, inTheBox);
            Check("the widget reads as a form text field", hit.Kind == PageHitKind.FormTextField);
            Check("and starts empty", hit.Field is { Value: "" });

            Check("clicking it opens the inline editor",
                  await textView.ActivatePageForTest(0, inTheBox) && textView.HasActiveEditorForTest);
            textView.ActiveEditorTextForTest = "Pat Adams";
            await textView.CommitActiveEditorForTest();
            await Task.Delay(500);

            Check("filling it marks the document dirty", textTab.HasUnsavedChanges);
            Check("and the value is readable back",
                  textTab.HitTestPage(0, inTheBox).Field is { Value: "Pat Adams" });

            var savedForm = Path.Combine(scratch, "formtext-filled.pdf");
            await textTab.SaveToPathForTestAsync(savedForm);
            using (var engine = new PdfiumEngine())
            using (var reopened = engine.Open(savedForm))
            using (var page = reopened.GetPage(0))
            {
                var saved = page.GetFormFields().FirstOrDefault(f => f.Name == "fullname");
                Check("the value survived save and reopen", saved is { Value: "Pat Adams" });
            }

            Check("undo empties it again", textTab.UndoCommand.CanExecute(null));
            await textTab.UndoCommand.ExecuteAsync(null);
            await Task.Delay(300);
            Check("  back to empty", textTab.HitTestPage(0, inTheBox).Field is { Value: "" });
        }

        // --- AcroForm checkbox: forms.pdf's "agree" widget, same (107,184) the Avalonia
        // leg's own AcroForm-checkbox check uses. ---
        var formsWork = Path.Combine(scratch, "forms-work.pdf");
        File.Copy(formsSource, formsWork, overwrite: true);
        var boxTab = await window.Shell.OpenInTabAsync(formsWork);
        await Task.Delay(1000);
        Check("a working tab opened on forms.pdf", boxTab.IsDocumentOpen && boxTab.View is not null);
        if (boxTab.View is { } boxView)
        {
            var widgetCentre = new PdfPoint(107, 184);
            var hit = boxTab.HitTestPage(0, widgetCentre);
            Check("the widget reads as a form checkbox", hit.Kind == PageHitKind.FormCheckbox);
            Check("and starts unchecked", hit.Field is { IsChecked: false });

            Check("clicking it ticks the field", await boxView.ActivatePageForTest(0, widgetCentre));
            await Task.Delay(300);
            Check("  the field now reads checked",
                  boxTab.HitTestPage(0, widgetCentre).Field is { IsChecked: true });
            Check("  and the document is dirty", boxTab.HasUnsavedChanges);

            Check("undo unticks it", boxTab.UndoCommand.CanExecute(null));
            await boxTab.UndoCommand.ExecuteAsync(null);
            await Task.Delay(300);
            Check("  back to unchecked", boxTab.HitTestPage(0, widgetCentre).Field is { IsChecked: false });
        }
    }

    /// <summary>
    /// A redaction mark through the whole lifecycle #329 fixed (#590): drawn, selected,
    /// moved, resized, removed, undone, redone, cleared and undone again — the mark
    /// lifecycle <c>RedactionMarkTests.cs</c> proves at the engine level, with no Windows
    /// self-test state driving the window's own route to it until now. Mirrors
    /// <see cref="RunWhiteoutAndTextChecksAsync"/>'s own shape, which proved the chrome
    /// this shares with a whiteout. Needs a document; the exit code is the test.
    /// </summary>
    private static async Task<bool> CheckRedactMarkAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } fixtureTab
            || fixtureTab.DocumentPath is not { } fixturePath)
        {
            Console.Error.WriteLine("--screenshot-state redact needs a document.");
            return false;
        }

        var passed = 0;
        var failed = 0;
        void Check(string what, bool ok)
        {
            Console.Error.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (ok)
                passed++;
            else
                failed++;
        }

        var scratch = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-redact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            await RunRedactMarkChecksAsync(window, fixtureTab, fixturePath, scratch, Check);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: the redaction-mark check threw: {ex}");
            failed++;
        }
        finally
        {
            foreach (var tab in window.Shell.Documents.Where(t => !ReferenceEquals(t, fixtureTab)).ToList())
            {
                tab.HasUnsavedChanges = false;
                await window.CloseTabAsync(tab);
            }
            try { Directory.Delete(scratch, recursive: true); } catch { /* best-effort scratch cleanup */ }
        }

        Console.Error.WriteLine($"redact: {passed} passed, {failed} failed ({passed + failed} checks)");
        return failed == 0;
    }

    private static async Task RunRedactMarkChecksAsync(
        MainWindow window, DocumentViewModel fixtureTab, string fixturePath, string scratch,
        Action<string, bool> Check)
    {
        var work = Path.Combine(scratch, "work.pdf");
        File.Copy(fixturePath, work, overwrite: true);
        var tab = await window.Shell.OpenInTabAsync(work);
        await Task.Delay(1000);
        Check("a working tab opened", tab.IsDocumentOpen && tab.View is not null);
        if (tab.View is not { } view)
            return;

        static bool Close(PdfRect a, PdfRect b, double tol = 1.5) =>
            Math.Abs(a.X - b.X) < tol && Math.Abs(a.Y - b.Y) < tol
            && Math.Abs(a.Width - b.Width) < tol && Math.Abs(a.Height - b.Height) < tol;

        // Clear of both of fixture.pdf's text lines (top-space y 50..102) and its drawn
        // checkbox (y 180..192, #439's top-left convention): a mark drawn over text grows to
        // whatever whole glyphs it touches rather than keeping the raw drag (#329's own
        // remark on MarkForRedactionOperation.Place) — a rect placed on blank page instead
        // takes the plain-area path and keeps the exact rectangle this check asserts against,
        // the same way `whiteout`'s own rect (which never text-snaps) gets to reuse (60,60).
        var placedAt = new PdfRect(60, 400, 80, 40);
        await tab.AddRedactionMarkAsync(0, placedAt);
        await Task.Delay(300);
        Check("a mark can be placed", tab.RedactionMarkAt(0, placedAt.Center) is { } m0 && Close(m0.Bounds, placedAt));
        Check("placing a mark is undoable, not a write to the file (#329: never applied until Save)",
              tab.UndoCommand.CanExecute(null));

        Check("clicking it selects it", await view.ActivatePageForTest(0, placedAt.Center));
        Check("  offering move", view.SelectionIsMovableForTest);
        Check("  and a resize handle", view.SelectionIsResizableForTest);

        var movedTo = new PdfRect(placedAt.X + 90, placedAt.Y + 50, placedAt.Width + 30, placedAt.Height - 10);
        var dragged = await view.DragSelectionForTest(movedTo);
        await Task.Delay(300);
        Check("dragging and resizing it in one gesture lands",
              dragged && view.SelectionBoundsForTest is { } landed && Close(landed, movedTo));
        Check("  the old spot has no mark any more", tab.RedactionMarkAt(0, placedAt.Center) is null);
        Check("  the new spot does", tab.RedactionMarkAt(0, movedTo.Center) is { } m1 && Close(m1.Bounds, movedTo));

        await tab.UndoCommand.ExecuteAsync(null);
        await Task.Delay(300);
        Check("undo restores the prior rect",
              tab.RedactionMarkAt(0, placedAt.Center) is not null && tab.RedactionMarkAt(0, movedTo.Center) is null);

        await tab.RedoCommand.ExecuteAsync(null);
        await Task.Delay(300);
        Check("redo re-applies the move",
              tab.RedactionMarkAt(0, movedTo.Center) is not null && tab.RedactionMarkAt(0, placedAt.Center) is null);

        // Undo and redo above ran under the chrome the drag left showing — a click on an
        // already-selected item deselects rather than reselects (pre-existing chrome
        // behaviour, shared by every selectable kind — RunWhiteoutAndTextChecksAsync's own
        // remark says the same), and neither Undo nor Redo clears a selection made before
        // them. One throwaway click clears whatever that left before the real one below
        // actually selects.
        await view.ActivatePageForTest(0, movedTo.Center);
        Check("re-selecting the moved mark", await view.ActivatePageForTest(0, movedTo.Center));
        Check("removing it takes off the right mark",
              await view.RemoveSelectionForTest() && tab.RedactionMarkAt(0, movedTo.Center) is null);
        await tab.UndoCommand.ExecuteAsync(null);
        await Task.Delay(300);
        Check("  and undo puts that same one back", tab.RedactionMarkAt(0, movedTo.Center) is not null);

        // A second mark, so Clear all has more than one to prove it clears every mark on
        // the page rather than coincidentally the only one.
        var secondAt = new PdfRect(300, 500, 60, 30);
        await tab.AddRedactionMarkAsync(0, secondAt);
        await Task.Delay(300);
        Check("a second mark can be added beside the first",
              tab.RedactionMarksOn(0).Count == 2);

        Check("Clear all marks takes both off the page", await view.ClearRedactionMarksAsync());
        await Task.Delay(300);
        Check("  nothing left on the page", tab.RedactionMarksOn(0).Count == 0);

        await tab.UndoCommand.ExecuteAsync(null);
        await Task.Delay(300);
        Check("undoing the clear puts both marks back, in one step", tab.RedactionMarksOn(0).Count == 2);

        // Clean up: undo everything this check did, so the fixture is left as it opened.
        // Bounded (the page-tools check's own "five page operations" cleanup sets the
        // precedent): a Revert() that throws leaves CanExecute true forever — UndoStack.Undo
        // re-pushes the failed op rather than drop it — and an unbounded drain here would
        // hang the state rather than report the real failure.
        for (var steps = 0; tab.UndoCommand.CanExecute(null) && steps < 10; steps++)
            await tab.UndoCommand.ExecuteAsync(null);
        await Task.Delay(300);
        Check("fully undone, no marks remain", tab.RedactionMarksOn(0).Count == 0);
    }

    /// <summary>
    /// Save, for real (#590): the Save command the toolbar button and Ctrl+S both run
    /// (<see cref="DocumentViewModel.SaveCommand"/>), not <see cref="DocumentViewModel.SaveToPathForTestAsync"/>'s
    /// direct call to <c>VerifiedSave</c> that `pages` and `whiteout-text` use to check
    /// persistence without a file picker — this is the route itself. Proves the file on disk
    /// actually changes and <see cref="DocumentViewModel.HasUnsavedChanges"/> clears, which is
    /// the whole of what "Save worked" means to whoever pressed it. Needs a document; the
    /// exit code is the test.
    /// </summary>
    private static async Task<bool> CheckSaveAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } fixtureTab
            || fixtureTab.DocumentPath is not { } fixturePath)
        {
            Console.Error.WriteLine("--screenshot-state save needs a document.");
            return false;
        }

        var passed = 0;
        var failed = 0;
        void Check(string what, bool ok)
        {
            Console.Error.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (ok)
                passed++;
            else
                failed++;
        }

        var scratch = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            await RunSaveChecksAsync(window, fixturePath, scratch, Check);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: the save check threw: {ex}");
            failed++;
        }
        finally
        {
            foreach (var tab in window.Shell.Documents.Where(t => !ReferenceEquals(t, fixtureTab)).ToList())
            {
                tab.HasUnsavedChanges = false;
                await window.CloseTabAsync(tab);
            }
            try { Directory.Delete(scratch, recursive: true); } catch { /* best-effort scratch cleanup */ }
        }

        Console.Error.WriteLine($"save: {passed} passed, {failed} failed ({passed + failed} checks)");
        return failed == 0;
    }

    private static async Task RunSaveChecksAsync(
        MainWindow window, string fixturePath, string scratch, Action<string, bool> Check)
    {
        var work = Path.Combine(scratch, "work.pdf");
        File.Copy(fixturePath, work, overwrite: true);
        var before = await File.ReadAllBytesAsync(work);

        var tab = await window.Shell.OpenInTabAsync(work);
        await Task.Delay(1000);
        Check("a working tab opened", tab.IsDocumentOpen);
        if (!tab.IsDocumentOpen)
            return;

        Check("a freshly opened document has nothing to save", !tab.HasUnsavedChanges);

        await tab.AddTextBoxAsync(0, new PdfPoint(72, 500), "Saved for real");
        await Task.Delay(500);
        Check("adding a note marks the document dirty", tab.HasUnsavedChanges);
        Check("Save is available once there is something to save", tab.SaveCommand.CanExecute(null));

        await tab.SaveCommand.ExecuteAsync(null);
        await Task.Delay(1000);

        Check("Save clears the unsaved flag", !tab.HasUnsavedChanges);

        var after = await File.ReadAllBytesAsync(work);
        Check($"the file on disk actually changed ({before.Length} -> {after.Length} bytes)",
              !before.AsSpan().SequenceEqual(after));

        using (var engine = new PdfiumEngine())
        using (var reopened = engine.Open(work))
        using (var page = reopened.GetPage(0))
        {
            Check("what was added is in the saved file, independently reopened",
                  page.GetTextBoxes().Any(t => t.Text == "Saved for real"));
        }

        // Clean up in memory, though the file on disk already carries the note and this
        // check does not undo it there — the scratch copy is deleted with the directory.
        // Bounded, not while(CanExecute) — see the redact check's own remark on why.
        for (var steps = 0; tab.UndoCommand.CanExecute(null) && steps < 10; steps++)
            await tab.UndoCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// The Password command's real dialog, and the write path behind it (#590). WinUI gives
    /// this process no way to press an ad hoc <see cref="ContentDialog"/>'s Primary button
    /// short of UI Automation on a live desktop session — the same honest limit
    /// <see cref="DocumentView.DragSelectionForTest"/> already documents for a gesture this
    /// process cannot synthesize either — so this proves the dialog itself is the real one,
    /// with its real two password fields, reachable through <see cref="DialogGate.Current"/>,
    /// cancels it exactly as pressing Escape would, and then drives the save-with-a-password
    /// path it would have run (<see cref="DocumentViewModel.SetPasswordForTestAsync"/>)
    /// directly — proving the saved file is genuinely encrypted by reopening it with no
    /// password (refused) and with the password (opens). Needs a document; the exit code is
    /// the test.
    /// </summary>
    private static async Task<bool> CheckSecurityDialogAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } fixtureTab
            || fixtureTab.DocumentPath is not { } fixturePath)
        {
            Console.Error.WriteLine("--screenshot-state security needs a document.");
            return false;
        }

        var passed = 0;
        var failed = 0;
        void Check(string what, bool ok)
        {
            Console.Error.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (ok)
                passed++;
            else
                failed++;
        }

        var scratch = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-security-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            await RunSecurityChecksAsync(window, fixturePath, scratch, Check);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: the security check threw: {ex}");
            failed++;
        }
        finally
        {
            foreach (var tab in window.Shell.Documents.Where(t => !ReferenceEquals(t, fixtureTab)).ToList())
            {
                tab.HasUnsavedChanges = false;
                await window.CloseTabAsync(tab);
            }
            try { Directory.Delete(scratch, recursive: true); } catch { /* best-effort scratch cleanup */ }
        }

        Console.Error.WriteLine($"security: {passed} passed, {failed} failed ({passed + failed} checks)");
        return failed == 0;
    }

    private static async Task RunSecurityChecksAsync(
        MainWindow window, string fixturePath, string scratch, Action<string, bool> Check)
    {
        const string password = "correct horse battery staple";
        var work = Path.Combine(scratch, "work.pdf");
        File.Copy(fixturePath, work, overwrite: true);
        var tab = await window.Shell.OpenInTabAsync(work);
        await Task.Delay(1000);
        Check("a working tab opened", tab.IsDocumentOpen);
        if (!tab.IsDocumentOpen)
            return;

        Check("Security is available on an unencrypted, fully-accessible document",
              tab.SecurityCommand.CanExecute(null));

        // --- The dialog itself: raised, real, and cancellable -------------------------
        //
        // DialogGate.Current is one static slot, not a stack: if anything else already had
        // the gate (a leftover dialog from an earlier step in this same process), Current
        // would be THAT dialog while this one waits its turn behind it, and this check would
        // read, title-match and Hide() the wrong one — then wait forever below on a Set
        // Password dialog nobody closes. Pumped rather than slept, and matched by title
        // rather than trusted on sight, so a mismatch is a clear FAIL instead of a hang.
        var showing = tab.SecurityCommand.ExecuteAsync(null);
        await PumpUntilAsync(() => DialogGate.Current is not null, TimeSpan.FromSeconds(5));
        Check("the toolbar is disabled while the password dialog shows", !window.IsToolbarEnabled);
        if (DialogGate.Current is { } dialog && Equals(dialog.Title, Strings.SetPasswordTitle))
        {
            Check("it is the Set Password dialog", true);
            var fields = (dialog.Content as Panel)?.Children.OfType<PasswordBox>().ToList() ?? [];
            Check("it shows the two real password fields (new, confirm)", fields.Count == 2);
            dialog.Hide(); // cancel — WinUI gives this process no way to press Primary itself
        }
        else
        {
            var found = DialogGate.Current is { } other ? $"found \"{other.Title}\" instead" : "nothing is open";
            Check($"the Set Password dialog is actually the one open ({found})", false);
        }
        // Bounded: if the dialog found above was not really the one SecurityCommand raised
        // (the mismatch branch just above), nothing ever closes it and an unconditional await
        // here would hang until the state's own CI timeout kills it with no PASS/FAIL to show
        // for the wait.
        if (await Task.WhenAny(showing, Task.Delay(TimeSpan.FromSeconds(5))) != showing)
            Check("the password command returned once the dialog closed (it did not, within 5s)", false);
        await Task.Delay(300);
        Check("cancelling leaves the document unencrypted", !tab.HasUnsavedChanges);
        Check("  and the toolbar re-enabled", window.IsToolbarEnabled);

        // --- The write path the dialog's Set button would have run ---------------------
        await tab.SetPasswordForTestAsync(password);
        await Task.Delay(1000);
        Check("setting a password clears the unsaved flag (it is a save)", !tab.HasUnsavedChanges);
        Check($"and announces it (\"{tab.SecurityNotice}\")", tab.IsSecurityNoticeOpen && tab.SecurityNotice == Strings.PasswordSetNotice);

        using (var engine = new PdfiumEngine())
        {
            var openedWithoutPassword = false;
            try
            {
                using var noPassword = engine.Open(work);
                openedWithoutPassword = true;
            }
            catch (PdfLoadException ex)
            {
                Check("opening the saved file with no password is refused as a password error", ex.IsPasswordError);
            }
            Check("the saved file genuinely needs a password now", !openedWithoutPassword);

            using var withPassword = engine.Open(work, password);
            Check("and opens with the right one", withPassword.PageCount > 0);
        }
    }

    /// <summary>
    /// About's version line, and the third-party notices dialog (#590) — #571 was exactly
    /// this shape on Linux: a window that had shipped for releases with nothing in any
    /// self-test leading to it. Needs a document (<see cref="ApplyStateAsync"/>'s own guard,
    /// not used otherwise here); the exit code is the test.
    /// </summary>
    private static async Task<bool> CheckAboutAsync(MainWindow window)
    {
        var passed = 0;
        var failed = 0;
        void Check(string what, bool ok)
        {
            Console.Error.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (ok)
                passed++;
            else
                failed++;
        }

        try
        {
            var expectedVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "dev";
            window.OpenSettingsFlyoutForTest();
            await PumpUntilAsync(() => window.AboutVersionTextForTest.Length > 0, TimeSpan.FromSeconds(5));
            Check($"About shows the real assembly version (\"{window.AboutVersionTextForTest}\", expected \"{expectedVersion}\")",
                  window.AboutVersionTextForTest == Strings.AboutVersion(expectedVersion));

            var notices = await MainWindow.LoadThirdPartyNoticesForTest();
            Check("the bundled notices file loads", notices.Length > 0 && !notices.StartsWith(Strings.NoticesLoadFailed, StringComparison.Ordinal));
            Check("  and actually lists a third-party component (PDFium)", notices.Contains("PDFium", StringComparison.Ordinal));

            // DialogGate.Current is one slot, not a stack (see the `security` check's own
            // remark) — matched by title, not trusted on sight, so a leftover dialog from
            // anywhere else in this run cannot be misreported as the notices dialog.
            window.OpenThirdPartyNoticesForTest();
            await PumpUntilAsync(() => DialogGate.Current is not null, TimeSpan.FromSeconds(5));
            if (DialogGate.Current is { } dialog && Equals(dialog.Title, Strings.NoticesTitle))
            {
                Check("the notices dialog is the real one", true);
                Check("  showing the same text", dialog.Content is TextBox tb && tb.Text.Contains("PDFium", StringComparison.Ordinal));
                dialog.Hide();
            }
            else
            {
                var found = DialogGate.Current is { } other ? $"found \"{other.Title}\" instead" : "nothing is open";
                Check($"the notices dialog actually opened ({found})", false);
            }
            await Task.Delay(300);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: the about check threw: {ex}");
            failed++;
        }
        finally
        {
            window.CloseSettingsFlyoutForTest();
        }

        Console.Error.WriteLine($"about: {passed} passed, {failed} failed ({passed + failed} checks)");
        return failed == 0;
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

        // #590: the library opening is only ever photographed, never the act of placing one on
        // a page — `signature-place` had no Windows self-test state at all. Same document, same
        // window: a card is armed and dropped on page 1, checked against the geometry SDD §3.3
        // promises (180pt wide, aspect preserved, centred on the click — the same numbers the
        // Avalonia leg's own CheckSignaturePlacement proves), then every edit this adds is
        // undone, so the library shot above is still what gets captured.
        var placementOk = await RunSignaturePlacementChecksAsync(vm);
        return placementOk;
    }

    private static async Task<bool> RunSignaturePlacementChecksAsync(DocumentViewModel vm)
    {
        var passed = 0;
        var failed = 0;
        void Check(string what, bool ok)
        {
            Console.Error.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (ok)
                passed++;
            else
                failed++;
        }

        if (vm.View is not { } view || vm.Signatures.FirstOrDefault(s => !s.IsMissing) is not { } card)
        {
            Console.Error.WriteLine("--screenshot-state sign: no usable card to place, or no page view to drop it on.");
            return false;
        }

        try
        {
            // Read independently of PlacePendingSignatureAsync's own call to the same loader
            // (same reasoning as RunZoomAnchorChecksAsync's own remark: computing the expected
            // geometry from the production code that is supposed to produce it would only prove
            // the code agrees with itself) — this is the same file the placement is about to
            // stamp, read a second time, from here.
            var source = await SignatureImageProcessor.LoadPngAsync(card.PngPath);
            var expectedWidth = 180.0;
            var expectedHeight = expectedWidth * source.Height / source.Width;

            vm.SelectSignatureForPlacement(card);
            Check("selecting a card arms placement", ReferenceEquals(vm.PendingSignature, card));

            var at = new PdfPoint(300, 400);
            Check("clicking the page places it", await view.ActivatePageForTest(0, at));
            // The commit re-renders the page (a new object index, a rebuilt PageCanvas) —
            // the same settle `whiteout-text`'s own move/resize section waits out before
            // the next ActivatePageForTest, which looks the canvas up fresh by page index
            // and finds nothing mid-rebuild otherwise.
            await Task.Delay(300);
            var hit = vm.HitTestPage(0, at);
            Check("it reads back as a signature stamp",
                  hit.Kind == PageHitKind.StampAnnotation && hit.AnnotationId is { } id && id.StartsWith("sig:", StringComparison.Ordinal));
            if (hit.Bounds is not { } bounds)
            {
                Console.Error.WriteLine("--screenshot-state sign: placement reported no bounds; the geometry and chrome checks below were skipped.");
                failed++;
            }
            else
            {
                Check($"it is 180pt wide (got {bounds.Width:F1})", Math.Abs(bounds.Width - expectedWidth) < 0.5);
                Check($"its aspect ratio is preserved ({expectedHeight:F1}pt tall, got {bounds.Height:F1})",
                      Math.Abs(bounds.Height - expectedHeight) < 0.5);
                Check("it is centred on the click",
                      Math.Abs(bounds.X + bounds.Width / 2 - at.X) < 1 && Math.Abs(bounds.Y + bounds.Height / 2 - at.Y) < 1);

                Check("clicking the placed signature selects it", await view.ActivatePageForTest(0, bounds.Center));
                Check("  offering move", view.SelectionIsMovableForTest);
                Check("  and a resize handle", view.SelectionIsResizableForTest);

                var movedTo = new PdfRect(bounds.X + 40, bounds.Y + 20, bounds.Width, bounds.Height);
                var dragged = await view.DragSelectionForTest(movedTo);
                await Task.Delay(300);
                Check("dragging it lands",
                      dragged && vm.HitTestPage(0, movedTo.Center).Kind == PageHitKind.StampAnnotation);

                Check("undo removes it", vm.UndoCommand.CanExecute(null));
                // Bounded — see the redact check's own remark on why while(CanExecute) alone is a hang risk.
                for (var steps = 0; vm.UndoCommand.CanExecute(null) && steps < 10; steps++)
                    await vm.UndoCommand.ExecuteAsync(null);
                await Task.Delay(300);
                Check("  the page has no stamp left where it was dropped",
                      vm.HitTestPage(0, at).Kind != PageHitKind.StampAnnotation);
            }
        }
        catch (Exception ex)
        {
            // Same reasoning as CheckReadingModeAsync's own try/catch: left to escape, this
            // hangs the process with nothing said rather than failing the one check.
            Console.Error.WriteLine($"FAIL: signature placement threw: {ex}");
            failed++;
        }
        finally
        {
            vm.CancelSignaturePlacement();
            // Bounded, and in a finally: an unbounded drain here could mask the real
            // exception this block's own catch was reporting, turning a FAIL with a stack
            // into a hang with nothing said.
            for (var steps = 0; vm.UndoCommand.CanExecute(null) && steps < 10; steps++)
                await vm.UndoCommand.ExecuteAsync(null);
        }

        Console.Error.WriteLine($"sign (placement): {passed} passed, {failed} failed ({passed + failed} checks)");
        return failed == 0;
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
