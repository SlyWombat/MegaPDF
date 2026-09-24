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
using MegaPDF.Core.Recovery;
using MegaPDF.Core.Services;

namespace MegaPDF.Avalonia.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// The file each open tab was opened from, kept so Save can write back to it
    /// under the App Sandbox (#348 — the plan's single most dangerous item). Keyed
    /// by <see cref="DocumentViewModel"/> rather than held on it: the view model
    /// stays UI-free (ADR-002), and a `Dictionary` keyed by the tab is exactly as
    /// "per tab" as a field on the tab itself, without pulling
    /// <c>Avalonia.Platform.Storage</c> into the ViewModels project.
    /// Entries are removed when their tab closes (<see cref="ForgetTab"/>).
    /// </summary>
    private readonly Dictionary<DocumentViewModel, IStorageFile?> _openedFiles = [];

    public MainWindow()
    {
        InitializeComponent();
        SizeToWorkingArea();

        // ADR-002 called this one of the two DocumentViewModel touch points that is a
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
            if (RecentList.SelectedItem is not ShellViewModel.RecentRow row)
                return;
            RecentList.SelectedItem = null;
            await GuardedAsync(() => OpenRecentAsync(row.Entry));
        };

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);

        // Drag-and-drop (#348 — the issue asks for it and it did not exist at all:
        // `grep DragDrop src/MegaPDF.Avalonia` was empty). On the whole window, so
        // the tab strip and the empty state both accept a drop, not just the page
        // area. Each .pdf routes the same way File > Open does: its own tab, or the
        // existing one activated if it is already open.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);

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

    /// <summary>The window's tabs and app-scoped services (#348). Set once, at construction.
    /// Internal (not private): App.axaml.cs's capture/diagnostic rigs and Program.cs's
    /// self-test read it the way they used to read <c>window.DataContext as MainViewModel</c>.</summary>
    internal ShellViewModel? Shell => DataContext as ShellViewModel;

    /// <summary>The active tab's document — what every toolbar/menu binding and every
    /// event handler below acts on. Standing in for the pre-#348 single <c>ViewModel</c>.</summary>
    internal DocumentViewModel? Active => Shell?.Active;

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
            if (Active is { } vm)
                vm.Status = Strings.WithDetail(Strings.FileCouldNotBeRead, ex.Message);
        }
    }

    /// <summary>A file the person chose, waiting for its document to finish opening before Save writes through it.</summary>
    private IStorageFile? _pendingFile;

    /// <summary>The tab <see cref="_pendingFile"/> belongs to, so a slower-opening tab cannot adopt a faster one's file.</summary>
    private DocumentViewModel? _pendingFileOwner;

    /// <summary>
    /// The storage file follows the open document (#145). Opening is async now, and a document
    /// that fails to open leaves the previous one on screen, so the file handle is adopted only
    /// once its document is the one open — or Save would write the old document into the new file.
    /// </summary>
    private void FollowDocumentPath(DocumentViewModel document, string documentPath)
    {
        if (_pendingFile is { } pending && ReferenceEquals(_pendingFileOwner, document)
            && SamePath(pending.TryGetLocalPath(), documentPath))
        {
            _openedFiles[document] = pending;
            _pendingFile = null;
            _pendingFileOwner = null;
        }
        else if (_openedFiles.TryGetValue(document, out var current) && current is { } file
                 && !SamePath(file.TryGetLocalPath(), documentPath))
        {
            // A document opened by path alone has no handle to write through: Save says so.
            _openedFiles[document] = null;
        }
    }

    /// <summary>Drops a closed tab's file handle and Save-related bookkeeping (#348).</summary>
    private void ForgetTab(DocumentViewModel document)
    {
        _openedFiles.Remove(document);
        if (ReferenceEquals(_pendingFileOwner, document))
        {
            _pendingFile = null;
            _pendingFileOwner = null;
        }
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Shell is { } shell)
        {
            shell.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ShellViewModel.Active))
                    OnActiveDocumentChanged();
            };
            // The menu bar (#144) lists the view model's font and size choices; the
            // structure is the same for every tab, so it is built once per window.
            BuildMenuBar();
            OnActiveDocumentChanged();
        }
    }

    /// <summary>The document whose events this window is currently wired to — see <see cref="OnActiveDocumentChanged"/>.</summary>
    private DocumentViewModel? _wiredDocument;

    /// <summary>
    /// Rewires every per-document event to the newly active tab, and unwires the one
    /// before it (#348 plan §6.6 — the RefreshMenuBar subscription leak, generalised
    /// to every subscription this window makes onto "the" view model). Without this,
    /// switching tabs would leave the window listening to whichever document was
    /// active when the window was created — or, before any tab exists, to nothing at
    /// all — and a closed tab's Busy/PropertyChanged would go on refreshing the menu
    /// bar and the toolbar forever.
    /// </summary>
    private void OnActiveDocumentChanged()
    {
        if (ReferenceEquals(_wiredDocument, Active))
            return;

        if (_wiredDocument is { } old)
        {
            old.Busy.PropertyChanged -= OnActiveBusyChanged;
            old.SaveRequested -= OnActiveSaveRequested;
            old.ScrollToRequested -= ScrollToMatch;
            old.FocusScrollRequested -= ScrollToMatch;
            old.EditLineRequested -= ShowLineEditor;
            old.PasswordRequested -= AskForPasswordAsync;
            old.EditFieldRequested -= ShowFieldEditor;
            old.RenameSignatureRequested -= OnRenameSignatureRequested;
            old.DeleteSignatureRequested -= OnDeleteSignatureRequested;
            old.PageRewriteConfirmationRequested -= ConfirmPageRewriteAsync;
            old.PrintDestinationRequested -= ChoosePrinterAsync;
            old.PropertyChanged -= OnActiveDocumentPropertyChanged;
        }

        _wiredDocument = Active;
        // Chrome tied to the previously active tab's page list does not belong to the
        // one now on screen (#348 §4 — the transient view state a full per-tab
        // DocumentView would otherwise keep separate; see the PR description for why
        // this pass shares one page host across tabs instead).
        DismissInlineEditor();
        RemoveChrome();
        RemoveFocusRing();

        if (Active is { } vm)
        {
            // A window: engine work runs off the UI thread from here on (#145).
            vm.RunsInBackground = true;
            vm.Busy.PropertyChanged += OnActiveBusyChanged;
            vm.SaveRequested += OnActiveSaveRequested;
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
            vm.PrintDestinationRequested += ChoosePrinterAsync;
            vm.PropertyChanged += OnActiveDocumentPropertyChanged;
        }

        ApplyToolbarLayout();
        UpdateViewport();
        RefreshMenuBar();
    }

    private void OnActiveBusyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshMenuBar();

    private void OnActiveSaveRequested() => _ = SaveAsync();

    private void OnActiveDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (sender is not DocumentViewModel vm)
            return;
        // A card was clicked: placement is armed, so the library closes and
        // the next click goes to the page.
        if (args.PropertyName is nameof(DocumentViewModel.IsPlacingSignature) && vm.IsPlacingSignature)
            SignButton.Flyout?.Hide();
        // The chrome is positioned in device-independent pixels, so it has to
        // be rebuilt when the selection changes and when zoom moves it.
        if (args.PropertyName is nameof(DocumentViewModel.Selection) or nameof(DocumentViewModel.Zoom))
            OnSelectionChanged();
        if (args.PropertyName is nameof(DocumentViewModel.PageFocus) or nameof(DocumentViewModel.Zoom))
            OnPageFocusChanged();
        // The pickers join and leave the row with their context (#144).
        if (args.PropertyName is nameof(DocumentViewModel.IsTextStyleContext))
            ApplyToolbarLayout();
        // An editor writing new text shows the face and size it will be written in.
        if (args.PropertyName is nameof(DocumentViewModel.TextFont) or nameof(DocumentViewModel.TextSize))
            FollowPickersInEditor();
        if (args.PropertyName is nameof(DocumentViewModel.DocumentPath) && vm.DocumentPath is { } documentPath)
            FollowDocumentPath(vm, documentPath);
        RefreshMenuBar();
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
        if (Active is { IsDocumentOpen: true, PageFocus: not null })
            e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Delete takes off whatever is selected, and a redaction mark is one of those things
        // now (#329): it rides the same chrome as a signature, so it comes off the same way.
        if (e.Key is Key.Delete or Key.Back && Active is { Selection: not null } selected)
        {
            selected.DeleteSelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && Active is { Selection: not null } hasSelection)
        {
            hasSelection.ClearSelection();
            e.Handled = true;
            return;
        }

        // Escape is how every desktop app leaves a mode. Placement first: if both are
        // active, the one the user most recently entered is the one they mean.
        if (e.Key == Key.Escape && Active is { IsPlacingSignature: true } vm)
        {
            vm.CancelPlacing();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && Active is { IsModeActive: true } modal)
        {
            DismissInlineEditor();
            modal.CancelModes();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && Active is { IsFindOpen: true })
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
        // it to every tab's view model — not just the active one — is what makes a
        // page sharp on a retina Mac rather than upscaled from a 96 DPI raster, and
        // it has to reach a tab opened later too (#348 §2b), which PushDpiScale below
        // and every AddTab call site both do.
        if (Shell is { } shell)
        {
            PushDpiScale(shell);
            shell.LoadRecents();
            UpdateViewport();
        }

        PageScroller.ScrollChanged += (_, _) => UpdateViewport();
        PageScroller.SizeChanged += (_, _) => UpdateViewport();

        LaunchSequence = RunLaunchSequenceAsync();
    }

    /// <summary>Pushes this window's current display scale into every open tab (#348 §2b).</summary>
    private void PushDpiScale(ShellViewModel shell)
    {
        foreach (var document in shell.Documents)
            document.DpiScale = RenderScaling;
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

    // --- Documents handed over by the OS (#143), and the launch sequence (#145, #153) ---

    /// <summary>
    /// True once the launch sequence is over. Until then a document the OS hands over
    /// waits, so the crash-recovery offer is never overtaken by it (#145).
    /// </summary>
    private bool _launchSettled;

    /// <summary>
    /// Documents the OS handed over while the launch sequence was still running, each
    /// opened into its own tab once the sequence settles (#348: this used to be a
    /// single slot with a comment reading "one window shows one document, so the last
    /// one handed over wins" — a multi-file Finder/Dock open dropped every file but
    /// the last. A list keeps them all; nothing here yet coalesces a *redirected*
    /// second launch into this window — that is Phase 2's single-instance work.
    /// </summary>
    private readonly List<(string? Path, Func<Task> Open)> _pendingOpens = [];

    /// <summary>Completed when one arrives, so the wait below ends on arrival rather than on the clock.</summary>
    private TaskCompletionSource? _handedOverDocumentArrived;

    /// <summary>
    /// The launch sequence, for the self-test to wait on. Already completed until
    /// <see cref="OnOpened"/> starts the real one.
    /// </summary>
    internal Task LaunchSequence { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Opens a document from Finder: a double-click, a PDF dropped on the Dock icon,
    /// Open With. macOS delivers these as an Apple Event after launch rather than as
    /// arguments, both to an app it is starting and to one already running, and
    /// Avalonia surfaces them as IActivatableLifetime.Activated (App wires that up).
    /// The storage file, not just its path, is kept so Save can write back through it.
    /// </summary>
    public void OpenFromSystem(IStorageFile file) =>
        OpenWhenReady(file.TryGetLocalPath(), () => OpenStorageFileAsync(file));

    /// <summary>A PDF path from the command line (the Windows file association, `open --args`).</summary>
    public void OpenFromSystem(string path) => OpenWhenReady(path, async () =>
    {
        // Asked for as a storage file for the same reason as above; without one
        // Save would have nothing to write through.
        if (await StorageProvider.TryGetFileFromPathAsync(path) is { } file)
            await OpenStorageFileAsync(file);
        else
            await OpenPathIntoTabAsync(path);
    });

    private void OpenWhenReady(string? path, Func<Task> open)
    {
        if (!_launchSettled)
        {
            // Every document handed over before the launch sequence settles gets its
            // own tab (#348) — this used to keep only the last one.
            _pendingOpens.Add((path, open));
            _handedOverDocumentArrived?.TrySetResult();
            return;
        }
        _ = RunOpenAsync(open);
        Activate();
    }

    /// <summary>How many documents handed over by the OS were actually opened, for the self-test.</summary>
    internal int OpenedFromSystemCount { get; private set; }

    private async Task RunOpenAsync(Func<Task> open)
    {
        OpenedFromSystemCount++;
        try
        {
            await open();
        }
        catch (Exception ex)
        {
            if (Active is { } vm)
                vm.Status = Strings.WithDetail(Strings.FileCouldNotBeRead, ex.Message);
        }
    }

    /// <summary>
    /// Opens a bare path (no <see cref="IStorageFile"/> — a document handed over from
    /// a location the storage provider would not resolve) into a tab of its own,
    /// activating a tab already on that path instead of opening a duplicate (#348 §1).
    /// </summary>
    private async Task OpenPathIntoTabAsync(string path, string? password = null)
    {
        if (Shell is not { } shell)
            return;
        if (shell.FindTab(path) is { } existing)
        {
            shell.ActivateTab(existing);
            return;
        }
        var document = shell.CreateDocument();
        // Added — and so activated and wired up — before the open runs, not after: a
        // password-protected document raises PasswordRequested from inside OpenAsync,
        // and only the active tab's window wiring listens for it. A tab that never
        // manages to open (and is not merely waiting on a password) is removed again
        // below, so this still keeps the "no Untitled tab" rule (#348 §1).
        shell.AddTab(document);
        document.DpiScale = RenderScaling;
        await document.OpenAsync(path, password);
        if (!document.IsDocumentOpen && document.PendingPasswordPath is null)
            shell.CloseTab(document);
    }

    /// <summary>
    /// The crash-recovery offer first, then the document the app was launched with (#145).
    ///
    /// It used to be the other way round, and deliberately so: the offer only asks when
    /// nothing is open, so opening first was how a file the person chose in Finder was
    /// kept clear of a prompt about some other document. It did not work — the Finder
    /// open arrives as an Apple Event that can land after the window, so the prompt went
    /// up anyway and landed on top of the document (#153) — and it cost far more than it
    /// bought: after a crash, a double-click or "Open with" never offered recovery at
    /// all, and when the file was the crashed document itself, opening it began a new
    /// journal session over that document's own journal. BeginSession truncates, so the
    /// unsaved edits went for good. Windows had the same bug and was fixed first (#249).
    ///
    /// Offering before anything opens keeps the original promise — the offer is never a
    /// prompt over the document the person asked for, because that document is not open
    /// yet — and it is what a Mac does at launch anyway: restore state first, then take
    /// the documents. The launched file opens afterwards unless the restore has already
    /// opened that same document, decided by what is open rather than by the answer
    /// (<see cref="LaunchedDocument.NeedsOpening"/>).
    /// </summary>
    private async Task RunLaunchSequenceAsync()
    {
        try
        {
            await OfferRecoveryOnLaunchAsync();
        }
        catch (Exception ex)
        {
            // The launched file still gets its chance below: a recovery offer that
            // failed must not also cost the person the document they double-clicked.
            if (Active is { } vm)
                vm.Status = Strings.WithDetail(Strings.FileCouldNotBeRead, ex.Message);
        }

        var pending = _pendingOpens.ToList();
        _pendingOpens.Clear();
        // From here a handed-over document opens straight away. Nothing is awaited
        // between reading the pending opens and this line, so an activation cannot land
        // in the gap and be dropped.
        _launchSettled = true;

        foreach (var (path, open) in pending)
        {
            // A path the OS would not give us (a document handed over out of a virtual
            // location) cannot be compared, so it is opened: the open asks about unsaved
            // changes rather than losing them. Everything else opens into its own tab
            // unless a just-restored crash session already put it in one (#348 §5.5).
            if (path is null || (Shell is { } shell && !shell.IsOpen(path)))
                await RunOpenAsync(open);
        }
    }

    /// <summary>
    /// Whether the launch sequence waits a moment for a document the OS may still be
    /// about to hand over.
    ///
    /// True on macOS, where a Finder open is an Apple Event that on a cold launch is
    /// delivered after the window has opened, so the app does not yet know which file it
    /// was launched with. Windows and Linux pass the path in argv, which App reads before
    /// the window exists, so there is nothing to wait for and the offer is not delayed.
    /// Settable so the self-test can drive both orders on one machine.
    /// </summary>
    internal static bool WaitsForHandedOverDocument { get; set; } = OperatingSystem.IsMacOS();

    /// <summary>
    /// How long that wait lasts. Long enough for an Apple Event that is already on its
    /// way, short enough not to read as a slow launch — and only ever paid when there is
    /// a crashed session to ask about.
    /// </summary>
    internal static TimeSpan HandedOverDocumentGrace { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Set for a capture or diagnostic run (App reads the arguments): those are never
    /// offered recovery. Nobody is there to answer, and the journal the offer would name
    /// was left by an earlier run of the rig rather than by a person — #153 watched
    /// exactly that happen, a `--story` run's journal prompting on every later launch.
    /// It used to be covered by accident, the document opening first and the offer
    /// standing down for it; with the offer ahead of the document it has to be said.
    /// </summary>
    internal bool SkipRecoveryOffer { get; set; }

    private async Task OfferRecoveryOnLaunchAsync()
    {
        if (SkipRecoveryOffer || Shell is not { } shell)
            return;

        // The ordinary case — no crashed session — costs nothing: no wait, no dialog,
        // and the launched document opens as immediately as it always did.
        var sessions = shell.FindRecoverableSessions();
        if (sessions.Count == 0)
            return;

        if (WaitsForHandedOverDocument && _pendingOpens.Count == 0)
            await WaitForHandedOverDocumentAsync();

        // Every crashed session gets its own offer, newest first, each restored into
        // its own tab (#348 §5.3) — this used to offer only the newest and silently
        // sit on the rest.
        foreach (var session in sessions)
            await OfferRecoveryAsync(session);
    }

    private async Task WaitForHandedOverDocumentAsync()
    {
        _handedOverDocumentArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Task.WhenAny(_handedOverDocumentArrived.Task, Task.Delay(HandedOverDocumentGrace));
        }
        finally
        {
            _handedOverDocumentArrived = null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Shell?.Dispose();
    }

    // --- Unsaved changes: closing, quitting, opening another (#145, D1) ---

    /// <summary>The person has answered for this close or quit; the next Closing goes ahead.</summary>
    private bool _closeConfirmed;
    private bool _confirmingClose;

    /// <summary>For capture runs, which quit whatever they changed with nobody there to answer.</summary>
    internal void SkipCloseConfirmation()
    {
        _closeConfirmed = true;
        // What a capture changed is not the person's: no journal is left behind for
        // any tab, not only the active one.
        if (Shell is { } shell)
            foreach (var document in shell.Documents)
                document.DiscardChanges();
    }

    /// <summary>Whether closing or quitting must ask first: any tab with unsaved changes, or work still running.</summary>
    internal bool NeedsConfirmationBeforeClose =>
        !_closeConfirmed && Shell is { } shell
        && shell.Documents.Any(vm => vm.Busy.IsWorking || (vm.IsDocumentOpen && vm.IsDirty));

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

    /// <summary>This window's every tab confirmed (Save/Don't Save/Cancel each), for App.axaml.cs's
    /// multi-window quit — see <see cref="App.ConfirmThenQuitAllAsync"/>. True when this window may close.</summary>
    internal Task<bool> ConfirmCloseForQuitAsync() => ConfirmCloseAsync();

    /// <summary>
    /// Asks the application to quit — Ctrl+Q on Linux (#158), and exactly what the Mac's
    /// Quit item calls. <c>TryShutdown</c> rather than <c>Shutdown</c> is the whole point:
    /// it raises ShutdownRequested, which is where the unsaved-changes question is put
    /// (App.axaml.cs). <c>Shutdown</c> would quit without asking anybody anything.
    /// </summary>
    internal void RequestQuit()
    {
        if (QuitForTest is { } quit)
        {
            quit();
            return;
        }
        if (global::Avalonia.Application.Current?.ApplicationLifetime
            is global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.TryShutdown();
    }

    /// <summary>
    /// Substituted by the headless self-test, which is set up with
    /// <c>SetupWithoutStarting</c> and so has no application lifetime to shut down —
    /// the same reason <see cref="AnswerUnsavedChangesForTest"/> exists.
    /// </summary>
    internal Action? QuitForTest { get; set; }

    /// <summary>
    /// Waits for every tab's save in progress, then asks about each one's unsaved
    /// changes in turn — window close and quit both ask every dirty tab before any of
    /// them closes, and a Cancel on tab 2 leaves tab 1's journal (and every other
    /// tab already confirmed) intact, because nothing here closes a tab itself; the
    /// caller only proceeds to <c>Close()</c>/shutdown once every tab has said yes
    /// (#348 §5.6). True when closing may go ahead.
    /// </summary>
    private async Task<bool> ConfirmCloseAsync()
    {
        if (_confirmingClose)
            return false;
        _confirmingClose = true;
        try
        {
            if (Shell is { } shell)
            {
                foreach (var document in shell.Documents.ToList())
                {
                    if (!await ConfirmUnsavedChangesAsync(document))
                        return false;
                }
            }
            _closeConfirmed = true;
            return true;
        }
        catch (Exception ex)
        {
            if (Active is { } vm)
                vm.Status = Strings.WithDetail(Strings.CouldNotSave, ex.Message);
            return false;
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    /// <summary>Save, Don't Save or Cancel for the active tab — see <see cref="ConfirmUnsavedChangesAsync(DocumentViewModel?)"/>.</summary>
    private Task<bool> ConfirmUnsavedChangesAsync() => ConfirmUnsavedChangesAsync(Active);

    /// <summary>
    /// Save, Don't Save or Cancel, when <paramref name="document"/> has unsaved changes —
    /// the standard macOS question. True when the caller may go on: nothing unsaved,
    /// saved, or Don't Save. Cancel changes nothing, the recovery journal included.
    ///
    /// Activates the tab first (#348): Save writes through whichever file handle
    /// <see cref="Active"/> resolves to, and the dialog should show the document it is
    /// asking about in front, not behind whatever tab happened to be selected.
    /// </summary>
    private async Task<bool> ConfirmUnsavedChangesAsync(DocumentViewModel? document)
    {
        if (document is not { IsDocumentOpen: true } vm)
            return true;
        await vm.Busy.WhenIdleAsync();
        if (!vm.IsDirty)
            return true;

        if (Shell is { } shell)
            shell.ActivateTab(vm);

        var choice = await AskAboutUnsavedChangesAsync(vm);
        switch (choice)
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

    /// <summary>
    /// Save, Don't Save or Cancel. Substituted by the headless self-test for the same
    /// reason as <see cref="AnswerRecoveryForTest"/>.
    /// </summary>
    internal Func<UnsavedChangesWindow.Decision>? AnswerUnsavedChangesForTest { get; set; }

    /// <summary>How many times the question was put, for the self-test.</summary>
    internal int UnsavedChangesAsked { get; private set; }

    private async Task<UnsavedChangesWindow.Decision> AskAboutUnsavedChangesAsync(DocumentViewModel vm)
    {
        UnsavedChangesAsked++;
        if (AnswerUnsavedChangesForTest is { } answer)
            return answer();

        var dialog = new UnsavedChangesWindow();
        dialog.SetDocument(vm.DocumentName ?? "");
        await dialog.ShowDialog(this);
        return dialog.Choice;
    }

    /// <summary>For the `unsaved` screenshot state: the question, shown beside the window rather than modal.</summary>
    internal Window ShowUnsavedChangesForScreenshot()
    {
        var dialog = new UnsavedChangesWindow();
        dialog.SetDocument(Active?.DocumentName ?? "");
        dialog.Show(this);
        return dialog;
    }

    // --- Text boxes and whiteout on the page (SDD §3.1, §3.3) ---

    /// <summary>The in-place editor, while one is open. Only ever one at a time.</summary>
    private TextBox? _inlineEditor;

    /// <summary>Rubber band for the whiteout and redaction drags, and where it started.</summary>
    private Rectangle? _band;
    private Point _bandOrigin;
    private Control? _bandHost;

    /// <summary>Whether the band in progress marks a redaction rather than covering.</summary>
    private bool _bandIsRedaction;

    /// <summary>
    /// An editor placed where the user clicked, showing the face and size the text
    /// will actually be written in. Typing into a dialog and hoping is the thing
    /// SDD §2.2 is against — you should see the words land where they will sit.
    /// </summary>
    private void ShowInlineEditor(
        Control container, Point at, double fontSizePoints, string fontFamily,
        string initialText, double minWidth, Action<string> commit)
    {
        if (Active is not { } vm || container is not ContentPresenter presenter)
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
        if (Active is not { } vm)
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
        if (!_editorFollowsPickers || _inlineEditor is not { } editor || Active is not { } vm)
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
        if (Active is not { } vm)
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
        if (Active is not { } vm || ContainerFor(pageIndex) is not { } container)
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
        if (Active is { } vm)
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
        if (Active is not { } vm)
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
    /// purpose. Asked at launch, before the document the app was launched with is
    /// opened (<see cref="RunLaunchSequenceAsync"/>), so it is never a prompt on top of
    /// the file the person asked Finder for (#153) and never arrives after that file has
    /// truncated the very journal it is offering (#145).
    /// </summary>
    private async Task OfferRecoveryAsync(RecoverableSession session)
    {
        if (Shell is not { } shell)
            return;

        var choice = await AskAboutRecoveryAsync(session);
        switch (choice)
        {
            case RecoveryWindow.Decision.Restore:
                // A crashed session restores into a brand new tab (#348 §5.3), not into
                // whichever document happened to be active — there may be none yet.
                var document = await shell.RestoreSessionAsync(session);
                document.DpiScale = RenderScaling;
                break;
            case RecoveryWindow.Decision.Discard:
                ShellViewModel.DiscardSession(session);
                break;
        }
    }

    /// <summary>
    /// Restore, Discard or Decide later. Substituted by the headless self-test, which has
    /// no message loop and would hang on a modal rather than answer it.
    /// </summary>
    internal Func<RecoverableSession, RecoveryWindow.Decision>? AnswerRecoveryForTest { get; set; }

    private async Task<RecoveryWindow.Decision> AskAboutRecoveryAsync(RecoverableSession session)
    {
        if (AnswerRecoveryForTest is { } answer)
            return answer(session);

        var dialog = new RecoveryWindow();
        dialog.SetSession(Path.GetFileName(session.DocumentPath), session.EntryCount, session.LastWriteUtc);
        await dialog.ShowDialog(this);
        return dialog.Choice;
    }

    // --- Clicking the page (SDD §3.2) ---

    private void OnPagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control container || container.DataContext is not PageViewModel page)
            return;
        if (Active is not { } vm)
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
            case DocumentViewModel.PageMode.AddText:
                ShowNewTextEditor(container, page, position, pagePoint);
                break;

            case DocumentViewModel.PageMode.Whiteout:
                BeginBand(container, position, e, redaction: false);
                break;

            case DocumentViewModel.PageMode.Redact:
                BeginBand(container, position, e, redaction: true);
                break;

            default:
                DismissInlineEditor();
                // A click that lands on nothing deselects, which is what every
                // desktop app does and what makes the chrome feel like chrome.
                // A click on a mark selects it, so ✕ or Delete can take it off again.
                if (vm.SelectRedactionMarkAt(page.Index, pagePoint))
                    break;
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
        if (Active is not { } vm)
            return;

        // In a placement mode the cursor describes the mode, not what is underneath.
        var shape = vm.Mode switch
        {
            DocumentViewModel.PageMode.AddText => StandardCursorType.Ibeam,
            DocumentViewModel.PageMode.Whiteout or DocumentViewModel.PageMode.Redact => StandardCursorType.Cross,
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

    private void BeginBand(Control container, Point origin, PointerPressedEventArgs e, bool redaction)
    {
        if (container is not ContentPresenter presenter || OverlayOf(presenter) is not { } overlay)
            return;

        _bandOrigin = origin;
        _bandHost = container;
        _bandIsRedaction = redaction;
        // The band shows what the tool does before it is done: white and opaque for a
        // cover, translucent ink for a redaction — where the text under it stays readable,
        // because a mark is something you check before you apply it (#173).
        _band = new Rectangle
        {
            Fill = redaction
                ? this.FindResource("BrandRedactionMark") as IBrush ?? Brushes.SlateGray
                : Brushes.White,
            Opacity = redaction ? 1.0 : 0.75,
            Stroke = redaction
                ? this.FindResource("BrandRedactionMarkOutline") as IBrush ?? Brushes.DimGray
                : Brushes.Gray,
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

        if (_band is null || _bandHost is null || Active is not { } vm)
            return;

        if (_bandHost.DataContext is PageViewModel page)
        {
            var dipToPoint = 1.0 / (PageBitmap.PointsToPixels * vm.Zoom);
            var bounds = new PdfRect(_band.Margin.Left * dipToPoint, _band.Margin.Top * dipToPoint,
                _band.Width * dipToPoint, _band.Height * dipToPoint);
            if (_bandIsRedaction)
                vm.AddRedactionMark(page.Index, bounds);
            else
                vm.AddWhiteout(page.Index, bounds);
        }

        (_band.Parent as Panel)?.Children.Remove(_band);
        _band = null;
        _bandHost = null;
        _bandIsRedaction = false;
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
        if (Active is not { } vm)
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
        if (Active is not { } vm)
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
        if (Active is not { } vm)
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
    /// Which printer, and how many copies (#158). CUPS' lp would have used the
    /// default queue without asking, and printing to the wrong printer is the one
    /// mistake in this app that cannot be undone.
    /// </summary>
    private async Task<Platform.Printing.Choice?> ChoosePrinterAsync(
        IReadOnlyList<Platform.Printing.Destination> destinations)
    {
        var dialog = new PrinterWindow();
        dialog.Present(Active?.DocumentName ?? "", destinations);
        await dialog.ShowDialog(this);
        return dialog.Chosen;
    }

    /// <summary>
    /// Takes a signature from a photograph or scan (SDD §3.3). Most people have a
    /// signature on paper long before they have one they are willing to draw with a
    /// trackpad, so this is the path that actually gets used.
    /// </summary>
    private async Task ImportSignatureAsync()
    {
        if (Active is not { } vm)
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
        if (Active is not { } vm)
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
                Active?.FindPreviousCommand.Execute(null);
            else
                Active?.FindNextCommand.Execute(null);
        };

        CloseFindButton.Click += (_, _) => CloseFind();

        WireFindBar();
    }

    /// <summary>Search off the UI thread (#145); a failure lands in the status line.</summary>
    private async Task RunSearchAsync(string term)
    {
        if (Active is not { } vm)
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
        if (Active is not { } vm)
            return;
        vm.IsFindOpen = true;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void CloseFind()
    {
        _findDebounce?.Stop();
        Active?.CloseFind();
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
        if (Active is not { } vm)
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
    internal static KeyModifiers CommandModifier =>
        OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private static string CommandSymbol => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";

    private void BindShortcuts()
    {
        // The gestures themselves are defined once, in MainWindow.MenuBar.cs, so the
        // key binding, the tooltip, the More menu and the menu bar cannot disagree.
        Bind(OpenButton, OpenGesture, Strings.OpenAPdf, () => _ = OpenDocumentAsync());
        Bind(SaveButton, SaveGesture, Strings.Save, () => Run(Active?.SaveCommand));
        Bind(null, SaveAsGesture, Strings.SaveAs, () => { if (Active?.IsDocumentOpen == true) _ = SaveAsAsync(); });
        Bind(null, PrintGesture, Strings.Print, () => Run(Active?.PrintCommand));
        Bind(UndoButton, UndoGesture, Strings.Undo, () => Run(Active?.UndoCommand));
        Bind(RedoButton, RedoGesture, Strings.Redo, () => Run(Active?.RedoCommand));
        Bind(ZoomOutButton, ZoomOutGesture, Strings.ZoomOut, () => Run(Active?.ZoomOutCommand));
        Bind(ZoomInButton, ZoomInGesture, Strings.ZoomIn, () => Run(Active?.ZoomInCommand));
        Bind(null, ActualSizeGesture, Strings.ActualSize, () => Run(Active?.ZoomResetCommand));

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

        BindLinuxWindowShortcuts();

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

    /// <summary>
    /// Close and Quit from the keyboard, on Linux only (#158).
    ///
    /// Every other shortcut in <see cref="BindShortcuts"/> reaches Linux because the
    /// command also has a toolbar button or a window binding. Close does not: it lives
    /// only as a <c>NativeMenuItem</c> gesture in MainWindow.MenuBar.cs, and
    /// MainWindow.axaml carries no <c>NativeMenuBar</c>, so on X11 nothing hosts the
    /// menu and the gesture is built and never heard. Quit has no item at all off
    /// macOS. Measured in the 2026-09-18 RC pass (#146): Ctrl+W and Ctrl+Q left a
    /// changed document open with no prompt.
    ///
    /// GNOME's HIG and KDE's KStandardShortcut both give Ctrl+W to closing the window
    /// and Ctrl+Q to quitting the application, so the Linux build answers both.
    ///
    /// Linux only, deliberately. macOS already answers ⌘W and ⌘Q through the real
    /// menu bar — a window binding there would be a second route to the same command,
    /// and on the Mac the menu's key equivalent answers first, so the pair would
    /// disagree about which one ran. Windows has neither convention and is untouched.
    ///
    /// Minimize (⌘M) is **not** mirrored. It is a Mac convention; on GNOME and KDE
    /// minimizing is the window manager's, not the application's (Super+H, Alt+F3),
    /// and an app that took Ctrl+M would be taking a key its desktop has not given it.
    ///
    /// Both routes ask what the window's close button asks. Ctrl+W is
    /// <see cref="Window.Close()"/>, which runs OnClosing → NeedsConfirmationBeforeClose
    /// → Save / Don't Save / Cancel. Ctrl+Q is <see cref="RequestQuit"/>, which is what
    /// the Mac's Quit item does, and the same ShutdownRequested handler puts the same
    /// question (App.axaml.cs).
    /// </summary>
    private void BindLinuxWindowShortcuts()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // Whichever window has the keys is the one that closes: a window's key binding
        // only fires while that window is focused, so this is `this`. About and the
        // notices answer Ctrl+W themselves, for the same reason.
        KeyBindings.Add(new KeyBinding { Gesture = CloseGesture, Command = new RelayCommand(() => _ = CloseActiveTabOrWindowAsync()) });
        KeyBindings.Add(new KeyBinding { Gesture = QuitGesture, Command = new RelayCommand(RequestQuit) });
        // GNOME/KDE's own tab-switching convention (#348) — the Mac side reaches
        // Show Next/Previous Tab through the real menu bar's ⌃Tab/⌃⇧Tab instead.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageDown, KeyModifiers.Control), Command = new RelayCommand(() => Shell?.ActivateNextTab()) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageUp, KeyModifiers.Control), Command = new RelayCommand(() => Shell?.ActivatePreviousTab()) });
        KeyBindings.Add(new KeyBinding { Gesture = NewWindowGestureLinux, Command = new RelayCommand(NewWindow) });
    }

    /// <summary>⌘W (Mac) / Ctrl+W (Linux): closes the active tab, or the window itself when it is the last tab
    /// (Safari/Preview/GNOME convention, #348 plan §1).</summary>
    internal async Task CloseActiveTabOrWindowAsync()
    {
        if (Shell is not { } shell)
        {
            Close();
            return;
        }
        if (shell.Documents.Count <= 1)
        {
            Close();
            return;
        }
        if (Active is { } vm)
            await CloseTabAsync(vm);
    }

    /// <summary>The ✕ on a tab-strip item (MainWindow.axaml).</summary>
    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DocumentViewModel document)
            _ = CloseTabAsync(document);
        e.Handled = true;
    }

    /// <summary>Closes one tab, asking about its unsaved changes first. Used by ⌘W/Ctrl+W and the tab strip's close button.</summary>
    internal async Task CloseTabAsync(DocumentViewModel document)
    {
        if (Shell is not { } shell || !shell.Documents.Contains(document))
            return;
        if (!await ConfirmUnsavedChangesAsync(document))
            return;
        ForgetTab(document);
        shell.CloseTab(document);
    }

    /// <summary>⇧⌘W (Mac) / no Linux binding yet: closes the whole window regardless of how many tabs it has.</summary>
    internal void CloseWindow() => Close();

    /// <summary>File ▸ New Window (⌘N / Ctrl+Shift+N): another window sharing this process's settings, recents and signature library.</summary>
    internal void NewWindow()
    {
        if (Shell is not { } shell)
            return;
        var window = new MainWindow
        {
            DataContext = new ShellViewModel(shell.Settings, shell.RecentFiles, shell.SignatureLibrary, shell.RecoveryDirectory),
        };
        window.Show();
    }

    private static KeyGesture NewWindowGestureLinux => new(Key.N, KeyModifiers.Control | KeyModifiers.Shift);

    private static string KeyLabel(Key key) => key switch
    {
        Key.OemMinus => "-",
        Key.OemPlus => "+",
        Key.OemComma => ",",
        Key.D0 => "0",
        _ => key.ToString(),
    };

    // --- Drag-and-drop (#348) ---

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = PdfFilesIn(e.Data).Any() ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object? sender, DragEventArgs e)
    {
        var files = PdfFilesIn(e.Data).ToList();
        e.Handled = true;
        if (files.Count == 0)
            return;
        _ = GuardedAsync(async () =>
        {
            foreach (var file in files)
                await OpenStorageFileAsync(file);
        });
    }

    private static IEnumerable<IStorageFile> PdfFilesIn(global::Avalonia.Input.IDataObject data) =>
        (data.GetFiles() ?? []).OfType<IStorageFile>().Where(IsPdfFile);

    private static bool IsPdfFile(IStorageFile file) =>
        file.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        || (file.TryGetLocalPath() is { } path && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    // --- Files ---

    private async Task OpenDocumentAsync()
    {
        if (Shell is not { } shell)
            return;

        // AllowMultiple (#348): Explorer/Finder-style multi-select, each file its own
        // tab (or activating one already open on it).
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.OpenAPdf,
            AllowMultiple = true,
            FileTypeFilter = [PdfFileType],
        });

        foreach (var file in files)
            await OpenStorageFileAsync(file);
    }

    /// <summary>
    /// Opens a file the picker, a drop or the OS gave us into its own tab — or
    /// activates a tab already open on it rather than opening a duplicate — and
    /// remembers it (#348 §1). Nothing here asks about unsaved changes any more:
    /// opening a document no longer replaces one.
    /// </summary>
    private async Task OpenStorageFileAsync(IStorageFile file)
    {
        if (Shell is not { } shell)
            return;

        // TryGetLocalPath returns null for a document the OS handed us out of a
        // sandboxed or virtual location; the engine takes a path, so say so rather
        // than failing silently.
        var path = file.TryGetLocalPath();
        if (path is null)
        {
            if (Active is { } active)
                active.Status = Strings.FileNotLocal;
            return;
        }

        if (shell.FindTab(path) is { } existing)
        {
            shell.ActivateTab(existing);
            return;
        }

        await OpenIntoNewTabAsync(shell, file, path);
    }

    /// <summary>
    /// Opens <paramref name="file"/>/<paramref name="path"/> into a freshly created
    /// tab. The tab is added (and so activated and wired up) before the open runs —
    /// see the comment on the same pattern in <see cref="OpenPathIntoTabAsync"/> — and
    /// removed again if the open fails outright rather than merely waiting on a
    /// password, so a failed open never leaves an "Untitled" tab behind (#348 §1).
    /// </summary>
    private async Task OpenIntoNewTabAsync(ShellViewModel shell, IStorageFile file, string path)
    {
        var document = shell.CreateDocument();
        shell.AddTab(document);
        document.DpiScale = RenderScaling;
        _pendingFile = file;
        _pendingFileOwner = document;
        await document.OpenAsync(path);
        if (!document.IsDocumentOpen && document.PendingPasswordPath is null)
        {
            shell.CloseTab(document);
            return;
        }
        if (document.IsDocumentOpen)
            await RememberAsync(shell, file, path);
    }

    /// <summary>
    /// Show in Finder, from a recent row's context menu (#165). Says so when Finder
    /// will not show the file — moved, or on a volume that is no longer there —
    /// rather than leaving a menu item that appears to do nothing.
    /// </summary>
    private void OnShowRecentInFinder(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ShellViewModel.RecentRow row)
            return;
        if (!OperatingSystem.IsMacOS() || !Platform.MacFileNames.RevealInFinder(row.Path))
            if (Active is { } vm)
                vm.Status = Strings.CouldNotShowInFinder(row.Name);
    }

    /// <summary>
    /// Reopens a document from the recents list, into its own tab (or activates one
    /// already open on it).
    ///
    /// On macOS under the App Sandbox the stored path is not a key to anything — the
    /// grant was to the file the user picked, in that session. The security-scoped
    /// bookmark is what carries permission across launches, so it is tried first and
    /// the path is only a fallback for platforms that do not need one.
    /// </summary>
    private async Task OpenRecentAsync(RecentEntry entry)
    {
        if (Shell is not { } shell)
            return;

        if (shell.FindTab(entry.Path) is { } existing)
        {
            shell.ActivateTab(existing);
            return;
        }

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
                await OpenIntoNewTabAsync(shell, file, bookmarked);
                return;
            }
        }

        if (!File.Exists(entry.Path))
        {
            if (Active is { } vm)
                vm.Status = Strings.FileMovedOrDeleted;
            return;
        }

        // Ask the platform for a real file handle rather than leaving one unset.
        // Without one Save has nothing to write through, and because CanSave only
        // looks at "open and dirty" the button would stay enabled and do nothing —
        // silently losing the user's work, which is worse than refusing outright.
        var fromPath = await StorageProvider.TryGetFileFromPathAsync(entry.Path);
        if (fromPath is null)
        {
            if (Active is { } vm)
                vm.Status = Strings.FileCannotBeOpenedFromHere;
            return;
        }

        await OpenIntoNewTabAsync(shell, fromPath, entry.Path);
    }

    /// <summary>
    /// Records the document in recents, with a bookmark where the platform supports
    /// one. Failing to mint a bookmark must not stop the document being remembered.
    /// </summary>
    private static async Task RememberAsync(ShellViewModel shell, IStorageFile file, string path)
    {
        string? bookmark = null;
        try
        {
            bookmark = await file.SaveBookmarkAsync();
        }
        catch (Exception)
        {
        }

        shell.RememberRecent(path, bookmark);
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
    /// <summary>
    /// The confirmation #173 asks for, before either save path writes anything: what
    /// redaction does, that it cannot be undone once saved, and Save as a copy as the
    /// default. Returns false when the user cancelled or the redaction refused — in which
    /// case nothing has been removed and nothing must be written.
    /// </summary>
    private async Task<bool> ConfirmAndApplyRedactionsAsync(bool alreadySavingACopy)
    {
        if (Active is not { } vm || !vm.HasRedactionMarks)
            return true;

        var dialog = new ConfirmRedactionWindow();
        dialog.SetMarkCount(vm.RedactionMarkCount);
        await dialog.ShowDialog(this);
        switch (dialog.Choice)
        {
            case ConfirmRedactionWindow.Decision.Cancel:
                return false;
            case ConfirmRedactionWindow.Decision.SaveAsCopy when !alreadySavingACopy:
                // Apply first: a refusal must not open a picker for a file that will not
                // be written. Then hand the whole save over to the copy path.
                if (!await vm.ApplyRedactionsAsync())
                    return false;
                await SaveAsAsync(DocumentViewModel.SuggestRedactedFileName(vm.DocumentName ?? ""));
                return false;   // the copy path has saved; the caller must not save again
            default:
                return await vm.ApplyRedactionsAsync();
        }
    }

    /// <summary>The active tab's file handle, or null if it has none (#348 — keyed per tab, see <see cref="_openedFiles"/>).</summary>
    private IStorageFile? OpenedFile
    {
        get => Active is { } vm && _openedFiles.TryGetValue(vm, out var file) ? file : null;
        set { if (Active is { } vm) _openedFiles[vm] = value; }
    }

    private async Task<bool> SaveAsync()
    {
        if (Active is not { } vm)
            return false;

        if (!await ConfirmAndApplyRedactionsAsync(alreadySavingACopy: false))
            return false;

        if (OpenedFile is not { } file)
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
    private async Task SaveAsAsync(string? suggestedName = null)
    {
        if (Active is not { IsIdle: true } vm)
            return;

        // Marks still on the document mean this Save As is the first time they are being
        // applied; the confirmation offers the copy, which is what this already is.
        var applyingMarks = suggestedName is null && vm.HasRedactionMarks;
        if (suggestedName is null && !await ConfirmAndApplyRedactionsAsync(alreadySavingACopy: true))
            return;

        // …and then it is a redacted copy, so it is named like one. The Save route already
        // passes this name in; arriving by Save As used to fall through to "<name> copy",
        // which says nothing about what was taken out of it (#173).
        if (applyingMarks && vm.DocumentName is { } redacted)
            suggestedName = DocumentViewModel.SuggestRedactedFileName(redacted);

        var suggested = suggestedName is { Length: > 0 }
            ? Path.GetFileName(suggestedName)
            : vm.DocumentName is { } name
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
                OpenedFile = file;
                if (ReferenceEquals(_pendingFileOwner, vm))
                {
                    _pendingFile = null;
                    _pendingFileOwner = null;
                }
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
        if (Active is not { IsDocumentOpen: true, IsIdle: true } vm)
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
            if (await vm.UnlockAsync(dialog.Password) != DocumentViewModel.UnlockOutcome.WrongPassword)
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
        if (Active is not { IsDocumentOpen: true, IsIdle: true } vm)
            return;

        if (!vm.Capabilities.CanChangeSecurity)
        {
            // Never offered without full access (ADR-004 §3); the prompt says why.
            await UnlockAsync();
            return;
        }

        if (OpenedFile is null)
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

        var file = OpenedFile!;
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
        if (Active is not { IsIdle: true } vm)
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
