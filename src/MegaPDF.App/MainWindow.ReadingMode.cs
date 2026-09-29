using System.Collections.Specialized;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Viewing;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MegaPDF.App;

/// <summary>
/// Reading mode (#504 tier 1, #510 tier 2; docs/reading-mode-plan.md §2 and §4
/// "Windows").
///
/// The page and nothing else. The page host never moves: only the chrome around it
/// goes, which is why leaving the mode puts you back on the same page, at the same
/// zoom and the same scroll offset, with no re-render — the invariant §1 of the plan
/// asks every platform to keep.
///
/// It is a **window** state, not a document's. The chrome it hides belongs to this
/// window, Ctrl+Tab still switches tabs inside it, and every tab is told about it
/// (<see cref="ApplyReadingModeToTabs"/>) because what changes per document is only
/// what a click on the page means. That is also why there is no per-document memory of
/// it: with tabs, a per-document flag would flip the whole window's chrome as you
/// switched tab (plan §2; decision 2 on #168). The one preference is
/// <c>AppSettings.OpenInReadingMode</c>, off by default, in the same settings.json the
/// Avalonia desktops read (#511).
///
/// The Windows idiom (plan §4), not a port of the Mac one:
///
/// * **Reading mode** in the zoom <c>DropDownButton</c>'s flyout, beside Fit width and
///   Fit page, and in the "…" overflow — the two places a Windows user looks for a
///   view command that is not a tool.
/// * **Ctrl+H** on <c>RootGrid.KeyboardAccelerators</c>, with the rest of the app's
///   shortcuts, so it keeps working when a command has overflowed.
/// * **F11** for full screen, and only inside reading mode: full screen on its own is
///   not offered (plan §2 — "that was Acrobat's confusion"). It is the first use of
///   <see cref="AppWindowPresenterKind.FullScreen"/> in this app; everything else runs
///   on the <see cref="OverlappedPresenter"/> MainWindow.Toolbar.cs configures.
/// * The existing page-indicator pill grows into the floating bar
///   (<c>DocumentView.ReadingMode.cs</c>), in Fluent acrylic at radius.control 4.
/// * Announcements through <c>RaiseNotificationEvent</c> on the pages pane, which is
///   what the rest of this app already announces through.
///
/// Accessibility is the point of the feature rather than a coat of paint on it (plan
/// §7). Hidden chrome is <see cref="Visibility.Collapsed"/> — never dimmed, never a
/// transparent overlay — because an invisible-but-present toolbar is a focus trap and
/// reading mode is meant to be *the* screen-reader-friendly view.
/// </summary>
public sealed partial class MainWindow
{
    private bool _isReadingMode;
    private OverlappedPresenter? _windowedPresenter;
    private DependencyObject? _tabStripHost;
    private bool _honouredOpenInReadingMode;

    /// <summary>Whether this window is in reading mode. A window state; see the class doc.</summary>
    internal bool IsReadingMode => _isReadingMode;

    /// <summary>
    /// Whether this window is full screen — only ever reached from inside reading mode.
    /// </summary>
    internal bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    /// <summary>
    /// The chrome reading mode is responsible for, in the order MainWindow.xaml
    /// declares it. The find bar is not here: it follows the active tab and is the one
    /// piece allowed to appear over the reading view, so it is closed on entry instead
    /// (see <see cref="EnterReadingMode"/>).
    /// </summary>
    private IEnumerable<FrameworkElement> ChromeHosts
    {
        get
        {
            yield return BusyStrip;
            yield return Toolbar;
            if (TabStripHost is FrameworkElement strip)
                yield return strip;
        }
    }

    /// <summary>
    /// The tab strip inside the <c>TabView</c>'s template. WinUI offers no
    /// <c>IsTabStripVisible</c>, and the strip is not a child of anything this file
    /// declares, so it is found by the template part name TabView itself uses
    /// (<c>TabContainerGrid</c>, with <c>TabListView</c> as the fallback if a future
    /// toolkit renames the outer grid). Collapsing it collapses the Auto row it sits
    /// in, which is what takes it out of the layout and out of the tab order alike.
    /// </summary>
    private DependencyObject? TabStripHost =>
        _tabStripHost ??= FindByName(DocumentsTabView, "TabContainerGrid")
                          ?? FindByName(DocumentsTabView, "TabListView");

    private static DependencyObject? FindByName(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name)
            return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindByName(VisualTreeHelper.GetChild(root, i), name) is { } found)
                return found;
        }
        return null;
    }

    private void InitializeReadingMode()
    {
        SetLabels(ReadingModeButton, Strings.ReadingMode, Strings.ReadingModeTip);
        ReadingModeItem.Text = Strings.ReadingMode;
        ToolTipService.SetToolTip(ReadingModeItem, Strings.ReadingModeTip);

        // The busy strip's visibility is bound to the active tab's busy state, so a
        // save that finishes inside reading mode would otherwise push it back on
        // screen — and with it a control the mode has promised is not there. Re-asserted
        // rather than unbound, so the binding stays the one source of truth outside the
        // mode.
        BusyStrip.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
        {
            if (_isReadingMode && BusyStrip.Visibility == Visibility.Visible)
                BusyStrip.Visibility = Visibility.Collapsed;
        });

        // "Open documents in reading mode" (#510, #168 decision 2): honoured once per
        // window, when its first tab arrives. Once per window and not per tab, so that
        // leaving the mode and then opening a second file does not drag the person back
        // into it.
        Shell.Documents.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add
                && !_honouredOpenInReadingMode
                && Shell.Settings.OpenInReadingMode)
            {
                _honouredOpenInReadingMode = true;
                EnterReadingMode();
            }
            ApplyReadingModeToTabs();
        };

        // Escape's ladder. On RootGrid's PreviewKeyDown, which tunnels from the root
        // down, so this runs *before* the find bar's and the page's own Escape handling
        // — which is the order the plan asks for. Outside reading mode it never claims
        // the key, so everything Escape did before it still does.
        RootGrid.PreviewKeyDown += OnRootPreviewKeyDown;
    }

    // --- Entering and leaving -------------------------------------------------

    private void OnReadingModeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleReadingMode();
    }

    private void OnReadingModeClicked(object sender, RoutedEventArgs e) => ToggleReadingMode();

    /// <summary>Ctrl+H, the zoom flyout's item, and the More menu's.</summary>
    internal void ToggleReadingMode()
    {
        if (_isReadingMode)
            ExitReadingMode();
        else
            EnterReadingMode();
    }

    /// <summary>
    /// Hides the chrome and puts the floating bar up. Nothing about any document
    /// changes: no save, no render, no scroll — the unsaved dot stays exactly as it was.
    /// </summary>
    internal void EnterReadingMode()
    {
        // A tab, not necessarily a loaded one: the empty state framed by nothing is not
        // a reading view, so that case is refused.
        if (_isReadingMode || !Shell.HasDocuments)
            return;

        _isReadingMode = true;

        // Editing is off, so anything already armed is disarmed on the way in and does
        // not come back on the way out (plan §2). Undo and redo stay live: they act on
        // the document, not on the page, and nothing can be added to the stack while
        // editing is off.
        foreach (var document in Shell.Documents)
        {
            document.CancelPlacementModes();
            document.ClearPageFocus();
        }

        // The find bar is the one piece of chrome that may come back over the reading
        // view (Ctrl+F still works). It is closed here so that entering the mode leaves
        // no chrome at all; reopening it is one keystroke.
        ActiveDocumentView?.CloseFindBar();

        // The bar first, so there is somewhere for focus to go before the toolbar it
        // may be sitting on disappears.
        ApplyReadingModeToTabs();

        var hadFocusInChrome = FocusIsInHiddenChrome();
        foreach (var host in ChromeHosts)
            host.Visibility = Visibility.Collapsed;

        // Focus only moves if it was on something that has just gone; otherwise it is
        // left where the person put it. Moving it unasked would also mean the bar holds
        // focus and therefore never fades, for everybody.
        if (hadFocusInChrome)
            ActiveDocumentView?.ReadingExitControl.Focus(FocusState.Keyboard);

        Announce(Strings.ReadingModeOn);
    }

    /// <summary>
    /// Puts every host back, drops full screen if it was on, restores keyboard focus to
    /// the toolbar and says so.
    /// </summary>
    internal void ExitReadingMode()
    {
        if (!_isReadingMode)
            return;

        _isReadingMode = false;
        // Full screen is a level *inside* reading mode, so leaving the mode leaves it
        // too — a full-screen window wearing its toolbar again is neither level.
        if (IsFullScreen)
            LeaveFullScreen();

        foreach (var host in ChromeHosts)
            host.Visibility = Visibility.Visible;
        // The busy strip is bound to work that may well have finished while the mode
        // was on; hand it back to its binding rather than leaving it pinned open.
        BusyStrip.Visibility = Shell.Active?.Busy.ShowsStrip == true ? Visibility.Visible : Visibility.Collapsed;

        ApplyReadingModeToTabs();

        // "Restores focus to the toolbar" means Open, which is the one control that is
        // enabled with or without a document.
        OpenButton.Focus(FocusState.Keyboard);

        Announce(Strings.ReadingModeOff);
    }

    /// <summary>
    /// Mirrors the window's mode onto every tab it holds, keeps the bar's file name
    /// honest, and pushes the app's page colours down. Called on entry, on exit, and
    /// whenever the set of tabs or the active tab changes.
    /// </summary>
    internal void ApplyReadingModeToTabs()
    {
        RealiseTabs();
        var tint = Shell.Settings.PageTint;
        // The file name only earns a place on the bar when there is more than one tab —
        // with one, the window title already says it (plan §2, tabs).
        var many = Shell.Documents.Count > 1;

        foreach (var document in Shell.Documents)
        {
            document.IsReadingMode = _isReadingMode;
            document.Tint = tint;
            if (document.View is not { } view)
                continue;
            view.ApplyReadingMode(_isReadingMode);
            view.ApplyTint(tint);
            view.ShowReadingFileName(many);
            view.ExitReadingModeRequested -= OnExitReadingModeRequested;
            view.ExitReadingModeRequested += OnExitReadingModeRequested;
        }
    }

    private void OnExitReadingModeRequested(object? sender, EventArgs e) => ExitReadingMode();

    /// <summary>
    /// A tab opened while the strip is collapsed never gets its content.
    ///
    /// TabView's <c>TabViewItem</c> containers are generated by the list *inside* that
    /// strip, so with the strip <see cref="Visibility.Collapsed"/> nothing measures,
    /// nothing is generated, and the new tab's <see cref="DocumentView"/> is never
    /// realised — the window shows the empty space where a page should be. The setting
    /// that opens documents straight into reading mode makes this the ordinary path,
    /// not an edge case.
    ///
    /// So the strip is put back for exactly one layout pass and collapsed again within
    /// the same call: no frame is presented in between, so nothing flickers, and the
    /// containers exist by the time it returns. Only when a tab is actually missing its
    /// view, so the common case costs nothing.
    ///
    /// Found by <c>--screenshot-state reading</c> rather than by anybody clicking, which
    /// is the whole argument of #462.
    /// </summary>
    private void RealiseTabs()
    {
        if (!_isReadingMode
            || TabStripHost is not FrameworkElement strip
            || Shell.Documents.All(d => d.View is not null))
        {
            return;
        }
        strip.Visibility = Visibility.Visible;
        RootGrid.UpdateLayout();
        strip.Visibility = Visibility.Collapsed;
    }

    /// <summary>Whether keyboard focus is on a control reading mode is about to hide.</summary>
    private bool FocusIsInHiddenChrome()
    {
        if (Content.XamlRoot is not { } root
            || FocusManager.GetFocusedElement(root) is not DependencyObject focused)
        {
            return false;
        }
        foreach (var host in ChromeHosts)
        {
            if (DocumentView.IsWithin(focused, host))
                return true;
        }
        return false;
    }

    // --- Full screen (F11, inside reading mode only) --------------------------

    private void OnFullScreenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Not handled outside reading mode, so F11 stays free for anything else that
        // wants it rather than being silently eaten.
        if (!_isReadingMode)
            return;
        args.Handled = true;
        ToggleFullScreen();
    }

    /// <summary>
    /// F11. Only inside reading mode (plan §2). The windowed presenter is kept and put
    /// back by hand rather than asking for a fresh Overlapped one, because
    /// MainWindow.Toolbar.cs configured this one with the app's minimum window size and
    /// a new presenter would not carry it.
    /// </summary>
    internal void ToggleFullScreen()
    {
        if (!_isReadingMode)
            return;
        if (IsFullScreen)
        {
            LeaveFullScreen();
        }
        else
        {
            _windowedPresenter = AppWindow.Presenter as OverlappedPresenter;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        ActiveDocumentView?.ShowReadingBar();
    }

    private void LeaveFullScreen()
    {
        if (_windowedPresenter is { } windowed)
            AppWindow.SetPresenter(windowed);
        else
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
    }

    // --- Escape ---------------------------------------------------------------

    /// <summary>
    /// One level back, and exactly one: the find bar, then full screen, then reading
    /// mode (<see cref="ReadingMode.NextEscape"/>, plan §7). Anything else leaves the
    /// key alone, so outside the mode Escape still cancels an armed tool, drops a
    /// selection and lets go of a focused region exactly as it always did.
    ///
    /// Get the order wrong and a person pressing Escape to mean "give me the toolbar
    /// back" loses a tool state they never meant to drop — which is why the decision
    /// lives in Core, where a test can reach it, rather than as an if-chain here.
    /// </summary>
    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || !_isReadingMode || DialogGate.IsShowing)
            return;
        e.Handled = StepBackFromReadingMode() != ReadingModeStep.Nothing;
    }

    /// <summary>
    /// Takes one level back and says which. Separate from the key handler so the
    /// self-test can drive the ladder itself: a WinUI process cannot synthesise its own
    /// key presses, so the alternative would be no check of the ordering at all.
    /// </summary>
    internal ReadingModeStep StepBackFromReadingMode()
    {
        var step = ReadingMode.NextEscape(
            isFindOpen: ActiveDocumentView?.IsFindBarOpen ?? false,
            isFullScreen: IsFullScreen,
            isReadingMode: _isReadingMode);

        switch (step)
        {
            case ReadingModeStep.CloseFind:
                ActiveDocumentView?.CloseFindBar();
                ActiveDocumentView?.ShowReadingBar();
                break;
            case ReadingModeStep.LeaveFullScreen:
                LeaveFullScreen();
                ActiveDocumentView?.ShowReadingBar();
                break;
            case ReadingModeStep.LeaveReadingMode:
                ExitReadingMode();
                break;
        }
        return step;
    }

    // --- Page colours (#510) --------------------------------------------------

    /// <summary>
    /// Pushes the app's chosen page colours onto every tab in this window. The engine
    /// tints the page (<c>MEGAPDF_RENDER_SEPIA</c> / <c>_NIGHT</c>, #509) and the
    /// <c>BrandReading*</c> tokens tint the gutter and the bar around it, so the two
    /// halves of the same choice arrive together.
    /// </summary>
    internal void ApplyPageColours()
    {
        var tint = Shell.Settings.PageTint;
        foreach (var document in Shell.Documents)
        {
            document.Tint = tint;
            document.View?.ApplyTint(tint);
        }
    }

    // --- For --screenshot-state reading, and the capture rig ------------------

    /// <summary>The mode, in one line, the way DescribeToolbar reports the toolbar.</summary>
    internal string DescribeReadingMode()
    {
        var hidden = ChromeHosts.Where(h => h.Visibility != Visibility.Visible).Select(h => h.Name);
        var bar = ActiveDocumentView is { } view
            ? view.IsReadingBarShown ? "shown" : "faded"
            : "no tab";
        return $"reading mode: {(_isReadingMode ? "on" : "off")}, full screen={IsFullScreen}, "
               + $"bar={bar}, page colours={Shell.Settings.PageTint}, "
               + $"hidden=[{string.Join(", ", hidden)}], tab strip found={TabStripHost is not null}";
    }

    /// <summary>
    /// The window's accelerator grid, as "Modifiers+Key" strings. The self-test reads
    /// it because a WinUI process cannot press its own keys: this is how far "Ctrl+H is
    /// bound, on RootGrid, where an overflowed command can still hear it" can be
    /// checked from inside the app.
    /// </summary>
    internal IReadOnlyList<string> ReadingAcceleratorsForTest() =>
        [.. RootGrid.KeyboardAccelerators.Select(a => $"{a.Modifiers}+{a.Key}")];

    /// <summary>
    /// Where Reading mode is offered from, by name: the zoom flyout's item and the More
    /// menu's button, both carrying the decided name (plan §4).
    /// </summary>
    internal IReadOnlyList<string> ReadingModeEntryPointsForTest()
    {
        var found = new List<string>();
        if (ZoomMenu.Items.Contains(ReadingModeItem) && ReadingModeItem.Text == Strings.ReadingMode)
            found.Add("ZoomMenu/ReadingModeItem");
        if (Toolbar.SecondaryCommands.Contains(ReadingModeButton) && ReadingModeButton.Label == Strings.ReadingMode)
            found.Add("More/ReadingModeButton");
        return found;
    }

    /// <summary>
    /// Every control in this window that Tab can currently reach, by automation id —
    /// the tab order as the keyboard sees it. What makes "hidden chrome leaves the tab
    /// order" an assertion rather than a hope.
    /// </summary>
    internal IReadOnlyList<string> FocusableControlIds()
    {
        var found = new List<string>();
        Walk(RootGrid);
        return found;

        void Walk(DependencyObject node)
        {
            if (node is Control { IsTabStop: true, IsEnabled: true, Visibility: Visibility.Visible } control
                && IsEffectivelyVisible(control))
            {
                found.Add(AutomationProperties.GetAutomationId(control) is { Length: > 0 } id
                    ? id
                    : control.GetType().Name);
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }
    }

    /// <summary>
    /// Whether every ancestor of <paramref name="element"/> is visible too. A control
    /// inside a Collapsed host reports Visible itself, so asking only the control would
    /// be exactly the mistake this is meant to catch.
    /// </summary>
    private static bool IsEffectivelyVisible(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: Visibility.Collapsed })
                return false;
        }
        return true;
    }
}
