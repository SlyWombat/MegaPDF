using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MegaPDF.Core.Recovery;
using MegaPDF.Core.Services;
using Microsoft.UI.Xaml;

namespace MegaPDF.App;

/// <summary>
/// One window's shell (#348 phase 1): the tabs it holds, which one is active, and the three
/// services shared by every document in the process — settings, recents, the signature
/// library. Exactly one instance of each lives on <see cref="App"/> for the life of the
/// process and is handed down here to every <see cref="DocumentViewModel"/> this shell
/// creates; before this split each document constructed its own copy, and whichever saved
/// last silently clobbered whatever another one had changed (the plan's §6.3).
///
/// Phase 1 gives every window exactly one document: <c>MainWindow</c> still creates the one
/// <see cref="DocumentViewModel"/> it starts with via <see cref="AddDocument"/> and nothing
/// yet adds a second, so <see cref="Documents"/> today always holds exactly one entry and no
/// tab strip exists. The collection, <see cref="Active"/> and <see cref="OpenInTabAsync"/>
/// are real, tested infrastructure for the TabView commit that follows — not placeholders —
/// but nothing in this commit calls <see cref="OpenInTabAsync"/> yet, so today's single-tab
/// behaviour (File ▸ Open and drag-and-drop replace the one open document, asking about its
/// unsaved changes first) is unchanged. A second <c>MainWindow</c> (New Window) already gets
/// its own <see cref="ShellViewModel"/> sharing the same three service instances, which is
/// what makes that safe once it is wired up.
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
        Documents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDocuments));
    }

    /// <summary>This window's open tabs. Exactly one entry until the TabView commit lands.</summary>
    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    /// <summary>The tab the toolbar, menus and accelerators act on.</summary>
    [ObservableProperty]
    private DocumentViewModel? _active;

    /// <summary>Drives the empty state: "no tabs", not "no document" — the same thing today.</summary>
    public bool HasDocuments => Documents.Count > 0;

    /// <summary>A fresh, not-yet-opened document — what a new tab starts as. Becomes the active tab.</summary>
    public DocumentViewModel AddDocument()
    {
        var doc = new DocumentViewModel(_window, Settings, RecentFiles, SignatureLibrary);
        Documents.Add(doc);
        Active = doc;
        return doc;
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
