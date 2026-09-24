using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MegaPDF.Core.Recovery;
using MegaPDF.Core.Services;

namespace MegaPDF.Avalonia.ViewModels;

/// <summary>
/// One window's worth of tabs (#348 phase 1): <see cref="Documents"/> holds every
/// open <see cref="DocumentViewModel"/>, <see cref="Active"/> is the one the tab
/// strip has selected, and everything the toolbar/menu bar used to read straight off
/// a single <see cref="DocumentViewModel"/> now reads off <c>Shell.Active</c>.
///
/// Also owns the services that must be one-per-process rather than one-per-tab —
/// <see cref="AppSettings"/>, the signature library, the recovery journal's
/// directory — and hands them to every <see cref="DocumentViewModel"/> it creates.
/// Two independent copies of the settings file or the signature store would each
/// load it once and write the whole thing back on every change, so a change in one
/// tab could silently clobber a change made in another (#348 plan §4) — the reason
/// this class exists at all, beyond just holding a list.
///
/// One <see cref="ShellViewModel"/> exists per <c>MainWindow</c>. File ▸ New Window
/// creates another window with its own <see cref="ShellViewModel"/>, but the three
/// process-wide stores below are still shared: <see cref="DocumentRouter"/> is what
/// hands a fresh <see cref="ShellViewModel"/> the same <see cref="AppSettings"/>,
/// <see cref="RecentFiles"/> and signature library instance the first window uses,
/// so "the same app, one more window" is true of the data as well as the chrome.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly RecentFiles _recents;
    private readonly ISignatureLibrary _signatures;
    private readonly string? _recoveryDirectory;

    /// <param name="stateDirectory">
    /// Where settings, recents, signatures and recovery journals live. Null means the
    /// real per-user locations. See <see cref="DocumentViewModel"/>'s former
    /// constructor doc for why a test never wants the real ones.
    /// </param>
    public ShellViewModel(string? stateDirectory = null)
    {
        if (stateDirectory is null)
        {
            _settings = new AppSettings();
            _recents = new RecentFiles();
            _signatures = new SignatureLibrary();
            _recoveryDirectory = null;
        }
        else
        {
            Directory.CreateDirectory(stateDirectory);
            _settings = new AppSettings(Path.Combine(stateDirectory, "settings.json"));
            _recents = new RecentFiles(Path.Combine(stateDirectory, "recent.json"));
            _signatures = new SignatureLibrary(Path.Combine(stateDirectory, "Signatures"));
            _recoveryDirectory = Path.Combine(stateDirectory, "Recovery");
        }
    }

    /// <summary>
    /// A shell built to share another shell's process-wide services (#348) — what
    /// File ▸ New Window uses, so the new window's settings, recents and signature
    /// library are the same live objects the first window has, not a second copy of
    /// the file each has loaded independently.
    /// </summary>
    internal ShellViewModel(AppSettings settings, RecentFiles recents, ISignatureLibrary signatures, string? recoveryDirectory)
    {
        _settings = settings;
        _recents = recents;
        _signatures = signatures;
        _recoveryDirectory = recoveryDirectory;
    }

    public AppSettings Settings => _settings;

    public RecentFiles RecentFiles => _recents;

    public ISignatureLibrary SignatureLibrary => _signatures;

    internal string? RecoveryDirectory => _recoveryDirectory;

    /// <summary>Every tab open in this window, in tab-strip order.</summary>
    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private DocumentViewModel? _active;

    partial void OnActiveChanged(DocumentViewModel? value) => OnPropertyChanged(nameof(WindowTitle));

    /// <summary>Whether this window has any tabs — drives the empty state (SDD §2.2).</summary>
    public bool HasDocuments => Documents.Count > 0;

    /// <summary>The welcome panel shows until there is at least one tab.</summary>
    public bool ShowEmptyState => !HasDocuments;

    /// <summary>The window title mirrors the active tab's (#348) — a bullet for unsaved work, then its name.</summary>
    public string WindowTitle => Active?.WindowTitle ?? "MegaPDF";

    /// <summary>
    /// A new tab's view model, built with this window's shared services. The caller
    /// opens a document into it and then calls <see cref="AddTab"/> — a tab that
    /// failed to open is never added, so there is never an "Untitled" tab (#348 plan §1).
    /// </summary>
    public DocumentViewModel CreateDocument() => new(_settings, _signatures, _recoveryDirectory);

    /// <summary>The tab already open on this path in this window, if any.</summary>
    public DocumentViewModel? FindTab(string path) =>
        Documents.FirstOrDefault(d => d.DocumentPath is { } open && LaunchedDocument.SameFile(open, path));

    /// <summary>Whether this window already has a tab on this path.</summary>
    public bool IsOpen(string path) => FindTab(path) is not null;

    /// <summary>Adds an already-opened document as a new tab and makes it active.</summary>
    public void AddTab(DocumentViewModel document)
    {
        if (Documents.Contains(document))
        {
            Active = document;
            return;
        }
        Documents.Add(document);
        Active = document;
        OnPropertyChanged(nameof(HasDocuments));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>Selects an already-open tab — what activating a match does instead of opening a duplicate.</summary>
    public void ActivateTab(DocumentViewModel document)
    {
        if (Documents.Contains(document))
            Active = document;
    }

    /// <summary>
    /// Removes a tab (it has already been asked about, if it was dirty) and disposes
    /// its view model, which ends its recovery-journal session. Selection follows to
    /// the tab that was next to it, the way a browser closes a tab.
    /// </summary>
    public void CloseTab(DocumentViewModel document)
    {
        var index = Documents.IndexOf(document);
        if (index < 0)
            return;
        Documents.RemoveAt(index);
        if (ReferenceEquals(Active, document))
            Active = Documents.Count == 0 ? null : Documents[Math.Min(index, Documents.Count - 1)];
        document.Dispose();
        OnPropertyChanged(nameof(HasDocuments));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>Moves keyboard focus to the next tab, wrapping (⌃Tab / Ctrl+PgDn).</summary>
    public void ActivateNextTab() => Step(forward: true);

    /// <summary>Moves keyboard focus to the previous tab, wrapping (⌃⇧Tab / Ctrl+PgUp).</summary>
    public void ActivatePreviousTab() => Step(forward: false);

    private void Step(bool forward)
    {
        if (Documents.Count < 2 || Active is not { } current)
            return;
        var index = Documents.IndexOf(current);
        if (index < 0)
            return;
        var next = forward ? (index + 1) % Documents.Count : (index - 1 + Documents.Count) % Documents.Count;
        Active = Documents[next];
    }

    // --- Recent documents (SDD §2.2 empty state) — moved from DocumentViewModel (#348):
    // the recents list is app/window-scoped, not owned by any one open document.

    /// <inheritdoc cref="DocumentViewModel.RecentRow"/>
    public sealed record RecentRow(RecentEntry Entry, string Name, string? Location, string? FullLocation)
    {
        public string Path => Entry.Path;

        public bool HasLocation => !string.IsNullOrEmpty(Location);

        public string Tip => FullLocation is { Length: > 0 } full ? full : Name;

        public string AccessibleName =>
            FullLocation is { Length: > 0 } full ? Strings.RecentInLocation(Name, full) : Name;
    }

    public ObservableCollection<RecentRow> Recents { get; } = [];

    public bool HasRecents => Recents.Count > 0;

    public void LoadRecents()
    {
        Recents.Clear();
        var places = OperatingSystem.IsMacOS()
            ? Platform.MacFileNames.Places()
            : (IReadOnlyList<NamedFolder>)[];
        var opaque = OperatingSystem.IsMacOS()
            ? Platform.MacFileNames.OpaqueRoots()
            : (IReadOnlyList<string>)[];

        var rows = _recents.Entries
            .Select(entry => (
                Entry: entry,
                Name: (OperatingSystem.IsMacOS() ? Platform.MacFileNames.DisplayName(entry.Path) : null)
                      ?? entry.DisplayName,
                Segments: RecentLocation.Segments(entry.Path, places, opaque)))
            .ToList();

        var depths = rows
            .GroupBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(g => g.Key,
                          g => RecentLocation.DistinguishingDepth(g.Select(r => r.Segments).ToList()),
                          StringComparer.CurrentCultureIgnoreCase);

        foreach (var row in rows)
        {
            var line = RecentLocation.Line(row.Segments, keepDeepest: depths[row.Name]);
            var full = string.Join(RecentLocation.Separator, row.Segments);
            Recents.Add(new RecentRow(row.Entry, row.Name,
                                      string.IsNullOrEmpty(line) ? null : line,
                                      string.IsNullOrEmpty(full) ? null : full));
        }
        OnPropertyChanged(nameof(HasRecents));
    }

    /// <inheritdoc cref="DocumentViewModel.ShowDemoRecents"/>
    internal void ShowDemoRecents(IReadOnlyList<(string Name, string Location)> rows)
    {
        Recents.Clear();
        foreach (var (name, location) in rows)
            Recents.Add(new RecentRow(new RecentEntry(name), name, location, location));
        OnPropertyChanged(nameof(HasRecents));
    }

    /// <inheritdoc cref="DocumentViewModel.RememberRecent"/>
    public void RememberRecent(string path, string? bookmark)
    {
        _recents.Add(path, bookmark);
        LoadRecents();
    }

    // --- Crash recovery scan/offer (#348 plan §5) — one scan per process launch,
    // not per window, and not gated on "a document is already open": it is gated on
    // "no tabs anywhere in the process", which App.axaml.cs enforces before it calls this.

    /// <summary>
    /// Every crashed session this window's recovery directory can see, newest first.
    /// A fresh, session-less <see cref="RecoveryJournal"/> is used deliberately: it
    /// never calls <c>BeginSession</c>, so it never holds a live journal of its own,
    /// and the scan's only exclusion is the exclusive lock every *live* session (in
    /// this process or another) holds on its own file — which is already enough,
    /// tested in <c>RecoveryJournalTests</c>.
    /// </summary>
    public IReadOnlyList<RecoverableSession> FindRecoverableSessions() =>
        new RecoveryJournal(_recoveryDirectory).FindRecoverableSessions();

    /// <summary>
    /// Opens a crashed session into a brand new tab and makes it active.
    ///
    /// The tab is added — and so activated and wired up by the window — *before* the
    /// restore runs, not after: a restore of a password-protected document raises
    /// <see cref="DocumentViewModel.PasswordRequested"/> from inside
    /// <c>RestoreSessionAsync</c>/<c>OpenAsync</c>, and only the active tab's window
    /// wiring is listening for it. Adding the tab first means that listener already
    /// exists when the event fires, so the password prompt still appears instead of
    /// the open silently reporting "protected" with nobody there to ask.
    /// </summary>
    public async Task<DocumentViewModel> RestoreSessionAsync(RecoverableSession session)
    {
        var document = CreateDocument();
        AddTab(document);
        await document.RestoreSessionAsync(session);
        return document;
    }

    public static void DiscardSession(RecoverableSession session) => RecoveryJournal.Discard(session.JournalPath);

    public void Dispose()
    {
        foreach (var document in Documents.ToList())
            document.Dispose();
        Documents.Clear();
    }
}
