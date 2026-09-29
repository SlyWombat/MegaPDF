using MegaPDF.Core.Engine;
using MegaPDF.Core.Viewing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MegaPDF.App;

/// <summary>
/// This tab's half of reading mode (#504 tier 1, #510 tier 2): the floating bar the
/// page-indicator pill grows into, and the page colours the gutter behind the pages
/// follows.
///
/// The mode itself is the window's (<see cref="MainWindow"/>'s
/// <c>MainWindow.ReadingMode.cs</c>) — the chrome it hides belongs to the window, and
/// Ctrl+Tab still switches tabs inside it. What belongs here is what is per document:
/// the page host, the pill over it, and what a click on the page means.
///
/// Three rules from the plan (§2, §7) are kept here rather than assumed:
///
/// * Everything the bar grows is <see cref="Visibility.Collapsed"/> outside the mode
///   and collapsed again on the way out — never dimmed, never merely transparent. A
///   collapsed control is not a tab stop; an invisible one is, and reading mode is the
///   view a screen-reader user is most likely to be in.
/// * The bar never fades while it holds keyboard focus or while a screen reader is
///   running (<see cref="ScreenReader"/>, <see cref="ReadingMode.BarMayFade"/>).
/// * Nothing animates. That is the reduced-motion answer taken unconditionally: the
///   plan asks for show/hide when the setting is on, and a fade nobody can switch off
///   would be worse than no fade at all.
/// </summary>
public sealed partial class DocumentView
{
    /// <summary>How long the bar stays up after the last movement (plan §2: ~2 s).</summary>
    private static readonly TimeSpan BarIdle = TimeSpan.FromSeconds(2);

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _barTimer;
    private bool _readingWired;

    /// <summary>Raised by the bar's Exit button; the window owns leaving the mode.</summary>
    internal event EventHandler? ExitReadingModeRequested;

    /// <summary>The bar's way out — where focus goes when the chrome it was on disappears.</summary>
    internal Control ReadingExitControl => ReadingExitButton;

    /// <summary>Whether the bar is on screen right now.</summary>
    internal bool IsReadingBarShown => PageIndicatorPill.Visibility == Visibility.Visible;

    /// <summary>The file name the bar is showing, or "" when it is not showing one.</summary>
    internal string ReadingFileNameText =>
        ReadingFileName.Visibility == Visibility.Visible ? ReadingFileName.Text : "";

    /// <summary>
    /// Whether the idle countdown is running — that is, whether the bar is allowed to
    /// fade at all. For the self-test: "a screen reader is on" has to mean the
    /// countdown never starts, not merely that it is slow.
    /// </summary>
    internal bool ReadingBarIsCountingDown => _barTimer is { IsRunning: true };

    /// <summary>Fires the idle tick now, as the timer would. For the self-test only.</summary>
    internal void FadeReadingBarNowForTest() => OnBarIdle();

    /// <summary>
    /// Routes an activation at the centre of page 1's first region of <paramref
    /// name="kind"/> — the one route a tap, Enter and Space all take — and says whether
    /// there was such a region to aim at. For <c>--screenshot-state reading</c>, which
    /// needs "a click that would change the document" to be a real click and not a
    /// flag: the check is worthless if the click would have done nothing anyway.
    /// </summary>
    internal async Task<bool> ActivateFirstRegionForTest(PageHitKind kind)
    {
        if (ViewModel.Pages.Count == 0
            || FindPageCanvas(0) is not { } canvas
            || ViewModel.Pages[0].Regions.FirstOrDefault(r => r.Kind == kind) is not { } region)
        {
            return false;
        }
        await RoutePageActivationAsync(canvas, ViewModel.Pages[0], region.Bounds.Center);
        return true;
    }

    /// <summary>Every control the bar grows, in the order it shows them.</summary>
    private Control[] ReadingBarControls =>
    [
        ReadingPreviousButton, ReadingPageButton, ReadingNextButton,
        ReadingFitWidthButton, ReadingFitPageButton,
        ReadingZoomOutButton, ReadingZoomInButton, ReadingExitButton,
    ];

    private void InitializeReadingMode()
    {
        if (_readingWired)
            return;
        _readingWired = true;

        // Words, not glyphs: what Narrator reads at an icon-only step is stated, the
        // contract the toolbar and the find bar already keep (#237).
        NameControl(ReadingPreviousButton, Strings.PreviousPage);
        NameControl(ReadingNextButton, Strings.NextPage);
        NameControl(ReadingPageButton, Strings.GoToPage);
        NameControl(ReadingFitWidthButton, Strings.ToolbarFitWidth);
        NameControl(ReadingFitPageButton, Strings.ToolbarFitPage);
        NameControl(ReadingZoomOutButton, Strings.ToolbarZoomOut);
        NameControl(ReadingZoomInButton, Strings.ToolbarZoomIn);
        NameControl(ReadingExitButton, Strings.ExitReadingMode);
        ReadingExitButton.Content = Strings.Exit;
        ReadingGoButton.Content = Strings.GoToPage;
        NameControl(ReadingPageBox, Strings.GoToPage);
        AutomationProperties.SetName(PageIndicatorPill, Strings.ReadingControls);

        // Movement anywhere over this tab's page area brings the bar back — over the
        // page, over the gutter, over the scroll bar. handledEventsToo, because the
        // page canvas handles its own moves while a whiteout band is being dragged.
        DocumentAreaRoot.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler((_, _) => ShowReadingBar()), handledEventsToo: true);

        // Keyboard focus inside the bar keeps it up for as long as it is there: a bar
        // that faded out from under the Tab key would be a control you can reach once.
        PageIndicatorPill.GotFocus += (_, _) => ShowReadingBar();
        PageIndicatorPill.LostFocus += (_, _) => RestartBarTimer();

        // A tab is added, becomes active and *then* finishes opening, so the window
        // asks for the file name before there is one. The window decides whether a name
        // belongs on the bar; this decides what it is, and refreshes it when the answer
        // changes. Pushing the string instead left the bar blank on a second tab — found
        // by --screenshot-state reading, not by hand.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DocumentViewModel.DocumentPath))
                RefreshReadingFileName();
        };
    }

    private static void NameControl(Control control, string name)
    {
        AutomationProperties.SetName(control, name);
        ToolTipService.SetToolTip(control, name);
    }

    /// <summary>
    /// Puts this tab into the mode, or takes it out. Called by the window on entry, on
    /// exit, and for every tab it holds — the mode is the window's, and a tab switched
    /// to inside it is already in it.
    ///
    /// Nothing about the document changes here: no save, no re-render, no scroll. The
    /// page host is untouched, which is why leaving puts you back on the same page at
    /// the same zoom and the same offset (plan §1).
    /// </summary>
    internal void ApplyReadingMode(bool on)
    {
        InitializeReadingMode();
        ViewModel.IsReadingMode = on;

        // Collapsed, not hidden: a collapsed control is out of the tab order, and the
        // ordinary document view must not have eight invisible buttons in it either.
        var barVisibility = on ? Visibility.Visible : Visibility.Collapsed;
        foreach (var control in ReadingBarControls)
            control.Visibility = barVisibility;
        PageIndicatorText.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        // The pill becomes the bar: bottom centre, hit-testable, Fluent acrylic and
        // radius.control 4 (docs/design-tokens.md §3).
        PageIndicatorPill.IsHitTestVisible = on;
        PageIndicatorPill.HorizontalAlignment = on ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        PageIndicatorPill.Margin = on ? new Thickness(0, 0, 0, 28) : new Thickness(0, 0, 28, 14);
        PageIndicatorPill.Padding = on ? new Thickness(6, 4, 6, 4) : new Thickness(10, 3, 10, 3);
        PageIndicatorPill.CornerRadius = on ? new CornerRadius(4) : new CornerRadius(12);
        ApplyBarGround();

        if (on)
        {
            RefreshReadingFileName();
            ShowReadingBar();
        }
        else
        {
            _barTimer?.Stop();
            // The plain pill is always up; only the bar fades.
            PageIndicatorPill.Visibility = ViewModel.DocumentVisibility;
            RefreshReadingFileName();
            ReadingPageFlyout.Hide();
        }
    }

    /// <summary>
    /// Whether the bar should name the file: only when the window has more than one tab
    /// — with one, the title bar already says it (plan §2, tabs). The window decides;
    /// the name itself comes from this tab's document, whenever it becomes known.
    /// </summary>
    internal void ShowReadingFileName(bool show)
    {
        _showFileName = show;
        RefreshReadingFileName();
    }

    private bool _showFileName;

    private void RefreshReadingFileName()
    {
        var name = ViewModel.OpenDocumentName;
        ReadingFileName.Text = name;
        ReadingFileName.Visibility = _showFileName && ViewModel.IsReadingMode && name.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // --- Showing and fading ---------------------------------------------------

    /// <summary>
    /// Up, and counting down again. Called on entry, on pointer movement over the page
    /// area, whenever the bar takes keyboard focus, and after every command it runs.
    /// </summary>
    internal void ShowReadingBar()
    {
        if (!ViewModel.IsReadingMode)
            return;
        PageIndicatorPill.Visibility = Visibility.Visible;
        RestartBarTimer();
    }

    /// <summary>
    /// Starts (or restarts) the idle countdown — unless something says the bar must
    /// stay. Both reasons are the same reason: the person cannot get it back by
    /// waggling the mouse (<see cref="ReadingMode.BarMayFade"/>).
    /// </summary>
    private void RestartBarTimer()
    {
        _barTimer?.Stop();
        if (!ViewModel.IsReadingMode || !BarMayFade())
            return;

        if (_barTimer is null)
        {
            _barTimer = DispatcherQueue.CreateTimer();
            _barTimer.IsRepeating = false;
            _barTimer.Tick += (_, _) => OnBarIdle();
        }
        _barTimer.Interval = BarIdle;
        _barTimer.Start();
    }

    /// <summary>
    /// The idle tick. The guard is repeated here and not only in
    /// <see cref="RestartBarTimer"/>: a later change that starts the timer from
    /// somewhere else must still not be able to take the bar away from a screen-reader
    /// user, and the self-test forces this tick to prove that.
    /// </summary>
    private void OnBarIdle()
    {
        _barTimer?.Stop();
        if (!ViewModel.IsReadingMode || !BarMayFade())
            return;
        // No animation, deliberately: see the class doc on reduced motion.
        PageIndicatorPill.Visibility = Visibility.Collapsed;
    }

    private bool BarMayFade() =>
        ReadingMode.BarMayFade(ScreenReader.IsRunning(), BarHasFocus());

    private bool BarHasFocus() =>
        XamlRoot is { } root
        && FocusManager.GetFocusedElement(root) is DependencyObject focused
        && IsWithin(focused, PageIndicatorPill);

    // --- Page colours (#510) --------------------------------------------------

    /// <summary>
    /// The gutter behind the pages and the bar over them, in the current tint. The
    /// engine tints the page itself (<c>MEGAPDF_RENDER_SEPIA</c> / <c>_NIGHT</c>,
    /// #509); this is the chrome around it, from the <c>BrandReading*</c> tokens
    /// (docs/design-tokens.md §5). Normal hands both properties back to the markup, so
    /// the window is exactly what it was — a brush resolved once here would still be
    /// the light one after Windows switched the app to dark underneath it, which is the
    /// trap Brand.cs's own doc describes.
    /// </summary>
    internal void ApplyTint(PageTint tint)
    {
        ViewModel.Tint = tint;
        if (tint == PageTint.Normal)
            PagesScroll.ClearValue(Control.BackgroundProperty);
        else
            PagesScroll.Background = Brand.Brush(tint == PageTint.Sepia ? "BrandReadingGutterSepiaBrush" : "BrandReadingGutterNightBrush");
        ApplyBarGround();
    }

    /// <summary>
    /// The bar's own ground, and the ink on it. In reading mode both follow the page
    /// colours where one is chosen and Fluent's in-app acrylic where none is; out of
    /// it, the pill is the solid chrome it always was.
    ///
    /// The ink is set on each label and button rather than on the Border, because a
    /// <see cref="Border"/> has no Foreground of its own for them to inherit — and
    /// clearing it (rather than setting the theme's brush by hand) is what keeps them
    /// following Windows into dark mode afterwards.
    /// </summary>
    private void ApplyBarGround()
    {
        var tint = ViewModel.Tint;
        var tinted = ViewModel.IsReadingMode && tint != PageTint.Normal;

        if (tinted)
            PageIndicatorPill.Background = Brand.Brush(tint == PageTint.Sepia ? "BrandReadingSurfaceSepiaBrush" : "BrandReadingSurfaceNightBrush");
        else if (ViewModel.IsReadingMode)
            PageIndicatorPill.Background = AcrylicOrSolid();
        else
            PageIndicatorPill.ClearValue(Border.BackgroundProperty);

        var ink = tinted
            ? Brand.Brush(tint == PageTint.Sepia ? "BrandReadingInkSepiaBrush" : "BrandReadingInkNightBrush")
            : null;
        foreach (var label in new[] { ReadingFileName, PageIndicatorText })
        {
            if (ink is null)
                label.ClearValue(TextBlock.ForegroundProperty);
            else
                label.Foreground = ink;
        }
        foreach (var button in ReadingBarControls)
        {
            if (ink is null)
                button.ClearValue(Control.ForegroundProperty);
            else
                button.Foreground = ink;
        }
    }

    /// <summary>
    /// Fluent's in-app acrylic for the floating bar, or the solid secondary ground when
    /// this Windows build has no acrylic to give (transparency off in Settings, a
    /// remote session): asking for a theme resource that is not there throws, and a
    /// missing backdrop is not a reason for reading mode to fail to open.
    /// </summary>
    private static Brush AcrylicOrSolid() =>
        Application.Current.Resources.TryGetValue("AcrylicInAppFillColorDefaultBrush", out var acrylic) && acrylic is Brush brush
            ? brush
            : (Brush)Application.Current.Resources["SolidBackgroundFillColorSecondaryBrush"];

    // --- The bar's commands ---------------------------------------------------

    private void OnReadingPreviousClicked(object sender, RoutedEventArgs e) => GoToPage(ViewModel.CurrentPage - 1);

    private void OnReadingNextClicked(object sender, RoutedEventArgs e) => GoToPage(ViewModel.CurrentPage + 1);

    private void OnReadingExitClicked(object sender, RoutedEventArgs e) =>
        ExitReadingModeRequested?.Invoke(this, EventArgs.Empty);

    private async void OnReadingFitWidthClicked(object sender, RoutedEventArgs e)
    {
        ShowReadingBar();
        await ViewModel.FitWidthAsync(PagesScroll.ViewportWidth);
    }

    private async void OnReadingFitPageClicked(object sender, RoutedEventArgs e)
    {
        ShowReadingBar();
        await ViewModel.FitPageAsync(PagesScroll.ViewportWidth, PagesScroll.ViewportHeight);
    }

    private void OnReadingZoomOutClicked(object sender, RoutedEventArgs e)
    {
        ShowReadingBar();
        if (ViewModel.ZoomOutCommand.CanExecute(null))
            ViewModel.ZoomOutCommand.Execute(null);
    }

    private void OnReadingZoomInClicked(object sender, RoutedEventArgs e)
    {
        ShowReadingBar();
        if (ViewModel.ZoomInCommand.CanExecute(null))
            ViewModel.ZoomInCommand.Execute(null);
    }

    private void OnReadingPageBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        CommitGoToPage();
    }

    private void OnReadingGoClicked(object sender, RoutedEventArgs e) => CommitGoToPage();

    private void CommitGoToPage()
    {
        if (int.TryParse(ReadingPageBox.Text, System.Globalization.NumberStyles.Integer,
                         System.Globalization.CultureInfo.CurrentCulture, out var page))
        {
            GoToPage(page);
        }
        ReadingPageBox.Text = "";
        ReadingPageFlyout.Hide();
    }

    /// <summary>
    /// Scrolls so page <paramref name="number"/> (1-based) starts at the top of the
    /// viewport. Out-of-range numbers are clamped rather than refused: a typed 0 or 999
    /// means "the start" or "the end", which is what every viewer does with them.
    ///
    /// The geometry is the same the scroll handler reads back — 24 dip of panel padding
    /// above the first page and 16 between them (DocumentView.xaml's ItemsPanel).
    /// </summary>
    internal void GoToPage(int number)
    {
        if (ViewModel.Pages.Count == 0)
            return;
        var index = Math.Clamp(number, 1, ViewModel.Pages.Count) - 1;

        var y = 24d;
        for (var i = 0; i < index; i++)
            y += ViewModel.Pages[i].Height + 16;

        PagesScroll.ChangeView(null, Math.Max(0, y), null, disableAnimation: !AnimationsEnabled);
        ShowReadingBar();
    }
}
