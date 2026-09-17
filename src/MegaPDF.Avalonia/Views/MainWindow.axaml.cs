using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Avalonia.Rendering;
using MegaPDF.Avalonia.ViewModels;
using MegaPDF.Core.Imaging;
using MegaPDF.Core.Viewing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Services;

namespace MegaPDF.Avalonia.Views;

public partial class MainWindow : Window
{
    /// <summary>The file the document was opened from, kept so Save can write back to it.</summary>
    private IStorageFile? _openedFile;

    public MainWindow()
    {
        InitializeComponent();
        SizeToWorkingArea();

        // ADR-002 called this one of the two MainViewModel touch points that is a
        // reshape rather than a rename: WinUI's FileOpenPicker is a type you
        // construct, Avalonia's IStorageProvider is reached through the TopLevel and
        // is async. Keeping it in the view is what lets the view model stay UI-free.
        // Save As, Password…, Shrink and Options are entries in the More menu and the
        // menu bar now (#144), wired where those are built.
        OpenButton.Click += async (_, _) => await GuardedAsync(OpenDocumentAsync);
        EmptyOpenButton.Click += async (_, _) => await GuardedAsync(OpenDocumentAsync);
        UnlockButton.Click += async (_, _) => await GuardedAsync(UnlockAsync);

        RecentList.SelectionChanged += async (_, _) =>
        {
            if (RecentList.SelectedItem is not RecentEntry entry)
                return;
            RecentList.SelectedItem = null;
            await GuardedAsync(() => OpenRecentAsync(entry));
        };

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);

        BindShortcuts();
        WireToolbar();
        WireSignatures();
        WireFind();

        // Only realised pages rasterise. ContainerPrepared/ContainerClearing are the
        // virtualization hooks — this is where "render what you can see" happens, and
        // where each page surface gets its click handler.
        PageList.ContainerPrepared += (_, e) =>
        {
            if (e.Container.DataContext is not PageViewModel page)
                return;

            page.EnsureRendered(RenderScaling);
            e.Container.PointerPressed -= OnPagePointerPressed;
            e.Container.PointerPressed += OnPagePointerPressed;
            e.Container.PointerMoved -= OnPagePointerMoved;
            e.Container.PointerMoved += OnPagePointerMoved;
            e.Container.PointerReleased -= OnPagePointerReleased;
            e.Container.PointerReleased += OnPagePointerReleased;
        };

        PageList.ContainerClearing += (_, e) =>
        {
            e.Container.PointerPressed -= OnPagePointerPressed;
            e.Container.PointerMoved -= OnPagePointerMoved;
            e.Container.PointerReleased -= OnPagePointerReleased;
            if (e.Container.DataContext is PageViewModel page)
                page.Unrender();
        };
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>
    /// Runs an async handler and puts any failure in the status line (#145): an exception
    /// escaping an async event handler would take the app down.
    /// </summary>
    private async Task GuardedAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            if (ViewModel is { } vm)
                vm.Status = Strings.WithDetail(Strings.FileCouldNotBeRead, ex.Message);
        }
    }

    /// <summary>A file the person chose, waiting for its document to finish opening before Save writes through it.</summary>
    private IStorageFile? _pendingFile;

    /// <summary>
    /// The storage file follows the open document (#145). Opening is async now, and a document
    /// that fails to open leaves the previous one on screen, so the file handle is adopted only
    /// once its document is the one open — or Save would write the old document into the new file.
    /// </summary>
    private void FollowDocumentPath(string documentPath)
    {
        if (_pendingFile is { } pending && SamePath(pending.TryGetLocalPath(), documentPath))
        {
            _openedFile = pending;
            _pendingFile = null;
        }
        else if (_openedFile is { } current && !SamePath(current.TryGetLocalPath(), documentPath))
        {
            // A document opened by path alone has no handle to write through: Save says so.
            _openedFile = null;
        }
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is { } vm)
        {
            // A window: engine work runs off the UI thread from here on (#145).
            vm.RunsInBackground = true;
            vm.Busy.PropertyChanged += (_, _) => RefreshMenuBar();
            // The menu bar (#144) lists the view model's font and size choices.
            BuildMenuBar();
            vm.SaveRequested += () => _ = SaveAsync();
            vm.ScrollToRequested += ScrollToMatch;
            // The focused region is brought into view by the same rules a search hit
            // is — tabbing to something off screen has to show it (#2, #32).
            vm.FocusScrollRequested += ScrollToMatch;
            vm.EditLineRequested += ShowLineEditor;
            vm.PasswordRequested += AskForPasswordAsync;
            vm.EditFieldRequested += ShowFieldEditor;
            vm.RenameSignatureRequested += OnRenameSignatureRequested;
            vm.DeleteSignatureRequested += OnDeleteSignatureRequested;
            vm.PageRewriteConfirmationRequested += ConfirmPageRewriteAsync;
            vm.PropertyChanged += (_, args) =>
            {
                // A card was clicked: placement is armed, so the library closes and
                // the next click goes to the page.
                if (args.PropertyName is nameof(MainViewModel.IsPlacingSignature) && vm.IsPlacingSignature)
                    SignButton.Flyout?.Hide();
                // The chrome is positioned in device-independent pixels, so it has to
                // be rebuilt when the selection changes and when zoom moves it.
                if (args.PropertyName is nameof(MainViewModel.Selection) or nameof(MainViewModel.Zoom))
                    OnSelectionChanged();
                if (args.PropertyName is nameof(MainViewModel.PageFocus) or nameof(MainViewModel.Zoom))
                    OnPageFocusChanged();
                // The pickers join and leave the row with their context (#144).
                if (args.PropertyName is nameof(MainViewModel.IsTextStyleContext))
                    ApplyToolbarLayout();
                // An editor writing new text shows the face and size it will be written in.
                if (args.PropertyName is nameof(MainViewModel.TextFont) or nameof(MainViewModel.TextSize))
                    FollowPickersInEditor();
                if (args.PropertyName is nameof(MainViewModel.DocumentPath) && vm.DocumentPath is { } documentPath)
                    FollowDocumentPath(documentPath);
                RefreshMenuBar();
            };
            RefreshMenuBar();
        }
    }

    /// <summary>
    /// Tunnelling, not bubbling: Avalonia's focus manager handles Tab before a
    /// bubbling handler would ever see it, so page traversal has to be claimed on
    /// the way down (#2).
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (HandlePageKey(e))
            e.Handled = true;
    }

    /// <summary>
    /// Space's key-*up* has to be swallowed as well as its key-down (#144).
    /// Avalonia's Button clicks on the release, and it does so whether or not it
    /// ever saw the press — so marking the key-down handled stopped the page
    /// region being activated twice but not the focused toolbar button being
    /// pressed alongside it. Tab moves the page's focus ring, not keyboard focus,
    /// so a toolbar button used from the keyboard still holds it: pressing Space
    /// on a page region opened the More menu at the same time.
    ///
    /// Only while the page's focus ring is up, and never while an editor or the
    /// find box owns the keys — the same guard <see cref="HandlePageKey"/> uses.
    /// </summary>
    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space)
            return;
        if (_inlineEditor is not null || FindBox.IsFocused)
            return;
        if (ViewModel is { IsDocumentOpen: true, PageFocus: not null })
            e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back && ViewModel is { Selection: not null } selected)
        {
            selected.DeleteSelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel is { Selection: not null } hasSelection)
        {
            hasSelection.ClearSelection();
            e.Handled = true;
            return;
        }

        // Escape is how every desktop app leaves a mode. Placement first: if both are
        // active, the one the user most recently entered is the one they mean.
        if (e.Key == Key.Escape && ViewModel is { IsPlacingSignature: true } vm)
        {
            vm.CancelPlacing();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel is { IsModeActive: true } modal)
        {
            DismissInlineEditor();
            modal.CancelModes();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel is { IsFindOpen: true })
        {
            CloseFind();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // RenderScaling is only meaningful once there is a window on a screen. Feeding
        // it to the view model is what makes a page sharp on a retina Mac rather than
        // upscaled from a 96 DPI raster.
        if (ViewModel is { } vm)
        {
            vm.DpiScale = RenderScaling;
            vm.LoadRecents();
            UpdateViewport();
        }

        PageScroller.ScrollChanged += (_, _) => UpdateViewport();
        PageScroller.SizeChanged += (_, _) => UpdateViewport();

        _isOpen = true;
        _ = OpenPendingThenOfferRecoveryAsync();
    }

    /// <summary>The size the window opens at, screen permitting (#143).</summary>
    private const double PreferredWidth = 1280;
    private const double PreferredHeight = 900;

    /// <summary>
    /// Opens the window as large as is comfortable on this screen (#143). A fixed
    /// 1000×800 was too narrow for the toolbar, and on a 15" MacBook Air (1470×956
    /// points) left a page viewport about 650 DIP tall. Up to 1280×900, never more
    /// than 90% of the working area (the screen less the menu bar and Dock, or the
    /// taskbar), and centred by WindowStartupLocation. A --window size on the command
    /// line still wins: App applies it after the constructor.
    /// </summary>
    private void SizeToWorkingArea()
    {
        try
        {
            if (Screens is not { } screens || (screens.ScreenFromWindow(this) ?? screens.Primary) is not { } screen)
                return;

            // WorkingArea is in pixels on Windows and in points on macOS, where
            // Avalonia 11.2 reports a Scaling of 1; divided by Scaling it is DIPs on both.
            var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
            Width = Math.Max(MinWidth, Math.Min(PreferredWidth, screen.WorkingArea.Width / scaling * 0.9));
            Height = Math.Max(MinHeight, Math.Min(PreferredHeight, screen.WorkingArea.Height / scaling * 0.9));
        }
        catch (Exception)
        {
            // No screen information: the XAML size stands.
        }
    }

    // --- Documents handed over by the OS (#143) ---

    /// <summary>True once OnOpened has run; before that a handed-over document waits.</summary>
    private bool _isOpen;

    /// <summary>A document the OS handed over before the window was open.</summary>
    private Func<Task>? _pendingOpen;

    /// <summary>
    /// Opens a document from Finder: a double-click, a PDF dropped on the Dock icon,
    /// Open With. macOS delivers these as an Apple Event after launch rather than as
    /// arguments, both to an app it is starting and to one already running, and
    /// Avalonia surfaces them as IActivatableLifetime.Activated (App wires that up).
    /// The storage file, not just its path, is kept so Save can write back through it.
    /// </summary>
    public void OpenFromSystem(IStorageFile file) => OpenWhenReady(() => OpenStorageFileAsync(file));

    /// <summary>A PDF path from the command line (the Windows file association, `open --args`).</summary>
    public void OpenFromSystem(string path) => OpenWhenReady(async () =>
    {
        // Asked for as a storage file for the same reason as above; without one
        // Save would have nothing to write through.
        if (await StorageProvider.TryGetFileFromPathAsync(path) is { } file)
            await OpenStorageFileAsync(file);
        else if (ViewModel is { } vm && await ConfirmUnsavedChangesAsync())
            await vm.OpenAsync(path);
    });

    private void OpenWhenReady(Func<Task> open)
    {
        if (!_isOpen)
        {
            // One window shows one document, so the last one handed over wins.
            _pendingOpen = open;
            return;
        }
        _ = RunOpenAsync(open);
        Activate();
    }

    private async Task RunOpenAsync(Func<Task> open)
    {
        try
        {
            await open();
        }
        catch (Exception ex)
        {
            if (ViewModel is { } vm)
                vm.Status = Strings.WithDetail(Strings.FileCouldNotBeRead, ex.Message);
        }
    }

    /// <summary>
    /// The handed-over document first, then the recovery offer — which only asks when
    /// nothing is open, so it must not run before the document has had its chance.
    /// </summary>
    private async Task OpenPendingThenOfferRecoveryAsync()
    {
        if (_pendingOpen is { } open)
        {
            _pendingOpen = null;
            await RunOpenAsync(open);
        }
        await OfferRecoveryAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ViewModel?.Dispose();
    }

    // --- Unsaved changes: closing, quitting, opening another (#145, D1) ---

    /// <summary>The person has answered for this close or quit; the next Closing goes ahead.</summary>
    private bool _closeConfirmed;
    private bool _confirmingClose;

    /// <summary>For capture runs, which quit whatever they changed with nobody there to answer.</summary>
    internal void SkipCloseConfirmation()
    {
        _closeConfirmed = true;
        // What a capture changed is not the person's: no journal is left behind for it.
        ViewModel?.DiscardChanges();
    }

    /// <summary>Whether closing or quitting must ask first: unsaved changes, or work still running.</summary>
    internal bool NeedsConfirmationBeforeClose =>
        !_closeConfirmed && ViewModel is { } vm && (vm.Busy.IsWorking || (vm.IsDocumentOpen && vm.IsDirty));

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing the window used to drop unsaved changes and delete their journal, silently.
        if (NeedsConfirmationBeforeClose)
        {
            e.Cancel = true;
            _ = CloseAfterConfirmingAsync();
        }
        base.OnClosing(e);
    }

    private async Task CloseAfterConfirmingAsync()
    {
        if (await ConfirmCloseAsync())
            Close();
    }

    /// <summary>Cmd+Q (App's ShutdownRequested): asks as closing does, then quits.</summary>
    internal async Task ConfirmThenQuitAsync(global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (await ConfirmCloseAsync())
            desktop.Shutdown();
    }

    /// <summary>Waits for a save in progress, then asks about unsaved changes. True when closing may go ahead.</summary>
    private async Task<bool> ConfirmCloseAsync()
    {
        if (_confirmingClose)
            return false;
        _confirmingClose = true;
        try
        {
            if (ViewModel is { } vm)
                await vm.Busy.WhenIdleAsync();
            if (!await ConfirmUnsavedChangesAsync())
                return false;
            _closeConfirmed = true;
            return true;
        }
        catch (Exception ex)
        {
            if (ViewModel is { } vm)
                vm.Status = Strings.WithDetail(Strings.CouldNotSave, ex.Message);
            return false;
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    /// <summary>
    /// Save, Don't Save or Cancel, when the open document has unsaved changes — the standard
    /// macOS question. True when the caller may go on: nothing unsaved, saved, or Don't Save.
    /// Cancel changes nothing, the recovery journal included.
    /// </summary>
    private async Task<bool> ConfirmUnsavedChangesAsync()
    {
        if (ViewModel is not { IsDocumentOpen: true } vm)
            return true;
        await vm.Busy.WhenIdleAsync();
        if (!vm.IsDirty)
            return true;

        var dialog = new UnsavedChangesWindow();
        dialog.SetDocument(vm.DocumentName ?? "");
        await dialog.ShowDialog(this);
        switch (dialog.Choice)
        {
            case UnsavedChangesWindow.Decision.Save:
                return await SaveAsync() && !vm.IsDirty;
            case UnsavedChangesWindow.Decision.DontSave:
                vm.DiscardChanges();
                return true;
            default:
                return false;
        }
    }

    /// <summary>For the `unsaved` screenshot state: the question, shown beside the window rather than modal.</summary>
    internal Window ShowUnsavedChangesForScreenshot()
    {
        var dialog = new UnsavedChangesWindow();
        dialog.SetDocument(ViewModel?.DocumentName ?? "");
        dialog.Show(this);
        return dialog;
    }

    // --- Text boxes and whiteout on the page (SDD §3.1, §3.3) ---

    /// <summary>The in-place editor, while one is open. Only ever one at a time.</summary>
    private TextBox? _inlineEditor;

    /// <summary>Rubber band for the whiteout drag, and where it started.</summary>
    private Rectangle? _band;
    private Point _bandOrigin;
    private Control? _bandHost;

    /// <summary>
    /// An editor placed where the user clicked, showing the face and size the text
    /// will actually be written in. Typing into a dialog and hoping is the thing
    /// SDD §2.2 is against — you should see the words land where they will sit.
    /// </summary>
    private void ShowInlineEditor(
        Control container, Point at, double fontSizePoints, string fontFamily,
        string initialText, double minWidth, Action<string> commit)
    {
        if (ViewModel is not { } vm || container is not ContentPresenter presenter)
            return;

        DismissInlineEditor();

        var dip = PageBitmap.PointsToPixels * vm.Zoom;
        var editor = new TextBox
        {
            // Off while the starting text goes in, on once the editor is up (#144): the
            // text a line or field already had is where editing starts, not an edit, so
            // Cmd+Z straight away must not empty the box. Turning undo off clears its
            // history; turning it back on after load leaves nothing to undo until typing.
            IsUndoEnabled = false,
            MinWidth = Math.Max(140, minWidth),
            Text = initialText,
            FontSize = fontSizePoints * dip,
            FontFamily = new FontFamily(fontFamily),
            Margin = new Thickness(at.X, at.Y, 0, 0),
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
            Watermark = Strings.TypeThenEnter,
        };

        void Commit()
        {
            var text = editor.Text ?? "";
            DismissInlineEditor();
            commit(text);
        }

        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                DismissInlineEditor();
                vm.CancelModes();
                e.Handled = true;
            }
        };
        // Clicking away commits rather than discarding: losing typing to a stray
        // click is the more annoying failure. Except into the font and size pickers
        // on the toolbar (#144): choosing a face for the text being typed is part of
        // typing it, and the picker hands focus back when it closes.
        editor.LostFocus += (_, _) =>
        {
            if (_inlineEditor == editor && !IsInTextPicker(FocusManager?.GetFocusedElement()))
                Commit();
        };

        // After its template and first layout, so nothing the editor does to show its
        // starting text lands in the history that Cmd+Z walks.
        editor.Loaded += (_, _) => editor.IsUndoEnabled = true;

        if (OverlayOf(presenter) is { } overlay)
        {
            overlay.Children.Add(editor);
            _inlineEditor = editor;
            editor.Focus();
            editor.SelectAll();
        }
    }

    /// <summary>New text at the click point, in the toolbar's chosen face and size.</summary>
    private void ShowNewTextEditor(Control container, PageViewModel page, Point at, PdfPoint pagePoint)
    {
        if (ViewModel is not { } vm)
            return;

        var dip = PageBitmap.PointsToPixels * vm.Zoom;
        ShowInlineEditor(
            container, new Point(at.X, at.Y - (vm.TextSize * dip)),
            vm.TextSize, FamilyFor(vm.TextFont), "", 0,
            text =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                    vm.AddTextBox(page.Index, pagePoint, text);
                else
                    vm.CancelModes();
            });
        _editorFollowsPickers = _inlineEditor is not null;
    }

    /// <summary>Whether the open editor is writing added text, whose face and size the pickers choose.</summary>
    private bool _editorFollowsPickers;

    /// <summary>A picker changed while added text is being typed: the editor shows the new face and size.</summary>
    private void FollowPickersInEditor()
    {
        if (!_editorFollowsPickers || _inlineEditor is not { } editor || ViewModel is not { } vm)
            return;
        editor.FontFamily = new FontFamily(FamilyFor(vm.TextFont));
        editor.FontSize = vm.TextSize * PageBitmap.PointsToPixels * vm.Zoom;
    }

    /// <summary>
    /// Editing the document's own text (SDD §3.1). The editor sits on the line, at
    /// its size, so the replacement is judged where it will live rather than in a
    /// dialog somewhere else.
    /// </summary>
    private void ShowLineEditor(int pageIndex, PdfTextLine line)
    {
        if (ViewModel is not { } vm)
            return;

        var container = ContainerFor(pageIndex);
        if (container is null)
            return;

        var dip = PageBitmap.PointsToPixels * vm.Zoom;
        ShowInlineEditor(
            container,
            new Point(line.Bounds.X * dip, line.Bounds.Y * dip),
            line.FontSize, "Helvetica, Arial, sans-serif",
            line.Text, line.Bounds.Width * dip,
            text => vm.EditLine(pageIndex, line, text));
    }

    /// <summary>
    /// Editing an AcroForm text field. The editor is sized to the widget so it reads
    /// as filling in the box that is already printed on the form, rather than as
    /// typing somewhere near it.
    /// </summary>
    private void ShowFieldEditor(int pageIndex, PdfFormField field)
    {
        if (ViewModel is not { } vm || ContainerFor(pageIndex) is not { } container)
            return;

        var dip = PageBitmap.PointsToPixels * vm.Zoom;
        // A widget's height is the box; the text inside it sits a little smaller.
        var fontSize = Math.Max(6, field.Bounds.Height * 0.7);

        ShowInlineEditor(
            container,
            new Point(field.Bounds.X * dip, field.Bounds.Y * dip),
            fontSize, "Helvetica, Arial, sans-serif",
            field.Value, field.Bounds.Width * dip,
            text => vm.SetFieldValue(pageIndex, field, text));
    }

    private Control? ContainerFor(int pageIndex)
    {
        for (var i = 0; i < PageList.ItemCount; i++)
        {
            if (PageList.ContainerFromIndex(i) is { DataContext: PageViewModel page } container
                && page.Index == pageIndex)
                return container;
        }
        return null;
    }

    private void DismissInlineEditor()
    {
        if (_inlineEditor is null)
            return;
        // Cleared first: removing a focused editor raises LostFocus, which must find it gone.
        var editor = _inlineEditor;
        _inlineEditor = null;
        _editorFollowsPickers = false;
        (editor.Parent as Panel)?.Children.Remove(editor);
        if (ViewModel is { } vm)
            vm.IsEditingTextBox = false;
    }

    /// <summary>Maps the three permitted base-14 names to fonts the OS actually has.</summary>
    private static string FamilyFor(string standardFont) => standardFont switch
    {
        "Times-Roman" => "Times New Roman, Times, serif",
        "Courier" => "Courier New, Courier, monospace",
        _ => "Helvetica, Arial, sans-serif",
    };

    /// <summary>The Panel inside a page's Border that overlays the raster.</summary>
    private static Panel? OverlayOf(ContentPresenter presenter) =>
        presenter.GetVisualDescendants().OfType<Panel>().FirstOrDefault(p => p is not StackPanel);

    /// <summary>
    /// The page surface, as the thing to measure a pointer position against. A row's
    /// container is the full viewport width with the white page centred inside it,
    /// so a position relative to the container carries the centring margin — and at
    /// every zoom except fit-width, where that margin is a few pixels, clicks landed
    /// off the page: no checkbox ticked, the cursor never changed, whiteout bands
    /// drawn beside where the drag was. The overlay Panel sits inside the page
    /// Border and shares its origin, which is the space every editor, highlight and
    /// band is already placed in.
    /// </summary>
    private static Visual SurfaceOf(Control container) =>
        container is ContentPresenter presenter && OverlayOf(presenter) is { } overlay ? overlay : container;

    /// <summary>
    /// Keeps the view model told how big the viewport is and which page is in it —
    /// what fit-to-width, fit-to-page and the "Page 3 of 12" readout all need.
    /// </summary>
    private void UpdateViewport()
    {
        if (ViewModel is not { } vm)
            return;

        vm.ViewportWidth = PageScroller.Viewport.Width;
        vm.ViewportHeight = PageScroller.Viewport.Height;
        // A document that opened before the window was laid out is fitted now (#143).
        vm.FitOnOpen();

        if (vm.Pages.Count == 0)
            return;

        // Whichever page covers the middle of the viewport is the one being read —
        // the topmost visible page is the wrong answer when a short page is
        // scrolling off the top.
        var middle = PageScroller.Offset.Y + (PageScroller.Viewport.Height / 2);
        var y = 0.0;
        foreach (var page in vm.Pages)
        {
            y += page.LayoutHeight + PageGap;
            if (middle <= y)
            {
                vm.CurrentPage = page.Index + 1;
                return;
            }
        }
        vm.CurrentPage = vm.Pages.Count;
    }

    /// <summary>
    /// Offers to recover work that was never saved (SDD §3.4).
    ///
    /// Asked rather than done: silently reopening a document and replaying edits
    /// onto it is startling, and the person may have abandoned those changes on
    /// purpose. Only asked when a document is not already open, so a file opened
    /// from Finder is never pushed aside by a prompt.
    /// </summary>
    private async Task OfferRecoveryAsync()
    {
        if (ViewModel is not { IsDocumentOpen: false } vm)
            return;

        var sessions = vm.FindRecoverableSessions();
        if (sessions.Count == 0)
            return;

        var session = sessions[0];
        var dialog = new RecoveryWindow();
        dialog.SetSession(Path.GetFileName(session.DocumentPath), session.EntryCount, session.LastWriteUtc);
        await dialog.ShowDialog(this);

        switch (dialog.Choice)
        {
            case RecoveryWindow.Decision.Restore:
                await vm.RestoreSessionAsync(session);
                break;
            case RecoveryWindow.Decision.Discard:
                vm.DiscardSession(session);
                break;
        }
    }

    // --- Clicking the page (SDD §3.2) ---

    private void OnPagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control container || container.DataContext is not PageViewModel page)
            return;
        if (ViewModel is not { } vm)
            return;
        if (!e.GetCurrentPoint(container).Properties.IsLeftButtonPressed)
            return;

        // Clicks while work runs are ignored, not queued (#145): a second change must not
        // follow a first still on its way, nor a second question a first.
        if (vm.Busy.IsBusy)
        {
            e.Handled = true;
            return;
        }

        // A click on the page takes keyboard focus off the toolbar (#169). Focus stayed on
        // the last toolbar button used, so a later Space or Enter pressed it: Open, or Undo
        // on work just done. Cleared before the click is handled, so an editor it opens
        // still takes focus.
        if (IsToolbarControl(FocusManager?.GetFocusedElement() as Control))
            FocusManager?.ClearFocus();

        // The page surface is laid out at exactly LayoutWidth/Height, so a position
        // inside it converts straight back to page points. Same conversion the WinUI
        // app uses (72/96 divided by zoom), which is what keeps a click landing on the
        // same checkbox on both desktops.
        var position = e.GetPosition(SurfaceOf(container));
        var dipToPoint = 1.0 / (PageBitmap.PointsToPixels * vm.Zoom);
        var pagePoint = new PdfPoint(position.X * dipToPoint, position.Y * dipToPoint);

        switch (vm.Mode)
        {
            case MainViewModel.PageMode.AddText:
                ShowNewTextEditor(container, page, position, pagePoint);
                break;

            case MainViewModel.PageMode.Whiteout:
                BeginBand(container, position, e);
                break;

            default:
                DismissInlineEditor();
                // A click that lands on nothing deselects, which is what every
                // desktop app does and what makes the chrome feel like chrome.
                if (vm.Selection is not null && vm.HitTest(page.Index, pagePoint).Kind == PageHitKind.None)
                {
                    vm.ClearSelection();
                    break;
                }
                vm.HandlePageClick(page.Index, pagePoint);
                break;
        }

        e.Handled = true;
    }

    /// <summary>
    /// The cursor says what a click will do before you make it (SDD §2.2). Read off
    /// the page's in-memory interaction map, so this costs a rectangle scan rather
    /// than an engine hit-test per mouse movement.
    /// </summary>
    private void UpdateCursor(object? sender, PointerEventArgs e)
    {
        if (sender is not Control container || container.DataContext is not PageViewModel page)
            return;
        if (ViewModel is not { } vm)
            return;

        // In a placement mode the cursor describes the mode, not what is underneath.
        var shape = vm.Mode switch
        {
            MainViewModel.PageMode.AddText => StandardCursorType.Ibeam,
            MainViewModel.PageMode.Whiteout => StandardCursorType.Cross,
            _ when vm.IsPlacingSignature => StandardCursorType.Cross,
            _ => CursorForContent(),
        };

        container.Cursor = new Cursor(shape);

        StandardCursorType CursorForContent()
        {
            var dipToPoint = 1.0 / (PageBitmap.PointsToPixels * vm.Zoom);
            var at = e.GetPosition(SurfaceOf(container));
            var kind = page.KindAt(new PdfPoint(at.X * dipToPoint, at.Y * dipToPoint));
            // No clickable affordance for what the document's owner does not allow (#131).
            if (!vm.Capabilities.Allows(kind))
                return StandardCursorType.Arrow;
            return kind switch
            {
                PageHitKind.TextRun or PageHitKind.FormTextField => StandardCursorType.Ibeam,
                PageHitKind.FormCheckbox or PageHitKind.DrawnCheckbox
                    or PageHitKind.StampAnnotation or PageHitKind.Whiteout
                    or PageHitKind.TextBox => StandardCursorType.Hand,
                _ => StandardCursorType.Arrow,
            };
        }
    }

    // --- Whiteout drag ---

    private void BeginBand(Control container, Point origin, PointerPressedEventArgs e)
    {
        if (container is not ContentPresenter presenter || OverlayOf(presenter) is not { } overlay)
            return;

        _bandOrigin = origin;
        _bandHost = container;
        _band = new Rectangle
        {
            Fill = Brushes.White,
            Opacity = 0.75,
            Stroke = Brushes.Gray,
            StrokeThickness = 1,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(origin.X, origin.Y, 0, 0),
            Width = 0,
            Height = 0,
        };
        overlay.Children.Add(_band);
        e.Pointer.Capture(container);
    }

    private void OnPagePointerMoved(object? sender, PointerEventArgs e)
    {
        OnSelectionPointerMoved(e);
        UpdateCursor(sender, e);

        if (_band is null || _bandHost is null)
            return;

        var p = e.GetPosition(SurfaceOf(_bandHost));
        var x = Math.Min(p.X, _bandOrigin.X);
        var y = Math.Min(p.Y, _bandOrigin.Y);
        _band.Margin = new Thickness(x, y, 0, 0);
        _band.Width = Math.Abs(p.X - _bandOrigin.X);
        _band.Height = Math.Abs(p.Y - _bandOrigin.Y);
    }

    private void OnPagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        OnSelectionPointerReleased(e);

        if (_band is null || _bandHost is null || ViewModel is not { } vm)
            return;

        if (_bandHost.DataContext is PageViewModel page)
        {
            var dipToPoint = 1.0 / (PageBitmap.PointsToPixels * vm.Zoom);
            vm.AddWhiteout(page.Index, new PdfRect(
                _band.Margin.Left * dipToPoint, _band.Margin.Top * dipToPoint,
                _band.Width * dipToPoint, _band.Height * dipToPoint));
        }

        (_band.Parent as Panel)?.Children.Remove(_band);
        _band = null;
        _bandHost = null;
        e.Pointer.Capture(null);
    }

    // --- Signatures (SDD §3.3) ---

    private void WireSignatures()
    {
        // The cards themselves command the view model (PlaceSignatureCommand); what
        // the view owns is closing the flyout the moment placement is armed, so the
        // next click lands on the page rather than being swallowed by an open popup.
        // Wired on the view model's own event in OnDataContextChanged, below.

        DrawSignatureButton.Click += async (_, _) =>
        {
            SignButton.Flyout?.Hide();
            await CaptureSignatureAsync();
        };

        ImportSignatureButton.Click += async (_, _) =>
        {
            SignButton.Flyout?.Hide();
            await ImportSignatureAsync();
        };

        TypeSignatureButton.Click += async (_, _) =>
        {
            SignButton.Flyout?.Hide();
            await TypeSignatureAsync();
        };
    }

    /// <summary>
    /// Type a name, get a signature (#101): the typed name is rendered to ink on a
    /// transparent raster and stored through the same path as a drawn one, then
    /// armed for placement like any new signature.
    /// </summary>
    private async Task TypeSignatureAsync()
    {
        if (ViewModel is not { } vm)
            return;

        var dialog = new TypeSignatureWindow();
        await dialog.ShowDialog(this);
        if (dialog.TypedName is not { } name)
            return;

        try
        {
            var entry = vm.AddSignatureFromImage(name, TypeSignatureWindow.Render(name), Rendering.SignatureImages.EncodePng);
            vm.BeginPlacing(entry);
        }
        catch (Exception ex)
        {
            vm.Status = Strings.WithDetail(Strings.CouldNotSaveSignature, ex.Message);
        }
    }

    /// <summary>
    /// Opens the signature library and keeps it open, for the `sign` screenshot
    /// state. A capture run's window is never the active one, and a flyout
    /// light-dismisses the moment activation is elsewhere, so the close is refused
    /// until the process exits.
    /// </summary>
    internal void ShowSignaturesFlyout()
    {
        if (SignButton.Flyout is not global::Avalonia.Controls.Primitives.PopupFlyoutBase flyout)
        {
            Console.Error.WriteLine("::error::--screenshot-state sign: the Sign button has no flyout");
            return;
        }
        flyout.Closing += (_, e) => e.Cancel = true;
        flyout.ShowAt(SignButton);
    }

    /// <summary>Rename, from the card's menu (#100): a small prefilled prompt.</summary>
    private async void OnRenameSignatureRequested(SignatureItem item)
    {
        if (ViewModel is not { } vm)
            return;
        SignButton.Flyout?.Hide();
        var dialog = new RenameSignatureWindow();
        dialog.SetName(item.Name);
        await dialog.ShowDialog(this);
        if (dialog.NewName is { } newName && newName != item.Name)
            vm.RenameSignature(item.Entry.Id, newName);
    }

    /// <summary>Delete, from the card's menu (#100): asks once, then removes.</summary>
    private async void OnDeleteSignatureRequested(SignatureItem item)
    {
        if (ViewModel is not { } vm)
            return;
        SignButton.Flyout?.Hide();
        var dialog = new ConfirmDeleteWindow();
        dialog.SetPrompt(item.Name);
        await dialog.ShowDialog(this);
        if (dialog.Confirmed)
            vm.RemoveSignature(item.Entry.Id);
    }

    /// <summary>The #139 warning, once per page: true for Continue.</summary>
    private async Task<bool> ConfirmPageRewriteAsync()
    {
        var dialog = new ConfirmPageRewriteWindow();
        await dialog.ShowDialog(this);
        return dialog.Confirmed;
    }

    /// <summary>
    /// Takes a signature from a photograph or scan (SDD §3.3). Most people have a
    /// signature on paper long before they have one they are willing to draw with a
    /// trackpad, so this is the path that actually gets used.
    /// </summary>
    private async Task ImportSignatureAsync()
    {
        if (ViewModel is not { } vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.ChoosePhotoOfSignature,
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });

        if (files.Count == 0)
            return;

        try
        {
            SignatureBitmap decoded;
            await using (var stream = await files[0].OpenReadAsync())
                decoded = Rendering.SignatureImages.LoadBgra(stream);

            var name = Path.GetFileNameWithoutExtension(files[0].Name);
            var entry = vm.AddSignatureFromImage(
                string.IsNullOrWhiteSpace(name) ? Strings.SignatureDefaultName : name,
                decoded,
                Rendering.SignatureImages.EncodePng);

            vm.BeginPlacing(entry);
        }
        catch (Exception ex)
        {
            vm.Status = Strings.WithDetail(Strings.CouldNotReadImage, ex.Message);
        }
    }

    private async Task CaptureSignatureAsync()
    {
        if (ViewModel is not { } vm)
            return;

        var capture = new SignatureCaptureWindow();
        await capture.ShowDialog(this);

        if (capture.Result is not { } bitmap)
            return;

        try
        {
            var png = Rendering.SignatureImages.EncodePng(bitmap);
            var entry = vm.AddSignature(capture.ResultName, png);
            // Straight into placement: someone who just drew a signature wants to put
            // it somewhere, not to admire the library.
            vm.BeginPlacing(entry);
        }
        catch (Exception ex)
        {
            vm.Status = Strings.WithDetail(Strings.CouldNotSaveSignature, ex.Message);
        }
    }

    // --- Find (SDD §3.6) ---

    /// <summary>
    /// Debounce for search-as-you-type (#66). Search walks every page and opens a
    /// pdfium handle per page, on the UI thread — so without this, an eight-letter
    /// word typed into a 200-page document is 1,600 sequential page opens and the
    /// UI cannot repaint between them. The WinUI app has used 250ms for the same
    /// reason since F6 landed.
    /// </summary>
    private DispatcherTimer? _findDebounce;

    private void WireFind()
    {
        _findDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _findDebounce.Tick += (_, _) =>
        {
            _findDebounce!.Stop();
            _ = RunSearchAsync(FindBox.Text ?? "");
        };

        FindBox.TextChanged += (_, _) =>
        {
            // Restarted on each keystroke, so the search runs once the typing
            // pauses rather than once per character.
            _findDebounce!.Stop();
            _findDebounce.Start();
        };

        // Enter advances, Shift+Enter goes back — the convention every find bar uses.
        FindBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            var backwards = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            // Enter means "now" — run any pending search before advancing, or the
            // first Enter after typing would cycle stale matches.
            if (_findDebounce is { IsEnabled: true })
            {
                _findDebounce.Stop();
                await RunSearchAsync(FindBox.Text ?? "");
            }
            if (backwards)
                ViewModel?.FindPreviousCommand.Execute(null);
            else
                ViewModel?.FindNextCommand.Execute(null);
        };

        CloseFindButton.Click += (_, _) => CloseFind();
    }

    /// <summary>Search off the UI thread (#145); a failure lands in the status line.</summary>
    private async Task RunSearchAsync(string term)
    {
        if (ViewModel is not { } vm)
            return;
        try
        {
            await vm.SearchAsync(term);
        }
        catch (Exception ex)
        {
            vm.Status = Strings.WithDetail(Strings.FileCouldNotBeRead, ex.Message);
        }
    }

    private void OpenFind()
    {
        if (ViewModel is not { } vm)
            return;
        vm.IsFindOpen = true;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void CloseFind()
    {
        _findDebounce?.Stop();
        ViewModel?.CloseFind();
        FindBox.Text = "";
    }

    /// <summary>
    /// Brings a hit into view, using the same rules as Windows (#32).
    ///
    /// This used to scroll vertically, always, and never horizontally — which is
    /// the bug #28 reported and Windows fixed: on a zoomed page a hit off to the
    /// side was highlighted where it could not be seen, and pressing Next jolted
    /// the view even when the hit was already on screen. Sharing the decision means
    /// the two desktops cannot drift apart on it again.
    /// </summary>
    private void ScrollToMatch(int pageIndex, PdfRect rect)
    {
        if (ViewModel is not { } vm)
            return;

        // Pages stack vertically and are centred horizontally, so a hit's content
        // position is the pages above it plus its own offset within its page.
        var above = 0.0;
        var pageWidth = 0.0;
        foreach (var page in vm.Pages)
        {
            if (page.Index == pageIndex)
            {
                pageWidth = page.LayoutWidth;
                break;
            }
            above += page.LayoutHeight + PageGap;
        }

        var scale = PageBitmap.PointsToPixels * vm.Zoom;
        var extentWidth = PageScroller.Extent.Width;
        // Where the page's own left edge sits in content space when it is narrower
        // than the extent (the panel centres it).
        var pageLeft = Math.Max(0, (extentWidth - pageWidth) / 2);

        var target = new PdfRect(
            pageLeft + (rect.X * scale), above + (rect.Y * scale),
            rect.Width * scale, rect.Height * scale);

        var decision = MatchScroll.Reveal(
            target,
            PageScroller.Offset.X, PageScroller.Offset.Y,
            PageScroller.Viewport.Width, PageScroller.Viewport.Height,
            extentWidth);

        if (decision.MovesAnything)
            PageScroller.Offset = new Vector(
                decision.Horizontal ?? PageScroller.Offset.X,
                decision.Vertical ?? PageScroller.Offset.Y);
    }

    /// <summary>Bottom margin on each page surface in MainWindow.axaml.</summary>
    private const double PageGap = 16;

    // --- Shortcuts ---

    /// <summary>
    /// macOS uses Cmd where Windows uses Ctrl. Avalonia does not translate this for
    /// you, so a XAML `HotKey="Ctrl+O"` would give Mac users the wrong shortcut — one
    /// of the "Mac idioms need explicit wiring" costs ADR-002 flagged against Option
    /// B. Bound here so the gesture and the tooltip advertising it cannot disagree.
    /// </summary>
    private static KeyModifiers CommandModifier =>
        OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private static string CommandSymbol => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";

    private void BindShortcuts()
    {
        // The gestures themselves are defined once, in MainWindow.MenuBar.cs, so the
        // key binding, the tooltip, the More menu and the menu bar cannot disagree.
        Bind(OpenButton, OpenGesture, Strings.OpenAPdf, () => _ = OpenDocumentAsync());
        Bind(SaveButton, SaveGesture, Strings.Save, () => Run(ViewModel?.SaveCommand));
        Bind(null, SaveAsGesture, Strings.SaveAs, () => { if (ViewModel?.IsDocumentOpen == true) _ = SaveAsAsync(); });
        Bind(null, PrintGesture, Strings.Print, () => Run(ViewModel?.PrintCommand));
        Bind(UndoButton, UndoGesture, Strings.Undo, () => Run(ViewModel?.UndoCommand));
        Bind(RedoButton, RedoGesture, Strings.Redo, () => Run(ViewModel?.RedoCommand));
        Bind(ZoomOutButton, ZoomOutGesture, Strings.ZoomOut, () => Run(ViewModel?.ZoomOutCommand));
        Bind(ZoomInButton, ZoomInGesture, Strings.ZoomIn, () => Run(ViewModel?.ZoomInCommand));
        Bind(null, ActualSizeGesture, Strings.ActualSize, () => Run(ViewModel?.ZoomResetCommand));

        // A key binding calls Execute directly, and a RelayCommand's Execute does not ask
        // CanExecute: Cmd+S with nothing changed rewrote the file while Save sat greyed
        // out (#144). A shortcut does what its button would, and nothing when it is off.
        static void Run(System.Windows.Input.ICommand? command)
        {
            if (command?.CanExecute(null) == true)
                command.Execute(null);
        }
        Bind(null, OptionsGesture, Strings.Options, ShowOptions);

        // Cmd/Ctrl+F has no toolbar button to hang a tooltip on — the find bar is
        // its own affordance once open, and the menu bar lists it.
        Bind(null, FindGesture, Strings.FindInDocument, OpenFind);

        void Bind(Button? button, KeyGesture gesture, string description, Action invoke)
        {
            KeyBindings.Add(new KeyBinding
            {
                Gesture = gesture,
                Command = new RelayCommand(invoke),
            });
            if (button is null)
                return;
            _toolbarGestures[button] = gesture;
            var shiftLabel = gesture.KeyModifiers.HasFlag(KeyModifiers.Shift) ? (OperatingSystem.IsMacOS() ? "⇧" : "Shift+") : "";
            ToolTip.SetTip(button, $"{description} ({CommandSymbol}{shiftLabel}{KeyLabel(gesture.Key)})");
        }
    }

    private static string KeyLabel(Key key) => key switch
    {
        Key.OemMinus => "-",
        Key.OemPlus => "+",
        Key.OemComma => ",",
        Key.D0 => "0",
        _ => key.ToString(),
    };

    // --- Files ---

    private async Task OpenDocumentAsync()
    {
        if (ViewModel is not { IsIdle: true })
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.OpenAPdf,
            AllowMultiple = false,
            FileTypeFilter = [PdfFileType],
        });

        if (files.Count == 0)
            return;

        await OpenStorageFileAsync(files[0]);
    }

    /// <summary>Opens a file the picker or the OS gave us, and remembers it.</summary>
    private async Task OpenStorageFileAsync(IStorageFile file)
    {
        if (ViewModel is not { } vm)
            return;

        // TryGetLocalPath returns null for a document the OS handed us out of a
        // sandboxed or virtual location; the engine takes a path, so say so rather
        // than failing silently.
        var path = file.TryGetLocalPath();
        if (path is null)
        {
            vm.Status = Strings.FileNotLocal;
            return;
        }

        // Opening another document drops this one: unsaved changes are asked about first (D1).
        if (!await ConfirmUnsavedChangesAsync())
            return;
        _pendingFile = file;
        await vm.OpenAsync(path);
        await RememberAsync(vm, file, path);
    }

    /// <summary>
    /// Reopens a document from the recents list.
    ///
    /// On macOS under the App Sandbox the stored path is not a key to anything — the
    /// grant was to the file the user picked, in that session. The security-scoped
    /// bookmark is what carries permission across launches, so it is tried first and
    /// the path is only a fallback for platforms that do not need one.
    /// </summary>
    private async Task OpenRecentAsync(RecentEntry entry)
    {
        if (ViewModel is not { IsIdle: true } vm)
            return;

        if (entry.Bookmark is { } bookmark)
        {
            IStorageFile? file = null;
            string? bookmarked = null;
            try
            {
                file = await StorageProvider.OpenFileBookmarkAsync(bookmark);
                bookmarked = file?.TryGetLocalPath();
            }
            catch (Exception)
            {
                // A stale bookmark is an ordinary outcome — the file moved, or the
                // grant expired. Fall through and try the path.
            }
            if (file is not null && bookmarked is not null)
            {
                if (!await ConfirmUnsavedChangesAsync())
                    return;
                _pendingFile = file;
                await vm.OpenAsync(bookmarked);
                await RememberAsync(vm, file, bookmarked);
                return;
            }
        }

        if (!File.Exists(entry.Path))
        {
            vm.Status = Strings.FileMovedOrDeleted;
            return;
        }

        // Ask the platform for a real file handle rather than nulling _openedFile.
        // Without one Save has nothing to write through, and because CanSave only
        // looks at "open and dirty" the button would stay enabled and do nothing —
        // silently losing the user's work, which is worse than refusing outright.
        var fromPath = await StorageProvider.TryGetFileFromPathAsync(entry.Path);
        if (fromPath is null)
        {
            vm.Status = Strings.FileCannotBeOpenedFromHere;
            return;
        }

        if (!await ConfirmUnsavedChangesAsync())
            return;
        _pendingFile = fromPath;
        await vm.OpenAsync(entry.Path);
        await RememberAsync(vm, fromPath, entry.Path);
    }

    /// <summary>
    /// Records the document in recents, with a bookmark where the platform supports
    /// one. Failing to mint a bookmark must not stop the document being remembered.
    /// </summary>
    private static async Task RememberAsync(MainViewModel vm, IStorageFile file, string path)
    {
        string? bookmark = null;
        try
        {
            bookmark = await file.SaveBookmarkAsync();
        }
        catch (Exception)
        {
        }

        vm.RememberRecent(path, bookmark);
    }

    /// <summary>
    /// Saves back over the opened file.
    ///
    /// Two paths on purpose. Where there is a usable local path — Windows, and macOS
    /// outside the sandbox — the write goes through AtomicFileWriter, which swaps a
    /// fully written temp file into place so the destination is never seen
    /// half-written (SDD §3.4). Under the App Sandbox that protocol is denied: the
    /// grant is to the file, not its folder, so the write goes through the
    /// already-open stream instead and accepts the weaker guarantee that
    /// StagedStreamWriter documents.
    /// </summary>
    /// <remarks>
    /// The sandboxed path used to open the file for writing — which truncates it — before the
    /// verified save ran, so a failed flatten or read-back left the file empty (D2, #145). The
    /// view model now builds and verifies the bytes first and opens the file only then. True
    /// when the document was saved.
    /// </remarks>
    private async Task<bool> SaveAsync()
    {
        if (ViewModel is not { } vm)
            return false;

        if (_openedFile is not { } file)
        {
            // Should not happen — but a Save that does nothing at all is the worst
            // possible outcome, so it says something and offers the way out.
            vm.Status = Strings.NowhereToSave;
            return false;
        }

        try
        {
            var path = file.TryGetLocalPath();
            if (path is not null && !OperatingSystem.IsMacOS())
                return await vm.SaveToPathAsync(path);

            return await vm.SaveThroughAsync(async () => await file.OpenWriteAsync());
        }
        catch (Exception ex)
        {
            vm.ReportSaveFailure(ex);
            return false;
        }
    }

    /// <summary>
    /// Save a copy (SDD §3.4). The picker gives back a file the sandbox has granted
    /// us, so this writes through its stream — the same path the sandboxed Save
    /// takes — and then adopts it as the document's home, which is what "Save As"
    /// means everywhere else.
    /// </summary>
    private async Task SaveAsAsync()
    {
        if (ViewModel is not { IsIdle: true } vm)
            return;

        var suggested = vm.DocumentName is { } name
            ? Strings.SuggestedCopyName(Path.GetFileNameWithoutExtension(name)) + ".pdf"
            : Strings.DefaultDocumentName + ".pdf";

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.SaveACopy,
            SuggestedFileName = suggested,
            DefaultExtension = "pdf",
            FileTypeChoices = [PdfFileType],
            ShowOverwritePrompt = true,
        });

        if (file is null)
            return;

        try
        {
            // One call, so the journal is marked against the file the bytes went to
            // and DocumentPath follows the copy (#68). The file is opened — and so
            // truncated — only once the verified bytes exist (#145).
            if (await vm.SaveAsThroughAsync(async () => await file.OpenWriteAsync(), file.TryGetLocalPath(), file.Name))
            {
                _openedFile = file;
                _pendingFile = null;
            }
        }
        catch (Exception ex)
        {
            vm.ReportSaveFailure(ex);
        }
    }

    // --- Password: unlock, set, change, remove (#131, ADR-004) ---

    /// <summary>
    /// Asks for a restricted document's owner password until it unlocks, the person
    /// cancels, or the reopen fails for another reason (which the status line reports).
    /// </summary>
    private async Task UnlockAsync()
    {
        if (ViewModel is not { IsDocumentOpen: true, IsIdle: true } vm)
            return;

        // Unlocking reopens the document from its file: unsaved changes would stay behind (D1).
        if (!await ConfirmUnsavedChangesAsync())
            return;

        var retry = false;
        while (true)
        {
            var dialog = new PasswordWindow();
            dialog.SetUnlockPrompt(vm.DocumentName ?? "", retry);
            await dialog.ShowDialog(this);
            if (string.IsNullOrEmpty(dialog.Password))
                return;
            if (await vm.UnlockAsync(dialog.Password) != MainViewModel.UnlockOutcome.WrongPassword)
                return;
            retry = true;
        }
    }

    /// <summary>
    /// The Password command. Setting, changing or removing security is a save
    /// (ADR-004 §6), so it writes the way <see cref="SaveAsync"/> does — AtomicFileWriter
    /// where there is a usable path, the granted file's stream under the sandbox — except
    /// that the verified bytes are produced first, so a refusal or an unreadable result
    /// never opens (and so never truncates) the user's file. The saved file is then
    /// reopened with the new password.
    /// </summary>
    private async Task ChangeSecurityAsync()
    {
        if (ViewModel is not { IsDocumentOpen: true, IsIdle: true } vm)
            return;

        if (!vm.Capabilities.CanChangeSecurity)
        {
            // Never offered without full access (ADR-004 §3); the prompt says why.
            await UnlockAsync();
            return;
        }

        if (_openedFile is null)
        {
            vm.Status = Strings.NowhereToSave;
            return;
        }

        // The reopen needs a path; a copy saved somewhere the platform gives no path for
        // could be written but not reopened, so it is refused before anything is written.
        if (vm.DocumentPath is not { } path)
        {
            vm.Status = Strings.FileNotLocal;
            return;
        }

        var dialog = new SecurityWindow();
        dialog.Configure(vm.DocumentName ?? Path.GetFileName(path), vm.IsEncrypted);
        await dialog.ShowDialog(this);
        if (dialog.Choice == SecurityWindow.Decision.None)
            return;

        var newPassword = dialog.Choice == SecurityWindow.Decision.Remove ? null : dialog.NewPassword;
        var done = dialog.Choice switch
        {
            SecurityWindow.Decision.Set => Strings.PasswordSetStatus,
            SecurityWindow.Decision.Change => Strings.PasswordChangedStatus,
            _ => Strings.PasswordRemovedStatus,
        };

        var file = _openedFile;
        var local = file.TryGetLocalPath();
        try
        {
            // The view model produces the verified bytes first, off the UI thread; the file is
            // written — and, under the sandbox, truncated — only then, and reopened after.
            await vm.ChangeSecurityAsync(path, newPassword, done, async staged =>
            {
                if (local is not null && !OperatingSystem.IsMacOS())
                {
                    await Task.Run(() => AtomicFileWriter.Write(local, staged.CopyTo));
                }
                else
                {
                    // In place, over the file the document reads: it moves off it first (#147).
                    await vm.KeepOpenDocumentOffFileAsync(local ?? path);
                    await using var stream = await file.OpenWriteAsync();
                    await Task.Run(() => staged.WriteOver(stream));
                }
            });
        }
        catch (Exception ex)
        {
            vm.ReportSaveFailure(ex);
        }
    }

    /// <summary>Which document the retry state below belongs to.</summary>
    private string? _passwordAskedFor;
    private bool _passwordRetry;

    private async Task<string?> AskForPasswordAsync(string fileName)
    {
        // Scoped to the file, not to the window. The flag used to persist for the
        // window's lifetime, so after unlocking one document the FIRST prompt for
        // the next one claimed a password had failed that was never entered (#65).
        if (_passwordAskedFor != fileName)
        {
            _passwordAskedFor = fileName;
            _passwordRetry = false;
        }

        var dialog = new PasswordWindow();
        dialog.SetPrompt(fileName, _passwordRetry);
        await dialog.ShowDialog(this);

        // Remember that we have asked once, so a second prompt for THIS file says
        // why it is back rather than looking like the first failed to register.
        _passwordRetry = dialog.Password is not null;
        return dialog.Password;
    }

    /// <summary>
    /// Saves a smaller copy for email (SDD §3.7). Asks where to put it first,
    /// because the shrink is destructive to image quality and belongs in a copy —
    /// never over the original.
    /// </summary>
    private async Task ShrinkForEmailAsync()
    {
        if (ViewModel is not { IsIdle: true } vm)
            return;

        if (vm.IsDirty)
        {
            vm.Status = Strings.SaveBeforeShrinking;
            return;
        }

        try
        {
            // Do the work FIRST, and only ask for a destination if there is
            // something to put in it. Opening a writable stream truncates whatever
            // is there, so asking first meant a document with nothing to shrink
            // left a 0-byte file behind — or destroyed the file the user picked to
            // overwrite — while reporting that nothing had happened (#59).
            var (result, staged) = await vm.PrepareShrunkCopyAsync();
            if (staged is null)
            {
                vm.Status = Strings.NothingToShrink;
                return;
            }
            using var stagedCopy = staged;

            var baseName = vm.DocumentName is { } n ? Path.GetFileNameWithoutExtension(n) : Strings.DefaultDocumentName;
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strings.SaveASmallerCopy,
                SuggestedFileName = Strings.SuggestedSmallerName(baseName) + ".pdf",
                DefaultExtension = "pdf",
                FileTypeChoices = [PdfFileType],
                ShowOverwritePrompt = true,
            });

            if (file is null)
                return;

            // Picked over the open document's own file, the write is in place (#147).
            await vm.KeepOpenDocumentOffFileAsync(file.TryGetLocalPath());
            await using (var stream = await file.OpenWriteAsync())
                await Task.Run(() => stagedCopy.WriteOver(stream));

            vm.Status = Strings.Plural(result.ImagesReplaced,
                Strings.SmallerCopySavedOne(result.ImagesReplaced),
                Strings.SmallerCopySavedOther(result.ImagesReplaced));
        }
        catch (Exception ex)
        {
            vm.Status = Strings.WithDetail(Strings.CouldNotShrink, ex.Message);
        }
    }

    private static FilePickerFileType PdfFileType => new(Strings.PdfDocument)
    {
        Patterns = ["*.pdf"],
        AppleUniformTypeIdentifiers = ["com.adobe.pdf"],
        MimeTypes = ["application/pdf"],
    };
}
