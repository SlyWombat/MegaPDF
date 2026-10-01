using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MegaPDF.Avalonia.ViewModels;
using MegaPDF.Core.Engine;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// Reading mode (#505, #511; docs/reading-mode-plan.md §2 tiers 1–2, §4 "Mac and Linux").
///
/// The page and nothing else. The page host never moves: only the chrome around it
/// goes, which is why leaving the mode puts you back on the same page, at the same
/// zoom and the same scroll offset, with no re-render — the invariant §1 of the plan
/// asks every platform to keep.
///
/// It is a **window** state, not a document's. The chrome it hides belongs to the
/// window, ⌃Tab still switches tabs inside it, and every tab in the window is told
/// about it (<see cref="ApplyReadingModeToTabs"/>) because what changes per document
/// is only what a click on the page means. That is also why there is no per-document
/// memory of it: with tabs, a per-document flag would flip the whole window's chrome
/// as you switched tab (plan §2, decision 2 on #168). The one preference is
/// <c>AppSettings.OpenInReadingMode</c>, off by default.
///
/// Two idioms, one code path (plan §4):
///
/// * **macOS** — View ▸ Reading Mode ⇧⌘R (Safari's Reader key; ⌘H is Hide and cannot
///   be taken), View ▸ Enter Full Screen ⌃⌘F, the standard AppKit item whose title
///   flips to Exit Full Screen. The menu bar itself never hides in a normal window —
///   that is macOS behaviour and not something to fight; full screen is what hides it.
/// * **Linux** — the same two items, reached by Ctrl+H and F11 (window key bindings:
///   MainWindow.axaml hosts no menu bar on X11, so a NativeMenuItem gesture is never
///   heard there — the same reason Ctrl+W and Ctrl+Q are bound in
///   <c>BindLinuxWindowShortcuts</c>).
///
/// Accessibility is the point of the feature, not a coat of paint on it (plan §7):
///
/// * Hidden chrome is <c>IsVisible=false</c>, never dimmed, never a transparent
///   overlay. An invisible-but-present toolbar is a focus trap, and reading mode is
///   meant to be the screen-reader-friendly view.
/// * The floating pill never fades while it holds keyboard focus or while a screen
///   reader is running (<see cref="Platform.ScreenReader"/>).
/// * Entering and leaving are announced through a live region, the Avalonia
///   equivalent of the WinUI <c>RaiseNotificationEvent</c> and the phones'
///   <c>UIAccessibility.post</c> / <c>liveRegion</c>.
/// * Nothing animates. That is the reduced-motion answer, taken unconditionally:
///   Avalonia exposes no reduced-motion setting on X11, and show/hide is what the
///   plan asks for when the setting is on. A fade nobody can turn off would be worse
///   than no fade at all.
/// </summary>
public partial class MainWindow
{
    /// <summary>How long the pill stays up after the last movement (plan §2: ~2 s).</summary>
    private static readonly TimeSpan PillIdle = TimeSpan.FromSeconds(2);

    private DispatcherTimer? _pillTimer;

    private Control[]? _chromeHosts;

    /// <summary>The chrome that goes, in the order MainWindow.axaml declares it.</summary>
    private Control[] ChromeHosts =>
        _chromeHosts ??= [ToolbarHost, TabStripHost, BusyStripHost, StatusBarHost];

    /// <summary>Every tab in this window, or none while the shell has not arrived.</summary>
    private IReadOnlyList<DocumentViewModel> Tabs =>
        Shell is { } shell ? shell.Documents : [];

    /// <summary>
    /// Every host reading mode is responsible for, including the find bar — which is
    /// not switched by this class (it follows <c>Active.IsFindOpen</c>, and find is the
    /// one thing allowed to appear over the reading view) but is closed on entry, so
    /// "all five are hidden and out of the tab order" holds unconditionally. Used by
    /// the self-test.
    /// </summary>
    internal IReadOnlyList<Control> ReadingChromeForTest => [.. ChromeHosts, FindBarHost, PageStripHost];

    /// <summary>Whether this window is in reading mode. A window state; see the class doc.</summary>
    internal bool IsReadingMode { get; private set; }

    /// <summary>Whether this window is full screen — only ever reached from inside reading mode.</summary>
    internal bool IsFullScreen => WindowState == WindowState.FullScreen;

    /// <summary>What the live region currently says, for the self-test.</summary>
    internal string ReadingAnnouncementText => ReadingAnnouncement.Text ?? "";

    /// <summary>
    /// Whether the idle countdown is running — i.e. whether the pill is currently
    /// allowed to fade at all. For the self-test: "a screen reader is on" has to mean
    /// the countdown never starts, not merely that it is slow.
    /// </summary>
    internal bool ReadingPillIsCountingDown => _pillTimer is { IsEnabled: true };

    /// <summary>
    /// Fires the idle tick now, as the timer would.
    ///
    /// For the self-test, which runs on the headless platform set up with
    /// <c>SetupWithoutStarting</c> — there is a dispatcher but no loop driving its
    /// timers, so a <see cref="DispatcherTimer"/> never ticks there however long the
    /// check waits. This calls the production handler, guard and all, so the rule being
    /// checked ("never while a reader is on, never while the pill has focus") is the
    /// real one and only the clock is stood in for.
    /// </summary>
    internal void FadeReadingPillNowForTest() => OnPillIdle(null, EventArgs.Empty);

    private void WireReadingMode()
    {
        ReadingPreviousButton.Click += (_, _) => GoToPage((Active?.CurrentPage ?? 1) - 1);
        ReadingNextButton.Click += (_, _) => GoToPage((Active?.CurrentPage ?? 1) + 1);
        ReadingFitWidthButton.Click += (_, _) => Run(Active?.FitWidthCommand);
        ReadingFitPageButton.Click += (_, _) => Run(Active?.FitPageCommand);
        ReadingZoomOutButton.Click += (_, _) => Run(Active?.ZoomOutCommand);
        ReadingZoomInButton.Click += (_, _) => Run(Active?.ZoomInCommand);
        ReadingExitButton.Click += (_, _) => ExitReadingMode();

        // Type a page and press Enter. The box is emptied each time it opens so the
        // last jump is never re-offered as a default nobody asked for.
        ReadingPageButton.Click += (_, _) => ReadingPageBox.Text = "";
        ReadingPageBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            if (int.TryParse(ReadingPageBox.Text, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.CurrentCulture, out var page))
                GoToPage(page);
            ReadingPageButton.Flyout?.Hide();
            e.Handled = true;
        };

        // The words a screen reader reads, stated rather than inferred from the glyph
        // each button wears — the contract the toolbar and the find bar already keep at
        // their icon-only steps (#237).
        Name(ReadingPreviousButton, Strings.PreviousPage);
        Name(ReadingNextButton, Strings.NextPage);
        Name(ReadingPageButton, Strings.GoToPage);
        Name(ReadingFitWidthButton, Strings.FitWidth);
        Name(ReadingFitPageButton, Strings.FitPage);
        Name(ReadingZoomOutButton, Strings.ZoomOut);
        Name(ReadingZoomInButton, Strings.ZoomIn);
        Name(ReadingExitButton, Strings.ExitReadingMode);

        // Keyboard focus anywhere in the pill keeps it up, for as long as it is there:
        // a pill that faded out from under the Tab key would be a control you cannot
        // reach twice.
        ReadingPill.GotFocus += (_, _) => ShowReadingPill();
        ReadingPill.LostFocus += (_, _) => RestartPillTimer();

        static void Name(Control control, string name)
        {
            AutomationProperties.SetName(control, name);
            ToolTip.SetTip(control, name);
        }

        static void Run(System.Windows.Input.ICommand? command)
        {
            if (command?.CanExecute(null) == true)
                command.Execute(null);
        }
    }

    /// <summary>⇧⌘R on macOS, Ctrl+H on Linux, and the View menu item.</summary>
    internal void ToggleReadingMode()
    {
        if (IsReadingMode)
            ExitReadingMode();
        else
            EnterReadingMode();
    }

    /// <summary>
    /// Hides the chrome and puts the pill up. Nothing about the document changes: no
    /// save, no render, no scroll — the dirty dot stays exactly as it was.
    /// </summary>
    internal void EnterReadingMode()
    {
        // A tab, not necessarily a loaded one: "Open documents in reading mode" applies
        // to a restored session too, whose load is still in flight when its tab
        // appears. With no tab at all there is nothing to read and the empty state
        // would be left framed by nothing, so that case is refused.
        if (IsReadingMode || Shell is not { Documents.Count: > 0 })
            return;

        IsReadingMode = true;

        // Editing is off, so anything already armed is disarmed on the way in and does
        // not come back on the way out (plan §2). Undo and redo stay live: they act on
        // the document, not on the page, and nothing can be added to the stack while
        // editing is off.
        DismissInlineEditor();
        CancelBand();
        foreach (var document in Tabs)
        {
            document.CancelModes();
            document.ClearSelection();
            document.ClearPageFocus();
        }
        RemoveChrome();
        RemoveFocusRing();

        // The find bar is the one piece of chrome that may come back over the reading
        // view (⌘F/Ctrl+F still works). It is closed here so that entering the mode
        // leaves no chrome at all; reopening it is one keystroke.
        if (Active is { IsFindOpen: true })
            CloseFind();

        // Out of the tab order and the accessible tree, not merely invisible.
        var hadFocusInChrome = FocusedElementIsInChrome();
        foreach (var host in ChromeHosts)
            host.IsVisible = false;

        // The Pages sidebar is chrome too (#174): the mode is the page and nothing else.
        // Switched through its own method, which knows the mode is on and so puts nothing
        // back until the mode is off.
        ApplyPageStripVisibility();

        ApplyReadingModeToTabs();
        ShowReadingPill();

        // Focus only moves if it was on something that has just gone; otherwise it is
        // left where the person put it. Moving it unasked would also mean the pill
        // holds focus and therefore never fades, for everybody.
        if (hadFocusInChrome)
        {
            FocusManager?.ClearFocus();
            ReadingExitButton.Focus();
        }

        Announce(Strings.ReadingModeOn);
        RefreshMenuBar();
    }

    /// <summary>
    /// Puts every host back, drops full screen if it was on, restores focus to the
    /// toolbar and says so.
    /// </summary>
    internal void ExitReadingMode()
    {
        if (!IsReadingMode)
            return;

        IsReadingMode = false;
        // Full screen is a level *inside* reading mode, so leaving the mode leaves it
        // too — a full-screen window wearing its toolbar again is neither level.
        if (IsFullScreen)
            WindowState = WindowState.Normal;

        HideReadingPill();
        foreach (var host in ChromeHosts)
            host.IsVisible = true;
        // Back only if the tab had it open before the mode (#174).
        ApplyPageStripVisibility();

        ApplyReadingModeToTabs();

        // "Restores focus" means the toolbar, which is where it would have been: Open
        // is the one control that is enabled with or without a document (#412).
        FocusManager?.ClearFocus();
        OpenButton.Focus();

        Announce(Strings.ReadingModeOff);
        RefreshMenuBar();
    }

    /// <summary>
    /// ⌃⌘F / F11. Only inside reading mode: full screen on its own is not offered
    /// (plan §2 — "that was Acrobat's confusion").
    /// </summary>
    internal void ToggleFullScreen()
    {
        if (!IsReadingMode)
            return;
        WindowState = IsFullScreen ? WindowState.Normal : WindowState.FullScreen;
        ShowReadingPill();
        RefreshMenuBar();
    }

    /// <summary>
    /// One level back, and exactly one (plan §7): the find bar, then full screen, then
    /// reading mode. False when none of the three applies, which leaves Escape to do
    /// whatever it did before.
    ///
    /// The order is the whole point. Escape in reading mode means "give me the toolbar
    /// back", and a level that swallowed it — or one that was skipped — costs the
    /// person a tool state they did not mean to drop.
    /// </summary>
    private bool StepBackFromReadingMode()
    {
        if (Active is { IsFindOpen: true })
        {
            CloseFind();
            return true;
        }
        if (IsFullScreen)
        {
            WindowState = WindowState.Normal;
            ShowReadingPill();
            RefreshMenuBar();
            return true;
        }
        if (IsReadingMode)
        {
            ExitReadingMode();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Mirrors the window's mode onto every tab it holds, and keeps the pill's file
    /// name honest. Called on entry, on exit, and whenever the set of tabs or the
    /// active tab changes.
    /// </summary>
    internal void ApplyReadingModeToTabs()
    {
        foreach (var document in Tabs)
            document.IsReadingMode = IsReadingMode;

        // The file name only earns a place on the pill when there is more than one tab
        // — with one, the window title already says it (plan §2, tabs).
        var many = Shell is { Documents.Count: > 1 };
        ReadingFileName.IsVisible = many;
        ReadingFileName.Text = many ? Active?.DocumentName ?? "" : "";
    }

    // --- The floating pill ---------------------------------------------------

    /// <summary>
    /// Up, and counting down again. Called on entry, on pointer movement over the page,
    /// and whenever the pill takes keyboard focus.
    /// </summary>
    internal void ShowReadingPill()
    {
        if (!IsReadingMode)
            return;
        ReadingPill.IsVisible = true;
        RestartPillTimer();
    }

    /// <summary>
    /// Up, and staying up: the idle countdown is stopped rather than restarted.
    ///
    /// For <c>--screenshot-state reading</c> only. The pill is the one thing in a
    /// reading-mode capture that identifies the application, and its own two-second
    /// fade expires at the moment the window is rendered, so a capture that just
    /// called <see cref="ShowReadingPill"/> would be a coin toss between the picture
    /// the listing wants and a bare page. Nothing a person does reaches this: the
    /// production rules — fade on idle, never while the pill holds focus, never while
    /// a screen reader is running — are untouched, and the next
    /// <see cref="ShowReadingPill"/> starts the clock again as usual.
    /// </summary>
    internal void PinReadingPillForCapture()
    {
        if (!IsReadingMode)
            return;
        ReadingPill.IsVisible = true;
        _pillTimer?.Stop();
    }

    private void HideReadingPill()
    {
        _pillTimer?.Stop();
        ReadingPill.IsVisible = false;
    }

    /// <summary>
    /// Starts (or restarts) the idle countdown — unless something says the pill must
    /// stay: keyboard focus inside it, or a screen reader running. Both are "this
    /// person cannot get it back by waggling the mouse", which is the whole reason the
    /// rule exists.
    /// </summary>
    private void RestartPillTimer()
    {
        _pillTimer?.Stop();
        if (!IsReadingMode || PillMustStay())
            return;

        _pillTimer ??= new DispatcherTimer { Interval = PillIdle };
        _pillTimer.Tick -= OnPillIdle;
        _pillTimer.Tick += OnPillIdle;
        _pillTimer.Interval = PillIdle;
        _pillTimer.Start();
    }

    private void OnPillIdle(object? sender, EventArgs e)
    {
        _pillTimer?.Stop();
        if (PillMustStay())
            return;
        // No animation, deliberately: see the class doc on reduced motion.
        ReadingPill.IsVisible = false;
    }

    /// <summary>Whether the pill is forbidden from fading right now.</summary>
    private bool PillMustStay() =>
        PillHasFocus() || Platform.ScreenReader.IsRunning();

    private bool PillHasFocus() =>
        FocusManager?.GetFocusedElement() is Control focused
        && (ReferenceEquals(focused, ReadingPill) || ReadingPill.IsVisualAncestorOf(focused));

    /// <summary>Whether keyboard focus is currently on a control that reading mode hides.</summary>
    private bool FocusedElementIsInChrome() =>
        FocusManager?.GetFocusedElement() is Control focused
        && ChromeHosts.Any(host => ReferenceEquals(focused, host) || host.IsVisualAncestorOf(focused));

    /// <summary>
    /// The live region (MainWindow.axaml, <c>ReadingAnnouncement</c>). Writing the same
    /// text twice running would say nothing on some stacks, so a repeat is cleared
    /// first — entering, leaving and entering again all have to be heard.
    /// </summary>
    private void Announce(string text)
    {
        if (ReadingAnnouncement.Text == text)
            ReadingAnnouncement.Text = "";
        ReadingAnnouncement.Text = text;
    }

    // --- Page colours (#511) -------------------------------------------------

    /// <summary>
    /// The gutter behind the pages and the floating pill, in the current tint. The
    /// engine tints the page itself (<c>MEGAPDF_RENDER_SEPIA</c> / <c>_NIGHT</c>); this
    /// is the chrome around it, from the <c>BrandReading*</c> tokens (design-tokens §5).
    /// Normal puts the theme's own brushes back, so the window is exactly what it was.
    /// </summary>
    internal void ApplyTintedChrome()
    {
        var tint = Shell?.PageTint ?? PageTint.Normal;
        if (tint == PageTint.Normal)
        {
            // Cleared, not set back to the theme's brush by hand. Both of these carry a
            // DynamicResource in MainWindow.axaml, and a brush resolved once here would
            // still be the light one after macOS switched the window to dark under it —
            // the same trap Brand.cs's own doc describes. Clearing the local value hands
            // the property back to the markup, which keeps following the theme.
            PageScroller.ClearValue(ScrollViewer.BackgroundProperty);
            ReadingPill.ClearValue(Border.BackgroundProperty);
            ReadingPill.ClearValue(TextElement.ForegroundProperty);
            return;
        }

        var (gutter, surface, ink) = tint == PageTint.Sepia
            ? ("BrandReadingGutterSepia", "BrandReadingSurfaceSepia", "BrandReadingInkSepia")
            : ("BrandReadingGutterNight", "BrandReadingSurfaceNight", "BrandReadingInkNight");

        PageScroller.Background = Brand.Brush(gutter);
        ReadingPill.Background = Brand.Brush(surface);
        // Border carries no Foreground of its own; TextElement's attached one is what
        // the labels inside it inherit.
        TextElement.SetForeground(ReadingPill, Brand.Brush(ink));
    }

    /// <summary>For --screenshot runs and the self-test: the mode, in one line.</summary>
    internal string DescribeReadingMode()
    {
        var hidden = ReadingChromeForTest.Where(c => !c.IsVisible).Select(c => c.Name).ToList();
        var tint = Shell?.PageTint ?? PageTint.Normal;
        return $"reading mode: {(IsReadingMode ? "on" : "off")}, "
             + $"full screen={IsFullScreen}, pill={(ReadingPill.IsVisible ? "shown" : "faded")}, "
             + $"page colours={tint}, hidden=[{string.Join(", ", hidden)}]";
    }

    // --- Page navigation, for the pill ---------------------------------------

    /// <summary>
    /// Scrolls so page <paramref name="number"/> (1-based) starts at the top of the
    /// viewport. Out-of-range numbers are clamped rather than refused: a typed 0 or 999
    /// means "the start" or "the end", which is what every viewer does with them.
    /// </summary>
    internal void GoToPage(int number)
    {
        if (Active is not { Pages.Count: > 0 } vm)
            return;
        var index = Math.Clamp(number, 1, vm.Pages.Count) - 1;

        var above = 0.0;
        foreach (var page in vm.Pages)
        {
            if (page.Index == index)
                break;
            above += page.LayoutHeight + PageGap;
        }
        // The list's own top margin (MainWindow.axaml, PageList Margin="0,16").
        PageScroller.Offset = new Vector(PageScroller.Offset.X, Math.Max(0, above));
        vm.CurrentPage = index + 1;
        ShowReadingPill();
    }
}
