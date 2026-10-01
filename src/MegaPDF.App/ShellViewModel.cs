using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Core.Recovery;
using MegaPDF.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace MegaPDF.App;

/// <summary>
/// One window's shell (#348 phase 1): the tabs it holds (<see cref="Documents"/>), which one
/// is active (<see cref="Active"/>), and the three services shared by every document in the
/// process — settings, recents, the signature library. Exactly one instance of each lives on
/// <see cref="App"/> for the life of the process and is handed down here to every
/// <see cref="DocumentViewModel"/> this shell creates; before this split each document
/// constructed its own copy, and whichever saved last silently clobbered whatever another one
/// had changed (the plan's §6.3).
///
/// A window may hold any number of tabs, including zero (the empty state, <see
/// cref="EmptyStateVisibility"/> — the plan's decision: no "Untitled" tab). <see
/// cref="MainWindow"/>'s <c>TabView</c> binds to <see cref="Documents"/>/<see cref="Active"/>
/// directly; opening a path goes through <see cref="OpenInTabAsync"/>, which activates an
/// already-open tab instead of opening a second copy. A second <c>MainWindow</c> (New Window)
/// gets its own <see cref="ShellViewModel"/> sharing the same three service instances, which is
/// what makes more than one window safe.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Window _window;

    /// <summary>One instance for the whole process (#348 §6.3) — never construct another.</summary>
    public AppSettings Settings { get; }

    /// <summary>One instance for the whole process — see <see cref="Settings"/>.</summary>
    public RecentFiles RecentFiles { get; }

    /// <summary>One instance for the whole process — see <see cref="Settings"/>.</summary>
    public SignatureLibrary SignatureLibrary { get; }

    public ShellViewModel(Window window, AppSettings settings, RecentFiles recentFiles, SignatureLibrary signatureLibrary)
    {
        _window = window;
        Settings = settings;
        RecentFiles = recentFiles;
        SignatureLibrary = signatureLibrary;
        Documents.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDocuments));
            OnPropertyChanged(nameof(EmptyStateVisibility));
        };
        LoadRecentDocuments();
    }

    /// <summary>This window's open tabs.</summary>
    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    /// <summary>The tab the toolbar, menus and accelerators act on.</summary>
    ///
    /// <remarks>
    /// #617: every <c>x:Bind</c> in <c>MainWindow.xaml</c> that reaches through <c>Shell.Active</c>
    /// used to break silently with zero tabs open, because <c>x:Bind</c> does not compute a
    /// converted fallback for a broken path — it leaves the *target* property sitting at its own
    /// declared default, whatever that is for that property, regardless of the source property's
    /// type. Measured off a real build (do not take the shape of this bug on faith — the original
    /// report guessed a narrower one and was wrong): <see cref="Visibility"/>'s default is
    /// <see cref="Visibility.Visible"/>; <c>Control.IsEnabled</c>'s is <see langword="true"/>, so
    /// <c>IsEnabled="{x:Bind Shell.Active.IsDocumentOpen}"</c> is just as broken as the busy strip
    /// was, not "fine because bool defaults to false" as it looked from the screenshot alone;
    /// <c>ArmableAppBarButton.IsArmed</c>'s is registered <see langword="false"/>, which actually is
    /// safe; and <c>ButtonBase.Command</c>'s is null, which independently leaves a button enabled
    /// because a null <c>Command</c> tells it nothing to disable for.
    ///
    /// So every <c>Visibility</c>, <c>IsEnabled</c> and <c>Command</c> the toolbar needs through
    /// <c>Active</c> is proxied here instead, each falling back to a safe non-null default
    /// (<see cref="Busy"/>'s idle <see cref="BusyState"/>, <see cref="DisabledCommand.Instance"/>,
    /// or a plain <see langword="false"/> given explicitly rather than hoped for) rather than to
    /// <c>Active</c>'s absence. Only <c>IsArmed</c> stays bound straight to <c>Active</c>: its
    /// registered default already is the correct one. <c>MainWindow.xaml</c> binds to
    /// <c>Shell.*</c>, never <c>Shell.Active.*</c>, for anything in the first category — see that
    /// file's toolbar comment — so the next binding anyone adds through <c>Active</c> is safe by
    /// construction only if it follows the same rule, the way <see cref="HasDocuments"/>/
    /// <see cref="EmptyStateVisibility"/> already did.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(Busy))]
    [NotifyPropertyChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(SaveAsCommand))]
    [NotifyPropertyChangedFor(nameof(SecurityCommand))]
    [NotifyPropertyChangedFor(nameof(ShrinkForEmailCommand))]
    [NotifyPropertyChangedFor(nameof(UndoCommand))]
    [NotifyPropertyChangedFor(nameof(RedoCommand))]
    [NotifyPropertyChangedFor(nameof(ZoomInCommand))]
    [NotifyPropertyChangedFor(nameof(ZoomOutCommand))]
    [NotifyPropertyChangedFor(nameof(RotatePagesRightCommand))]
    [NotifyPropertyChangedFor(nameof(RotatePagesLeftCommand))]
    [NotifyPropertyChangedFor(nameof(DeletePagesCommand))]
    [NotifyPropertyChangedFor(nameof(MovePageEarlierCommand))]
    [NotifyPropertyChangedFor(nameof(MovePageLaterCommand))]
    [NotifyPropertyChangedFor(nameof(InsertBlankPageCommand))]
    [NotifyPropertyChangedFor(nameof(InsertPagesFromFileCommand))]
    [NotifyPropertyChangedFor(nameof(ExtractSelectedPagesCommand))]
    [NotifyPropertyChangedFor(nameof(SaveButtonLabel))]
    [NotifyPropertyChangedFor(nameof(ZoomLabel))]
    [NotifyPropertyChangedFor(nameof(IsDocumentOpen))]
    [NotifyPropertyChangedFor(nameof(IsSigningAllowed))]
    [NotifyPropertyChangedFor(nameof(IsTextBoxAllowed))]
    [NotifyPropertyChangedFor(nameof(IsEditingAllowed))]
    [NotifyPropertyChangedFor(nameof(IsPrintAllowed))]
    [NotifyPropertyChangedFor(nameof(HasRedactionMarks))]
    private DocumentViewModel? _active;

    /// <summary>Drives the empty state: "no tabs" (plan §1 decision: no "Untitled" tab).</summary>
    public bool HasDocuments => Documents.Count > 0;

    public Visibility EmptyStateVisibility => HasDocuments ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The window's title bar text: the active tab's, or just the app name with no tabs.</summary>
    public string WindowTitle => Active?.WindowTitle ?? DocumentViewModel.AppName;

    // --- #617: Active's busy state and commands, proxied so MainWindow.xaml never has to reach
    // through a nullable Active. See the remarks on Active above for why reaching through it
    // directly is the wrong shape for anything but a plain bool. ---

    private readonly BusyState _idleBusy = new();

    /// <summary>The active tab's busy state, or an always-idle instance with no tab open. Never
    /// null, so every <c>Shell.Busy.*</c> binding gets the ordinary bool-to-Visibility conversion
    /// instead of a broken path's fallback to the target's own default.</summary>
    public BusyState Busy => Active?.Busy ?? _idleBusy;

    public ICommand SaveCommand => Active?.SaveCommand ?? (ICommand)DisabledCommand.Instance;
    public ICommand SaveAsCommand => Active?.SaveAsCommand ?? (ICommand)DisabledCommand.Instance;
    public ICommand SecurityCommand => Active?.SecurityCommand ?? (ICommand)DisabledCommand.Instance;
    public ICommand ShrinkForEmailCommand => Active?.ShrinkForEmailCommand ?? (ICommand)DisabledCommand.Instance;
    public ICommand UndoCommand => Active?.UndoCommand ?? (ICommand)DisabledCommand.Instance;
    public ICommand RedoCommand => Active?.RedoCommand ?? (ICommand)DisabledCommand.Instance;
    public ICommand ZoomInCommand => Active?.ZoomInCommand ?? (ICommand)DisabledCommand.Instance;
    public ICommand ZoomOutCommand => Active?.ZoomOutCommand ?? (ICommand)DisabledCommand.Instance;
    public ICommand RotatePagesRightCommand => Active?.RotatePagesRightCommand ?? (ICommand)DisabledCommand.Instance;
    public ICommand RotatePagesLeftCommand => Active?.RotatePagesLeftCommand ?? (ICommand)DisabledCommand.Instance;
    public ICommand DeletePagesCommand => Active?.DeletePagesCommand ?? (ICommand)DisabledCommand.Instance;
    public ICommand MovePageEarlierCommand => Active?.MovePageEarlierCommand ?? (ICommand)DisabledCommand.Instance;
    public ICommand MovePageLaterCommand => Active?.MovePageLaterCommand ?? (ICommand)DisabledCommand.Instance;
    public ICommand InsertBlankPageCommand => Active?.InsertBlankPageCommand ?? (ICommand)DisabledCommand.Instance;
    public ICommand InsertPagesFromFileCommand => Active?.InsertPagesFromFileCommand ?? (ICommand)DisabledCommand.Instance;
    public ICommand ExtractSelectedPagesCommand => Active?.ExtractSelectedPagesCommand ?? (ICommand)DisabledCommand.Instance;

    // Cosmetic text reached through Active: a reference-type path break assigns a literal null
    // (there is no conversion function to skip), which just blanks the label -- not the wrong
    // kind of bug #617 is about, but the same discipline applies so Save and the zoom level keep
    // their ordinary text while correctly disabled rather than going empty.
    public string SaveButtonLabel => Active?.SaveButtonLabel ?? Strings.Save;
    public string ZoomLabel => Active?.ZoomLabel ?? "";

    // IsEnabled reached through Active (#617, verified on a real build -- see the remarks on
    // Active above): Control.IsEnabled's own default is true, not false, so every one of these
    // was exactly as broken as the busy strip, just less visible in a screenshot. IsArmed is not
    // here: ArmableAppBarButton registers false as its default, which is already correct, so
    // MainWindow.xaml keeps those three bound straight to Shell.Active.
    public bool IsDocumentOpen => Active?.IsDocumentOpen ?? false;
    public bool IsSigningAllowed => Active?.IsSigningAllowed ?? false;
    public bool IsTextBoxAllowed => Active?.IsTextBoxAllowed ?? false;
    public bool IsEditingAllowed => Active?.IsEditingAllowed ?? false;
    public bool IsPrintAllowed => Active?.IsPrintAllowed ?? false;
    public bool HasRedactionMarks => Active?.HasRedactionMarks ?? false;

    /// <summary>
    /// A command that can never execute, for a Shell.Active-less empty state (#617). WinUI's
    /// <c>ButtonBase</c> treats a null <c>Command</c> as "nothing to ask, so stay however I was" —
    /// which for a never-touched <c>AppBarButton</c> is enabled — rather than as "disabled". Every
    /// <c>Shell.*Command</c> property above falls back to this instead of to null, so the button
    /// is actually told it cannot run. <see cref="CanExecuteChanged"/> never needs to fire:
    /// "always false" never changes.
    /// </summary>
    private sealed class DisabledCommand : ICommand
    {
        public static readonly DisabledCommand Instance = new();
        private DisabledCommand() { }
        public bool CanExecute(object? parameter) => false;
        public void Execute(object? parameter) { }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    /// <summary>A fresh, not-yet-opened document — what a new tab starts as. Becomes the active tab.</summary>
    public DocumentViewModel AddDocument()
    {
        var doc = new DocumentViewModel(_window, Settings, RecentFiles, SignatureLibrary);
        // Lost in the #348 phase 1 split (2d9960c): when there was one DocumentViewModel per
        // window, MainWindow's constructor called this once, on the window's one instance.
        // Now that AddDocument is where a document is actually born, this is where the call
        // belongs — without it, a Busy transition after the initial open (Save, Shrink, an
        // edit's #139 check) never tells IsEditingAllowed/IsSigningAllowed/IsTextBoxAllowed/
        // IsPrintAllowed or Save/SaveAs/Security/ShrinkForEmail/Undo/Redo's CanExecute that
        // anything changed, so the toolbar can be a stale step behind Busy for the rest of
        // this tab's life (#427).
        doc.WatchBusyState();
        doc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DocumentViewModel.WindowTitle) && doc == Active)
                OnPropertyChanged(nameof(WindowTitle));
        };
        doc.RecentFilesChanged += (_, _) => LoadRecentDocuments();
        // A brand-new tab's DocumentView is not realized (Loaded has not run) at the instant
        // it becomes Active — MainWindow's per-active-tab wiring needs another nudge once it
        // is (see DocumentViewModel.ViewAttached).
        doc.ViewAttached += (_, _) =>
        {
            if (doc == Active)
                OnPropertyChanged(nameof(Active));
        };
        // #427: a tab this method makes Active before its document has even started opening
        // (below) can finish that open in a fast enough burst of PropertyChanged notifications
        // that x:Bind drops one — see DocumentViewModel.OpenSettled for the full account. The
        // fix is the same shape as ViewAttached just above: force one more Active-rooted
        // refresh once the burst is over, rather than reaching into WinUI's binding engine.
        doc.OpenSettled += (_, _) =>
        {
            if (doc == Active)
                OnPropertyChanged(nameof(Active));
        };
        Documents.Add(doc);
        Active = doc;
        return doc;
    }

    /// <summary>
    /// Removes a tab from this window without asking about unsaved changes — the caller
    /// decides that first — and disposes its document (#543): the PDFium document, its
    /// form environment, every page still loaded, and the core's file source, none of
    /// which anything else reclaims. Removed from <see cref="Documents"/>, and <see
    /// cref="Active"/> re-pointed, before the dispose — the same order the Avalonia leg's
    /// <c>ShellViewModel.CloseTab</c> uses — so nothing still bound to this tab sees a
    /// disposed document.
    /// </summary>
    public void RemoveDocument(DocumentViewModel doc)
    {
        var index = Documents.IndexOf(doc);
        if (index < 0)
            return;
        Documents.RemoveAt(index);
        if (Active == doc)
            Active = Documents.Count == 0 ? null : Documents[Math.Min(index, Documents.Count - 1)];
        doc.Dispose();
    }

    // --- Recents / first-run card (#348 phase 1): one list per process, shown on the
    // empty state, not per document — moved here from DocumentViewModel (plan §4). ---

    /// <summary>Recent documents for the empty state (SDD §2.2), newest first.</summary>
    public ObservableCollection<RecentDocument> RecentDocuments { get; } = [];

    public Visibility RecentDocumentsVisibility => RecentDocuments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>For "Reopen last file": the newest one still on disk (#165 keeps missing ones listed).</summary>
    public string? MostRecentDocument => RecentFiles.All.FirstOrDefault(File.Exists);

    /// <summary>
    /// The recents list, each row with the folder it lives in (#165). Rows whose file
    /// names clash get as much of the path as it takes to tell them apart: the folder
    /// above, then the one above that.
    /// </summary>
    public void LoadRecentDocuments()
    {
        RecentDocuments.Clear();
        var named = ShellFolderNames.Get();
        var paths = RecentFiles.All;
        var segments = paths.Select(p => RecentLocation.Segments(p, named)).ToList();

        foreach (var (path, index) in paths.Select((p, i) => (p, i)))
        {
            var name = Path.GetFileName(path);
            // How deep this row has to go is decided among the rows that share its name.
            var clashing = paths
                .Select((other, i) => (Segments: segments[i], Name: Path.GetFileName(other)))
                .Where(other => string.Equals(other.Name, name, StringComparison.CurrentCultureIgnoreCase))
                .Select(other => other.Segments)
                .ToList();
            var depth = RecentLocation.DistinguishingDepth(clashing);
            var location = RecentLocation.Line(segments[index], maxLength: 44, keepDeepest: depth);
            RecentDocuments.Add(new RecentDocument(name, path, location, !File.Exists(path)));
        }
        OnPropertyChanged(nameof(RecentDocumentsVisibility));
    }

    /// <summary>"Remove from Recent", and what a row whose file has gone offers.</summary>
    public void RemoveFromRecent(string path)
    {
        RecentFiles.Remove(path);
        LoadRecentDocuments();
    }

    /// <summary>
    /// Opens a recent row into a tab, or — when its file has gone — says so and offers to
    /// take it off the list (#165). Explorer and Office both ask rather than removing it
    /// silently.
    /// </summary>
    public async Task OpenRecentAsync(RecentDocument recent)
    {
        if (File.Exists(recent.Path))
        {
            await OpenInTabAsync(recent.Path);
            return;
        }
        if (_window.Content?.XamlRoot is not { } xamlRoot)
            return;
        var dialog = new ContentDialog
        {
            Title = Strings.RecentMissingTitle,
            Content = Strings.RecentMissingBody(recent.Name, recent.Location),
            PrimaryButtonText = Strings.RemoveFromRecent,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        if (await dialog.ShowOneAtATimeAsync() == ContentDialogResult.Primary)
            RemoveFromRecent(recent.Path);
    }

    /// <summary>First-run "Make MegaPDF your PDF app?" card (SDD §5.4) — shows once, ever.</summary>
    [ObservableProperty]
    private bool _isDefaultAppCardOpen;

    public void MaybeShowDefaultAppCard()
    {
        if (Settings.DefaultAppCardShown)
            return;
        IsDefaultAppCardOpen = true;
    }

    public void DismissDefaultAppCard()
    {
        Settings.DefaultAppCardShown = true;
        IsDefaultAppCardOpen = false;
    }

    /// <summary>
    /// File ▸ Open (#348 phase 1, plan §3a): multi-select, each picked file routed through
    /// <see cref="OpenInTabAsync"/> — a tab per file, an already-open one activated rather
    /// than opened twice. Always available: unlike the old per-document Open command, this
    /// one does not need an active tab to run (there may be none), and Open is meant to work
    /// with zero tabs open.
    /// </summary>
    [RelayCommand]
    private async Task OpenAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");
        // Unpackaged apps must associate pickers with their window handle.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_window));

        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files)
            await OpenInTabAsync(file.Path);
    }

    /// <summary>
    /// Find-or-activate (plan §1, decision 1): a path already open in this shell's tabs is
    /// activated instead of opened a second time; otherwise a new tab opens it. This is the
    /// per-window half of the router the plan describes; the part that reaches across every
    /// window in the process — an external open, redirected from another launch — is
    /// <c>App.OpenExternalPathsAsync</c> (#348 phase 2, App.Activation.cs), which calls back
    /// into this same method once it has picked which window a new tab belongs in.
    /// </summary>
    public async Task<DocumentViewModel> OpenInTabAsync(string path, string? initialPassword = null)
    {
        var full = Path.GetFullPath(path);
        var existing = Documents.FirstOrDefault(d => d.DocumentPath is { } open && LaunchedDocument.SameFile(open, full));
        if (existing is not null)
        {
            Active = existing;
            return existing;
        }

        var doc = AddDocument();
        await doc.OpenDocumentAsync(full, initialPassword);
        return doc;
    }
}
