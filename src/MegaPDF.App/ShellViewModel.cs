using System.Collections.ObjectModel;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private DocumentViewModel? _active;

    /// <summary>Drives the empty state: "no tabs" (plan §1 decision: no "Untitled" tab).</summary>
    public bool HasDocuments => Documents.Count > 0;

    public Visibility EmptyStateVisibility => HasDocuments ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The window's title bar text: the active tab's, or just the app name with no tabs.</summary>
    public string WindowTitle => Active?.WindowTitle ?? DocumentViewModel.AppName;

    /// <summary>A fresh, not-yet-opened document — what a new tab starts as. Becomes the active tab.</summary>
    public DocumentViewModel AddDocument()
    {
        var doc = new DocumentViewModel(_window, Settings, RecentFiles, SignatureLibrary);
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
        Documents.Add(doc);
        Active = doc;
        return doc;
    }

    /// <summary>Removes a tab from this window without asking about unsaved changes — the caller decides that first.</summary>
    public void RemoveDocument(DocumentViewModel doc)
    {
        var index = Documents.IndexOf(doc);
        if (index < 0)
            return;
        Documents.RemoveAt(index);
        if (Active == doc)
            Active = Documents.Count == 0 ? null : Documents[Math.Min(index, Documents.Count - 1)];
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
    /// per-window half of the router the plan describes — the part that also has to reach
    /// across every window in the process is app-level work the TabView commit adds.
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
