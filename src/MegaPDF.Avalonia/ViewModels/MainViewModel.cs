using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Imaging;
using MegaPDF.Core.Recovery;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Services;

namespace MegaPDF.Avalonia.ViewModels;

/// <summary>
/// A stored signature and its preview. You pick a signature by how it looks, so the
/// list needs the image, not just the name.
/// </summary>
public sealed record SignatureItem(SignatureEntry Entry, global::Avalonia.Media.Imaging.Bitmap? Thumbnail)
{
    public string Name => Entry.Name;

    /// <summary>
    /// The view model the card's menu items command. A menu is a popup outside the
    /// window's visual tree, where an ancestor binding cannot find the main view
    /// model; the item carries it instead (#100).
    /// </summary>
    public MainViewModel? Owner { get; init; }

    /// <summary>Narrator/VoiceOver name for the card: what it is, and what a click does.</summary>
    public string AccessibleName => Strings.SignatureCardA11y(Entry.Name);
}

/// <summary>
/// A check-mark style with the words the Options flyout shows for it (#91). The
/// enum is what the engine and the settings file speak; the label is what a
/// person reads, and it is translated.
/// </summary>
public sealed record MarkStyleChoice(CheckMarkStyle Style, string Label);

/// <summary>
/// A text-box face with its display name. The PostScript name ("Times-Roman") is
/// the cross-platform contract with the engine; the label ("Times") is what the
/// toolbar shows. Brand names, so the label is the same in every language.
/// </summary>
public sealed record FontChoice(string PostScriptName, string Label)
{
    /// <summary>
    /// The label, because a list item's accessible name falls back to its item's text:
    /// VoiceOver read the record's own "FontChoice { PostScriptName = … }" for every
    /// face in the picker and for the one chosen (#144).
    /// </summary>
    public override string ToString() => Label;
}

/// <summary>
/// The document shell: open, view, check, save.
///
/// The editing behaviour is not reimplemented here — MegaPDF.Core's reversible
/// operations (CheckboxToggleOperation, AddMarkOperation, RemoveMarkOperation) and
/// UndoStack are the same ones the WinUI app drives, which is the whole argument
/// for ADR-002 Option B. What this class adds is the platform-facing half: which
/// page to re-render, what the status line says, and when the document is dirty.
///
/// File dialogs stay in the view. Avalonia reaches them through the TopLevel's
/// IStorageProvider, so a path or a stream comes in and this stays UI-free.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly double[] ZoomStops =
        [0.5, 0.67, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0];

    private readonly IPdfEngine _engine = new PdfiumEngine();
    private readonly UndoStack _undoStack = new();

    /// <summary>Pages already asked about in this document (#139): the warning comes once per page.</summary>
    private readonly PageRegenerationWarnings _pageWarnings = new();

    /// <summary>
    /// The #139 warning before the first whiteout or text box change on a page PDFium's rewrite
    /// would alter: the view asks, true for Continue. With nobody listening the change applies.
    /// </summary>
    public event Func<Task<bool>>? PageRewriteConfirmationRequested;

    /// <summary>
    /// Which printer, and how many copies (#158, Linux). The view shows the dialog
    /// and answers; null is a cancel. With nobody listening — a headless run — the
    /// system default queue is used, which is what a bare `lp` would have done.
    /// </summary>
    internal event Func<IReadOnlyList<Platform.Printing.Destination>,
                      Task<Platform.Printing.Choice?>>? PrintDestinationRequested;
    private readonly ISignatureLibrary _signatures;
    private readonly RecentFiles _recents;
    private readonly AppSettings _settings;
    private readonly RecoveryJournal _journal;
    private IPdfDocument? _document;

    // --- Busy state and engine work off the UI thread (#145) ---

    /// <summary>
    /// The document's busy state: disables editing and file commands at once, shows the strip
    /// (or a spinner on the page) after 0.5 s. Created on the UI thread with the view model.
    /// </summary>
    public BusyState Busy { get; } = new();

    /// <summary>Nothing blocking is running: what the Open button and the menu bar's file commands bind.</summary>
    public bool IsIdle => !Busy.IsBusy;

    /// <summary>
    /// Set by the window: engine work then runs off the UI thread, so the window stays
    /// responsive while a document opens, saves or is searched (#145). Without a window — the
    /// self-test, --render-check — it runs inline, as it always did, and every public method
    /// has finished its work when it returns.
    /// </summary>
    public bool RunsInBackground { get; set; }

    /// <summary>Held by the sync wrappers the self-test calls: the work runs inline even with a window.</summary>
    private int _inlineDepth;

    private bool Inline => !RunsInBackground || _inlineDepth > 0;

    /// <summary>Counts edits, undos and redos: a save marks the document saved only if none ran meanwhile (D3, #145).</summary>
    private int _editCount;

    /// <summary>The person chose Don't Save: the journal goes with the document on close.</summary>
    private bool _changesDiscarded;

    private bool _disposed;

    /// <summary>Runs engine work off the UI thread when there is a window, inline otherwise.</summary>
    private Task<T> OffUiThread<T>(Func<T> work) => Inline ? Task.FromResult(work()) : Task.Run(work);

    /// <inheritdoc cref="OffUiThread{T}"/>
    private Task OffUiThread(Action work)
    {
        if (!Inline)
            return Task.Run(work);
        work();
        return Task.CompletedTask;
    }

    /// <summary>
    /// For the synchronous methods the self-test and the capture runs call: the work runs inline
    /// and has finished when this returns. No synchronization context, so nothing it awaits waits
    /// for the UI thread this may be blocking.
    /// </summary>
    private void RunSynchronously(Func<Task> work)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        _inlineDepth++;
        try
        {
            work().GetAwaiter().GetResult();
        }
        finally
        {
            _inlineDepth--;
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private void OnBusyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BusyState.IsBusy))
        {
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(CanEditContent));
            OnPropertyChanged(nameof(CanAddText));
            OnPropertyChanged(nameof(CanSign));
            OnPropertyChanged(nameof(CanPrint));
            OnPropertyChanged(nameof(CanShrink));
            SaveCommand.NotifyCanExecuteChanged();
            PrintCommand.NotifyCanExecuteChanged();
            ToggleAddTextCommand.NotifyCanExecuteChanged();
            ToggleWhiteoutCommand.NotifyCanExecuteChanged();
            ToggleRedactCommand.NotifyCanExecuteChanged();
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        }
        if (e.PropertyName is nameof(BusyState.IsIndicatorVisible) or nameof(BusyState.Scope)
            or nameof(BusyState.PageIndex) or nameof(BusyState.Area) or nameof(BusyState.Label))
            UpdatePageSpinners();
    }

    /// <summary>Puts the page-level spinner on the page (or line) the busy work is about, and takes it off the rest.</summary>
    private void UpdatePageSpinners()
    {
        foreach (var page in Pages)
        {
            var mine = Busy.ShowsPageSpinner && Busy.PageIndex == page.Index;
            page.ShowBusy(mine, mine ? Busy.Area : null, Busy.Label);
        }
    }

    /// <summary>
    /// Starts the #139 check for a page in the background (#145): when it is first shown, when
    /// Add text or Cover is armed on it, and when something regenerating is selected on it, so
    /// the answer is usually ready by the change. Only with a window: the self-test and the
    /// capture runs judge at the change, as they always did.
    /// </summary>
    private void PreparePageCheck(int pageIndex)
    {
        if (!RunsInBackground || _document is not { } document || pageIndex < 0 || pageIndex >= Pages.Count)
            return;
        if (!Capabilities.CanEditContent && !Capabilities.CanAddText)
            return;
        _pageWarnings.Prepare(document, pageIndex);
    }

    partial void OnCurrentPageChanged(int value) => PreparePageCheck(value - 1);

    /// <param name="stateDirectory">
    /// Where settings, recents, signatures and the recovery journal live. Null means
    /// the real per-user locations, which is what the app uses.
    ///
    /// It exists because --self-test used to run against those real locations: it
    /// wrote to the user's signature library and recent files, and — worse — left
    /// FlattenOnSave switched on, which then broke the *next* run's checks. A test
    /// that mutates the state of the machine it runs on is not a test.
    /// </param>
    public MainViewModel(string? stateDirectory = null)
    {
        if (stateDirectory is null)
        {
            _settings = new AppSettings();
            _recents = new RecentFiles();
            _signatures = new SignatureLibrary();
            _journal = new RecoveryJournal();
        }
        else
        {
            Directory.CreateDirectory(stateDirectory);
            _settings = new AppSettings(Path.Combine(stateDirectory, "settings.json"));
            _recents = new RecentFiles(Path.Combine(stateDirectory, "recent.json"));
            _signatures = new SignatureLibrary(Path.Combine(stateDirectory, "Signatures"));
            _journal = new RecoveryJournal(Path.Combine(stateDirectory, "Recovery"));
        }
        Busy.PropertyChanged += OnBusyChanged;
    }

    public ObservableCollection<PageViewModel> Pages { get; } = [];

    [ObservableProperty]
    private string? _documentPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string? _documentName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(CanShrink))]
    private bool _isDirty;

    /// <summary>The welcome panel shows until there is something to look at.</summary>
    public bool ShowEmptyState => !IsDocumentOpen;

    // Every command whose CanExecute reads IsDocumentOpen must be listed here. The
    // generator notifies only what it is told to, and a command left off the list
    // never raises CanExecuteChanged — so its button evaluates IsEnabled once, at
    // startup, and stays grey for the life of the window (#58). Add a command that
    // guards on this property, add it here too.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand))]
    [NotifyCanExecuteChangedFor(nameof(ZoomOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ZoomResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetZoomCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleAddTextCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleWhiteoutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRedactCommand))]
    [NotifyCanExecuteChangedFor(nameof(FitWidthCommand))]
    [NotifyCanExecuteChangedFor(nameof(FitPageCommand))]
    [NotifyPropertyChangedFor(nameof(CanShrink))]
    [NotifyPropertyChangedFor(nameof(CanEditContent))]
    [NotifyPropertyChangedFor(nameof(CanAddText))]
    [NotifyPropertyChangedFor(nameof(CanSign))]
    [NotifyPropertyChangedFor(nameof(CanPrint))]
    [NotifyPropertyChangedFor(nameof(IsRestricted))]
    private bool _isDocumentOpen;

    // --- Document security (#131, ADR-004 §2, §3) ---

    /// <summary>
    /// What this open of the document may do, read from it on every open so an
    /// owner-restricted document is not an editing loophole. The commands and flags
    /// below read it, so it notifies them the way IsDocumentOpen does (#58).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditContent))]
    [NotifyPropertyChangedFor(nameof(CanAddText))]
    [NotifyPropertyChangedFor(nameof(CanSign))]
    [NotifyPropertyChangedFor(nameof(CanPrint))]
    [NotifyPropertyChangedFor(nameof(CanShrink))]
    [NotifyPropertyChangedFor(nameof(IsRestricted))]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleAddTextCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleWhiteoutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleRedactCommand))]
    private DocumentCapabilities _capabilities = DocumentCapabilities.Unprotected;

    // Each also waits while blocking work runs (#145): a save, an open, a change on its way.

    /// <summary>Editing the document's own text and covers (modify).</summary>
    public bool CanEditContent => IsDocumentOpen && Capabilities.CanEditContent && !Busy.IsBusy;

    /// <summary>Adding and changing text boxes, and their font and size (modify, fill forms or annotate).</summary>
    public bool CanAddText => IsDocumentOpen && Capabilities.CanAddText && !Busy.IsBusy;

    /// <summary>Signatures and check marks (fill forms or annotate).</summary>
    public bool CanSign => IsDocumentOpen && Capabilities.CanSign && !Busy.IsBusy;

    public bool CanPrint => IsDocumentOpen && Capabilities.CanPrint && !Busy.IsBusy;

    /// <summary>The owner restricted this document; the banner offers the owner password.</summary>
    public bool IsRestricted => IsDocumentOpen && Capabilities.IsRestricted;

    /// <summary>Whether the open document has any security at all — what the Password command offers.</summary>
    public bool IsEncrypted => _document?.Security.IsEncrypted ?? false;

    /// <summary>
    /// The edited-marker convention macOS and Windows share: the title carries the
    /// document name, and unsaved work is a bullet rather than an asterisk.
    /// </summary>
    public string WindowTitle => DocumentName is null
        ? "MegaPDF"
        : Strings.WindowTitleFormat($"{(IsDirty ? "• " : "")}{DocumentName}");

    [ObservableProperty]
    private string _status = Strings.OpenToGetStarted;

    /// <summary>
    /// Display scale of the monitor the window is on. Set by the view; feeding it into
    /// the raster is what keeps a page sharp on a retina Mac instead of upscaled.
    /// </summary>
    [ObservableProperty]
    private double _dpiScale = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercentLabel))]
    private double _zoom = 1.0;

    /// <summary>What the toolbar's zoom menu button reads, "100%" (#144).</summary>
    public string ZoomPercentLabel => Strings.ZoomPercent((int)Math.Round(Zoom * 100));

    public bool CanUndo => _undoStack.CanUndo;
    public bool CanRedo => _undoStack.CanRedo;

    partial void OnZoomChanged(double value)
    {
        // Push zoom down so each page reports its layout size; the scroll extent has
        // to change even for pages that are not realised.
        foreach (var page in Pages)
            page.Zoom = value;
        RerenderRealisedPages();
    }

    partial void OnDpiScaleChanged(double value) => RerenderRealisedPages();

    /// <summary>Raised when a document needs a password before it can be opened.</summary>
    public event Func<string, Task<string?>>? PasswordRequested;

    /// <summary>Opens a document and returns once it is open: for the self-test and window-less checks.</summary>
    public void Open(string path) => Open(path, password: null);

    /// <inheritdoc cref="Open(string)"/>
    public void Open(string path, string? password) => RunSynchronously(() => OpenAsync(path, password));

    /// <summary>
    /// Opens a document, off the UI thread with "Opening…" (#145): every page is read for its
    /// size, which on a long document takes a while. The document on screen stays until the
    /// new one has loaded, so one that fails to open leaves it as it was. The window asks
    /// about unsaved changes first.
    /// </summary>
    public async Task OpenAsync(string path, string? password = null)
    {
        if (Busy.IsBusy)
            return;

        (IPdfDocument Document, IReadOnlyList<(double Width, double Height)> Sizes) loaded;
        using (Busy.Begin(Strings.BusyOpening))
        {
            try
            {
                loaded = await OffUiThread(() => LoadDocument(path, password));
            }
            catch (PdfLoadException ex) when (ex.IsPasswordError && PasswordRequested is { } ask)
            {
                // Ask, then retry. Deliberately not a loop here — the view keeps
                // asking, so a wrong password re-prompts with the reason showing
                // rather than dumping the user back to an empty window.
                Status = password is null
                    ? Strings.PdfIsPasswordProtected
                    : Strings.PasswordDidNotWork;
                PendingPasswordPath = path;
                _ = RetryWithPasswordAsync(path, ask);
                return;
            }
            catch (PdfLoadException ex)
            {
                // The engine's message is English and carries the path; the person
                // holding the document gets the reason in their own words (#91).
                Status = DescribeLoadFailure(ex);
                return;
            }
            catch (Exception ex)
            {
                // Moved, locked, or the grant lapsed while it was being read.
                Status = Strings.WithDetail(Strings.FileCouldNotBeRead, ex.Message);
                return;
            }
        }

        CloseDocument();
        Adopt(loaded.Document, path, openedWithPassword: password is not null, loaded.Sizes);

        // A restore that had to wait for the password replays now that it is open (#133).
        if (_pendingRestore is { } restore && restore.DocumentPath == path)
        {
            _pendingRestore = null;
            await ReplayRecoveredAsync(restore.Entries);
        }
    }

    /// <summary>Off the UI thread: the document and every page's size, which is what opening costs.</summary>
    private (IPdfDocument Document, IReadOnlyList<(double Width, double Height)> Sizes) LoadDocument(string path, string? password)
    {
        var document = _engine.Open(path, password);
        try
        {
            var sizes = new List<(double Width, double Height)>(document.PageCount);
            for (var i = 0; i < document.PageCount; i++)
            {
                using var page = document.GetPage(i);
                sizes.Add((page.Width, page.Height));
            }
            return (document, sizes);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>Makes a loaded document the open one. The caller has closed the previous one.</summary>
    private void Adopt(IPdfDocument document, string path, bool openedWithPassword,
                       IReadOnlyList<(double Width, double Height)> sizes)
    {
        _document = document;
        _changesDiscarded = false;
        // Permissions before IsDocumentOpen, so no tool is enabled for a moment it should
        // not be (#131).
        Capabilities = DocumentCapabilities.From(document.Security);
        _matches.Clear();
        MatchCount = 0;
        CurrentMatchIndex = -1;
        LoadSignatures();
        for (var i = 0; i < sizes.Count; i++)
            Pages.Add(new PageViewModel(document, i, sizes[i].Width, sizes[i].Height) { Zoom = Zoom });

        // Opens fitted to the window, not at whatever the last document was zoomed
        // to (#143). Before any page is realised, so nothing renders twice.
        _fitOnOpenPending = true;
        FitOnOpen();

        DocumentPath = path;
        DocumentName = Path.GetFileName(path);
        IsDocumentOpen = true;
        CurrentPage = 1;
        // Starting a session truncates any previous journal for this document, which
        // is why it happens after a successful open and not before. A document opened
        // with a password is not journaled at all, so its text never reaches disk
        // unencrypted (#135).
        _journal.BeginSession(path, contentIsProtected: openedWithPassword);
        OnPropertyChanged(nameof(PageIndicator));
        OnPropertyChanged(nameof(ShowEmptyState));
        IsDirty = false;
        Status = Strings.Plural(document.PageCount,
            Strings.DocumentOpenedOne(DocumentName, document.PageCount),
            Strings.DocumentOpenedOther(DocumentName, document.PageCount));
        // After the "opened" line, which would otherwise replace it: nothing of this
        // document is journaled, and the person should know before they start (ADR-004 §7).
        if (openedWithPassword)
            Status = Strings.RecoveryOffForProtected;
    }

    /// <summary>
    /// Words for a load failure, from the typed reason rather than the engine's
    /// English message. The password case only lands here when there is no
    /// prompt wired up to ask for one.
    /// </summary>
    private static string DescribeLoadFailure(PdfLoadException ex) => ex switch
    {
        { IsPasswordError: true } => Strings.PdfIsPasswordProtected,
        { IsTooLargeError: true } => Strings.FileTooLarge,
        { IsFileError: true } => Strings.FileCouldNotBeRead,
        { IsFormatError: true } => Strings.FileNotValidPdf,
        // Not corrupt and not a wrong password: a handler PDFium cannot open (ADR-004 §8).
        { IsSecurityError: true } => Strings.UnsupportedProtection,
        _ => Strings.FileCouldNotBeOpened(ex.ErrorCode),
    };

    /// <summary>The document waiting on a password, if any.</summary>
    public string? PendingPasswordPath { get; private set; }

    private async Task RetryWithPasswordAsync(string path, Func<string, Task<string?>> ask)
    {
        var password = await ask(Path.GetFileName(path));
        PendingPasswordPath = null;

        if (string.IsNullOrEmpty(password))
        {
            // Nothing opened, so no session truncated the journal: a cancelled restore is
            // still on disk to be offered next time (#133).
            _pendingRestore = null;
            Status = Strings.OpeningCancelled;
            return;
        }

        await OpenAsync(path, password);
    }

    /// <summary>MegaPDF-added text boxes on a page — what restyle and move address.</summary>
    internal IReadOnlyList<PdfTextRun> BoxesOn(int pageIndex)
    {
        if (_document is null)
            return [];
        using var page = _document.GetPage(pageIndex);
        return page.GetTextBoxes();
    }

    /// <summary>Visual lines of body text on a page — what F1 edits (SDD §3.1).</summary>
    internal IReadOnlyList<PdfTextLine> LinesOn(int pageIndex)
    {
        if (_document is null)
            return [];
        using var page = _document.GetPage(pageIndex);
        return page.GetTextLines();
    }

    // --- Interaction (SDD §3.2) ---

    public PageHit HitTest(int pageIndex, PdfPoint point)
    {
        if (_document is null)
            return new PageHit(PageHitKind.None);
        using var page = _document.GetPage(pageIndex);
        return page.HitTest(point);
    }

    /// <summary>
    /// Routes a click on the page. Mirrors the WinUI app's routing so a document
    /// behaves the same on both desktops: form checkboxes toggle, drawn squares take
    /// a ✗ stamp, and clicking an existing mark clears it.
    /// </summary>
    public void HandlePageClick(int pageIndex, PdfPoint point)
    {
        if (_document is null)
            return;

        // A click while work runs — a change on its way, its page check, a save — is ignored
        // rather than queued (#145): repeat clicks must not stack up edits or questions.
        if (Busy.IsBusy)
            return;

        // Placement modes win over everything: the click is choosing a spot, not
        // asking what is under it.
        if (PendingSignature is { } pending)
        {
            Start(() => PlacePendingSignatureAsync(pageIndex, point, pending));
            return;
        }

        if (Mode is PageMode.AddText or PageMode.Whiteout)
            return;   // the view drives these — an editor and a drag respectively

        var hit = HitTest(pageIndex, point);
        // #131: what the document's owner does not allow opens no editor and selects
        // nothing; the status says why.
        if (!Capabilities.Allows(hit.Kind))
        {
            Status = Strings.ActionRestricted;
            return;
        }
        switch (hit.Kind)
        {
            case PageHitKind.FormCheckbox:
                Apply(new CheckboxToggleOperation(_document, pageIndex, hit.Field!), Strings.CheckboxToggled);
                break;

            case PageHitKind.DrawnCheckbox:
                Apply(new AddMarkOperation(_document, pageIndex, hit.Bounds!.Value, MarkStyle), Strings.Checked);
                break;

            case PageHitKind.StampAnnotation when !hit.AnnotationId!.StartsWith("sig:", StringComparison.Ordinal):
                // Check marks stay click-to-toggle: one size, one place, so there is
                // nothing to select them for (SDD §3.2).
                Apply(new RemoveMarkOperation(_document, pageIndex, hit.AnnotationId, hit.Bounds!.Value, MarkStyle),
                      Strings.Unchecked);
                break;

            case PageHitKind.StampAnnotation:
                // A placed signature selects for move, resize and delete (SDD §3.3).
                Select(new PageSelection(pageIndex, SelectionKind.Signature, hit.Bounds!.Value,
                                         AnnotationId: hit.AnnotationId));
                break;

            case PageHitKind.Whiteout:
                // Remove-only chrome: a cover is redrawn rather than nudged, which is
                // simpler and is how the Windows app behaves.
                Select(new PageSelection(pageIndex, SelectionKind.Whiteout, hit.Bounds!.Value,
                                         ObjectIndex: hit.ObjectIndex!.Value));
                break;

            case PageHitKind.FormTextField when hit.Field is { } field:
                EditFieldRequested?.Invoke(pageIndex, field);
                break;

            case PageHitKind.TextRun when hit.TextLine is { } line:
                // The view opens an editor over the line; the commit comes back
                // through EditLine. Pages PDFium cannot rewrite faithfully say so
                // instead (#118), after a check that runs off the UI thread (#145).
                Start(() => BeginLineEditAsync(pageIndex, line));
                break;

            case PageHitKind.TextBox when hit.TextRun is { } run:
                // Added text moves and can be restyled, but not resized — its size is
                // a font size, not a rectangle.
                Select(new PageSelection(pageIndex, SelectionKind.TextBox, run.Bounds, Run: run));
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Starts async work from a synchronous entry point. With a window it runs on (its engine
    /// work off the UI thread) and this returns at once; without one it has finished when this
    /// returns, as every entry point did before #145.
    /// </summary>
    private void Start(Func<Task> work)
    {
        if (Inline)
            RunSynchronously(work);
        else
            _ = work();
    }

    private void Apply(IPageEditOperation operation, string doneMessage, Action? cancelled = null, Action? applied = null) =>
        Start(() => ApplyAsync(operation, doneMessage, cancelled, applied));

    /// <summary>
    /// Applies one change: its #139 page check if it needs one, then the edit, off the UI thread,
    /// under the page's busy state. Every failure lands in the status line; nothing escapes.
    /// </summary>
    private async Task ApplyAsync(IPageEditOperation operation, string doneMessage, Action? cancelled, Action? applied)
    {
        // The central gate (#131): the entry points check first so no editor opens, and
        // this is what holds if one is ever missed.
        if (!Capabilities.Allows(operation))
        {
            Status = Strings.ActionRestricted;
            return;
        }
        // One change at a time (#145): a change waiting on its page check, or with the warning
        // on screen, is never joined by a second, so there is never a second question.
        if (Busy.IsBusy || _document is not { } document)
        {
            cancelled?.Invoke();
            return;
        }

        using var busy = Busy.Begin(Strings.BusyApplying, scope: BusyScope.Page, pageIndex: operation.PageIndex);
        try
        {
            // #139: whiteouts and text boxes make PDFium rewrite the page, which on a few pages
            // changes parts the person never touched. Never refused: the first such change on a
            // page waits for the page's check — started when the page was shown — at most
            // 1.5 s, and warns when the page would change. A check still running by then is
            // cancelled and the change applies without a warning (#145). With nobody to ask
            // (no window: the self-test, --render-check), the answer can only be Continue, so
            // the edit applies at once, as it always did.
            if (PageRewriteConfirmationRequested is { } ask
                && PageRegenerationWarnings.RegeneratesUnjudged(operation)
                && !_pageWarnings.IsSettled(operation.PageIndex))
            {
                busy.SetLabel(Strings.BusyCheckingPage);
                var answer = await _pageWarnings.AskAsync(document, operation);
                if (!ReferenceEquals(document, _document))
                    return; // closed or replaced while the page was being judged
                if (answer == PageCheckAnswer.WouldChange)
                {
                    if (!await ask() || !ReferenceEquals(document, _document))
                    {
                        cancelled?.Invoke();
                        return;
                    }
                    _pageWarnings.Settle(operation.PageIndex);
                }
                busy.SetLabel(Strings.BusyApplying);
            }

            await OffUiThread(() => _undoStack.Do(operation));
            if (!ReferenceEquals(document, _document))
                return;
            // Journalled after Apply, because an operation's entry can only be written
            // once it knows what it did — a placed stamp's id, for instance.
            if (operation.ChangesTheFile)
                _journal.Record(operation.ToJournalEntry(inverse: false));
            AfterEdit(operation.PageIndex, doneMessage, operation.ChangesTheFile);
            applied?.Invoke();
        }
        catch (TextEditException ex) when (ex.Reason == TextEditFailure.LayoutWouldChange)
        {
            Status = LayoutRefusalText(ex.Layout);
            cancelled?.Invoke();
        }
        catch (Exception ex)
        {
            Status = Strings.WithDetail(Strings.ChangeFailed, ex.Message);
            cancelled?.Invoke();
        }
    }

    /// <summary>
    /// What an applied change owes the window, split on whether it reaches the file (#329).
    /// A redaction mark never does: it is not written, so there is no unsaved dot, no recovery
    /// entry and no re-render — the overlay redraw is the whole visible change (ADR-005
    /// decision 4). It is still an undo step, so Save stays live through <c>HasRedactionMarks</c>.
    /// </summary>
    private void AfterEdit(int pageIndex, string message, bool changesTheFile = true)
    {
        if (!changesTheFile)
        {
            RefreshRedactionMarks();
            Status = message;
            RaiseUndoRedo();
            return;
        }
        _editCount++;
        IsDirty = true;
        Status = message;
        RerenderPage(pageIndex);
        RaiseUndoRedo();
    }

    private bool CanUndoNow() => _undoStack.CanUndo && !Busy.IsBusy;

    private bool CanRedoNow() => _undoStack.CanRedo && !Busy.IsBusy;

    [RelayCommand(CanExecute = nameof(CanUndoNow))]
    private async Task UndoAsync()
    {
        if (!_undoStack.CanUndo || Busy.IsBusy || _document is not { } document)
            return;
        var op = _undoStack.PeekUndo as IPageEditOperation;
        using (Busy.Begin(Strings.BusyApplying, scope: BusyScope.Page, pageIndex: op?.PageIndex ?? -1))
        {
            try
            {
                await OffUiThread(_undoStack.Undo);
            }
            catch (Exception ex)
            {
                Status = Strings.WithDetail(Strings.ChangeFailed, ex.Message);
                RaiseUndoRedo();
                return;
            }
        }
        if (!ReferenceEquals(document, _document))
            return;
        // An undo is journalled as its own inverse entry: replaying the journal
        // after a crash must reproduce what was on screen, not what was ever done.
        // A mark is the exception — see AfterHistoryChange.
        AfterHistoryChange(op, inverse: true, Strings.Undone);
    }

    [RelayCommand(CanExecute = nameof(CanRedoNow))]
    private async Task RedoAsync()
    {
        if (!_undoStack.CanRedo || Busy.IsBusy || _document is not { } document)
            return;
        var op = _undoStack.PeekRedo as IPageEditOperation;
        using (Busy.Begin(Strings.BusyApplying, scope: BusyScope.Page, pageIndex: op?.PageIndex ?? -1))
        {
            try
            {
                await OffUiThread(_undoStack.Redo);
            }
            catch (Exception ex)
            {
                Status = Strings.WithDetail(Strings.ChangeFailed, ex.Message);
                RaiseUndoRedo();
                return;
            }
        }
        if (!ReferenceEquals(document, _document))
            return;
        AfterHistoryChange(op, inverse: false, Strings.Redone);
    }

    /// <summary>
    /// The bookkeeping an undo or a redo owes, split on whether the operation reaches the
    /// file (#329). A redaction mark never does: undoing a mis-drag must not leave the
    /// document looking unsaved, and must not put a mark into the recovery journal, which is
    /// a record of what the file should be. What it does owe is a redraw of the marks.
    /// </summary>
    private void AfterHistoryChange(IPageEditOperation? op, bool inverse, string message)
    {
        if (op is { ChangesTheFile: false })
        {
            AfterEdit(op.PageIndex, message, changesTheFile: false);
            return;
        }
        if (op is not null)
            _journal.Record(op.ToJournalEntry(inverse));
        AfterEdit(op?.PageIndex ?? 0, message);
    }

    private void RaiseUndoRedo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    // --- Printing (SDD §3.5) and shrink-for-email (SDD §3.7) ---

    /// <summary>
    /// Writes the LIVE document — unsaved edits included — to a temp file and hands
    /// it to the platform printer. Printing what is on screen rather than what is on
    /// disk is the behaviour the Windows app documents, and the one people expect:
    /// you tick the boxes, then print.
    ///
    /// The temp file goes in <see cref="Path.GetTempPath"/>, which under the App
    /// Sandbox is inside the container, and is deleted as soon as the operation
    /// returns.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPrint))]
    private async Task PrintAsync()
    {
        if (_document is not { } document || Busy.IsBusy)
            return;

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            // Deliberately not implemented for Avalonia-on-Windows: MegaPDF.App is
            // the Windows product and already prints. A second, half-working
            // implementation would be a liability for a case that does not exist.
            Status = Strings.PrintingUnavailableHere;
            return;
        }

        // Linux asks which printer *before* anything is written, so cancelling the
        // dialog costs nothing — not even a copy of the document in the temp
        // directory. macOS cannot: NSPrintOperation's panel is part of the print,
        // and it needs the file to preview (#158).
        // Inside a Flatpak or the snap the desktop's portal owns the dialog: it lists
        // the printers, it takes the copies, it prints. Asking our own question first
        // would be asking the same question twice, so the app goes straight to
        // writing the document and hands it over (#158).
        var throughPortal = OperatingSystem.IsLinux()
                            && Platform.LinuxPrinter.InSandbox;

        Platform.Printing.Choice? choice = null;
        if (OperatingSystem.IsLinux() && !throughPortal)
        {
            if (!Platform.LinuxPrinter.IsAvailable)
            {
                Status = Strings.PrintingNeedsCups;
                return;
            }

            if (PrintDestinationRequested is { } ask)
            {
                choice = await ask(Platform.LinuxPrinter.Destinations());
                if (choice is null)
                {
                    // Cancelled at the dialog. Not an error, and the same words macOS
                    // uses when the print panel is dismissed.
                    Status = Strings.PrintingCancelled;
                    return;
                }
            }
            else
            {
                // Nothing is listening — a headless run. Behave as a bare `lp` would
                // and use the system default rather than doing nothing.
                choice = new Platform.Printing.Choice(null, 1);
            }
        }

        var temp = Path.Combine(Path.GetTempPath(), $"megapdf-print-{Guid.NewGuid():N}.pdf");
        try
        {
            // Serialising the document is the slow part, and it runs off the UI thread (#145).
            using (Busy.Begin(Strings.BusyPrinting))
            {
                await OffUiThread(() =>
                {
                    using var file = File.Create(temp);
                    document.Save(file);
                });
            }

            if (throughPortal)
            {
                // The portal answers when its dialog has been used, so this await
                // is as long as the person takes. The busy strip stays up, which
                // is honest: the document is being printed.
                var title = DocumentName ?? "MegaPDF";
                using (Busy.Begin(Strings.BusyPrinting))
                {
                    var sent = await Platform.PortalPrinter.PrintAsync(
                        temp, title, TimeSpan.FromMinutes(10));
                    Status = sent.Message;
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                // lp reads the file and returns once the job is queued, but it is
                // still a process launch: off the UI thread, so a wedged spooler
                // cannot freeze the window while it waits.
                var linux = choice!;
                var title = DocumentName ?? "MegaPDF";
                using (Busy.Begin(Strings.BusyPrinting))
                {
                    var sent = await OffUiThread(() =>
                    {
                        // The guard is repeated inside the closure on purpose: the
                        // platform compatibility analyser does not carry the outer
                        // OperatingSystem.IsLinux() across a lambda, and silencing it
                        // with a pragma would silence the next real mistake too.
                        if (!OperatingSystem.IsLinux())
                            return new Platform.Printing.Outcome(false, Strings.PrintingUnavailableHere);
                        return Platform.LinuxPrinter.Print(temp, linux.Destination, title, linux.Copies);
                    });
                    Status = sent.Message;
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                // NSPrintOperation drives AppKit, so the print panel runs on the UI thread.
                Status = Platform.MacPrinter.Print(temp).Message;
            }
        }
        catch (Exception ex)
        {
            Status = Strings.WithDetail(Strings.CouldNotPrint, ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Re-encodes oversized images so the document can be emailed (SDD §3.7). Works
    /// on a fresh copy from disk so the open document is never degraded, which is
    /// also why it insists on a saved file first.
    /// </summary>
    /// <summary>
    /// Prepares a smaller copy, without touching any destination.
    ///
    /// Returns a staged copy only when something was actually re-encoded, and the caller
    /// must not open a destination before it has that answer. Opening one is
    /// destructive on its own — Avalonia's writable stream truncates — so deciding
    /// afterwards meant a document with nothing to shrink left a 0-byte file
    /// behind, or destroyed whichever file the user chose to overwrite, while the
    /// status line said nothing had happened (#59).
    /// </summary>
    public (ImageShrinker.Result Result, VerifiedSave.StagedCopy? Copy) PrepareShrunkCopy()
    {
        (ImageShrinker.Result Result, VerifiedSave.StagedCopy? Copy) prepared = (new ImageShrinker.Result(0), null);
        RunSynchronously(async () => prepared = await PrepareShrunkCopyAsync());
        return prepared;
    }

    /// <inheritdoc cref="PrepareShrunkCopy"/>
    /// <remarks>
    /// Off the UI thread, with "Making a smaller copy…" (#145). The copy comes back staged in a
    /// temporary file, not as bytes, so a document of any size shrinks without being held in
    /// memory (#147); the caller writes it where the person chooses and disposes it.
    /// </remarks>
    public async Task<(ImageShrinker.Result Result, VerifiedSave.StagedCopy? Copy)> PrepareShrunkCopyAsync()
    {
        if (DocumentPath is not { } path)
        {
            // Not the same as "nothing to shrink": we have no file to read. Saying
            // so beats reporting that the pictures are already small (#68).
            Status = Strings.ReopenBeforeShrinking;
            return (new ImageShrinker.Result(0), (VerifiedSave.StagedCopy?)null);
        }

        if (_document is not { } document || Busy.IsBusy)
            return (new ImageShrinker.Result(0), (VerifiedSave.StagedCopy?)null);

        using var busy = Busy.Begin(Strings.BusyShrinking);
        return await OffUiThread(() =>
        {
            // Opened like the document, so a protected one opens with its own password; the
            // copy then saves and verifies protected like any other (#134).
            using var copy = _engine.OpenLike(document, path);
            var result = ImageShrinker.Shrink(copy, Platform.SkiaJpeg.Encode);
            if (result.ImagesReplaced == 0)
                return (result, (VerifiedSave.StagedCopy?)null);

            return (result, VerifiedSave.ToStagedFile(_engine, copy));
        });
    }

    /// <summary>Shrinking rewrites the document's images, which is modify (#131).</summary>
    public bool CanShrink => IsDocumentOpen && !IsDirty && Capabilities.CanShrink && !Busy.IsBusy;

    // --- Recent documents (SDD §2.2 empty state) ---

    /// <summary>
    /// One row of the empty state's recents list: the file name, and under it where
    /// the file lives (#165).
    ///
    /// Every row carries its location, not only the rows whose names clash. Files
    /// from one template or one scanner share a name, and a list that added the
    /// folder only sometimes would rearrange itself as entries came and went — the
    /// shared rule for this issue, which Windows and iOS follow too.
    /// </summary>
    /// <param name="Name">What Finder calls the file, so ".pdf" is hidden when the
    /// person has Finder set to hide extensions.</param>
    /// <param name="Location">The line under the name, shortened in the middle if it
    /// is long. Null only for a path with no folder above it at all.</param>
    /// <param name="FullLocation">Every segment, for the help tag.</param>
    public sealed record RecentRow(RecentEntry Entry, string Name, string? Location, string? FullLocation)
    {
        public string Path => Entry.Path;

        public bool HasLocation => !string.IsNullOrEmpty(Location);

        /// <summary>The help tag: where the file is, in full, never a POSIX path.</summary>
        public string Tip => FullLocation is { Length: > 0 } full ? full : Name;

        /// <summary>
        /// The name and the place together, so a screen reader can tell two rows with
        /// the same file name apart (#2). The same sentence the Windows and iOS halves
        /// of #165 read out.
        /// </summary>
        public string AccessibleName =>
            FullLocation is { Length: > 0 } full ? Strings.RecentInLocation(Name, full) : Name;
    }

    public ObservableCollection<RecentRow> Recents { get; } = [];

    public bool HasRecents => Recents.Count > 0;

    public void LoadRecents()
    {
        Recents.Clear();
        // Finder's names for the places a document can live — localised, and the
        // account's own name rather than a POSIX home path. Empty off macOS, where the
        // raw folder names are all there is and all that is wanted.
        var places = OperatingSystem.IsMacOS()
            ? Platform.MacFileNames.Places()
            : (IReadOnlyList<NamedFolder>)[];
        // The sandbox container is inside the home folder, so without this a file the
        // app opened from its own container reads as the whole way down to it (#146 §3).
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

        // How far up a row has to go before it reads differently from the others with
        // the same name. The parent folder usually does it; when it does not, the line
        // keeps that much more of the path instead of shortening it away. Compared the
        // way a person reads the list, not the way a byte comparison would.
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

    /// <summary>
    /// Replaces the recents list with rows made for a capture (#146 §3).
    ///
    /// The home screenshot was whatever the machine had last opened, which is not a
    /// screenshot anyone can re-take. These rows go in as RecentRow directly rather
    /// than through the store: nothing about them has to exist on disk, and going
    /// through the store would write into the person's real list.
    /// </summary>
    internal void ShowDemoRecents(IReadOnlyList<(string Name, string Location)> rows)
    {
        Recents.Clear();
        foreach (var (name, location) in rows)
            Recents.Add(new RecentRow(new RecentEntry(name), name, location, location));
        OnPropertyChanged(nameof(HasRecents));
    }

    /// <summary>
    /// Records a document as recently opened. The bookmark is what lets macOS
    /// reopen it in a later session at all — under the sandbox a stored path is not
    /// a key to anything.
    /// </summary>
    public void RememberRecent(string path, string? bookmark)
    {
        _recents.Add(path, bookmark);
        LoadRecents();
    }

    // --- Crash recovery (SDD §3.4) ---

    /// <summary>
    /// Documents that were being edited when the app last stopped without saving.
    /// Empty in the ordinary case, which is why the view only asks about it once.
    /// </summary>
    public IReadOnlyList<RecoverableSession> FindRecoverableSessions() =>
        _journal.FindRecoverableSessions();

    /// <summary>
    /// Reopens a document and replays the edits that were never saved.
    ///
    /// The entries are read BEFORE opening, because opening begins a new session and
    /// that truncates this very journal — a detail the Windows implementation calls
    /// out too, and one that silently loses the recovery if it is got wrong.
    /// </summary>
    public async Task RestoreSessionAsync(RecoverableSession session)
    {
        IReadOnlyList<JournalEntry> entries;
        try
        {
            entries = RecoveryJournal.LoadEntries(session.JournalPath);
        }
        catch (Exception ex)
        {
            // Held by another instance, or gone: nothing is lost, and nothing is opened.
            Status = Strings.WithDetail(Strings.FileCouldNotBeRead, ex.Message);
            return;
        }

        // For a protected document OpenAsync only starts the password prompt and returns.
        // The entries wait here and replay when the retry opens it; checking only after
        // Open() returned lost them, and the retry's new session truncated the journal (#133).
        _pendingRestore = (session.DocumentPath, entries);
        await OpenAsync(session.DocumentPath);
        if (PendingPasswordPath is null)
            _pendingRestore = null;   // opened and replayed, or failed outright
    }

    /// <summary>A restore waiting on its document's password prompt (#133).</summary>
    private (string DocumentPath, IReadOnlyList<JournalEntry> Entries)? _pendingRestore;

    private async Task ReplayRecoveredAsync(IReadOnlyList<JournalEntry> entries)
    {
        if (_document is not { } document || entries.Count == 0)
            return;

        // Re-journalled before the replay, not after: opening started a new session over the
        // journal they came from, so if the replay fails part way the edits must already be
        // back on disk for a second crash, or a quit without saving, to offer again (#145).
        foreach (var entry in entries)
            _journal.Record(entry);

        int applied;
        using (Busy.Begin(Strings.BusyRestoring))
        {
            try
            {
                applied = await OffUiThread(() => JournalReplayer.Replay(document, entries));
            }
            catch (Exception ex)
            {
                // It used to vanish here, with the journal already truncated.
                if (ReferenceEquals(document, _document))
                {
                    _editCount++;
                    IsDirty = true;
                    foreach (var page in Pages)
                        if (page.IsRealised)
                            page.Rerender(DpiScale);
                    Status = Strings.WithDetail(Strings.ChangeFailed, ex.Message);
                }
                return;
            }
        }
        if (!ReferenceEquals(document, _document))
            return;

        _editCount++;
        IsDirty = applied > 0;
        // Every rendered page predates the replay.
        foreach (var page in Pages)
            if (page.IsRealised)
                page.Rerender(DpiScale);

        Status = applied > 0
            ? Strings.Plural(applied,
                Strings.RecoveredChangesOne(applied, DocumentName),
                Strings.RecoveredChangesOther(applied, DocumentName))
            : Strings.ReopenedNothingToRecover(DocumentName);
    }

    public void DiscardSession(RecoverableSession session)
    {
        RecoveryJournal.Discard(session.JournalPath);
        Status = Strings.DiscardedUnsavedChanges;
    }

    // --- Keyboard traversal of the page (SDD §2.2 — required, #2) ---

    /// <summary>
    /// Where keyboard focus is on the page: which page, and which of its regions in
    /// reading order. Null means focus is not on the page at all.
    ///
    /// Built on the per-page interaction maps that already exist for the cursor, so
    /// tabbing costs a list walk rather than an engine hit-test — the same reason
    /// the cursor reads from them.
    /// </summary>
    public sealed record FocusedRegion(int PageIndex, int RegionIndex, PdfRect Bounds, PageHitKind Kind)
    {
        /// <summary>What a screen reader should say about this region.</summary>
        public string Describe(bool isChecked) => Kind switch
        {
            PageHitKind.FormCheckbox => isChecked ? Strings.RegionCheckboxTicked : Strings.RegionCheckboxNotTicked,
            PageHitKind.DrawnCheckbox => Strings.RegionBoxToTick,
            PageHitKind.FormTextField => Strings.RegionFormField,
            PageHitKind.TextRun => Strings.RegionTextEditable,
            PageHitKind.TextBox => Strings.RegionAddedText,
            PageHitKind.StampAnnotation => Strings.RegionSignatureOrMark,
            PageHitKind.Whiteout => Strings.RegionCover,
            _ => Strings.RegionPage,
        };
    }

    [ObservableProperty]
    private FocusedRegion? _pageFocus;

    /// <summary>Raised when the view should bring the focused region into view.</summary>
    public event Action<int, PdfRect>? FocusScrollRequested;

    /// <summary>
    /// Moves keyboard focus to the next interactive region, crossing page
    /// boundaries and wrapping at the end. Pages that have not been rasterised have
    /// no interaction map yet, so they are realised on demand by asking for it.
    /// </summary>
    public void MoveFocus(bool forward)
    {
        if (Pages.Count == 0)
            return;

        // FromEnd means "start at this page's last region" — the count is not known
        // until the page's map is built inside the loop, and a sentinel index of
        // int.MaxValue does not work: MaxValue - 1 is never a valid index, so going
        // backwards skipped whole pages instead of entering them at the end.
        const int FromEnd = -2;

        var pageIndex = PageFocus?.PageIndex ?? (forward ? 0 : Pages.Count - 1);
        var regionIndex = PageFocus?.RegionIndex ?? (forward ? -1 : FromEnd);

        // At most one full lap, so a document with nothing interactive terminates
        // rather than spinning.
        for (var visited = 0; visited <= Pages.Count; visited++)
        {
            var regions = RegionsFor(pageIndex);
            var next = regionIndex == FromEnd
                ? regions.Count - 1
                : forward ? regionIndex + 1 : regionIndex - 1;

            if (next >= 0 && next < regions.Count)
            {
                var (bounds, kind) = regions[next];
                PageFocus = new FocusedRegion(pageIndex, next, bounds, kind);
                FocusScrollRequested?.Invoke(pageIndex, bounds);
                Status = PageFocus.Describe(IsCheckedAt(pageIndex, bounds, kind));
                return;
            }

            pageIndex = forward
                ? (pageIndex + 1) % Pages.Count
                : (pageIndex - 1 + Pages.Count) % Pages.Count;
            regionIndex = forward ? -1 : FromEnd;
        }

        PageFocus = null;
        Status = Strings.NothingKeyboardEditable;
    }

    /// <summary>Activates the focused region — the keyboard's equivalent of a click.</summary>
    public void ActivateFocus()
    {
        if (PageFocus is not { } focus)
            return;

        // Redact armed: the focused region is exactly what a drag would have covered,
        // so Enter marks it rather than opening whatever is under it (#173). Without
        // this the tool is drag-only — and a tool that needs a pointer is no tool at
        // all for someone who has none. The same gap the Windows real-window check
        // found; HandlePageClick below would open the line editor instead.
        if (Mode == PageMode.Redact)
        {
            AddRedactionMark(focus.PageIndex, focus.Bounds);
            return;
        }

        // Routed through the same handler a click uses, aimed at the region's
        // centre, so the keyboard can never diverge from the mouse.
        HandlePageClick(focus.PageIndex,
            new PdfPoint(focus.Bounds.X + (focus.Bounds.Width / 2),
                         focus.Bounds.Y + (focus.Bounds.Height / 2)));

        // The map is rebuilt by the edit, so re-read the region under focus rather
        // than trusting the bounds we came in with.
        var regions = RegionsFor(focus.PageIndex);
        if (focus.RegionIndex < regions.Count)
        {
            var (bounds, kind) = regions[focus.RegionIndex];
            PageFocus = focus with { Bounds = bounds, Kind = kind };
        }
    }

    public void ClearPageFocus() => PageFocus = null;

    private IReadOnlyList<(PdfRect Bounds, PageHitKind Kind)> RegionsFor(int pageIndex)
    {
        var page = Pages.FirstOrDefault(p => p.Index == pageIndex);
        if (page is null)
            return [];

        // A page the view has never realised has no map. Tab must still reach it,
        // so build the map — which needs the engine, not a raster. Rendering here
        // would rasterise a page nobody is looking at.
        page.EnsureRegions();
        return page.RegionsInReadingOrder();
    }

    private bool IsCheckedAt(int pageIndex, PdfRect bounds, PageHitKind kind)
    {
        if (kind != PageHitKind.FormCheckbox || _document is null)
            return false;

        var hit = HitTest(pageIndex,
            new PdfPoint(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2)));
        return hit.Field is { IsChecked: true };
    }

    // --- Selection (SDD §3.3: place it, then adjust it) ---

    public enum SelectionKind { Signature, TextBox, Whiteout, RedactionMark }

    /// <summary>
    /// Something placed on the page that the user has selected. One record for all
    /// four kinds because the chrome is one mechanism — what differs is which
    /// handles it offers and what committing a drag calls.
    /// </summary>
    public sealed record PageSelection(
        int PageIndex, SelectionKind Kind, PdfRect Bounds,
        string? AnnotationId = null, int ObjectIndex = -1, PdfTextRun? Run = null, int MarkId = -1)
    {
        /// <summary>
        /// A signature has a rectangle worth resizing, and so does a redaction mark (#329):
        /// the mark is an area, and the person is deciding what it covers, so it wants the
        /// same corner handles rather than the drag the core happened to grow.
        /// </summary>
        public bool CanResize => Kind is SelectionKind.Signature or SelectionKind.RedactionMark;

        /// <summary>A cover is redrawn rather than nudged, and a mark moves like a signature.</summary>
        public bool CanMove => Kind is SelectionKind.Signature or SelectionKind.TextBox or SelectionKind.RedactionMark;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTextStyleContext))]
    private PageSelection? _selection;

    // --- The contextual font and size pickers (#144) ---

    private bool _isEditingTextBox;

    /// <summary>
    /// Set by the view while its in-place editor is open over an added text box. The
    /// pickers stay up, and a change to them waits for the edit to be committed rather
    /// than rewriting the box under the editor.
    /// </summary>
    public bool IsEditingTextBox
    {
        get => _isEditingTextBox;
        set
        {
            if (SetProperty(ref _isEditingTextBox, value))
                OnPropertyChanged(nameof(IsTextStyleContext));
        }
    }

    /// <summary>
    /// Whether the font and size pickers belong on the toolbar: while Add text is armed,
    /// while an added text box is selected, and while one is being edited. Anywhere else
    /// they would be settings for nothing (#144).
    /// </summary>
    public bool IsTextStyleContext =>
        IsAddingText || IsEditingTextBox || Selection is { Kind: SelectionKind.TextBox };

    /// <summary>True while the pickers are being set from a selection, so that is not taken as a change.</summary>
    private bool _syncingTextStyle;

    partial void OnSelectionChanged(PageSelection? value)
    {
        if (value is { Kind: SelectionKind.TextBox, Run: { } run })
            ShowTextStyleOf(run);
    }

    /// <summary>Points the pickers at a box's own face and size.</summary>
    private void ShowTextStyleOf(PdfTextRun run)
    {
        _syncingTextStyle = true;
        try
        {
            TextFont = run.TextBoxFont ?? StandardTextBoxFonts.Default;
            TextSize = Math.Round(run.FontSize, 2);
            OnPropertyChanged(nameof(SelectedTextFont));
        }
        finally
        {
            _syncingTextStyle = false;
        }
    }

    partial void OnTextFontChanged(string value) => RestyleSelectedTextBox();

    partial void OnTextSizeChanged(double value) => RestyleSelectedTextBox();

    /// <summary>
    /// A picker changed while an added box is selected: the box takes the new face or
    /// size at once, as one undoable edit, and stays selected.
    /// </summary>
    private void RestyleSelectedTextBox()
    {
        if (_syncingTextStyle || IsEditingTextBox || _document is null
            || Selection is not { Kind: SelectionKind.TextBox, Run: { } run } selection)
            return;
        var face = run.TextBoxFont ?? StandardTextBoxFonts.Default;
        if (face == TextFont && Math.Abs(run.FontSize - TextSize) < 0.01)
            return;
        if (!StandardTextBoxFonts.IsSupported(TextFont))
            return;

        Apply(new RestyleTextBoxOperation(_document, selection.PageIndex, run.ObjectIndex, run,
                                          run.Text, TextFont, TextSize),
              Strings.TextUpdated,
              cancelled: () =>
              {
                  // Cancelled at the #139 warning: the pickers go back to what the box still is.
                  if (Selection is { Run: { } now } && ReferenceEquals(now, run))
                      ShowTextStyleOf(run);
              },
              applied: () => ReselectTextBox(selection.PageIndex, run));
    }

    /// <summary>
    /// After a restyle the selection points at the rewritten box: same id, new bounds,
    /// and possibly a new object index.
    /// </summary>
    private void ReselectTextBox(int pageIndex, PdfTextRun previous)
    {
        if (Selection is not { Kind: SelectionKind.TextBox, Run: { } selected } || !ReferenceEquals(selected, previous))
            return;
        var box = BoxesOn(pageIndex).FirstOrDefault(b => previous.TextBoxId is { } id
            ? b.TextBoxId == id
            : b.ObjectIndex == previous.ObjectIndex);
        Selection = box is null ? null : new PageSelection(pageIndex, SelectionKind.TextBox, box.Bounds, Run: box);
    }

    private void Select(PageSelection selection)
    {
        Selection = selection;
        // A text box or cover selected is about to be moved, restyled or removed: its page's
        // #139 check starts now, so the answer is usually ready by the change (#145).
        if (selection.Kind is SelectionKind.TextBox or SelectionKind.Whiteout)
            PreparePageCheck(selection.PageIndex);
        Status = selection.Kind switch
        {
            SelectionKind.Signature => Strings.SignatureSelectedHint,
            SelectionKind.TextBox => Strings.TextBoxSelectedHint,
            SelectionKind.RedactionMark => Strings.RedactMarkSelectedHint,
            _ => Strings.CoverSelectedHint,
        };
    }

    public void ClearSelection() => Selection = null;

    /// <summary>Removes whatever is selected, whichever kind it is.</summary>
    public void DeleteSelection()
    {
        if (_document is null || Selection is not { } sel)
            return;

        IPageEditOperation op = sel.Kind switch
        {
            SelectionKind.Signature =>
                new RemoveSignatureOperation(_document, sel.PageIndex, sel.AnnotationId!, sel.Bounds),
            SelectionKind.Whiteout =>
                new RemoveWhiteoutOperation(_document, sel.PageIndex, sel.ObjectIndex, sel.Bounds),
            SelectionKind.RedactionMark =>
                new RemoveRedactionMarkOperation(_document, sel.PageIndex, sel.MarkId, sel.Bounds),
            _ => new RemoveTextBoxOperation(_document, sel.PageIndex, sel.Run!.ObjectIndex, sel.Run),
        };

        Selection = null;
        Apply(op, sel.Kind switch
        {
            SelectionKind.Signature => Strings.SignatureRemoved,
            SelectionKind.Whiteout => Strings.CoverRemoved,
            SelectionKind.RedactionMark => Strings.RedactMarkRemoved,
            _ => Strings.TextRemoved,
        });
    }

    /// <summary>Commits a drag or resize of whatever is selected.</summary>
    public void CommitSelectionBounds(PdfRect newBounds)
    {
        if (Selection is not { } sel || newBounds == sel.Bounds)
            return;

        switch (sel.Kind)
        {
            case SelectionKind.Signature:
                MoveSignature(sel.PageIndex, sel.AnnotationId!, sel.Bounds, newBounds);
                break;
            case SelectionKind.RedactionMark:
                MoveRedactionMark(sel.PageIndex, sel.MarkId, sel.Bounds, newBounds);
                break;
            case SelectionKind.TextBox:
                // Cancel on the #139 warning leaves the box where it was: so does the selection.
                MoveTextBox(sel.PageIndex, sel.Run!, newBounds, cancelled: () =>
                {
                    if (Selection is { } now && now.PageIndex == sel.PageIndex && now.Kind == sel.Kind)
                        Selection = sel;
                });
                break;
            default:
                return;
        }

        // Re-anchor the chrome; the id and object index survive a move, so the
        // selection is still valid afterwards.
        Selection = sel with { Bounds = newBounds };
    }

    // --- Preferences (SDD §3.2, §3.3, §4.4) ---

    /// <summary>
    /// Which mark a ticked box gets: ✗ by default, per the 2026-07-08 stakeholder
    /// decision (SDD Appendix B #3). ✓ and ■ exist because a tick means "yes" in
    /// some countries and "this one" in others, and a filled square is what some
    /// official forms ask for.
    /// </summary>
    public CheckMarkStyle MarkStyle
    {
        get => _settings.MarkStyle;
        set
        {
            if (_settings.MarkStyle == value)
                return;
            _settings.MarkStyle = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<MarkStyleChoice> MarkStyleChoices { get; } =
    [
        new(CheckMarkStyle.Cross, Strings.MarkStyleCross),
        new(CheckMarkStyle.Check, Strings.MarkStyleCheck),
        new(CheckMarkStyle.FilledSquare, Strings.MarkStyleFilledSquare),
    ];

    /// <summary>What the Options flyout binds: the choice whose style is current.</summary>
    public MarkStyleChoice? SelectedMarkStyle
    {
        get => MarkStyleChoices.FirstOrDefault(c => c.Style == MarkStyle);
        set
        {
            if (value is null || value.Style == MarkStyle)
                return;
            MarkStyle = value.Style;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Bakes marks, signatures and form values permanently into the page on save
    /// (SDD §3.3). Off by default: flattening is irreversible, and someone who
    /// ticks a box today may need to untick it tomorrow.
    /// </summary>
    public bool FlattenOnSave
    {
        get => _settings.FlattenOnSave;
        set
        {
            if (_settings.FlattenOnSave == value)
                return;
            _settings.FlattenOnSave = value;
            OnPropertyChanged();
        }
    }

    // --- Placement modes (SDD §3.1, §3.3) ---

    /// <summary>
    /// What the next click on the page will do. Only one can be armed at a time —
    /// arming one cancels the others, because a click cannot mean two things.
    /// </summary>
    public enum PageMode
    {
        Select,
        AddText,
        Whiteout,
        Redact,
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAddingText))]
    [NotifyPropertyChangedFor(nameof(IsWhiteoutMode))]
    [NotifyPropertyChangedFor(nameof(IsRedactMode))]
    [NotifyPropertyChangedFor(nameof(ModeHint))]
    [NotifyPropertyChangedFor(nameof(IsModeActive))]
    [NotifyPropertyChangedFor(nameof(IsTextStyleContext))]
    private PageMode _mode = PageMode.Select;

    public bool IsAddingText => Mode == PageMode.AddText;
    public bool IsWhiteoutMode => Mode == PageMode.Whiteout;
    public bool IsRedactMode => Mode == PageMode.Redact;
    public bool IsModeActive => Mode != PageMode.Select || IsPlacingSignature;

    /// <summary>
    /// A banner telling the user what the next click does, and how to get out. A
    /// mode with no visible affordance is a mode people get stuck in (SDD §2.2).
    /// </summary>
    public string ModeHint => Mode switch
    {
        PageMode.AddText => Strings.ModeHintAddText,
        PageMode.Whiteout => Strings.ModeHintWhiteout,
        PageMode.Redact => Strings.RedactHint,
        _ => IsPlacingSignature ? Strings.ModeHintPlaceSignature : "",
    };

    /// <summary>The face and size the next text box is written in (SDD §3.1: three faces).</summary>
    [ObservableProperty]
    private string _textFont = StandardTextBoxFonts.Default;

    [ObservableProperty]
    private double _textSize = 12;

    public IReadOnlyList<string> TextFonts { get; } = StandardTextBoxFonts.All;

    /// <summary>The three faces with the names the toolbar shows for them.</summary>
    public IReadOnlyList<FontChoice> TextFontChoices { get; } =
    [
        new(StandardTextBoxFonts.Sans, "Helvetica"),
        new(StandardTextBoxFonts.Serif, "Times"),
        new(StandardTextBoxFonts.Mono, "Courier"),
    ];

    /// <summary>What the font box binds: the choice whose face is current.</summary>
    public FontChoice? SelectedTextFont
    {
        get => TextFontChoices.FirstOrDefault(c => c.PostScriptName == TextFont);
        set
        {
            if (value is null || value.PostScriptName == TextFont)
                return;
            TextFont = value.PostScriptName;
            OnPropertyChanged();
        }
    }
    public IReadOnlyList<double> TextSizes { get; } = [8, 9, 10, 11, 12, 14, 16, 18, 24];

    [RelayCommand(CanExecute = nameof(CanAddText))]
    private void ToggleAddText() => SetMode(Mode == PageMode.AddText ? PageMode.Select : PageMode.AddText);

    [RelayCommand(CanExecute = nameof(CanEditContent))]
    private void ToggleWhiteout() => SetMode(Mode == PageMode.Whiteout ? PageMode.Select : PageMode.Whiteout);

    [RelayCommand(CanExecute = nameof(CanEditContent))]
    private void ToggleRedact() => SetMode(Mode == PageMode.Redact ? PageMode.Select : PageMode.Redact);

    private void SetMode(PageMode mode)
    {
        // Added text needs a text-box capability, covering the document's own content
        // needs modify (#131).
        var allowed = mode switch
        {
            PageMode.AddText => Capabilities.CanAddText,
            PageMode.Whiteout => Capabilities.CanEditContent,
            // Redaction changes the document, so it needs modify (ADR-004 decision 2).
            PageMode.Redact => Capabilities.CanEditContent,
            _ => true,
        };
        if (!allowed)
        {
            Status = Strings.ActionRestricted;
            return;
        }

        Selection = null;
        // Arming one mode disarms everything else, signature placement included.
        if (PendingSignature is not null && mode != PageMode.Select)
            PendingSignature = null;

        Mode = mode;
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(IsModeActive));
        if (mode == PageMode.Select)
            Status = Strings.Ready;
        else
            PreparePageCheck(CurrentPage - 1); // a tool armed: its page's #139 check starts now (#145)
    }

    public void CancelModes()
    {
        PendingSignature = null;
        SetMode(PageMode.Select);
    }

    /// <summary>
    /// Edits a line of the document's own body text (SDD §3.1 — F1, the feature the
    /// product is named for). Tiered: the document's own font is used where it can
    /// render the new text, a similar standard font where it cannot, and scanned
    /// text is refused outright rather than silently mangled.
    /// </summary>
    public void EditLine(int pageIndex, PdfTextLine line, string newText) =>
        Start(() => EditLineAsync(pageIndex, line, newText));

    private async Task EditLineAsync(int pageIndex, PdfTextLine line, string newText)
    {
        if (_document is not { } document || newText == line.Text)
            return;

        var operation = new LineEditOperation(document, pageIndex, line, newText);
        // Not routed through Apply, so gated here as well (#131).
        if (!Capabilities.Allows(operation))
        {
            Status = Strings.ActionRestricted;
            return;
        }
        if (Busy.IsBusy)
            return;

        using (Busy.Begin(Strings.BusyApplying, scope: BusyScope.Page, pageIndex: pageIndex, area: line.Bounds))
        {
            try
            {
                await OffUiThread(() => _undoStack.Do(operation));
            }
            catch (TextEditException ex)
            {
                // These are the two honest refusals, and the wording matters more than
                // the exception: the person is holding a form, not a stack trace.
                Status = ex.Reason switch
                {
                    TextEditFailure.NotExtractable => Strings.TextIsScanned,
                    TextEditFailure.LayoutWouldChange => LayoutRefusalText(ex.Layout),
                    _ => Strings.TextFontCannotWrite,
                };
                return;
            }
            catch (Exception ex)
            {
                Status = Strings.WithDetail(Strings.ChangeFailed, ex.Message);
                return;
            }
        }
        if (!ReferenceEquals(document, _document))
            return;

        // Journalled here rather than via Apply, which EditLine does not route
        // through because it needs its own try/catch — but the recovery journal
        // must not have a hole where F1 should be (#61). Inside the success path,
        // so a refused edit records nothing, which is right: nothing happened.
        _journal.Record(operation.ToJournalEntry(inverse: false));

        var note = operation.LastOutcome == TextEditOutcome.EditedWithSubstitutedFont
            ? Strings.TextEditedSubstitutedFont
            : Strings.TextEdited;
        AfterEdit(pageIndex, note);
    }

    /// <summary>Fills an AcroForm text field (SDD §3.1, the form path).</summary>
    public void SetFieldValue(int pageIndex, PdfFormField field, string value)
    {
        if (_document is null || value == field.Value)
            return;

        Apply(new FormTextEditOperation(_document, pageIndex, field, value),
              string.IsNullOrEmpty(value) ? Strings.FieldCleared : Strings.FieldFilled);
    }

    /// <summary>
    /// Removes a line of the document's own text (SDD §3.1). Separate from an edit
    /// to empty string: Core detaches the runs and keeps them alive, so undo
    /// restores the original fragmentation and fonts byte-identical rather than
    /// leaving an empty run behind.
    /// </summary>
    public void DeleteLine(int pageIndex, PdfTextLine line)
    {
        if (_document is null)
            return;

        try
        {
            Apply(new DeleteLineOperation(_document, pageIndex, line), Strings.TextDeleted);
        }
        catch (TextEditException ex) when (ex.Reason == TextEditFailure.LayoutWouldChange)
        {
            Status = LayoutRefusalText(ex.Layout);
        }
    }

    /// <summary>
    /// Whether every run of <paramref name="line"/> can be changed without PDFium
    /// disturbing the rest of the page (#118). Asked before the editor opens; when it
    /// cannot, the status says why and no editor appears.
    /// </summary>
    /// <remarks>
    /// The check is a dry run of the rewrite, about three seconds on a heavy page, so it runs off
    /// the UI thread under a spinner on the line, and clicks while it runs are ignored (#145).
    /// </remarks>
    private async Task BeginLineEditAsync(int pageIndex, PdfTextLine line)
    {
        if (_document is not { } document || Busy.IsBusy)
            return;

        (bool Editable, LayoutVerdict? Refusal) judged;
        using (Busy.Begin(Strings.BusyCheckingPage, scope: BusyScope.Page, pageIndex: pageIndex, area: line.Bounds))
        {
            try
            {
                judged = await OffUiThread(() => JudgeLine(document, pageIndex, line));
            }
            catch (Exception ex)
            {
                Status = Strings.WithDetail(Strings.ChangeFailed, ex.Message);
                return;
            }
        }
        if (!ReferenceEquals(document, _document))
            return;
        if (!judged.Editable)
        {
            Status = LayoutRefusalText(judged.Refusal);
            return;
        }
        EditLineRequested?.Invoke(pageIndex, line);
    }

    /// <summary>Off the UI thread: whether every run of the line can be rewritten, and the first refusal.</summary>
    private static (bool Editable, LayoutVerdict? Refusal) JudgeLine(IPdfDocument document, int pageIndex, PdfTextLine line)
    {
        using var page = document.GetPage(pageIndex);
        foreach (var run in line.Runs)
        {
            if (run.TextBoxId is not null)
                continue;
            // A run that is no longer text is refused, as IsTextEditable always has.
            var verdict = page.GetLayoutVerdict(run.ObjectIndex);
            if (verdict is { Editable: true })
                continue;
            return (false, verdict);
        }
        return (true, null);
    }

    /// <summary>The status for a line the layout guard refused (#118), by its cause (#128).</summary>
    private static string LayoutRefusalText(LayoutVerdict? verdict) => verdict switch
    {
        { TextWouldMove: true } => Strings.TextLayoutTextWouldMove,
        { Cause: LayoutCause.Render } => Strings.TextLayoutRenderWouldChange,
        _ => Strings.TextLayoutWouldChange,
    };

    /// <summary>
    /// Rewrites an added text box in a new face, size or wording (#43, SDD §6.2
    /// contract 4). The box keeps its id across the change, which is what lets the
    /// mobile apps still address it afterwards.
    /// </summary>
    public void RestyleTextBox(int pageIndex, PdfTextRun box, string newText, string fontName, double fontSize)
    {
        if (_document is null)
            return;

        if (!StandardTextBoxFonts.IsSupported(fontName))
        {
            // The engine rejects anything outside the three, and it should: a
            // substituted face would silently break the cross-platform contract.
            Status = Strings.FontNotAvailable(fontName);
            return;
        }

        Apply(new RestyleTextBoxOperation(_document, pageIndex, box.ObjectIndex, box,
                                          newText, fontName, fontSize),
              Strings.TextUpdated,
              applied: () => ReselectTextBox(pageIndex, box));
    }

    /// <summary>Moves an added text box (SDD §3.3 drag/nudge).</summary>
    public void MoveTextBox(int pageIndex, PdfTextRun box, PdfRect newBounds, Action? cancelled = null)
    {
        if (_document is null || newBounds == box.Bounds)
            return;

        Apply(new MoveTextBoxOperation(_document, pageIndex, box.ObjectIndex, box.Bounds, newBounds),
              Strings.TextMoved, cancelled);
    }

    /// <summary>Moves or resizes a placed signature (SDD §3.3).</summary>
    public void MoveSignature(int pageIndex, string annotationId, PdfRect oldBounds, PdfRect newBounds)
    {
        if (_document is null || newBounds == oldBounds)
            return;

        Apply(new MoveSignatureOperation(_document, pageIndex, annotationId, oldBounds, newBounds),
              Strings.SignatureMoved);
    }

    /// <summary>
    /// Moves or resizes a redaction mark (#329). A mark's id survives the move, so the
    /// selection still points at it afterwards — and nothing here reaches the file.
    /// </summary>
    public void MoveRedactionMark(int pageIndex, int markId, PdfRect oldBounds, PdfRect newBounds)
    {
        if (_document is null || newBounds == oldBounds)
            return;

        Apply(new MoveRedactionMarkOperation(_document, pageIndex, markId, oldBounds, newBounds),
              Strings.RedactMarkMoved);
    }

    /// <summary>Adds a text box with the current face and size (SDD §3.1).</summary>
    public void AddTextBox(int pageIndex, PdfPoint topLeft, string text)
    {
        if (_document is null || string.IsNullOrWhiteSpace(text))
            return;

        Apply(new AddTextBoxOperation(_document, pageIndex, text, TextSize, topLeft, TextFont),
              Strings.TextAdded);
        SetMode(PageMode.Select);
    }

    /// <summary>Covers page content with a white rectangle (SDD §3.3).</summary>
    public void AddWhiteout(int pageIndex, PdfRect bounds)
    {
        // A stray click while the tool is armed should not stamp an invisible speck.
        if (_document is null || bounds.Width < 2 || bounds.Height < 2)
        {
            SetMode(PageMode.Select);
            return;
        }

        Apply(new AddWhiteoutOperation(_document, pageIndex, bounds), Strings.Covered);
        SetMode(PageMode.Select);
    }

    // --- Redaction (SDD §3.8 / F7, #173) ---

    /// <summary>
    /// How many areas are marked across the document. The save path asks this before it
    /// offers the confirmation, and the toolbar shows it so a mark is never forgotten.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRedactionMarks))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearRedactionMarksCommand))]
    private int _redactionMarkCount;

    public bool HasRedactionMarks => RedactionMarkCount > 0;

    /// <summary>
    /// Marks the dragged area (#173). Nothing is removed: a mark is a mark until the
    /// document is saved, and it is never written to the file, so this is free and
    /// completely undoable.
    ///
    /// One gesture is one undo step however many marks it made (#329): a drag across six
    /// lines is six core marks and one press of Undo.
    /// </summary>
    public void AddRedactionMark(int pageIndex, PdfRect bounds)
    {
        // A stray click while the tool is armed should not mark an invisible speck.
        if (_document is not { } document || bounds.Width < 2 || bounds.Height < 2)
        {
            SetMode(PageMode.Select);
            return;
        }
        Start(() => PlaceRedactionMarkAsync(document, pageIndex, bounds));
    }

    /// <summary>
    /// Places a gesture's marks and records them as one step (#329). The engine call that
    /// answers "did this drag cover text?" *makes* the marks as it answers, so the operation
    /// is built from what came back and recorded already-applied rather than applied on top
    /// of itself.
    ///
    /// Nothing here reaches the file: a mark is never written (ADR-005 decision 1), so there
    /// is no unsaved dot, no recovery entry and no re-render — the overlay redraw is the whole
    /// visible change. Undo is still how a mis-drag is taken back.
    /// </summary>
    private async Task PlaceRedactionMarkAsync(IPdfDocument document, int pageIndex, PdfRect bounds)
    {
        if (!Capabilities.CanEditContent)
        {
            Status = Strings.ActionRestricted;
            SetMode(PageMode.Select);
            return;
        }
        if (Busy.IsBusy)
        {
            SetMode(PageMode.Select);
            return;
        }

        IPageEditOperation? op;
        using (Busy.Begin(Strings.BusyApplying, scope: BusyScope.Page, pageIndex: pageIndex))
        {
            try
            {
                op = await OffUiThread(() => MarkForRedactionOperation.Place(document, pageIndex, bounds));
            }
            catch (Exception ex)
            {
                Status = Strings.WithDetail(Strings.ChangeFailed, ex.Message);
                SetMode(PageMode.Select);
                return;
            }
        }
        if (!ReferenceEquals(document, _document))
            return;

        if (op is not null)
        {
            _undoStack.Record(op);
            RaiseUndoRedo();
        }
        RefreshRedactionMarks();
        SetMode(PageMode.Select);
        // Last, and it has to be: SetMode(Select) puts "Ready." in the status line, so
        // saying this before it meant the mark was never announced at all — not in the
        // status bar and not to a screen reader, which is the only confirmation there
        // is that a faint translucent band went where it was meant to (#173).
        Status = Strings.RedactMarkPlaced;
    }

    /// <summary>Re-reads the marks from the core onto every loaded page.</summary>
    private void RefreshRedactionMarks()
    {
        if (_document is null)
        {
            RedactionMarkCount = 0;
            Selection = null;
            return;
        }
        RedactionMarkCount = _document.RedactionMarkCount;

        // The core is the truth for a mark's rectangle, and the only thing that knows which
        // ids still exist (#329). A chrome left on a mark that has gone would let the ✕ remove
        // nothing — and its undo would re-mark from a rectangle nobody asked for — while one
        // left on a mark that has moved would record the wrong "from" for the next drag. So a
        // selection whose mark is gone lets go here, and one whose mark has moved follows it.
        // Both happen on the same paths: undoing a clear, or undoing a drag.
        var selected = SelectedMarkId();
        if (selected is { } current)
        {
            if (current.PageIndex < 0 || current.PageIndex >= _document.PageCount)
            {
                Selection = null;
            }
            else
            {
                using var page = _document.GetPage(current.PageIndex);
                RedactionMark? live = null;
                foreach (var mark in page.GetRedactionMarks())
                {
                    if (mark.MarkId == current.MarkId)
                    {
                        live = mark;
                        break;
                    }
                }
                if (live is not { } found)
                    Selection = null;
                else if (Selection is { } held && held.Bounds != found.Bounds)
                    Selection = held with { Bounds = found.Bounds };
            }
        }

        var keep = SelectedMarkId();
        foreach (var page in Pages)
        {
            if (page.Image is null && page.Highlights.Count == 0 && RedactionMarkCount == 0)
                continue;
            if (page.Index < 0 || page.Index >= _document.PageCount)
                continue;
            using var handle = _document.GetPage(page.Index);
            page.SetRedactionMarks(handle.GetRedactionMarks(),
                keep is { } sel && sel.PageIndex == page.Index ? sel.MarkId : -1);
        }
    }

    /// <summary>The selected mark, from the one selection the chrome and the ✕ both read.</summary>
    private (int PageIndex, int MarkId)? SelectedMarkId() =>
        Selection is { Kind: SelectionKind.RedactionMark, MarkId: >= 0 } mark
            ? (mark.PageIndex, mark.MarkId)
            : null;

    /// <summary>The mark under the point, if any — how a click selects one to move or remove.</summary>
    public bool SelectRedactionMarkAt(int pageIndex, PdfPoint point)
    {
        if (_document is null || pageIndex < 0 || pageIndex >= _document.PageCount)
            return false;
        using var page = _document.GetPage(pageIndex);
        foreach (var mark in page.GetRedactionMarks())
        {
            if (point.X < mark.Bounds.X || point.X > mark.Bounds.X + mark.Bounds.Width ||
                point.Y < mark.Bounds.Y || point.Y > mark.Bounds.Y + mark.Bounds.Height)
            {
                continue;
            }
            Select(new PageSelection(pageIndex, SelectionKind.RedactionMark, mark.Bounds,
                                     MarkId: mark.MarkId));
            RefreshRedactionMarks();
            return true;
        }
        return false;
    }

    /// <summary>Removes the selected mark. Nothing was removed from the document, so this
    /// is a plain undoable edit — and Delete goes through the one removal path like every
    /// other kind of selection.</summary>
    public bool RemoveSelectedRedactionMark()
    {
        if (Selection is not { Kind: SelectionKind.RedactionMark })
            return false;
        DeleteSelection();
        return true;
    }

    /// <summary>
    /// Drops every mark on the document as **one** undo step (#329) — a person who says
    /// "clear all marks" means one action, not one per mark and not one per page.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClearRedactionMarks))]
    private void ClearRedactionMarks()
    {
        // Capture reads every mark's rectangle first, because only the core knows them and
        // the clear is about to take them away: an ordinary Apply then runs the clear, and
        // the operation that is recorded can put them all back.
        if (_document is not { } document || ClearRedactionMarksOperation.Capture(document, 0) is not { } op)
            return;
        Apply(op, Strings.RedactMarksCleared);
    }

    private bool CanClearRedactionMarks() => HasRedactionMarks && !Busy.IsBusy;

    /// <summary>What the summary says after a redaction, from the report's counts.</summary>
    internal static string DescribeRedaction(RedactionCounts counts)
    {
        var removed = RedactionSummary.Removed(counts,
            (kind, n) => kind switch
            {
                RedactionSummary.RedactionKind.Characters => Strings.RedactedCharacters(n),
                RedactionSummary.RedactionKind.Images => Strings.RedactedImages(n),
                RedactionSummary.RedactionKind.FormFields => Strings.RedactedFormFields(n),
                _ => Strings.RedactedAnnotations(n),
            },
            kind => kind switch
            {
                RedactionSummary.RedactionKind.Characters => Strings.RedactedCharactersOne,
                RedactionSummary.RedactionKind.Images => Strings.RedactedImagesOne,
                RedactionSummary.RedactionKind.FormFields => Strings.RedactedFormFieldsOne,
                _ => Strings.RedactedAnnotationsOne,
            },
            Strings.RedactedNothing);
        return counts.Areas == 1 ? Strings.RedactSummaryOne(removed) : Strings.RedactSummaryMany(counts.Areas, removed);
    }

    /// <summary>
    /// Applies every mark, and says what happened. Returns true when the document was
    /// redacted and may be saved; false when the redaction refused, in which case NOTHING
    /// was removed, the marks are still there, and the caller must not save.
    /// </summary>
    public async Task<bool> ApplyRedactionsAsync()
    {
        if (_document is not { } document || !HasRedactionMarks)
            return true;
        try
        {
            var report = await OffUiThread(document.ApplyRedactions);
            if (!report.Applied)
            {
                var refusal = report.Refusals.Count > 0 ? report.Refusals[0] : default;
                Status = Strings.RedactRefusedTitle + " " +
                         Strings.RedactRefusedBody(refusal.PageIndex + 1) + " " + DescribeRefusal(refusal);
                RefreshRedactionMarks();
                return false;
            }
            // The removed content is gone, and so is every way back to it. The undo stack
            // held the very objects the redaction freed (#173) — the core discards its
            // handles, so an undo could not put them back even if we kept it — and the
            // journal starts again, without the entries that led here.
            _undoStack.Clear();
            RaiseUndoRedo();
            if (DocumentPath is { Length: > 0 } path)
                _journal.MarkSaved(path);
            // Every mark is gone, and so is whatever was selected — a chrome around a mark
            // that no longer exists would let a person drag nothing (#329).
            Selection = null;
            RefreshRedactionMarks();
            Status = DescribeRedaction(report.Counts);
            return true;
        }
        catch (DocumentRestrictedException)
        {
            Status = Strings.RedactNeedsPermission;
            return false;
        }
        catch (RedactionFailedException)
        {
            // The rehearsal says this cannot happen; if it does the document can never be
            // saved, so the only honest thing is to say so and stop.
            Status = Strings.RedactFailed;
            return false;
        }
    }

    /// <summary>The name Save as a copy offers: "lease.pdf" becomes "lease-redacted.pdf".</summary>
    public static string SuggestRedactedFileName(string original)
    {
        var directory = Path.GetDirectoryName(original) ?? "";
        var name = Path.GetFileNameWithoutExtension(original);
        var extension = Path.GetExtension(original);
        var suggestion = name + Strings.RedactedFileSuffix + extension;
        return directory.Length == 0 ? suggestion : Path.Combine(directory, suggestion);
    }

    /// <summary>Why a redaction refused, in the user's words rather than the engine's.</summary>
    internal static string DescribeRefusal(RedactionRefusal refusal) => refusal.Reason switch
    {
        RedactionRefusalReason.Type3Font or RedactionRefusalReason.FontCannotRedraw => Strings.RedactRefusedFont,
        RedactionRefusalReason.FormXObject => Strings.RedactRefusedShared,
        RedactionRefusalReason.LayoutGuard => Strings.RedactRefusedLayout,
        _ => Strings.RedactRefusedOther,
    };

    // --- Find in document (SDD §3.6 / F6) ---

    /// <summary>One hit: which page it is on and the rectangles covering it.</summary>
    private sealed record Match(int PageIndex, IReadOnlyList<PdfRect> Rects);

    private readonly List<Match> _matches = [];

    [ObservableProperty]
    private string _searchTerm = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchSummary))]
    private int _currentMatchIndex = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchSummary))]
    private int _matchCount;

    [ObservableProperty]
    private bool _isFindOpen;

    /// <summary>
    /// What the find bar reads. Deliberately words rather than a bare "0/0": an empty
    /// box is not the same as a term that genuinely is not in the document, and the
    /// person filling in a form should not have to infer which they are looking at.
    /// </summary>
    public string MatchSummary => string.IsNullOrEmpty(SearchTerm)
        ? ""
        : MatchCount == 0
            ? Strings.NotFound
            : Strings.MatchOf(CurrentMatchIndex + 1, MatchCount);

    /// <summary>Raised when the view should open an editor over a line of body text.</summary>
    public event Action<int, PdfTextLine>? EditLineRequested;

    /// <summary>Raised when the view should open an editor over an AcroForm text field.</summary>
    public event Action<int, PdfFormField>? EditFieldRequested;

    /// <summary>Raised when the view should bring a page rectangle into view.</summary>
    public event Action<int, PdfRect>? ScrollToRequested;

    /// <summary>Searches and returns once the matches are in: for the self-test and the capture runs.</summary>
    public void Search(string term) => RunSynchronously(() => SearchAsync(term));

    /// <summary>A newer search, a closed find bar or another document supersedes a search still running.</summary>
    private int _searchGeneration;

    /// <summary>
    /// Search walks every page, which on a long document takes seconds: off the UI thread, with
    /// "Searching…" (#145). It disables nothing — typing on supersedes it.
    /// </summary>
    public async Task SearchAsync(string term)
    {
        SearchTerm = term;
        var generation = ++_searchGeneration;
        var found = new List<Match>();

        if (_document is { } document && !string.IsNullOrWhiteSpace(term))
        {
            var pageCount = Pages.Count;
            using (Busy.Begin(Strings.BusySearching, blocksEditing: false))
            {
                try
                {
                    found = await OffUiThread(() =>
                    {
                        var hits = new List<Match>();
                        for (var i = 0; i < pageCount && generation == _searchGeneration; i++)
                        {
                            using var page = document.GetPage(i);
                            foreach (var hit in page.FindText(term))
                                hits.Add(new Match(i, hit.Rects));
                        }
                        return hits;
                    });
                }
                catch (Exception) when (!ReferenceEquals(document, _document) || generation != _searchGeneration)
                {
                    return; // closed or superseded under it
                }
            }
            if (generation != _searchGeneration || !ReferenceEquals(document, _document))
                return;
        }

        _matches.Clear();
        _matches.AddRange(found);
        CurrentMatchIndex = -1;
        MatchCount = _matches.Count;
        ApplyHighlights();

        if (MatchCount > 0)
            GoToMatch(0);
    }

    [RelayCommand]
    private void FindNext()
    {
        if (MatchCount == 0)
            return;
        GoToMatch((CurrentMatchIndex + 1) % MatchCount);
    }

    [RelayCommand]
    private void FindPrevious()
    {
        if (MatchCount == 0)
            return;
        GoToMatch((CurrentMatchIndex - 1 + MatchCount) % MatchCount);
    }

    public void CloseFind()
    {
        _searchGeneration++;
        IsFindOpen = false;
        SearchTerm = "";
        _matches.Clear();
        MatchCount = 0;
        CurrentMatchIndex = -1;
        ApplyHighlights();
    }

    private void GoToMatch(int index)
    {
        CurrentMatchIndex = index;
        ApplyHighlights();

        var match = _matches[index];
        if (match.Rects.Count > 0)
            ScrollToRequested?.Invoke(match.PageIndex, match.Rects[0]);
    }

    private void ApplyHighlights()
    {
        foreach (var page in Pages)
        {
            var rects = new List<PdfRect>();
            var current = -1;
            for (var i = 0; i < _matches.Count; i++)
            {
                if (_matches[i].PageIndex != page.Index)
                    continue;
                if (i == CurrentMatchIndex)
                    current = rects.Count;
                rects.AddRange(_matches[i].Rects);
            }
            page.SetMatches(rects, current);
        }
    }

    // --- Signatures (SDD §3.3) ---

    /// <summary>The stored signature library, newest first.</summary>
    public ObservableCollection<SignatureItem> Signatures { get; } = [];

    /// <summary>
    /// The signature awaiting a click on the page. While this is set the next page
    /// click places it rather than routing to a checkbox — the same modal placement
    /// the WinUI app uses, and the reason HandlePageClick checks it first.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlacingSignature))]
    [NotifyPropertyChangedFor(nameof(ModeHint))]
    [NotifyPropertyChangedFor(nameof(IsModeActive))]
    private SignatureEntry? _pendingSignature;

    public bool IsPlacingSignature => PendingSignature is not null;

    public void LoadSignatures()
    {
        foreach (var existing in Signatures)
            existing.Thumbnail?.Dispose();
        Signatures.Clear();

        foreach (var entry in _signatures.All.OrderByDescending(e => e.CreatedUtc))
            Signatures.Add(new SignatureItem(entry, Rendering.SignatureImages.LoadThumbnail(entry.PngPath)) { Owner = this });

        HasSignatures = Signatures.Count > 0;
    }

    // The card and its menu (#100). Place is the card's own click; Rename and Delete
    // need a dialog, which is the view's business, so they are raised as requests
    // the way a password prompt is.

    [RelayCommand]
    private void PlaceSignature(SignatureItem item) => BeginPlacing(item);

    /// <summary>Raised when the user asks to rename a signature; the view shows the prompt.</summary>
    public event Action<SignatureItem>? RenameSignatureRequested;

    /// <summary>Raised when the user asks to delete a signature; the view asks once, then calls <see cref="RemoveSignature"/>.</summary>
    public event Action<SignatureItem>? DeleteSignatureRequested;

    [RelayCommand]
    private void RequestRenameSignature(SignatureItem item) => RenameSignatureRequested?.Invoke(item);

    [RelayCommand]
    private void RequestDeleteSignature(SignatureItem item) => DeleteSignatureRequested?.Invoke(item);

    public void RenameSignature(Guid id, string newName)
    {
        var trimmed = newName.Trim();
        if (trimmed.Length == 0)
            return;
        _signatures.Rename(id, trimmed);
        LoadSignatures();
        Status = Strings.SignatureRenamed(trimmed);
    }

    [ObservableProperty]
    private bool _hasSignatures;

    /// <summary>
    /// Adds a signature from a photograph or scan (SDD §3.3). The cleanup is the
    /// §6.2 contract-3 pipeline: near-white becomes transparent, then trim to the
    /// ink — but only when the image does not already carry transparency. A drawn
    /// PNG that arrives here would be damaged by white-removal, since its
    /// background is already nothing.
    /// </summary>
    public SignatureEntry AddSignatureFromImage(string name, SignatureBitmap image, Func<SignatureBitmap, byte[]> encodePng)
    {
        var cleaned = SignatureCleanup.HasTransparency(image.Bgra)
            ? SignatureCleanup.TrimToInk(image.Bgra, image.Width, image.Height)
            : SignatureCleanup.Clean(image);

        return AddSignature(name, encodePng(cleaned));
    }

    public SignatureEntry AddSignature(string name, byte[] png)
    {
        var entry = _signatures.Add(name, png);
        LoadSignatures();
        Status = Strings.SignatureSavedClickToPlace(entry.Name);
        return entry;
    }

    public void RemoveSignature(Guid id)
    {
        _signatures.Remove(id);
        LoadSignatures();
        Status = Strings.SignatureDeleted;
    }

    public void BeginPlacing(SignatureItem item) => BeginPlacing(item.Entry);

    public void BeginPlacing(SignatureEntry entry)
    {
        // A signature is an annotation (#131). The library itself stays usable.
        if (!Capabilities.CanSign)
        {
            Status = Strings.ActionRestricted;
            return;
        }
        PendingSignature = entry;
        Status = Strings.ClickWhereSignatureGoes(entry.Name);
    }

    public void CancelPlacing()
    {
        if (PendingSignature is null)
            return;
        PendingSignature = null;
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(IsModeActive));
        Status = Strings.PlacementCancelled;
    }

    /// <summary>
    /// Places the pending signature centred on the click, 180pt wide with the aspect
    /// ratio preserved and clamped inside the page — the same geometry the WinUI app
    /// uses, so a document signed on one desktop looks the same on the other.
    /// </summary>
    private async Task PlacePendingSignatureAsync(int pageIndex, PdfPoint point, SignatureEntry pending)
    {
        PendingSignature = null;

        SignatureBitmap image;
        try
        {
            // Decoding the PNG is the slow half of placing; the stamp itself goes through Apply (#145).
            image = await OffUiThread(() => Rendering.SignatureImages.LoadBgra(pending.PngPath));
        }
        catch (Exception ex)
        {
            Status = Strings.WithDetail(Strings.CouldNotReadSignature, ex.Message);
            return;
        }

        PlaceSignature(pageIndex, point, image, Strings.PlacedSignature(pending.Name));
    }

    /// <summary>
    /// The placement geometry, separated from loading a PNG so it can be exercised
    /// without a file — and without an initialised graphics stack — by --self-test.
    /// </summary>
    internal void PlaceSignature(int pageIndex, PdfPoint point, SignatureBitmap image, string doneMessage)
    {
        if (_document is null)
            return;

        const double defaultWidthPoints = 180;
        var width = defaultWidthPoints;
        var height = width * image.Height / image.Width;

        var page = Pages[pageIndex];
        var x = Math.Clamp(point.X - (width / 2), 0, Math.Max(0, page.PointWidth - width));
        var y = Math.Clamp(point.Y - (height / 2), 0, Math.Max(0, page.PointHeight - height));

        Apply(new AddSignatureOperation(
                  _document, pageIndex, image.Bgra, image.Width, image.Height,
                  new PdfRect(x, y, width, height)),
              doneMessage);
    }

    // --- Saving (SDD §3.4) ---

    /// <summary>
    /// Writes the document to a stream the host already holds open.
    ///
    /// Under the macOS App Sandbox this is the only way to save: the app is granted
    /// the file the user picked, not its folder, so AtomicFileWriter's write-a-sibling
    /// -and-swap protocol is denied outright. <see cref="StagedStreamWriter"/> keeps
    /// as much of that protocol's safety as the sandbox allows — the serialise happens
    /// against a temp file, and the user's bytes are only touched once it has
    /// succeeded.
    /// </summary>
    public void SaveTo(Stream destination) =>
        RunSynchronously(() => SaveThroughAsync(() => Task.FromResult(destination), ownsDestination: false));

    /// <summary>
    /// Save under the sandbox, where the destination is a stream the host opens (D2, #145).
    /// Opening the person's file for writing truncates it, so <paramref name="openDestination"/>
    /// is called only once the bytes have been built and verified in memory: a failed flatten,
    /// serialise or read-back leaves the original untouched. False when nothing was written.
    /// </summary>
    public Task<bool> SaveThroughAsync(Func<Task<Stream>> openDestination, bool ownsDestination = true) =>
        SaveCoreAsync(openDestination, ownsDestination, atomicPath: null, unchanged =>
        {
            if (unchanged && DocumentPath is { } saved)
                _journal.MarkSaved(saved);
            Status = Strings.SavedFile(DocumentName);
        }, inPlacePath: DocumentPath);

    /// <summary>
    /// Writes to a real path, which Windows can do with the stronger guarantee:
    /// AtomicFileWriter's swap means the destination is never seen half-written
    /// (SDD §3.4). Used when the host has a usable local path and no sandbox in the
    /// way.
    /// </summary>
    public void SaveToPath(string path) => RunSynchronously(() => SaveToPathAsync(path));

    /// <inheritdoc cref="SaveToPath"/>
    public Task<bool> SaveToPathAsync(string path) =>
        SaveCoreAsync(openDestination: null, ownsDestination: false, atomicPath: path, unchanged =>
        {
            if (unchanged)
                _journal.MarkSaved(path);
            DocumentPath = path;
            DocumentName = Path.GetFileName(path);
            Status = Strings.SavedFile(DocumentName);
        });

    /// <summary>For the self-test (D2): throws inside the save, where a failed flatten or read-back would.</summary>
    internal Action? FailSaveForTest { get; set; }

    /// <summary>
    /// Every save: off the UI thread, "Saving…" then "Checking the saved file…", with editing,
    /// Close and file commands waiting (#145). The document is marked saved only if nothing
    /// changed while the save ran (D3). Failures land in the status line.
    /// </summary>
    private async Task<bool> SaveCoreAsync(Func<Task<Stream>>? openDestination, bool ownsDestination, string? atomicPath,
                                           Action<bool> saved, string? inPlacePath = null)
    {
        if (_document is not { } document || Busy.IsBusy)
            return false;

        using var busy = Busy.Begin(Strings.BusySaving);
        var editsBefore = _editCount;
        void Stage(VerifiedSave.SaveStage stage) =>
            busy.SetLabel(stage == VerifiedSave.SaveStage.Verifying ? Strings.BusyCheckingSavedFile : Strings.BusySaving);
        try
        {
            if (FlattenOnSave)
                await FlattenOpenDocumentAsync(document);

            if (atomicPath is not null)
            {
                // Verified rather than merely staged (#56): the bytes are reopened with the
                // engine before the user's file is touched.
                await OffUiThread(() =>
                {
                    FailSaveForTest?.Invoke();
                    VerifiedSave.ToPath(_engine, document, atomicPath, Stage);
                });
            }
            else
            {
                // Staged in a temporary file and verified, not built in memory (#147).
                using var staged = await OffUiThread(() =>
                {
                    FailSaveForTest?.Invoke();
                    return VerifiedSave.ToStagedFile(_engine, document, Stage);
                });
                busy.SetLabel(Strings.BusySaving);
                // The write below is in place, so the document moves off its file first if that
                // is the file being written (#147).
                await KeepOffFileAsync(document, inPlacePath);
                // Only now is the person's file opened, and so truncated.
                var destination = await openDestination!();
                try
                {
                    await OffUiThread(() => staged.WriteOver(destination));
                }
                finally
                {
                    if (ownsDestination)
                        await destination.DisposeAsync();
                }
            }
        }
        catch (Exception ex)
        {
            ReportSaveFailure(ex);
            return false;
        }

        if (!ReferenceEquals(document, _document))
            return true;
        var unchanged = _editCount == editsBefore;
        saved(unchanged);
        if (unchanged)
            IsDirty = false;
        return true;
    }

    /// <summary>
    /// Bakes annotations into page content (SDD §3.3), and cleans up after itself.
    ///
    /// This mutates the document that is still open. FPDFPage_Flatten rewrites
    /// content streams in place, and there is no way to flatten "only the bytes on
    /// their way out" while sharing one document handle — an earlier comment here
    /// claimed exactly that, and the gap between the claim and the code was the bug
    /// (#60). Every stamp id on the undo stack pointed at an annotation that no
    /// longer existed, so the next Undo threw KeyNotFoundException, and the page on
    /// screen kept showing stamps the document no longer had.
    ///
    /// So the undo history is dropped — it genuinely cannot be replayed against a
    /// flattened document — the selection is cleared for the same reason, and every
    /// realised page is re-rastered. The WinUI app's OnDocumentFlattenedAsync does
    /// the same thing for the same reason.
    /// </summary>
    private async Task FlattenOpenDocumentAsync(IPdfDocument document)
    {
        await OffUiThread(document.FlattenAllPages);
        if (!ReferenceEquals(document, _document))
            return;

        _undoStack.Clear();
        Selection = null;
        RaiseUndoRedo();

        foreach (var page in Pages)
            if (page.IsRealised)
                page.Rerender(DpiScale);
    }

    /// <summary>
    /// Marks count, even though they are not a change to the document (#173).
    ///
    /// A mark deliberately leaves the document clean — nothing is written until a save
    /// is confirmed — so IsDirty alone left Save greyed out with areas marked, Cmd+S
    /// doing nothing, and the confirmation reachable only through Save As. Windows
    /// raises it from Ctrl+S, and the person who marks something and presses Cmd+S
    /// deserves the same answer.
    /// </summary>
    private bool CanSave() => IsDocumentOpen && (IsDirty || HasRedactionMarks) && !Busy.IsBusy;

    /// <summary>Raised when the view should perform a save; the view owns the file handle.</summary>
    public event Action? SaveRequested;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => SaveRequested?.Invoke();

    /// <summary>
    /// Saves a copy and adopts it, in one operation (#68).
    ///
    /// One operation rather than a save followed by an adopt, because the journal
    /// must be marked against the file the bytes actually went to. Splitting them
    /// meant SaveTo marked the OLD path as saved and the adopt then changed only
    /// the display name — so DocumentPath kept pointing at the file the user opened
    /// from. Shrink reopened that stale original and produced a "smaller copy"
    /// missing the edits that had just been saved, with no error to show for it.
    /// </summary>
    /// <param name="newPath">
    /// Where the copy landed, or null when the platform gives back no usable local
    /// path. Null is recorded as null rather than left stale: a Shrink that refuses
    /// is better than one that silently reads the wrong document.
    /// </param>
    public void SaveAsTo(Stream destination, string? newPath, string fileName) =>
        RunSynchronously(() => SaveAsThroughAsync(() => Task.FromResult(destination), newPath, fileName, ownsDestination: false));

    /// <inheritdoc cref="SaveAsTo"/>
    /// <remarks>The destination is opened only once the verified bytes exist (D2, #145).</remarks>
    public Task<bool> SaveAsThroughAsync(Func<Task<Stream>> openDestination, string? newPath, string fileName,
                                         bool ownsDestination = true) =>
        SaveCoreAsync(openDestination, ownsDestination, atomicPath: null, unchanged =>
        {
            DocumentPath = newPath;
            DocumentName = fileName;
            if (unchanged && newPath is not null)
                _journal.MarkSaved(newPath);
            Status = newPath is null
                ? Strings.SavedCannotShrink(fileName)
                : Strings.SavedFile(fileName);
        }, inPlacePath: newPath);

    /// <summary><see cref="KeepOffFileAsync"/> for the open document, before the view writes a file in place.</summary>
    public Task KeepOpenDocumentOffFileAsync(string? path) =>
        _document is { } document ? KeepOffFileAsync(document, path) : Task.CompletedTask;

    /// <summary>
    /// Before a file is written in place: if it is the file <paramref name="document"/> reads,
    /// the document moves onto a private copy first (#147). The document is read from its file
    /// on demand, so a write over that file would change what it reads. On APFS the copy is a
    /// clone and costs nothing. Throws when the copy cannot be made, before anything is written.
    /// </summary>
    private Task KeepOffFileAsync(IPdfDocument document, string? path) =>
        path is null
            ? Task.CompletedTask
            : OffUiThread(() =>
            {
                if (document.ReadsFile(path))
                    document.ReadFromCopy();
            });

    /// <summary>
    /// A save that failed, in words. The one typed failure — the engine could not
    /// read back what it wrote, so the original was left alone — has its own
    /// sentence; anything else gets the lead sentence and the exception's message.
    /// </summary>
    public void ReportSaveFailure(Exception ex) =>
        Status = ex switch
        {
            VerifiedSave.UnreadableOutputException => Strings.SavedDocumentUnreadable,
            DocumentRestrictedException => Strings.ActionRestricted,
            _ => Strings.WithDetail(Strings.CouldNotSave, ex.Message),
        };

    // --- Password: unlock, set, change, remove (#131, ADR-004 §3, §5, §6) ---

    public enum UnlockOutcome { Unlocked, WrongPassword, Failed }

    /// <summary>
    /// Reopens the open document with its owner password, for full access. The current
    /// document stays open until the password is proven: a password that opens it but
    /// not as its owner (the user password) is as wrong as one that does not open it.
    /// </summary>
    public async Task<UnlockOutcome> UnlockAsync(string ownerPassword)
    {
        if (_document is null || DocumentPath is not { } path || Busy.IsBusy)
            return UnlockOutcome.Failed;

        (IPdfDocument Document, IReadOnlyList<(double Width, double Height)> Sizes) loaded;
        using (Busy.Begin(Strings.BusyOpening))
        {
            try
            {
                loaded = await OffUiThread(() => LoadDocument(path, ownerPassword));
            }
            catch (PdfLoadException ex) when (ex.IsPasswordError)
            {
                return UnlockOutcome.WrongPassword;
            }
            catch (PdfLoadException ex)
            {
                Status = DescribeLoadFailure(ex);
                return UnlockOutcome.Failed;
            }
            catch (Exception ex)
            {
                // Moved, locked, or the grant lapsed: the restricted document stays open.
                Status = Strings.WithDetail(Strings.FileCouldNotBeRead, ex.Message);
                return UnlockOutcome.Failed;
            }
        }

        if (!loaded.Document.Security.HasFullAccess || DocumentPath != path)
        {
            var wrong = !loaded.Document.Security.HasFullAccess;
            loaded.Document.Dispose();
            return wrong ? UnlockOutcome.WrongPassword : UnlockOutcome.Failed;
        }

        CloseDocument();
        Adopt(loaded.Document, path, openedWithPassword: true, loaded.Sizes);
        Status = Strings.WithDetail(Strings.DocumentUnlocked, Strings.RecoveryOffForProtected);
        return UnlockOutcome.Unlocked;
    }

    /// <summary>
    /// The open document, unsaved edits included, under new security or none — verified by
    /// opening it with the new password, or without one — as bytes, before any
    /// destination is opened. Opening the user's file for writing truncates it (#59), so
    /// a refusal (<see cref="DocumentRestrictedException"/>) or an unreadable result
    /// must throw here, while the file is still untouched.
    /// </summary>
    /// <param name="newPassword">The one password (ADR-004 §5), or null to remove security.</param>
    /// <remarks>
    /// Off the UI thread, "Saving…" then "Checking the saved file…", with editing, Close and file
    /// commands waiting (#145). <paramref name="writeBytes"/> writes the verified bytes over the
    /// document's own file; only then is it reopened with the new password, so the open
    /// document, its credentials and its permissions match what is on disk (ADR-004 §6). False
    /// when nothing was written; the status line says why.
    /// </remarks>
    public async Task<bool> ChangeSecurityAsync(string path, string? newPassword, string doneMessage,
                                                Func<VerifiedSave.StagedCopy, Task> writeStaged)
    {
        if (_document is not { } document || Busy.IsBusy)
            return false;

        using (var busy = Busy.Begin(Strings.BusySaving))
        {
            void Stage(VerifiedSave.SaveStage stage) =>
                busy.SetLabel(stage == VerifiedSave.SaveStage.Verifying ? Strings.BusyCheckingSavedFile : Strings.BusySaving);
            try
            {
                if (FlattenOnSave)
                    await FlattenOpenDocumentAsync(document);

                // Staged in a temporary file, not built in memory (#147).
                using var staged = await OffUiThread(() => newPassword is null
                    ? VerifiedSave.ToStagedFileWithoutSecurity(_engine, document, Stage)
                    : VerifiedSave.ToStagedFileWithSecurity(_engine, document, newPassword, ownerPassword: null, PdfPermissions.All, Stage));
                busy.SetLabel(Strings.BusySaving);
                await writeStaged(staged);
            }
            catch (Exception ex)
            {
                ReportSaveFailure(ex);
                return false;
            }
        }

        if (!ReferenceEquals(document, _document))
            return true;
        _journal.MarkSaved(path);
        IsDirty = false;
        await OpenAsync(path, newPassword);
        if (IsDocumentOpen)
            Status = newPassword is null
                ? doneMessage
                : Strings.WithDetail(doneMessage, Strings.RecoveryOffForProtected);
        return true;
    }

    // --- Zoom ---

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void ZoomIn() => Zoom = NextStop(Zoom, forward: true);

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void ZoomOut() => Zoom = NextStop(Zoom, forward: false);

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void ZoomReset() => Zoom = 1.0;

    /// <summary>The fixed levels the zoom menu offers under Actual size and the fits (#144).</summary>
    public static IReadOnlyList<double> ZoomPresets { get; } = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0];

    /// <summary>A preset from the zoom menu.</summary>
    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void SetZoom(double zoom) => Zoom = Clamp(zoom);

    /// <summary>
    /// Viewport size in device-independent pixels, set by the view. Fit-to-width and
    /// fit-to-page are meaningless without it.
    /// </summary>
    public double ViewportWidth { get; set; }
    public double ViewportHeight { get; set; }

    /// <summary>Widest page in the document — fit-to-width must suit all of them.</summary>
    private double WidestPagePoints => Pages.Count == 0 ? 0 : Pages.Max(p => p.PointWidth);
    private double TallestPagePoints => Pages.Count == 0 ? 0 : Pages.Max(p => p.PointHeight);

    /// <summary>Page margins in the item template, so a fitted page is not clipped.</summary>
    private const double FitPadding = 32;

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void FitWidth()
    {
        if (WidestPagePoints <= 0 || ViewportWidth <= 0)
            return;
        Zoom = Clamp((ViewportWidth - FitPadding) / (WidestPagePoints * Rendering.PageBitmap.PointsToPixels));
    }

    [RelayCommand(CanExecute = nameof(IsDocumentOpen))]
    private void FitPage()
    {
        if (TallestPagePoints <= 0 || ViewportHeight <= 0)
            return;
        Zoom = Clamp((ViewportHeight - FitPadding) / (TallestPagePoints * Rendering.PageBitmap.PointsToPixels));
    }

    /// <summary>Set when a document opens; cleared once it has been fitted (#143).</summary>
    private bool _fitOnOpenPending;

    /// <summary>
    /// The zoom a document opens at (#143). At actual size a Letter page is 1056 DIP
    /// tall and the micro:bit schematic 1487 DIP wide, so in a laptop-sized window
    /// the first view hid part of the page, and on macOS nothing said there was more.
    ///
    /// The rule, in full: the width of the widest page fits the viewport; if the
    /// first page is landscape, its height must fit as well; and never above 100%,
    /// so a small page is not blown up. Nothing else — no memory of the last
    /// document's zoom, no per-document heuristics.
    ///
    /// Waits for a real viewport: a file handed over at launch opens before the
    /// window has been laid out, and the view calls this again once it has.
    /// </summary>
    public void FitOnOpen()
    {
        if (!_fitOnOpenPending || Pages.Count == 0
            || ViewportWidth <= FitPadding || ViewportHeight <= FitPadding)
            return;
        _fitOnOpenPending = false;

        var scale = Rendering.PageBitmap.PointsToPixels;
        var first = Pages[0];
        var fit = (ViewportWidth - FitPadding) / (WidestPagePoints * scale);
        if (first.PointWidth > first.PointHeight)
        {
            // The list's top margin and the page's bottom gap as well as the sides'.
            var verticalPadding = FitPadding + 16;
            fit = Math.Min(fit, (ViewportHeight - verticalPadding) / (first.PointHeight * scale));
        }
        Zoom = Clamp(Math.Min(1.0, fit));
    }

    /// <summary>Fitted zooms are free-form, but still bounded by the stops' range.</summary>
    private static double Clamp(double zoom) => Math.Clamp(zoom, ZoomStops[0], ZoomStops[^1]);

    /// <summary>Which page is in view, 1-based. Set by the view as it scrolls.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageIndicator))]
    private int _currentPage = 1;

    public string PageIndicator => Pages.Count > 0 ? Strings.PageOf(CurrentPage, Pages.Count) : "";

    private static double NextStop(double current, bool forward)
    {
        if (forward)
        {
            foreach (var stop in ZoomStops)
                if (stop > current + 0.001)
                    return stop;
            return ZoomStops[^1];
        }

        for (var i = ZoomStops.Length - 1; i >= 0; i--)
            if (ZoomStops[i] < current - 0.001)
                return ZoomStops[i];
        return ZoomStops[0];
    }

    private void RerenderPage(int pageIndex)
    {
        var page = Pages.FirstOrDefault(p => p.Index == pageIndex);
        // Only if the view has it on screen; an off-screen page re-renders when it
        // scrolls back in, and will pick up the edit then.
        if (page is { IsRealised: true })
            page.Rerender(DpiScale);
    }

    private void RerenderRealisedPages()
    {
        foreach (var page in Pages)
            if (page.IsRealised)
                page.EnsureRendered(DpiScale);
    }

    private void CloseDocument()
    {
        foreach (var page in Pages)
            page.Dispose();
        Pages.Clear();

        _undoStack.Clear();
        _pageWarnings.Reset();
        _searchGeneration++;
        Selection = null;
        RaiseUndoRedo();

        _document?.Dispose();
        _document = null;
        DocumentPath = null;
        DocumentName = null;
        IsDocumentOpen = false;
        IsDirty = false;
        Capabilities = DocumentCapabilities.Unprotected;
        // Marks belong to the document that carried them (#329): the old count and the old
        // overlays must not outlive it, and neither must the mode that places them.
        RedactionMarkCount = 0;
        SetMode(PageMode.Select);
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    /// <summary>The person chose Don't Save: closing now ends the session and removes the journal.</summary>
    public void DiscardChanges() => _changesDiscarded = true;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // A clean exit ends the session, so the next launch does not offer to recover a
        // document the user deliberately finished with. Only a clean one (D1, #145): with
        // unsaved edits nobody agreed to lose, the journal stays for the next launch to offer.
        if (!IsDirty || _changesDiscarded)
            _journal.EndSession();
        _journal.Dispose();
        CloseDocument();
        _engine.Dispose();
    }
}
