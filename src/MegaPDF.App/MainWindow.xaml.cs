using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace MegaPDF.App;

/// <summary>
/// The window's chrome (#348 phase 1): the toolbar, the tab strip, the flyouts, dialogs,
/// closing and printing. Everything about a specific document's pages — the inline editor,
/// selection chrome, find bar, focus ring — moved to <see cref="DocumentView"/>, one instance
/// per tab; this file no longer knows about any of it directly; it reaches the active tab's
/// through <see cref="ActiveDocumentView"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>This window's tabs and the process-wide services they share (#348 phase 1).</summary>
    public ShellViewModel Shell { get; }

    private bool _allowClose;
    private readonly PdfPrinter _printer;

    public MainWindow()
    {
        var app = (App)Application.Current;
        Shell = new ShellViewModel(this, app.Settings, app.RecentFiles, app.SignatureLibrary);
        InitializeComponent();
        _printer = new PdfPrinter(this, () => Shell.Active?.CurrentDocument, () => Shell.Active?.OpenDocumentName ?? "",
                                  () => Shell.Active?.Busy, ShowActiveErrorAsync);
        _printer.Register();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "megapdf.ico"));
        ApplyTheme();
        AppWindow.Closing += OnAppWindowClosing;
        WireOverflowTooltip();
        InitializeToolbar();
        InitializeWindowKeyboard();

        Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShellViewModel.WindowTitle))
                Title = Shell.WindowTitle;
            if (e.PropertyName is nameof(ShellViewModel.Active))
                OnActiveDocumentChanged();
        };
        Title = Shell.WindowTitle;
        OnActiveDocumentChanged(); // wires up the initial (absent) tab's picker/focus hooks
    }

    /// <summary>
    /// A save/print error with no specific tab to blame (the printer's document accessor
    /// races a tab switch) reports against whichever tab is active when it is called.
    /// </summary>
    private Task ShowActiveErrorAsync(string title, string message) =>
        Shell.Active?.ShowErrorAsync(title, message) ?? Task.CompletedTask;

    /// <summary>The active tab's view — what the toolbar, accelerators and dialogs act through.</summary>
    private DocumentView? ActiveDocumentView => Shell.Active?.View;

    /// <summary>The active tab's page scroller, for --screenshot-state find-zoomed (#32) and self-tests.</summary>
    internal ScrollViewer? PageScroller => ActiveDocumentView?.PageScroller;

    /// <summary>
    /// Re-homes everything that only makes sense for one tab at a time onto whichever tab
    /// just became active (#348 phase 1, plan §4): the text-style picker sync and the
    /// keyboard/focus callbacks a <see cref="DocumentView"/> cannot reach on its own. Old
    /// subscriptions are dropped so a deactivated tab's view stops driving window chrome —
    /// the leak the plan's §6.6 flags for Avalonia's menu bar applies here too.
    /// </summary>
    private DocumentView? _wiredView;

    /// <summary>
    /// The document last wired as active, tracked separately from <see cref="_wiredView"/>
    /// because a brand-new tab becomes Active before its DocumentView is loaded (plan §4's
    /// background-tab eviction, #348 phase 1 item 7) — this must still know which document
    /// just stopped being active even on the call where the new one has no view yet.
    /// </summary>
    private DocumentViewModel? _lastActiveDocument;

    private void OnActiveDocumentChanged()
    {
        if (_wiredView is { } old)
        {
            old.TextStyleContextChanged -= OnActiveTextStyleContextChanged;
            old.FocusOpenButton = null;
            old.FocusIsInToolbar = null;
            old.IsFocusMovingToPickers = null;
            old.RequestPickerFocusCallback = null;
        }

        // A background tab's bitmaps are freed the moment it stops being active, and the
        // newly active one's viewport re-renders — the eviction/re-render code paths
        // already existed (RestoreSessionAsync's placeholder loop, UpdateViewportAsync);
        // this is what wires them to a tab switch rather than only to restore.
        if (_lastActiveDocument is { } previouslyActive && previouslyActive != Shell.Active)
            previouslyActive.EvictBackgroundRenders();
        _lastActiveDocument = Shell.Active;
        if (Shell.Active is { } nowActive)
            _ = nowActive.ReactivateRendersAsync();

        var view = ActiveDocumentView;
        _wiredView = view;
        if (view is not null)
        {
            view.TextStyleContextChanged += OnActiveTextStyleContextChanged;
            view.FocusOpenButton = () => OpenButton.Focus(FocusState.Keyboard);
            view.FocusIsInToolbar = () => Content.XamlRoot is { } root && IsInToolbar(FocusManager.GetFocusedElement(root) as DependencyObject);
            view.IsFocusMovingToPickers = () =>
                Content.XamlRoot is { } root
                && FocusManager.GetFocusedElement(root) is DependencyObject focused
                && (DocumentView.IsWithin(focused, FontPicker) || DocumentView.IsWithin(focused, SizePicker) || focused is ComboBoxItem);
            view.RequestPickerFocusCallback = () =>
            {
                if (FontPickerItem.Visibility != Visibility.Visible || FontPickerItem.IsInOverflow)
                    return false;
                FontPicker.Focus(FocusState.Keyboard);
                return true;
            };
        }
        UpdateTextPickers();
        if (Shell.Active is { } active)
            ShowStyleInPickers(active.LastTextStyle);
    }

    private void OnActiveTextStyleContextChanged(object? sender, EventArgs e) => UpdateTextPickers();

    // --- Drag-and-drop: every .pdf dropped anywhere in the window opens as a tab (#348 phase 1) ---

    private void OnDocumentAreaDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private async void OnDocumentAreaDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var pdf in items.OfType<StorageFile>()
                     .Where(f => f.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase)))
        {
            await Shell.OpenInTabAsync(pdf.Path);
        }
    }

    // --- The tab strip ---

    private void OnDocumentsTabViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectedItem is already TwoWay-bound to Shell.Active; this only exists so a
        // pointer-driven tab switch (not through the binding) still re-syncs the pickers —
        // in practice the binding covers it, but belt-and-braces costs nothing here.
        OnActiveDocumentChanged();
    }

    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is not DocumentViewModel doc)
            return;
        await CloseTabAsync(doc);
    }

    /// <summary>
    /// Closes one tab, asking about its unsaved changes first (#348 phase 1, plan §5.6) — a
    /// Cancel leaves this tab's journal and its place in the strip untouched, and never
    /// touches any other tab. Closing the last tab closes the window (Ctrl+W's convention,
    /// plan §1), the same as pressing the × on it.
    /// </summary>
    internal async Task CloseTabAsync(DocumentViewModel doc)
    {
        await doc.Busy.WhenIdleAsync();
        if (doc.HasUnsavedChanges && !await doc.ConfirmSaveChangesAsync())
            return; // Cancel — this tab (and only this tab) stays open, its journal intact

        doc.EndJournalSession();
        if (Shell.Documents.Count == 1 && Shell.Documents[0] == doc)
        {
            // The last tab: close the window rather than leave it showing the empty state,
            // matching Ctrl+W's "closes the tab; with one tab it closes the window" rule.
            _allowClose = true;
            Close();
            return;
        }
        Shell.RemoveDocument(doc);
    }

    private void OnCloseTabAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Shell.Active is { } active)
            _ = CloseTabAsync(active);
    }

    private void OnCloseTabClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.Active is { } active)
            _ = CloseTabAsync(active);
    }

    private void OnNewWindowAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenNewWindow();
    }

    private void OnNewWindowClicked(object sender, RoutedEventArgs e) => OpenNewWindow();

    private static void OpenNewWindow()
    {
        var window = new MainWindow();
        window.Activate();
    }

    private async void OnPrintClicked(object sender, RoutedEventArgs e)
    {
        // The button is disabled without the print permission; Ctrl+P rides on it (#131).
        if (Shell.Active?.IsPrintAllowed == true)
            await _printer.ShowPrintUiAsync();
    }

    /// <summary>The restricted notice's action moved with the InfoBar into DocumentView; nothing here now.</summary>

    // --- Find in document (toolbar Find / Ctrl+F, issue #26) — acts on the active tab ---

    private void OnFindAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        args.Handled = ActiveDocumentView?.ShowFindBar() ?? false;

    private void OnFindClicked(object sender, RoutedEventArgs e) => ActiveDocumentView?.ShowFindBar();

    /// <summary>
    /// ⋮ → Clear all marks (#329): drops every mark on the active tab's document as **one**
    /// undo step, so a person who over-marked starts again with one press of Undo to regret it.
    /// </summary>
    private async void OnClearRedactionMarksClicked(object sender, RoutedEventArgs e)
    {
        if (ActiveDocumentView is not { } view)
            return;
        if (await view.ClearRedactionMarksAsync())
            Announce(Strings.RedactMarksCleared);
    }

    /// <summary>
    /// Says <paramref name="text"/> through Narrator, on the active tab's pages pane (the
    /// window itself has nothing to anchor a notification on).
    /// </summary>
    private void Announce(string text) => ActiveDocumentView?.Announce(text);

    private async void OnFitWidthClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.Active is { } active && ActiveDocumentView is { } view)
            await active.FitWidthAsync(view.PageScroller.ViewportWidth);
    }

    private async void OnFitPageClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.Active is { } active && ActiveDocumentView is { } view)
            await active.FitPageAsync(view.PageScroller.ViewportWidth, view.PageScroller.ViewportHeight);
    }

    // --- Crash recovery offer (SDD §3.4: one-click restore after an unclean exit) ---
    // Once per app launch (#348 phase 1, plan §5.3) — App.xaml.cs's OnLaunched calls this
    // once, on the first window, before any launched/reopened file opens. Loops every
    // crashed session newest-first, each into its own new tab; a redirected launch, the
    // Linux socket path and a Finder open to a running app are all out of phase 1's scope
    // (no single-instance redirection yet), so "once per launch" and "once per window" are
    // the same thing today — this only had to stop being "only sessions[0]".

    public async Task OfferCrashRecoveryAsync()
    {
        // A session-less scan (no BeginSession): this process's own live tabs, once any
        // exist, exclude themselves from FindRecoverableSessions by holding their journals
        // with FileShare.None, not by identity — see RecoveryJournalTests.
        var scan = new Core.Recovery.RecoveryJournal();
        var sessions = scan.FindRecoverableSessions();
        if (sessions.Count == 0)
            return;

        // Right after Activate the visual tree may not be loaded yet, and
        // ContentDialog needs a live XamlRoot.
        if (Content is FrameworkElement { IsLoaded: false } root)
        {
            var loaded = new TaskCompletionSource();
            root.Loaded += (_, _) => loaded.TrySetResult();
            await loaded.Task;
        }

        foreach (var session in sessions)
        {
            var dialog = new ContentDialog
            {
                Title = Strings.RestoreTitle,
                Content = Strings.RestoreBody(DocumentViewModel.AppName, Path.GetFileName(session.DocumentPath)),
                PrimaryButtonText = Strings.Restore,
                CloseButtonText = Strings.Discard,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };

            if (await dialog.ShowOneAtATimeAsync() == ContentDialogResult.Primary)
            {
                var doc = Shell.AddDocument();
                await doc.RestoreSessionAsync(session);
            }
            else
            {
                Core.Recovery.RecoveryJournal.Discard(session.JournalPath);
            }
        }
    }

    // --- Unsaved-changes close prompt (SDD §2.2 forgiveness, P3) ---

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        foreach (var doc in Shell.Documents)
            doc.SaveViewState();
        var anyDirtyOrBusy = Shell.Documents.Any(d => d.HasUnsavedChanges || d.Busy.IsWorking);
        if (_allowClose || !anyDirtyOrBusy)
        {
            // Consented close — nothing left to recover (SDD §3.4).
            foreach (var doc in Shell.Documents)
                doc.EndJournalSession();
            return;
        }
        args.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    private bool _confirmingClose;

    /// <summary>
    /// Waits for work still running — a save, a change — and then asks about unsaved changes
    /// per dirty tab, ask-all-then-close-all (#348 phase 1, plan §5.6): every dirty tab is
    /// asked before any journal is touched, so a Cancel on tab 2 leaves tab 1's journal
    /// exactly as it was — including when tab 1's answer was "Don't Save", which leaves it
    /// dirty in memory and therefore still needing its journal, not merely saved-and-safe.
    /// Only once every dirty tab has answered (none cancelled) does any journal end.
    /// </summary>
    private async Task ConfirmCloseAsync()
    {
        if (_confirmingClose)
            return;
        _confirmingClose = true;
        try
        {
            var documents = Shell.Documents.ToList();
            foreach (var doc in documents)
                await doc.Busy.WhenIdleAsync();

            foreach (var doc in documents)
            {
                if (doc.HasUnsavedChanges && !await doc.ConfirmSaveChangesAsync())
                    return; // Cancel — nothing answered so far is undone, no journal touched
            }

            // Every tab is now either clean (saved, or was already) or "Don't Save"d — the
            // window is really closing, so every journal ends here, not before.
            foreach (var doc in documents)
                doc.EndJournalSession();

            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            await ShowActiveErrorAsync(Strings.CouldNotSaveTitle, UserFacing.Describe(ex));
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    private async void OnRecentDocumentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton { DataContext: RecentDocument recent })
            await Shell.OpenRecentAsync(recent);
    }

    private void OnRemoveFromRecentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: RecentDocument recent })
            Shell.RemoveFromRecent(recent.Path);
    }

    // --- Settings flyout ---

    private bool _settingsLoading;

    private void OnSettingsOpening(object sender, object e)
    {
        _settingsLoading = true;
        MarkStyleChoice.SelectedIndex = (int)Shell.Settings.MarkStyle;
        ThemeChoice.SelectedIndex = Shell.Settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        LanguageChoice.SelectedIndex = AppLanguage.ChoiceIndex(Shell.Settings.Language);
        ReopenToggle.IsOn = Shell.Settings.ReopenLastFile;
        FlattenToggle.IsOn = Shell.Settings.FlattenOnSave;
        var version = typeof(MainWindow).Assembly.GetName().Version;
        AboutVersion.Text = Strings.AboutVersion(version?.ToString(3) ?? "dev");
        _settingsLoading = false;
    }

    private void OnMarkStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsLoading && MarkStyleChoice.SelectedIndex >= 0)
            Shell.Settings.MarkStyle = (Core.Engine.CheckMarkStyle)MarkStyleChoice.SelectedIndex;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsLoading || ThemeChoice.SelectedIndex < 0)
            return;
        Shell.Settings.Theme = ThemeChoice.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "" };
        ApplyTheme();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsLoading || LanguageChoice.SelectedIndex < 0)
            return;
        Shell.Settings.Language = AppLanguage.TagForChoice(LanguageChoice.SelectedIndex);
        // Resolved at startup, not live: the note says so instead of pretending.
        LanguageRestartNote.Visibility = Visibility.Visible;
    }

    private void OnReopenToggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoading)
            Shell.Settings.ReopenLastFile = ReopenToggle.IsOn;
    }

    private void OnFlattenToggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoading)
            Shell.Settings.FlattenOnSave = FlattenToggle.IsOn;
    }

    /// <summary>Opens the bundled THIRD-PARTY-NOTICES.txt in a scrollable in-app viewer.</summary>
    private async void OnThirdPartyNoticesClicked(object sender, RoutedEventArgs e)
    {
        string text;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "THIRD-PARTY-NOTICES.txt");
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            text = Strings.NoticesLoadFailed + "\n\n" + ex.Message;
        }

        var viewer = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            MinWidth = 480,
            MinHeight = 360,
        };
        Microsoft.UI.Xaml.Controls.ScrollViewer.SetVerticalScrollBarVisibility(viewer, ScrollBarVisibility.Auto);
        viewer.SetValue(AutomationProperties.NameProperty, Strings.NoticesTextName);

        var dialog = new ContentDialog
        {
            Title = Strings.NoticesTitle,
            Content = viewer,
            CloseButtonText = Strings.Close,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowOneAtATimeAsync();
    }

    public void ApplyTheme()
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = Shell.Settings.Theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
            if (!_titleBarFollowsTheme)
            {
                _titleBarFollowsTheme = true;
                root.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
            }
            ApplyTitleBarTheme();
        }
    }

    private bool _titleBarFollowsTheme;

    /// <summary>
    /// The caption bar is drawn by Windows, not XAML, so it follows the Windows app mode and
    /// stayed white over a dark window when the app's own Theme setting said Dark (#164).
    /// DWMWA_USE_IMMERSIVE_DARK_MODE puts it in step with the content's actual theme.
    /// </summary>
    private void ApplyTitleBarTheme()
    {
        if (Content is not FrameworkElement root)
            return;
        var dark = root.ActualTheme == ElementTheme.Dark ? 1 : 0;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // --- Signature library & placement (SDD §3.3) ---

    private void OnSignaturePicked(object sender, SignatureItem item)
    {
        if (Shell.Active is not { } active)
            return;
        active.SelectSignatureForPlacement(item);
        SignaturesFlyout.Hide();
        // The placement hint says "click"; a keyboard user places it with Enter (#2).
        if (active.PendingSignature is not null)
            Announce(Strings.PlaceSignatureKeyHint);
    }

    /// <summary>
    /// Shows the in-tree copy of the library where the flyout would open (the `sign`
    /// screenshot state): the popup layer is invisible to RenderTargetBitmap.
    /// </summary>
    public void ShowSignatureLibraryForScreenshot()
    {
        var below = SignaturesToolbarButton.TransformToVisual(RootGrid)
            .TransformPoint(new Windows.Foundation.Point(0, SignaturesToolbarButton.ActualHeight + 4));
        SignatureLibraryShot.Visibility = Visibility.Visible;
        SignatureLibraryShot.UpdateLayout();
        // A real flyout slides left to stay inside the window; so does this.
        var x = Math.Max(8, Math.Min(below.X, RootGrid.ActualWidth - SignatureLibraryShot.ActualWidth - 8));
        SignatureLibraryShot.Margin = new Thickness(x, below.Y, 0, 0);
    }

    /// <summary>
    /// Rename from a card's overflow or right-click (#100). The flyout light-dismisses
    /// under the dialog, so it is reopened afterwards: the user was in the library
    /// and still is.
    /// </summary>
    private async void OnRenameSignatureRequested(object sender, SignatureItem item)
    {
        if (Shell.Active is not { } active)
            return;
        SignaturesFlyout.Hide();

        var input = new TextBox { Text = item.Name, PlaceholderText = Strings.SignatureNamePlaceholder, IsSpellCheckEnabled = false };
        input.SelectAll();
        var dialog = new ContentDialog
        {
            Title = Strings.RenameSignatureTitle,
            Content = input,
            PrimaryButtonText = Strings.Rename,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowOneAtATimeAsync() == ContentDialogResult.Primary)
            await active.RenameSignatureAsync(item, input.Text);
        ShowSignaturesFlyout();
    }

    /// <summary>Delete asks once; a signature is not recoverable once its file is gone.</summary>
    private async void OnDeleteSignatureRequested(object sender, SignatureItem item)
    {
        if (Shell.Active is not { } active)
            return;
        SignaturesFlyout.Hide();

        var dialog = new ContentDialog
        {
            Title = Strings.DeleteSignatureTitle(item.Name),
            Content = Strings.DeleteSignatureBody,
            PrimaryButtonText = Strings.Delete,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowOneAtATimeAsync() == ContentDialogResult.Primary)
            active.RemoveSignatureFromLibrary(item);
        ShowSignaturesFlyout();
    }

    private void OnDefaultAppCardClosed(InfoBar sender, object args) =>
        Shell.DismissDefaultAppCard();

    private async void OnChooseDefaultAppsClicked(object sender, RoutedEventArgs e)
    {
        Shell.DismissDefaultAppCard();
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
    }

    // The three tools are toggles (#268): pressing one that is on turns it off, as on the
    // Mac. Their automation peer says "pressed", so pressing it again has to release it.
    private void OnWhiteoutModeClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.Active is not { } active)
            return;
        if (active.IsWhiteoutMode) active.CancelPlacementModes();
        else active.StartWhiteoutMode();
    }

    private void OnRedactModeClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.Active is not { } active)
            return;
        if (active.IsRedactMode) active.CancelPlacementModes();
        else active.StartRedactMode();
    }

    private void OnTextBoxModeClicked(object sender, RoutedEventArgs e)
    {
        if (Shell.Active is not { } active)
            return;
        if (active.IsTextBoxMode) active.CancelPlacementModes();
        else active.StartTextBoxMode();
    }

    private async void OnAddSignatureFromImageClicked(object sender, EventArgs e)
    {
        if (Shell.Active is not { } active)
            return;
        SignaturesFlyout.Hide();
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        var image = await SignatureImageProcessor.LoadAndCleanAsync(file);
        await active.AddSignatureFromImageAsync(image, Path.GetFileNameWithoutExtension(file.Name));
    }

    private async void OnTypeSignatureClicked(object sender, EventArgs e)
    {
        if (Shell.Active is not { } active)
            return;
        SignaturesFlyout.Hide();
        var input = new TextBox { PlaceholderText = Strings.YourNamePlaceholder, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Script"), FontSize = 24, IsSpellCheckEnabled = false };
        var dialog = new ContentDialog
        {
            Title = Strings.TypeSignatureTitle,
            Content = input,
            PrimaryButtonText = Strings.Add,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowOneAtATimeAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text))
            return;

        var image = await RenderTypedSignatureAsync(input.Text.Trim());
        await active.AddSignatureFromImageAsync(image, input.Text.Trim());
    }

    /// <summary>
    /// Freehand signature drawing (SDD §3.3). WinUI 3 has no InkCanvas, so this is a
    /// pointer-event stroke canvas: each press starts a rounded polyline, moves extend
    /// it, release ends it. Works with mouse, touch, and pen.
    /// </summary>
    private async void OnDrawSignatureClicked(object sender, EventArgs e)
    {
        if (Shell.Active is not { } active)
            return;
        SignaturesFlyout.Hide();

        var strokes = new Canvas();
        var drawHost = new Grid
        {
            Width = 460,
            Height = 180,
            // White, and not themeable: the drawn signature is rasterised from this
            // surface and then background-removed at luminance > 235 (SDD §6.2). A
            // dark pad would survive the cleanup as a black rectangle.
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            CornerRadius = new CornerRadius(4),
        };
        drawHost.Children.Add(strokes);

        Microsoft.UI.Xaml.Shapes.Polyline? currentStroke = null;
        drawHost.PointerPressed += (_, args) =>
        {
            drawHost.CapturePointer(args.Pointer);
            currentStroke = new Microsoft.UI.Xaml.Shapes.Polyline
            {
                // SDD §6.2: the user's mark is #202020, near-black, so it reads as
                // ink on paper rather than as UI. Not a theme colour, not a token.
                Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)),
                StrokeThickness = 3,
                StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Round,
                StrokeStartLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round,
                StrokeEndLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round,
            };
            currentStroke.Points.Add(args.GetCurrentPoint(drawHost).Position);
            strokes.Children.Add(currentStroke);
        };
        drawHost.PointerMoved += (_, args) =>
        {
            if (currentStroke is null)
                return;
            var point = args.GetCurrentPoint(drawHost).Position;
            var last = currentStroke.Points[^1];
            // Light smoothing: skip sub-pixel jitter.
            if (Math.Abs(point.X - last.X) + Math.Abs(point.Y - last.Y) >= 1.5)
                currentStroke.Points.Add(point);
        };
        drawHost.PointerReleased += (_, args) =>
        {
            drawHost.ReleasePointerCapture(args.Pointer);
            currentStroke = null;
        };
        drawHost.PointerCanceled += (_, _) => currentStroke = null;

        var nameInput = new TextBox { PlaceholderText = Strings.SignatureNamePlaceholder, Text = Strings.MySignature, IsSpellCheckEnabled = false };
        var clear = new Button { Content = Strings.Clear };
        clear.Click += (_, _) => strokes.Children.Clear();

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = Strings.DrawHint,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        content.Children.Add(drawHost);
        content.Children.Add(clear);
        content.Children.Add(nameInput);

        var dialog = new ContentDialog
        {
            Title = Strings.DrawSignatureTitle,
            Content = content,
            PrimaryButtonText = Strings.Add,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        SignatureImage? captured = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (strokes.Children.Count == 0)
            {
                args.Cancel = true; // nothing drawn yet
                return;
            }
            // Capture while the dialog (and canvas) are still in the visual tree.
            var deferral = args.GetDeferral();
            try
            {
                var target = new RenderTargetBitmap();
                await target.RenderAsync(drawHost);
                var buffer = await target.GetPixelsAsync();
                captured = SignatureImageProcessor.Clean(
                    new SignatureImage(buffer.ToArray(), target.PixelWidth, target.PixelHeight));
            }
            finally
            {
                deferral.Complete();
            }
        };

        if (await dialog.ShowOneAtATimeAsync() != ContentDialogResult.Primary || captured is null)
            return;

        var name = string.IsNullOrWhiteSpace(nameInput.Text) ? Strings.MySignature : nameInput.Text.Trim();
        await active.AddSignatureFromImageAsync(captured, name);
    }

    /// <summary>Renders the offscreen Segoe Script TextBlock to BGRA pixels.</summary>
    private async Task<SignatureImage> RenderTypedSignatureAsync(string text)
    {
        TypedSignatureText.Text = text;
        TypedSignatureHost.UpdateLayout();

        var target = new RenderTargetBitmap();
        await target.RenderAsync(TypedSignatureHost);
        var buffer = await target.GetPixelsAsync();
        return new SignatureImage(buffer.ToArray(), target.PixelWidth, target.PixelHeight);
    }
}
