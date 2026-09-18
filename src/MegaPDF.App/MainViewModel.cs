using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Recovery;
using MegaPDF.Core.Services;
using MegaPDF.Core.Viewing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace MegaPDF.App;

/// <summary>A clickable area on a page, cached so hover affordances don't hit the engine.</summary>
public sealed record InteractiveRegion(PdfRect Bounds, PageHitKind Kind);

/// <summary>
/// One page slot: geometry is always present; the bitmap and interaction map exist
/// only while the page is inside the render window (SDD §4.2 virtualization).
/// </summary>
public sealed record PageView(
    int Index,
    ImageSource? Source,
    double PointsWidth,
    double PointsHeight,
    double Width,
    double Height,
    IReadOnlyList<InteractiveRegion> Regions)
{
    /// <summary>Narrator/UIA name for the page surface.</summary>
    public string AccessibleName => Strings.PageN(Index + 1);

    /// <summary>Find-match highlights at this slot's zoom; empty when no search is active.</summary>
    public IReadOnlyList<SearchHighlight> Highlights { get; init; } = [];

    /// <summary>
    /// The engine could not rasterise this page (#93). The slot says so instead of
    /// staying a blank sheet, and the viewport loop stops asking for it.
    /// </summary>
    public bool RenderFailed { get; init; }

    /// <summary>
    /// The bitmap is a stand-in — a quarter-size raster, or the previous zoom's
    /// raster stretched to the new size — and the full render is still owed (#94).
    /// </summary>
    public bool IsPreview { get; init; }

    public Visibility FailedVisibility => RenderFailed ? Visibility.Visible : Visibility.Collapsed;
    public string FailedMessage => Strings.PageRenderFailed;
}

/// <summary>One find-match rectangle on a page, in DIPs (zoom baked in at creation).</summary>
public sealed record SearchHighlight(double X, double Y, double Width, double Height, bool IsCurrent)
{
    public Thickness Margin => new(X, Y, 0, 0);

    /// <summary>
    /// Hue, not opacity, separates "one of forty hits" from "the hit you are on":
    /// cyan for every match, brand blue for the current one (issue #26, and
    /// docs/design-tokens.md §1.2). Two strengths of one colour are hard to tell
    /// apart on a dark scan; two colours are not. The tokens carry their own alpha.
    /// </summary>
    public Brush Fill => Brand.Brush(IsCurrent ? "BrandFindMatchCurrentBrush" : "BrandFindMatchBrush");

    /// <summary>The current match is also outlined so it stands out beside its neighbors.</summary>
    public Brush Stroke => IsCurrent
        ? Brand.Brush("BrandAccentBrush")
        : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public double StrokeThickness => IsCurrent ? 1.5 : 0;
}

/// <summary>A library signature shown in the flyout.</summary>
public sealed record SignatureItem(Guid Id, string Name, string PngPath, ImageSource Thumbnail)
{
    /// <summary>Narrator name for the card: the signature's name, then what it is.</summary>
    public string AccessibleName => Strings.SignatureCardName(Name);
}

/// <summary>
/// A recent document on the empty state (#165): its name, and where it lives, so two
/// files called "scan.pdf" can be told apart without hovering.
/// </summary>
/// <param name="Location">The folder, as Explorer names it: "Documents › Clients › Smith".</param>
/// <param name="IsMissing">The file is no longer there; the row says so and offers to remove it.</param>
public sealed record RecentDocument(string Name, string Path, string Location, bool IsMissing)
{
    /// <summary>Narrator reads the name and where it is, since the names repeat.</summary>
    public string AccessibleName => IsMissing
        ? Strings.RecentItemMissingName(Name, Location)
        : Strings.RecentItemName(Name, Location);

    /// <summary>A missing file's name and icon are dimmed, the way Explorer greys one out.</summary>
    public double MissingOpacity => IsMissing ? 0.5 : 1.0;

    /// <summary>The location line, prefixed with "Not found" when the file has gone.</summary>
    public string LocationLine => IsMissing ? $"{Strings.RecentNotFound} · {Location}" : Location;
}

public partial class MainViewModel(Window window) : ObservableObject
{
    private static readonly IPdfEngine Engine = new PdfiumEngine();

    private readonly UndoStack _undoStack = new();

    /// <summary>Pages already asked about in this document (#139): the warning comes once per page.</summary>
    private readonly PageRegenerationWarnings _pageWarnings = new();
    private readonly RecoveryJournal _journal = new();
    // Missing files stay on the list and are shown as unavailable (#165), rather than
    // disappearing as though the app had lost them.
    private readonly RecentFiles _recentFiles = new(path: null, pruneMissing: false);
    private readonly AppSettings _settings = new();

    // --- Busy state (#145) ---

    /// <summary>
    /// The document's busy state: editing and file commands wait at once, the strip under the
    /// toolbar (or a spinner on the page) shows after 0.5 s. Created with the window, on the UI thread.
    /// </summary>
    public BusyState Busy { get; } = new();

    /// <summary>Nothing blocking is running.</summary>
    public bool IsIdle => !Busy.IsBusy;

    /// <summary>Counts edits, undos and redos: a save marks the document saved only if none ran meanwhile (D3, #145).</summary>
    private int _editCount;

    /// <summary>Called once by the window: commands and flags follow the busy state.</summary>
    public void WatchBusyState() => Busy.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName != nameof(BusyState.IsBusy))
            return;
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsEditingAllowed));
        OnPropertyChanged(nameof(IsSigningAllowed));
        OnPropertyChanged(nameof(IsTextBoxAllowed));
        OnPropertyChanged(nameof(IsPrintAllowed));
        OpenCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();
        SecurityCommand.NotifyCanExecuteChanged();
        ShrinkForEmailCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    };

    private static string SaveStageLabel(VerifiedSave.SaveStage stage) =>
        stage == VerifiedSave.SaveStage.Verifying ? Strings.BusyCheckingSavedFile : Strings.BusySaving;

    /// <summary>
    /// Starts the #139 check for a page in the background (#145): when it is first shown, when
    /// Add text or Whiteout is armed on it, and when an added box or cover is selected on it, so
    /// the answer is usually ready by the change.
    /// </summary>
    public void PreparePageCheck(int pageIndex)
    {
        if (_document is not { } document || pageIndex < 0 || pageIndex >= Pages.Count)
            return;
        if (!Capabilities.CanEditContent && !Capabilities.CanAddText)
            return;
        _pageWarnings.Prepare(document, pageIndex);
    }

    partial void OnCurrentPageChanged(int value) => PreparePageCheck(value - 1);

    /// <summary>
    /// Save, Don't save or Cancel when the open document has unsaved changes — before closing
    /// and, since #145 (D5), before another document replaces it. True when the caller may go
    /// on: nothing unsaved, saved, or Don't save. Waits for a save still running first.
    /// </summary>
    public async Task<bool> ConfirmSaveChangesAsync()
    {
        await Busy.WhenIdleAsync();
        if (_document is null || !HasUnsavedChanges || window.Content?.XamlRoot is not { } xamlRoot)
            return true;

        var dialog = new ContentDialog
        {
            Title = Strings.SaveChangesTitle(OpenDocumentName),
            Content = Strings.SaveChangesBody,
            PrimaryButtonText = Strings.Save,
            SecondaryButtonText = Strings.DontSave,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        switch (await dialog.ShowOneAtATimeAsync())
        {
            case ContentDialogResult.Primary:
                await SaveCommand.ExecuteAsync(null);
                return !HasUnsavedChanges;
            case ContentDialogResult.Secondary:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The text-edit check before the line editor opens (#118): a dry run of the rewrite, about
    /// three seconds on a heavy page. Off the UI thread, under a spinner on the line, while
    /// clicks are ignored (#145). Null when every run of the line can be changed.
    /// </summary>
    public async Task<LayoutVerdict?> CheckLineAsync(int pageIndex, PdfTextLine line)
    {
        using (Busy.Begin(Strings.BusyCheckingPage, scope: BusyScope.Page, pageIndex: pageIndex, area: line.Bounds))
            return await Task.Run(() => LineLayoutRefusal(pageIndex, line));
    }

    // --- Settings (SDD §2.2 flyout; deliberately tiny) ---

    public CheckMarkStyle MarkStyle
    {
        get => _settings.MarkStyle;
        set => _settings.MarkStyle = value;
    }

    public string ThemeSetting
    {
        get => _settings.Theme;
        set => _settings.Theme = value;
    }

    /// <summary>"" follows Windows; otherwise a BCP-47 tag such as "fr-CA". Applied at startup by App.</summary>
    public string LanguageSetting
    {
        get => _settings.Language;
        set => _settings.Language = value;
    }

    public bool ReopenLastFile
    {
        get => _settings.ReopenLastFile;
        set => _settings.ReopenLastFile = value;
    }

    public bool FlattenOnSave
    {
        get => _settings.FlattenOnSave;
        set => _settings.FlattenOnSave = value;
    }

    /// <summary>For "Reopen last file": the newest one still on disk (#165 keeps missing ones listed).</summary>
    public string? MostRecentDocument => _recentFiles.All.FirstOrDefault(File.Exists);

    // --- Per-document view state (SDD §3.4: restore last scroll position) ---

    /// <summary>Kept current by the scroll handler; persisted on close/switch.</summary>
    public double CurrentScrollOffset { get; set; }

    /// <summary>Raised after a document opens with a remembered scroll position.</summary>
    public event Action<double>? ScrollRestoreRequested;

    public void SaveViewState()
    {
        if (DocumentPath is { } path)
            _recentFiles.UpdateViewState(path, CurrentScrollOffset, ZoomPercent);
    }

    /// <summary>First-run "Make MegaPDF your PDF app?" card (SDD §5.4) — shows once, ever.</summary>
    [ObservableProperty]
    private bool _isDefaultAppCardOpen;

    public void MaybeShowDefaultAppCard()
    {
        if (_settings.DefaultAppCardShown)
            return;
        IsDefaultAppCardOpen = true;
    }

    public void DismissDefaultAppCard()
    {
        _settings.DefaultAppCardShown = true;
        IsDefaultAppCardOpen = false;
    }
    private IPdfDocument? _document;
    private int _openGeneration;

    /// <summary>Recent documents for the empty state (SDD §2.2), newest first.</summary>
    public ObservableCollection<RecentDocument> RecentDocuments { get; } = [];

    public Visibility RecentDocumentsVisibility =>
        !IsDocumentOpen && RecentDocuments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The recents list, each row with the folder it lives in (#165). Rows whose file
    /// names clash get as much of the path as it takes to tell them apart: the folder
    /// above, then the one above that.
    /// </summary>
    public void LoadRecentDocuments()
    {
        RecentDocuments.Clear();
        var named = ShellFolderNames.Get();
        var paths = _recentFiles.All;
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
        _recentFiles.Remove(path);
        LoadRecentDocuments();
        OnPropertyChanged(nameof(RecentDocumentsVisibility));
    }

    /// <summary>
    /// Opens a recent row, or — when its file has gone — says so and offers to take it off
    /// the list (#165). Explorer and Office both ask rather than removing it silently.
    /// </summary>
    public async Task OpenRecentAsync(RecentDocument recent)
    {
        if (File.Exists(recent.Path))
        {
            await OpenDocumentAsync(recent.Path);
            return;
        }
        if (window.Content?.XamlRoot is not { } xamlRoot)
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

    /// <summary>
    /// Something worth saying out loud happened (#190). The window raises it as a UI
    /// Automation notification on the pages pane, the same way a focus move is announced.
    /// </summary>
    public event Action<string>? Announced;

    public ObservableCollection<PageView> Pages { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand), nameof(ZoomOutCommand))]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(OpenDocumentName), nameof(EmptyStateVisibility), nameof(DocumentVisibility), nameof(IsDocumentOpen))]
    [NotifyPropertyChangedFor(nameof(IsEditingAllowed), nameof(IsSigningAllowed), nameof(IsTextBoxAllowed), nameof(IsPrintAllowed))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(SaveAsCommand), nameof(ShrinkForEmailCommand), nameof(SecurityCommand))]
    private string? _documentPath;

    /// <summary>The live document — printing renders what's on screen, unsaved edits included.</summary>
    internal IPdfDocument? CurrentDocument => _document;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(SaveButtonLabel))]
    private bool _hasUnsavedChanges;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageIndicator))]
    private int _pageCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageIndicator))]
    private int _currentPage = 1;

    public bool IsDocumentOpen => DocumentPath is not null;

    // --- Document security (#131, ADR-004 §2, §3) ---

    /// <summary>
    /// What this open of the document may do. Read from the document on every open, so
    /// an owner-restricted document is not an editing loophole; every tool until then.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingAllowed), nameof(IsSigningAllowed), nameof(IsTextBoxAllowed), nameof(IsPrintAllowed))]
    [NotifyCanExecuteChangedFor(nameof(ShrinkForEmailCommand))]
    private DocumentCapabilities _capabilities = DocumentCapabilities.Unprotected;

    // Each also waits while blocking work runs (#145): a save, an open, a change on its way.

    /// <summary>Editing the document's own text and whiteout (modify).</summary>
    public bool IsEditingAllowed => IsDocumentOpen && Capabilities.CanEditContent && !Busy.IsBusy;

    /// <summary>Signatures and check marks (fill forms or annotate).</summary>
    public bool IsSigningAllowed => IsDocumentOpen && Capabilities.CanSign && !Busy.IsBusy;

    /// <summary>Adding and changing text boxes (modify, fill forms or annotate).</summary>
    public bool IsTextBoxAllowed => IsDocumentOpen && Capabilities.CanAddText && !Busy.IsBusy;

    public bool IsPrintAllowed => IsDocumentOpen && Capabilities.CanPrint && !Busy.IsBusy;

    /// <summary>The owner restricted this document; the notice offers the owner password.</summary>
    [ObservableProperty]
    private bool _isRestrictedNoticeOpen;

    /// <summary>Opened with a password, so nothing is journaled (#135, ADR-004 §7).</summary>
    [ObservableProperty]
    private bool _isRecoveryOffNoticeOpen;

    [ObservableProperty]
    private bool _isSecurityNoticeOpen;

    /// <summary>"Password set." and its siblings. Never carries the password itself.</summary>
    [ObservableProperty]
    private string _securityNotice = "";

    /// <summary>
    /// Whether a click on this kind of region may do anything. When it may not, the
    /// restricted notice says why rather than the click silently doing nothing.
    /// </summary>
    public bool AllowsInteraction(PageHitKind kind)
    {
        if (Capabilities.Allows(kind))
            return true;
        IsRestrictedNoticeOpen = true;
        return false;
    }

    public string OpenDocumentName => DocumentPath is null ? "" : Path.GetFileName(DocumentPath);

    // Unsaved-changes dot convention (SDD §2.2). The product is "MegaPDF" on every
    // platform (Dave, 2026-09-18). "Mega PDF" with the space survives only as the
    // reserved Store name, so the manifest's DisplayName keeps it — the Store checks
    // that against the reservation — while the app itself never says it.
    public const string AppName = "MegaPDF";

    public string WindowTitle =>
        DocumentPath is null ? AppName
        : $"{(HasUnsavedChanges ? "● " : "")}{OpenDocumentName} — {AppName}";

    public string SaveButtonLabel => HasUnsavedChanges ? Strings.SaveWithDot : Strings.Save;

    public string PageIndicator => PageCount > 0 ? Strings.PageOf(CurrentPage, PageCount) : "";

    public Visibility EmptyStateVisibility => IsDocumentOpen ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DocumentVisibility => IsDocumentOpen ? Visibility.Visible : Visibility.Collapsed;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task OpenAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");
        // Unpackaged apps must associate pickers with their window handle.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            await OpenDocumentAsync(file.Path);
    }

    /// <param name="initialPassword">
    /// Tried first, without prompting — how the document reopens after its password was
    /// set or changed (#131). Held only for the length of the open; the core keeps what
    /// the document was opened with, and nothing here stores it.
    /// </param>
    public async Task OpenDocumentAsync(string path, string? initialPassword = null)
    {
        if (Busy.IsBusy)
            return;
        // D5 (#145): another document replaces this one, so its unsaved changes are asked about first.
        if (!await ConfirmSaveChangesAsync())
            return;

        SaveViewState(); // remember where we left the previous document
        var generation = ++_openGeneration;

        IPdfDocument doc;
        var password = initialPassword;
        while (true)
        {
            try
            {
                var attempt = password;
                using (Busy.Begin(Strings.BusyOpening))
                    doc = await Task.Run(() => Engine.Open(path, attempt));
                break;
            }
            catch (PdfLoadException ex) when (ex.IsPasswordError)
            {
                var fileName = Path.GetFileName(path);
                password = await ShowPasswordPromptAsync(Strings.PasswordRequiredTitle, Strings.PasswordPrompt(fileName),
                    Strings.PasswordWrong, Strings.Open, wrongPassword: password is not null);
                if (password is null)
                    return; // user cancelled
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(Strings.CouldNotOpenTitle, UserFacing.Describe(ex));
                return;
            }
        }

        await AdoptDocumentAsync(doc, path, openedWithPassword: password is not null, generation);
    }

    /// <summary>Makes a loaded document the open one: state, journal, notices, pages.</summary>
    private async Task AdoptDocumentAsync(IPdfDocument doc, string path, bool openedWithPassword, int generation)
    {
        if (generation != _openGeneration)
        {
            // A newer open superseded this one while it loaded.
            doc.Dispose();
            return;
        }

        var rememberedView = _recentFiles.FindEntry(path);
        _document?.Dispose();
        _cappedRenders.Clear();
        _document = doc;

        // Permissions first, so nothing bound to the new path sees the old document's (#131).
        Capabilities = DocumentCapabilities.From(doc.Security);
        if (!Capabilities.CanEditContent)
            IsWhiteoutMode = false;
        if (!Capabilities.CanAddText)
            IsTextBoxMode = false;
        if (!Capabilities.CanSign)
            PendingSignature = null;
        IsRestrictedNoticeOpen = Capabilities.IsRestricted;
        IsRecoveryOffNoticeOpen = openedWithPassword;
        IsSecurityNoticeOpen = false;

        DocumentPath = path;
        HasUnsavedChanges = false;
        _undoStack.Clear();
        _pageWarnings.Reset();
        ClearSearch(); // matches belong to the previous document
        // Not journaled when opened with a password: its text must not reach disk
        // unencrypted (#135). The notice above says so (ADR-004 §7).
        _journal.BeginSession(path, contentIsProtected: openedWithPassword);
        _recentFiles.Add(path);
        _ = JumpListRecents.RecordAsync(path); // the taskbar's Recent list (#165)
        if (rememberedView is not null)
            ZoomPercent = Math.Clamp(rememberedView.ZoomPercent, MinZoom, MaxZoom);
        LoadRecentDocuments();
        OnPropertyChanged(nameof(RecentDocumentsVisibility));
        ResetPageFocus(); // keyboard focus and maps belong to the previous document (#2)
        Pages.Clear();
        PageCount = doc.PageCount;
        CurrentPage = 1;

        // Fast size-only pass: geometry for every page, no rendering (SDD §4.2).
        List<(double W, double H)> sizes;
        using (Busy.Begin(Strings.BusyOpening))
        {
            sizes = await Task.Run(() =>
            {
                var list = new List<(double W, double H)>(doc.PageCount);
                for (var i = 0; i < doc.PageCount; i++)
                {
                    using var page = doc.GetPage(i);
                    list.Add((page.Width, page.Height));
                }
                return list;
            });
        }
        if (generation != _openGeneration)
            return;

        for (var i = 0; i < sizes.Count; i++)
            Pages.Add(Placeholder(i, sizes[i].W, sizes[i].H));
        PreparePageCheck(0); // the first page is shown: its #139 check starts now (#145)

        await UpdateViewportAsync(0, Math.Min(2, PageCount - 1));

        if (rememberedView is { ScrollOffset: > 0 })
            ScrollRestoreRequested?.Invoke(rememberedView.ScrollOffset);
    }

    // --- Fit zoom presets ---

    public async Task FitWidthAsync(double viewportWidthDips)
    {
        if (Pages.Count == 0)
            return;
        var page = Pages[Math.Clamp(CurrentPage - 1, 0, Pages.Count - 1)];
        var pageWidthAt100 = page.PointsWidth * 96 / 72;
        await SetZoomAsync((int)((viewportWidthDips - 64) / pageWidthAt100 * 100));
    }

    public async Task FitPageAsync(double viewportWidthDips, double viewportHeightDips)
    {
        if (Pages.Count == 0)
            return;
        var page = Pages[Math.Clamp(CurrentPage - 1, 0, Pages.Count - 1)];
        var widthFit = (viewportWidthDips - 64) / (page.PointsWidth * 96 / 72);
        var heightFit = (viewportHeightDips - 56) / (page.PointsHeight * 96 / 72);
        await SetZoomAsync((int)(Math.Min(widthFit, heightFit) * 100));
    }

    private PageView Placeholder(int index, double pointsWidth, double pointsHeight) =>
        new(index, null, pointsWidth, pointsHeight,
            pointsWidth * 96 / 72 * ZoomFactor, pointsHeight * 96 / 72 * ZoomFactor, [])
        { Highlights = HighlightsFor(index, ZoomFactor) };

    // --- Viewport-window rendering (SDD §4.2: visible pages ± 2, evict the rest) ---

    private const int RenderMargin = 2;
    private int _viewFirst;
    private int _viewLast = 2;
    private bool _viewportUpdateRunning;
    private bool _viewportDirty;

    public async Task UpdateViewportAsync(int firstVisible, int lastVisible)
    {
        if (_document is null || Pages.Count == 0)
            return;
        _viewFirst = Math.Clamp(firstVisible, 0, Pages.Count - 1);
        _viewLast = Math.Clamp(lastVisible, _viewFirst, Pages.Count - 1);

        if (_viewportUpdateRunning)
        {
            _viewportDirty = true; // the running loop picks up the new window
            return;
        }

        _viewportUpdateRunning = true;
        try
        {
            do
            {
                _viewportDirty = false;
                var generation = _openGeneration;
                var doc = _document;
                if (doc is null)
                    return;
                var lo = Math.Max(0, _viewFirst - RenderMargin);
                var hi = Math.Min(Pages.Count - 1, _viewLast + RenderMargin);

                // Evict bitmaps that left the window — memory stays bounded.
                for (var i = 0; i < Pages.Count; i++)
                {
                    if ((i < lo || i > hi) && Pages[i].Source is not null)
                        Pages[i] = Placeholder(i, Pages[i].PointsWidth, Pages[i].PointsHeight);
                }

                // Render missing pages, nearest-to-viewport-center first.
                var center = (_viewFirst + _viewLast) / 2;
                var order = Enumerable.Range(lo, hi - lo + 1).OrderBy(i => Math.Abs(i - center)).ToList();

                // Two passes (#94): first a quarter-size preview of every empty slot, so a
                // heavy page shows something within a frame or two, then the full raster
                // for every slot still holding a preview (including the stretched previous
                // zoom SetZoomAsync leaves behind).
                foreach (var preview in new[] { true, false })
                {
                    foreach (var i in order)
                    {
                        if (generation != _openGeneration)
                            return;
                        var slot = Pages[i];
                        if (!slot.RenderFailed && (preview ? slot.Source is null : slot.Source is null || slot.IsPreview))
                            Pages[i] = await RenderPageAsync(doc, i, preview);
                        if (_viewportDirty)
                            break; // the window moved — restart with the new one
                    }
                    if (_viewportDirty)
                        break;
                }
            } while (_viewportDirty);
        }
        finally
        {
            _viewportUpdateRunning = false;
        }
    }

    private async Task<PageView> RenderPageAsync(IPdfDocument doc, int pageIndex, bool preview = false)
    {
        // Render at monitor rasterization scale × zoom so pages stay crisp.
        var scale = (window.Content?.XamlRoot?.RasterizationScale ?? 1.0) * ZoomFactor;
        var zoom = ZoomFactor;

        RenderedPage rendered;
        double pointsW, pointsH;
        List<InteractiveRegion> regions;
        bool isPreview;
        try
        {
            (rendered, pointsW, pointsH, regions, isPreview) = await Task.Run(() => RenderPage(doc, pageIndex, scale, preview));
        }
        catch (Exception ex)
        {
            // A page the engine cannot rasterise (a broken content stream, or PDFium
            // refusing the bitmap) used to escape this fire-and-forget loop as an
            // unhandled exception and leave a blank sheet behind (#93). The slot now
            // says what happened and is not asked for again until it re-enters the
            // render window.
            System.Diagnostics.Debug.WriteLine($"page {pageIndex + 1} could not be rendered: {ex}");
            var slot = Pages[pageIndex];
            return new PageView(pageIndex, null, slot.PointsWidth, slot.PointsHeight,
                slot.PointsWidth * 96 / 72 * zoom, slot.PointsHeight * 96 / 72 * zoom, [])
            { Highlights = HighlightsFor(pageIndex, zoom), RenderFailed = true };
        }

        var bitmap = new WriteableBitmap(rendered.PixelWidth, rendered.PixelHeight);
        using (var pixelStream = bitmap.PixelBuffer.AsStream())
            pixelStream.Write(rendered.Bgra, 0, rendered.Bgra.Length);
        bitmap.Invalidate();

        return new PageView(pageIndex, bitmap, pointsW, pointsH,
            pointsW * 96 / 72 * zoom, pointsH * 96 / 72 * zoom, regions)
        { Highlights = HighlightsFor(pageIndex, zoom), IsPreview = isPreview };
    }

    /// <summary>
    /// Rasters of pages too large to render at their ideal size (see
    /// <see cref="RenderLimits"/>), kept so a zoom step does not decode the page
    /// again. Cleared on open, and per page when an edit touches it.
    /// </summary>
    private readonly CappedRenderCache _cappedRenders = new(capacity: 2);

    /// <summary>
    /// Off the UI thread: the page raster and its interaction map.
    ///
    /// The raster is clamped to <see cref="RenderLimits"/> and the view scales it up
    /// the rest of the way (#93). Past the clamp every zoom level asks for the same
    /// pixels, and below it the view can scale the same raster down, so a page that
    /// ever needed clamping is rendered once and served from
    /// <see cref="_cappedRenders"/> at every zoom until an edit invalidates it. That
    /// is the difference between 20 s and 0 s per zoom click on an 88 MB scan (#94).
    /// </summary>
    private (RenderedPage Rendered, double PointsW, double PointsH, List<InteractiveRegion> Regions, bool IsPreview)
        RenderPage(IPdfDocument doc, int pageIndex, double scale, bool preview)
    {
        using var page = doc.GetPage(pageIndex);

        if (_cappedRenders.TryGet(pageIndex, out var kept))
            return (kept, page.Width, page.Height, BuildRegions(page), false);

        var idealW = page.Width * 96 / 72 * scale;
        var idealH = page.Height * 96 / 72 * scale;
        if (preview)
        {
            // A quarter of the size is a sixteenth of the work for anything
            // fill-rate bound, and the interaction map can wait for the full pass.
            var (pw, ph) = RenderLimits.Fit(idealW / 4, idealH / 4);
            return (page.Render(pw, ph), page.Width, page.Height, [], true);
        }

        var regions = BuildRegions(page);
        var (w, h) = RenderLimits.Fit(idealW, idealH);
        var rendered = page.Render(w, h);
        if (RenderLimits.IsCapped(idealW, idealH))
            _cappedRenders.Put(pageIndex, rendered);
        return (rendered, page.Width, page.Height, regions, false);
    }

    /// <summary>Interaction map in HitTest priority order: stamps, form fields, squares, text.</summary>
    private static List<InteractiveRegion> BuildRegions(IPdfPage page)
    {
        var regions = new List<InteractiveRegion>();
        foreach (var stamp in page.GetStamps())
            regions.Add(new InteractiveRegion(stamp.Bounds, PageHitKind.StampAnnotation));
        // Before body text, so hovering a text box shows the move affordance, not the caret.
        foreach (var box in page.GetTextBoxes())
            regions.Add(new InteractiveRegion(box.Bounds, PageHitKind.TextBox));
        foreach (var field in page.GetFormFields())
        {
            var kind = field.Kind switch
            {
                FormFieldKind.Text => PageHitKind.FormTextField,
                FormFieldKind.Checkbox or FormFieldKind.RadioButton => PageHitKind.FormCheckbox,
                _ => PageHitKind.None,
            };
            if (kind != PageHitKind.None)
                regions.Add(new InteractiveRegion(field.Bounds, kind));
        }
        foreach (var line in page.GetTextLines())
            regions.Add(new InteractiveRegion(line.Bounds, PageHitKind.TextRun));
        foreach (var whiteout in page.GetWhiteouts())
            regions.Add(new InteractiveRegion(whiteout.Bounds, PageHitKind.Whiteout));
        foreach (var square in page.DetectCheckboxSquares())
            regions.Add(new InteractiveRegion(square, PageHitKind.DrawnCheckbox));
        return regions;
    }

    // --- Zoom (SDD §2.2 toolbar) ---

    public const int MinZoom = 50;
    public const int MaxZoom = 300;
    private const int ZoomStep = 25;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomLabel))]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand), nameof(ZoomOutCommand))]
    private int _zoomPercent = 100;

    public double ZoomFactor => ZoomPercent / 100.0;

    /// <summary>"100%" — or "100 %" in French — on the toolbar's zoom menu button (#144).</summary>
    public string ZoomLabel => Strings.ZoomPercent(ZoomPercent);

    /// <summary>The fixed levels the zoom menu offers under Actual size and the fits (#144).</summary>
    public static IReadOnlyList<int> ZoomPresets { get; } = [50, 75, 100, 125, 150, 200, 300];

    /// <summary>A level from the zoom menu, or Actual size (100).</summary>
    public Task SetZoomPercentAsync(int percent) => SetZoomAsync(percent);

    /// <summary>
    /// An added text box on a page, found again after an edit rewrote it (#144): by its
    /// id where it has one, which survives a restyle, else by object index.
    /// </summary>
    public PdfTextRun? FindTextBox(int pageIndex, string? textBoxId, int objectIndex)
    {
        if (_document is null || pageIndex < 0 || pageIndex >= Pages.Count)
            return null;
        using var page = _document.GetPage(pageIndex);
        return page.GetTextBoxes().FirstOrDefault(b => textBoxId is not null
            ? b.TextBoxId == textBoxId
            : b.ObjectIndex == objectIndex);
    }

    /// <summary>The most recently added text box on a page, for the `textbox` screenshot state.</summary>
    public PdfTextRun? LastTextBoxOn(int pageIndex)
    {
        if (_document is null || pageIndex < 0 || pageIndex >= Pages.Count)
            return null;
        using var page = _document.GetPage(pageIndex);
        return page.GetTextBoxes().LastOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private async Task ZoomInAsync() => await SetZoomAsync(ZoomPercent + ZoomStep);

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private async Task ZoomOutAsync() => await SetZoomAsync(ZoomPercent - ZoomStep);

    // Zoom needs something to zoom (#170): with no document open the two buttons were the
    // only enabled commands on the row besides Open, which a screen reader reads out as
    // choices that do nothing.
    private bool CanZoomIn() => IsDocumentOpen && ZoomPercent < MaxZoom;
    private bool CanZoomOut() => IsDocumentOpen && ZoomPercent > MinZoom;

    private async Task SetZoomAsync(int percent)
    {
        ZoomPercent = Math.Clamp(percent, MinZoom, MaxZoom);
        if (_document is null)
            return;
        // Resize every slot and re-render just the viewport. A slot that already has
        // a raster keeps it, stretched to the new size, as the preview the full render
        // replaces (#94) — a zoom step no longer flashes to blank paper first.
        for (var i = 0; i < Pages.Count; i++)
        {
            var slot = Pages[i];
            Pages[i] = slot.Source is null || slot.RenderFailed
                ? Placeholder(i, slot.PointsWidth, slot.PointsHeight)
                : slot with
                {
                    Width = slot.PointsWidth * 96 / 72 * ZoomFactor,
                    Height = slot.PointsHeight * 96 / 72 * ZoomFactor,
                    IsPreview = true,
                    Highlights = HighlightsFor(i, ZoomFactor),
                };
        }
        await UpdateViewportAsync(_viewFirst, _viewLast);
    }

    private async Task RefreshPageAsync(int pageIndex)
    {
        if (_document is null || pageIndex < 0 || pageIndex >= Pages.Count)
            return;
        _cappedRenders.Remove(pageIndex); // the edit changed what the page looks like
        _keyboardMaps.TryRemove(pageIndex, out _); // and what is on it (#2)
        Pages[pageIndex] = await RenderPageAsync(_document, pageIndex);
    }

    // --- Find in document (Ctrl+F, issue #26: simple search) ---

    private readonly List<(int PageIndex, IReadOnlyList<PdfRect> Rects)> _searchMatches = [];
    private string _searchTerm = "";
    private int _searchGeneration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchStatus), nameof(HasSearchResults))]
    private int _searchMatchCount;

    /// <summary>1-based position of the current match; 0 when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchStatus))]
    private int _currentSearchMatch;

    public bool HasSearchResults => SearchMatchCount > 0;

    /// <summary>The find bar's readout: "N of M", "No results", or blank without a term.</summary>
    public string SearchStatus =>
        _searchTerm.Length == 0 ? ""
        : SearchMatchCount == 0 ? Strings.NoResults
        : Strings.MatchOf(CurrentSearchMatch, SearchMatchCount);

    /// <summary>Raised when navigation lands on a match — the view scrolls it into view.</summary>
    /// <summary>Where the current match sits in the scroll content, in DIPs.</summary>
    public readonly record struct SearchScrollTarget(double X, double Y, double Width, double Height);

    public event Action<SearchScrollTarget>? SearchScrollRequested;

    /// <summary>Whole-document search (as-you-type; the view debounces the calls).</summary>
    public async Task SearchAsync(string term)
    {
        _searchTerm = term;
        var generation = ++_searchGeneration;
        var openGeneration = _openGeneration;
        var doc = _document;

        if (doc is null || term.Length == 0)
        {
            ResetSearchState();
            return;
        }

        // Page by page off the UI thread, abandoning as soon as a newer search
        // (or document) supersedes this one — same idea as viewport rendering.
        // "Searching…" shows on a long document; typing on is never blocked (#145).
        List<(int PageIndex, IReadOnlyList<PdfRect> Rects)>? found;
        try
        {
            using (Busy.Begin(Strings.BusySearching, blocksEditing: false))
            {
                found = await Task.Run(() =>
                {
                    var list = new List<(int PageIndex, IReadOnlyList<PdfRect> Rects)>();
                    for (var i = 0; i < doc.PageCount; i++)
                    {
                        if (generation != _searchGeneration || openGeneration != _openGeneration)
                            return null;
                        using var page = doc.GetPage(i);
                        foreach (var match in page.FindText(term))
                            list.Add((i, match.Rects));
                    }
                    return list;
                });
            }
        }
        catch (Exception ex)
        {
            // A document closed under the search, or a page the engine could not read: no
            // matches rather than an unhandled exception from the find bar's timer (#145).
            System.Diagnostics.Debug.WriteLine($"search failed: {ex}");
            if (generation == _searchGeneration)
                ResetSearchState();
            return;
        }

        if (found is null || generation != _searchGeneration || openGeneration != _openGeneration)
            return;

        _searchMatches.Clear();
        _searchMatches.AddRange(found);
        SearchMatchCount = _searchMatches.Count;
        CurrentSearchMatch = SearchMatchCount > 0 ? 1 : 0;
        OnPropertyChanged(nameof(SearchStatus));
        ApplySearchHighlights();
        if (CurrentSearchMatch > 0)
            ScrollToCurrentMatch();
    }

    /// <summary>Next/previous match (+1/−1), wrapping around the document ends.</summary>
    public void MoveToMatch(int delta)
    {
        if (SearchMatchCount == 0)
            return;
        CurrentSearchMatch = (CurrentSearchMatch - 1 + delta + SearchMatchCount) % SearchMatchCount + 1;
        ApplySearchHighlights();
        ScrollToCurrentMatch();
    }

    /// <summary>Close/Esc: drop the term, the matches, and every highlight.</summary>
    public void ClearSearch()
    {
        _searchGeneration++;
        _searchTerm = "";
        ResetSearchState();
    }

    private void ResetSearchState()
    {
        _searchMatches.Clear();
        SearchMatchCount = 0;
        CurrentSearchMatch = 0;
        OnPropertyChanged(nameof(SearchStatus));
        ApplySearchHighlights();
    }

    /// <summary>Highlight rectangles for one page slot, in DIPs at the given zoom.</summary>
    private IReadOnlyList<SearchHighlight> HighlightsFor(int pageIndex, double zoom)
    {
        if (_searchMatches.Count == 0)
            return [];
        var toDip = 96.0 / 72 * zoom;
        var highlights = new List<SearchHighlight>();
        for (var m = 0; m < _searchMatches.Count; m++)
        {
            if (_searchMatches[m].PageIndex != pageIndex)
                continue;
            foreach (var rect in _searchMatches[m].Rects)
                highlights.Add(new SearchHighlight(
                    rect.X * toDip, rect.Y * toDip, rect.Width * toDip, rect.Height * toDip,
                    IsCurrent: m == CurrentSearchMatch - 1));
        }
        return highlights;
    }

    /// <summary>Re-stamps every page slot whose highlights changed (bitmaps are kept).</summary>
    private void ApplySearchHighlights()
    {
        for (var i = 0; i < Pages.Count; i++)
        {
            var highlights = HighlightsFor(i, ZoomFactor);
            if (!highlights.SequenceEqual(Pages[i].Highlights))
                Pages[i] = Pages[i] with { Highlights = highlights };
        }
    }

    private void ScrollToCurrentMatch()
    {
        var (pageIndex, rects) = _searchMatches[CurrentSearchMatch - 1];
        SearchScrollRequested?.Invoke(ContentTargetFor(pageIndex, rects));
    }

    /// <summary>
    /// Where page-space rectangles sit in the scroll content, in DIPs — for a search
    /// hit, and for the keyboard-focused region (#2), which is revealed by the same rules.
    /// </summary>
    internal SearchScrollTarget ContentTargetFor(int pageIndex, IReadOnlyList<PdfRect> rects)
    {
        var toDip = 96.0 / 72 * ZoomFactor;

        // Mirrors the layout math in OnPagesScrollViewChanged: 24 panel padding,
        // 16 spacing. The panel is centre-aligned, so a page's left edge sits at the
        // padding whenever the content is wide enough to scroll at all — which is the
        // only case the horizontal offset matters.
        var top = 24d;
        for (var i = 0; i < pageIndex && i < Pages.Count; i++)
            top += Pages[i].Height + 16;

        // A match can wrap across lines; take the union so the whole hit is targeted.
        var left = rects.Min(r => r.X) * toDip;
        var right = rects.Max(r => r.X + r.Width) * toDip;
        var matchTop = rects.Min(r => r.Y) * toDip;
        var matchBottom = rects.Max(r => r.Y + r.Height) * toDip;

        return new SearchScrollTarget(
            24 + left, top + matchTop, right - left, matchBottom - matchTop);
    }

    /// <summary>Hit-tests a click (page-space points, top-left origin): form fields, then body text.</summary>
    public PageHit HitTestPage(int pageIndex, PdfPoint point)
    {
        if (_document is null)
            return new PageHit(PageHitKind.None);
        using var page = _document.GetPage(pageIndex);
        return page.HitTest(point);
    }

    public async Task ToggleCheckboxAsync(int pageIndex, PdfFormField field)
    {
        if (_document is null)
            return;
        await DoEditAsync(new CheckboxToggleOperation(_document, pageIndex, field));
    }

    public async Task ApplyFormTextAsync(int pageIndex, PdfFormField field, string newValue)
    {
        if (_document is null || newValue == field.Value)
            return;
        await DoEditAsync(new FormTextEditOperation(_document, pageIndex, field, newValue));
    }

    public async Task AddMarkAsync(int pageIndex, PdfRect squareBounds)
    {
        if (_document is null)
            return;
        await DoEditAsync(new AddMarkOperation(_document, pageIndex, squareBounds, MarkStyle));
    }

    public async Task MoveSignatureAsync(int pageIndex, string annotationId, PdfRect oldBounds, PdfRect newBounds)
    {
        if (_document is null || oldBounds == newBounds)
            return;
        await DoEditAsync(new MoveSignatureOperation(_document, pageIndex, annotationId, oldBounds, newBounds));
    }

    /// <summary>Removes a MegaPDF stamp — routes by id prefix (mark vs. signature).</summary>
    public async Task RemoveStampAsync(int pageIndex, string annotationId, PdfRect bounds)
    {
        if (_document is null)
            return;
        IPageEditOperation op = annotationId.StartsWith("sig:", StringComparison.Ordinal)
            ? new RemoveSignatureOperation(_document, pageIndex, annotationId, bounds)
            : new RemoveMarkOperation(_document, pageIndex, annotationId, bounds, MarkStyle);
        await DoEditAsync(op);
    }

    // --- Signature library & placement (SDD §3.3) ---

    private readonly SignatureLibrary _signatureLibrary = new();

    public ObservableCollection<SignatureItem> Signatures { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlacementHintVisibility), nameof(PlacementHint))]
    private SignatureItem? _pendingSignature;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlacementHintVisibility), nameof(PlacementHint))]
    private bool _isWhiteoutMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlacementHintVisibility), nameof(PlacementHint))]
    private bool _isTextBoxMode;

    /// <summary>The Redact tool is armed (#173): the next drag marks rather than covers.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlacementHintVisibility), nameof(PlacementHint))]
    private bool _isRedactMode;

    public Visibility PlacementHintVisibility =>
        PendingSignature is not null || IsWhiteoutMode || IsTextBoxMode || IsRedactMode
            ? Visibility.Visible : Visibility.Collapsed;

    public string PlacementHint =>
        PendingSignature is not null ? Strings.PlaceSignatureHint(PendingSignature.Name)
        : IsWhiteoutMode ? Strings.WhiteoutHint
        : IsRedactMode ? Strings.RedactHint
        : IsTextBoxMode ? Strings.TextBoxHint
        : "";

    public void StartWhiteoutMode()
    {
        if (Busy.IsBusy)
            return;
        if (!Capabilities.CanEditContent)
        {
            IsRestrictedNoticeOpen = true; // #131: the owner does not allow changes
            return;
        }
        CancelPlacementModes();
        IsWhiteoutMode = true;
        PreparePageCheck(CurrentPage - 1); // a tool armed: its page's #139 check starts now (#145)
    }

    /// <summary>
    /// Arms the Redact tool (#173). Redaction changes the document, so it needs the modify
    /// permission, exactly as covering does (ADR-004 decision 2).
    /// </summary>
    public void StartRedactMode()
    {
        if (Busy.IsBusy)
            return;
        if (!Capabilities.CanEditContent)
        {
            IsRestrictedNoticeOpen = true;
            return;
        }
        CancelPlacementModes();
        IsRedactMode = true;
        PreparePageCheck(CurrentPage - 1);
    }

    public void StartTextBoxMode()
    {
        if (Busy.IsBusy)
            return;
        if (!Capabilities.CanAddText)
        {
            IsRestrictedNoticeOpen = true;
            return;
        }
        CancelPlacementModes();
        IsTextBoxMode = true;
        PreparePageCheck(CurrentPage - 1);
    }

    public void CancelPlacementModes()
    {
        PendingSignature = null;
        IsWhiteoutMode = false;
        IsTextBoxMode = false;
        IsRedactMode = false;
    }

    public async Task AddWhiteoutAsync(int pageIndex, PdfRect bounds)
    {
        if (_document is null || bounds.Width < 4 || bounds.Height < 4)
            return;
        await DoEditAsync(new AddWhiteoutOperation(_document, pageIndex, bounds));
    }

    public async Task RemoveWhiteoutAsync(int pageIndex, int objectIndex, PdfRect bounds)
    {
        if (_document is null)
            return;
        await DoEditAsync(new RemoveWhiteoutOperation(_document, pageIndex, objectIndex, bounds));
    }

    // --- Redaction (SDD §3.8 / F7, #173) ---

    /// <summary>
    /// How many areas are marked across the document: what the save path asks before it
    /// offers the confirmation.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRedactionMarks))]
    private int _redactionMarkCount;

    public bool HasRedactionMarks => RedactionMarkCount > 0;

    private (int PageIndex, int MarkId, PdfRect Bounds)? _selectedRedactionMark;

    /// <summary>
    /// Marks the dragged area (#173). Nothing is removed and nothing on the page changes: a
    /// mark is the core's own and is never written to the file.
    /// </summary>
    public async Task AddRedactionMarkAsync(int pageIndex, PdfRect bounds)
    {
        if (_document is null || bounds.Width < 4 || bounds.Height < 4)
            return;
        // A drag across text marks the text, grown to whole glyphs; a drag across a picture
        // marks the rectangle.
        var marked = false;
        using (var page = _document.GetPage(pageIndex))
            marked = page.MarkTextForRedaction(bounds).Count > 0;
        if (!marked)
            await DoEditAsync(new MarkForRedactionOperation(_document, pageIndex, bounds));
        RefreshRedactionMarks();
    }

    /// <summary>The marks on a page, for the overlay that draws them.</summary>
    public IReadOnlyList<RedactionMark> RedactionMarksOn(int pageIndex)
    {
        if (_document is null)
            return [];
        using var page = _document.GetPage(pageIndex);
        return page.GetRedactionMarks();
    }

    public void RefreshRedactionMarks() =>
        RedactionMarkCount = _document?.RedactionMarkCount ?? 0;

    /// <summary>Selects the mark under the point, so ✕ or Delete removes the right one.</summary>
    public bool SelectRedactionMarkAt(int pageIndex, PdfPoint point)
    {
        if (_document is null)
            return false;
        using var page = _document.GetPage(pageIndex);
        foreach (var mark in page.GetRedactionMarks())
        {
            if (point.X < mark.Bounds.X || point.X > mark.Bounds.X + mark.Bounds.Width ||
                point.Y < mark.Bounds.Y || point.Y > mark.Bounds.Y + mark.Bounds.Height)
            {
                continue;
            }
            _selectedRedactionMark = (pageIndex, mark.MarkId, mark.Bounds);
            return true;
        }
        return false;
    }

    public async Task<bool> RemoveSelectedRedactionMarkAsync()
    {
        if (_document is null || _selectedRedactionMark is not { } mark)
            return false;
        await DoEditAsync(new RemoveRedactionMarkOperation(_document, mark.PageIndex, mark.MarkId, mark.Bounds));
        _selectedRedactionMark = null;
        RefreshRedactionMarks();
        return true;
    }

    /// <summary>
    /// Sizes offered for added text (#43). A short list, not a free-entry number box:
    /// the job is "match the form I am filling in", and six presets cover it.
    /// </summary>
    public IReadOnlyList<double> TextSizes { get; } = [8, 10, 12, 14, 18, 24];

    /// <summary>
    /// The size and face the last added box was given. Sticky for the session, so
    /// filling six fields on one form is not six trips through the pickers. Not
    /// persisted — a new document is usually a new job.
    /// </summary>
    public TextStyleChoice LastTextStyle { get; private set; } =
        new(12, StandardTextBoxFonts.Default);

    public async Task AddTextBoxAsync(int pageIndex, PdfPoint topLeft, string text,
                                      string fontName = StandardTextBoxFonts.Default,
                                      double fontSize = 12)
    {
        if (_document is null || string.IsNullOrWhiteSpace(text))
            return;
        LastTextStyle = new TextStyleChoice(fontSize, fontName);
        await DoEditAsync(new AddTextBoxOperation(
            _document, pageIndex, text.Trim(), fontSize, topLeft, fontName));
    }

    /// <summary>
    /// Changes an added box's text, size and face together (#43) — one undoable edit,
    /// anchored to the corner the box already sits on so it does not wander.
    /// </summary>
    public async Task RestyleTextBoxAsync(int pageIndex, PdfTextRun run, string text,
                                          string fontName, double fontSize)
    {
        if (_document is null || string.IsNullOrWhiteSpace(text))
            return;
        var trimmed = text.Trim();
        if (trimmed == run.Text && fontName == (run.TextBoxFont ?? StandardTextBoxFonts.Default)
            && Math.Abs(fontSize - run.FontSize) < 0.01)
        {
            return;
        }
        LastTextStyle = new TextStyleChoice(fontSize, fontName);
        await DoEditAsync(new RestyleTextBoxOperation(
            _document, pageIndex, run.ObjectIndex, run, trimmed, fontName, fontSize));
    }

    /// <summary>Repositions an added text box (drag/nudge, SDD §3.3). False when nothing moved (#139: Cancel).</summary>
    public async Task<bool> MoveTextBoxAsync(int pageIndex, int objectIndex, PdfRect oldBounds, PdfRect newBounds)
    {
        if (_document is null || oldBounds == newBounds)
            return false;
        return await DoEditAsync(new MoveTextBoxOperation(_document, pageIndex, objectIndex, oldBounds, newBounds));
    }

    /// <summary>Removes an added text box (✕/Delete on the selection).</summary>
    public async Task RemoveTextBoxAsync(int pageIndex, int objectIndex, PdfRect bounds)
    {
        if (_document is null)
            return;
        // The run's text/font is only needed to recreate it during crash-recovery replay.
        var run = HitTestPage(pageIndex, bounds.Center).TextRun
            ?? new PdfTextRun(objectIndex, "", bounds, "Helvetica", 12);
        await DoEditAsync(new RemoveTextBoxOperation(_document, pageIndex, objectIndex, run));
    }

    public void LoadSignatures()
    {
        Signatures.Clear();
        foreach (var entry in _signatureLibrary.All)
            Signatures.Add(ToItem(entry));
        Signatures.CollectionChanged -= OnSignaturesChanged;
        Signatures.CollectionChanged += OnSignaturesChanged;
        OnSignaturesChanged(this, null);
    }

    /// <summary>The flyout shows either the library or the empty-state copy, never both (#100).</summary>
    public Visibility HasSignaturesVisibility => Signatures.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoSignaturesVisibility => Signatures.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

    private void OnSignaturesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs? e)
    {
        OnPropertyChanged(nameof(HasSignaturesVisibility));
        OnPropertyChanged(nameof(NoSignaturesVisibility));
    }

    /// <summary>Renames a library signature in place; the card keeps its position.</summary>
    public async Task RenameSignatureAsync(SignatureItem item, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0 || newName == item.Name)
            return;
        try
        {
            _signatureLibrary.Rename(item.Id, newName);
            var index = Signatures.IndexOf(item);
            if (index >= 0)
                Signatures[index] = item with { Name = newName };
            if (PendingSignature == item)
                PendingSignature = Signatures.FirstOrDefault(s => s.Id == item.Id);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Strings.CouldNotRenameSignatureTitle, UserFacing.Describe(ex));
        }
    }

    private static SignatureItem ToItem(SignatureEntry entry) =>
        new(entry.Id, entry.Name, entry.PngPath, new BitmapImage(new Uri(entry.PngPath)));

    public async Task AddSignatureFromImageAsync(SignatureImage image, string name)
    {
        try
        {
            var png = await SignatureImageProcessor.EncodePngAsync(image);
            var entry = _signatureLibrary.Add(name, png);
            Signatures.Add(ToItem(entry));
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Strings.CouldNotAddSignatureTitle, UserFacing.Describe(ex));
        }
    }

    public void RemoveSignatureFromLibrary(SignatureItem item)
    {
        _signatureLibrary.Remove(item.Id);
        Signatures.Remove(item);
    }

    public void SelectSignatureForPlacement(SignatureItem item)
    {
        if (!Capabilities.CanSign)
        {
            IsRestrictedNoticeOpen = true; // #131: the owner does not allow annotations
            return;
        }
        PendingSignature = item;
    }

    public void CancelSignaturePlacement() => PendingSignature = null;

    /// <summary>Places the pending signature centered on the clicked point (SDD §3.3: 180pt default width).</summary>
    public async Task PlacePendingSignatureAsync(int pageIndex, PdfPoint point)
    {
        if (_document is null || PendingSignature is null)
            return;
        var pending = PendingSignature;
        PendingSignature = null;

        try
        {
            var image = await SignatureImageProcessor.LoadPngAsync(pending.PngPath);

            const double defaultWidthPoints = 180;
            var width = defaultWidthPoints;
            var height = width * image.Height / image.Width;

            // Clamp within the page.
            var pageView = Pages[pageIndex];
            double pageW = pageView.Width * 72 / 96, pageH = pageView.Height * 72 / 96;
            var x = Math.Clamp(point.X - width / 2, 0, Math.Max(0, pageW - width));
            var y = Math.Clamp(point.Y - height / 2, 0, Math.Max(0, pageH - height));

            await DoEditAsync(new AddSignatureOperation(
                _document, pageIndex, image.Bgra, image.Width, image.Height, new PdfRect(x, y, width, height)));
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Strings.CouldNotPlaceSignatureTitle, UserFacing.Describe(ex));
        }
    }

    /// <summary>Applies an edit through the undo stack; false when it was not applied (restricted, or Cancel on the #139 warning).</summary>
    /// <remarks>
    /// One change at a time (#145): while a change waits on its page check or its warning is
    /// on screen, a second is refused rather than queued, so a second ContentDialog — which
    /// WinUI throws on — is never asked for. A failure other than the text guard's refusal is
    /// reported here rather than escaping an async handler.
    /// </remarks>
    private async Task<bool> DoEditAsync(IPageEditOperation op)
    {
        // The central gate (#131): every entry point above checks first so no editor
        // opens, and this is what holds if one is ever missed.
        if (!Capabilities.Allows(op))
        {
            IsRestrictedNoticeOpen = true;
            return false;
        }
        if (Busy.IsBusy || _document is not { } document)
            return false;

        using (var busy = Busy.Begin(Strings.BusyApplying, scope: BusyScope.Page, pageIndex: op.PageIndex))
        {
            try
            {
                // #139: whiteouts and text boxes make PDFium rewrite the page, which on a few
                // pages changes parts the person never touched. Never refused; asked once per
                // page. The page's check started when it was shown; the change waits for it at
                // most 1.5 s and then applies without a warning (#145).
                if (PageRegenerationWarnings.RegeneratesUnjudged(op) && !_pageWarnings.IsSettled(op.PageIndex))
                {
                    busy.SetLabel(Strings.BusyCheckingPage);
                    var answer = await _pageWarnings.AskAsync(document, op);
                    if (!ReferenceEquals(document, _document))
                        return false;
                    if (answer == PageCheckAnswer.WouldChange)
                    {
                        if (!await ConfirmPageRewriteAsync() || !ReferenceEquals(document, _document))
                            return false;
                        _pageWarnings.Settle(op.PageIndex);
                    }
                    busy.SetLabel(Strings.BusyApplying);
                }
                await Task.Run(() => _undoStack.Do(op));
            }
            catch (TextEditException)
            {
                throw; // the text guard's refusal: the caller words it
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(Strings.CouldNotEditTitle, UserFacing.Describe(ex));
                return false;
            }
        }
        if (!ReferenceEquals(document, _document))
            return false;
        _journal.Record(op.ToJournalEntry(inverse: false));
        _editCount++;
        HasUnsavedChanges = true;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        await RefreshPageAsync(op.PageIndex);
        return true;
    }

    /// <summary>The #139 warning: Continue applies the change, Cancel leaves the page as it is.</summary>
    private async Task<bool> ConfirmPageRewriteAsync()
    {
        if (window.Content?.XamlRoot is not { } xamlRoot)
            return true;
        var dialog = new ContentDialog
        {
            Title = Strings.PageRewriteWarningTitle,
            Content = Strings.PageRewriteWarning,
            PrimaryButtonText = Strings.Continue,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        return await dialog.ShowOneAtATimeAsync() == ContentDialogResult.Primary;
    }

    public async Task ApplyLineEditAsync(int pageIndex, PdfTextLine line, string newText)
    {
        if (_document is null || string.IsNullOrEmpty(newText) || newText == line.Text)
            return;

        var op = new LineEditOperation(_document, pageIndex, line, newText);
        try
        {
            await DoEditAsync(op);
        }
        catch (TextEditException ex)
        {
            await ShowErrorAsync(Strings.CannotEditTextTitle, UserFacing.Describe(ex));
            return;
        }

        // Non-modal, non-blocking notice per SDD §3.1 tier 2.
        IsFontNoticeOpen = op.LastOutcome == TextEditOutcome.EditedWithSubstitutedFont;
    }

    /// <summary>Removes a whole visual line (the user cleared it in the inline editor).</summary>
    public async Task DeleteLineAsync(int pageIndex, PdfTextLine line)
    {
        if (_document is null)
            return;
        try
        {
            await DoEditAsync(new DeleteLineOperation(_document, pageIndex, line));
        }
        catch (TextEditException ex)
        {
            await ShowErrorAsync(Strings.CannotEditTextTitle, UserFacing.Describe(ex));
        }
    }

    /// <summary>
    /// The layout guard's refusal of the first run of <paramref name="line"/> that cannot be
    /// changed without PDFium disturbing the rest of the page (#118), or null when every run
    /// can. Asked before the editor opens, so the person is told why before they type (#128).
    /// </summary>
    public LayoutVerdict? LineLayoutRefusal(int pageIndex, PdfTextLine line)
    {
        if (_document is null)
            return NotJudged;
        using var page = _document.GetPage(pageIndex);
        foreach (var run in line.Runs)
        {
            if (run.TextBoxId is not null)
                continue;
            // A run that is no longer text is refused, as IsTextEditable always has.
            var verdict = page.GetLayoutVerdict(run.ObjectIndex) ?? NotJudged;
            if (!verdict.Editable)
                return verdict;
        }
        return null;
    }

    private static readonly LayoutVerdict NotJudged = new(false, LayoutCause.RewriteFailed, LayoutArea.None, 0, 0, 0);

    public Task ShowLayoutRefusalAsync(LayoutVerdict refusal) =>
        ShowErrorAsync(Strings.CannotEditTextTitle, UserFacing.DescribeLayout(refusal));

    [ObservableProperty]
    private bool _isFontNoticeOpen;

    /// <summary>SDD §3.1 tier 3: clicking a scanned page explains why nothing is editable.</summary>
    [ObservableProperty]
    private bool _isScannedHintOpen;

    private bool CanSave() => IsDocumentOpen && !Busy.IsBusy;

    /// <summary>
    /// The confirmation #173 asks for, before either save path writes anything: what
    /// redaction does, that it cannot be undone once saved, and Save as a copy as the
    /// DEFAULT action. Returns false when the user cancelled or the redaction refused, in
    /// which case nothing was removed and nothing must be written.
    /// </summary>
    private async Task<bool> ConfirmAndApplyRedactionsAsync(bool alreadySavingACopy)
    {
        if (_document is null || !HasRedactionMarks || window.Content?.XamlRoot is not { } xamlRoot)
            return true;

        var dialog = new ContentDialog
        {
            Title = Strings.RedactConfirmTitle,
            Content = Strings.RedactConfirmBody + "\n\n" +
                      (RedactionMarkCount == 1 ? Strings.RedactMarkCountOne : Strings.RedactMarkCount(RedactionMarkCount)),
            // Save as a copy is primary because redaction cannot be taken back once saved:
            // the reversible choice should be the one Enter lands on.
            PrimaryButtonText = alreadySavingACopy ? Strings.Save : Strings.RedactSaveCopy,
            SecondaryButtonText = alreadySavingACopy ? null : Strings.RedactOverwrite,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        switch (await dialog.ShowOneAtATimeAsync())
        {
            case ContentDialogResult.Primary when !alreadySavingACopy:
                // Apply first: a refusal must not open a picker for a file that will not be
                // written. Then hand the whole save over to the copy path.
                if (!await ApplyRedactionsAsync())
                    return false;
                await SaveAsCommand.ExecuteAsync(null);
                return false;   // the copy path has saved; the caller must not save again
            case ContentDialogResult.Primary:
            case ContentDialogResult.Secondary:
                return await ApplyRedactionsAsync();
            default:
                return false;
        }
    }

    /// <summary>
    /// Applies every mark and says what happened. True when the document was redacted and
    /// may be saved; false when the redaction refused, and then NOTHING was removed.
    /// </summary>
    public async Task<bool> ApplyRedactionsAsync()
    {
        if (_document is not { } document || !HasRedactionMarks)
            return true;
        try
        {
            RedactionReport report;
            using (Busy.Begin(Strings.BusyApplying))
                report = await Task.Run(document.ApplyRedactions);
            if (!report.Applied)
            {
                var refusal = report.Refusals.Count > 0 ? report.Refusals[0] : default;
                await ShowErrorAsync(Strings.RedactRefusedTitle,
                    Strings.RedactRefusedBody(refusal.PageIndex + 1) + "\n\n" + DescribeRefusal(refusal));
                RefreshRedactionMarks();
                return false;
            }
            // The removed content is gone, and so is every way back to it: the undo stack
            // held the very objects the redaction freed (#173), and the journal starts again.
            _undoStack.Clear();
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
            _selectedRedactionMark = null;
            RefreshRedactionMarks();
            await RefreshPagesAfterRedactionAsync();
            RedactionSummaryText = DescribeRedaction(report.Counts);
            IsRedactionSummaryOpen = true;
            return true;
        }
        catch (DocumentRestrictedException)
        {
            await ShowErrorAsync(Strings.RedactRefusedTitle, Strings.RedactNeedsPermission);
            return false;
        }
        catch (RedactionFailedException)
        {
            await ShowErrorAsync(Strings.RedactRefusedTitle, Strings.RedactFailed);
            return false;
        }
    }

    /// <summary>Every page's raster is stale once content has been removed.</summary>
    private async Task RefreshPagesAfterRedactionAsync()
    {
        _keyboardMaps.Clear();
        for (var i = 0; i < Pages.Count; i++)
        {
            if (Pages[i].Source is not null)
                Pages[i] = Placeholder(i, Pages[i].PointsWidth, Pages[i].PointsHeight);
        }
        await UpdateViewportAsync(_viewFirst, _viewLast);
    }

    /// <summary>The summary after saving, e.g. "3 areas redacted: 41 characters, 1 image".</summary>
    [ObservableProperty]
    private string _redactionSummaryText = "";

    [ObservableProperty]
    private bool _isRedactionSummaryOpen;

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

    internal static string DescribeRefusal(RedactionRefusal refusal) => refusal.Reason switch
    {
        RedactionRefusalReason.Type3Font or RedactionRefusalReason.FontCannotRedraw => Strings.RedactRefusedFont,
        RedactionRefusalReason.FormXObject => Strings.RedactRefusedShared,
        RedactionRefusalReason.LayoutGuard => Strings.RedactRefusedLayout,
        _ => Strings.RedactRefusedOther,
    };

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_document is null || DocumentPath is null || Busy.IsBusy)
            return;

        if (!await ConfirmAndApplyRedactionsAsync(alreadySavingACopy: false))
            return;

        var document = _document;
        var path = DocumentPath;
        var editsBefore = _editCount;
        try
        {
            bool flattened;
            // "Saving…", then "Checking the saved file…", with editing and file commands waiting (#145).
            using (var busy = Busy.Begin(Strings.BusySaving))
            {
                flattened = await FlattenIfConfiguredAsync(document);
                // Atomic save protocol (SDD §3.4): temp file in place, flush, swap.
                await Task.Run(() => VerifiedSave.ToPath(Engine, document, path, stage => busy.SetLabel(SaveStageLabel(stage))));
            }
            // D3 (#145): an edit made while the save ran is not in the file, so the document
            // stays unsaved and its journal keeps it.
            if (_editCount == editsBefore)
            {
                HasUnsavedChanges = false;
                _journal.MarkSaved(path);
            }
            // Nothing on screen changes except the dot on Save, so a screen reader had no
            // way to know the save finished — NVDA said nothing at all (#190).
            Announced?.Invoke(Strings.SavedAnnouncement(Path.GetFileName(path)));
            if (flattened)
                await OnDocumentFlattenedAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Strings.CouldNotSaveTitle, $"{UserFacing.Describe(ex)}\n\n{Strings.TrySaveAsHint}");
        }
    }

    /// <summary>Applies the flatten-on-save setting. Returns true when the document was baked.</summary>
    private async Task<bool> FlattenIfConfiguredAsync(IPdfDocument document)
    {
        if (!FlattenOnSave)
            return false;
        await Task.Run(document.FlattenAllPages);
        return true;
    }

    /// <summary>After flattening, prior edits reference annotations that no longer exist.</summary>
    private async Task OnDocumentFlattenedAsync()
    {
        _undoStack.Clear();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        _keyboardMaps.Clear(); // stamps are page content now (#2)
        for (var i = 0; i < Pages.Count; i++)
        {
            if (Pages[i].Source is not null)
                Pages[i] = Placeholder(i, Pages[i].PointsWidth, Pages[i].PointsHeight);
        }
        await UpdateViewportAsync(_viewFirst, _viewLast);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsAsync()
    {
        if (_document is null || DocumentPath is null)
            return;

        // Marks still on the document mean this Save As is the first time they are applied;
        // the confirmation then offers the copy, which is what this already is.
        var wasRedacted = HasRedactionMarks;
        if (wasRedacted && !await ConfirmAndApplyRedactionsAsync(alreadySavingACopy: true))
            return;

        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add(Strings.PdfDocumentFilter, [".pdf"]);
        picker.SuggestedFileName = wasRedacted || RedactionSummaryText.Length > 0
            ? Path.GetFileNameWithoutExtension(DocumentPath) + Strings.RedactedFileSuffix
            : Strings.EditedFileName(Path.GetFileNameWithoutExtension(DocumentPath));
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;

        var document = _document;
        if (Busy.IsBusy || !ReferenceEquals(document, _document))
            return;
        var editsBefore = _editCount;
        try
        {
            bool flattened;
            using (var busy = Busy.Begin(Strings.BusySaving))
            {
                flattened = await FlattenIfConfiguredAsync(document);
                await Task.Run(() => VerifiedSave.ToPath(Engine, document, file.Path, stage => busy.SetLabel(SaveStageLabel(stage))));
            }
            // The newly saved file becomes the active document (SDD §3.4).
            DocumentPath = file.Path;
            if (_editCount == editsBefore) // D3 (#145)
            {
                HasUnsavedChanges = false;
                _journal.MarkSaved(file.Path);
            }
            if (flattened)
                await OnDocumentFlattenedAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Strings.CouldNotSaveTitle, UserFacing.Describe(ex));
        }
    }

    // --- Shrink for email: a smaller COPY, original untouched ---

    private const double EmailTargetDpi = 150;
    private const double JpegQuality = 0.75;

    /// <summary>Shrinking rewrites the document's images, which is modify (#131).</summary>
    private bool CanShrink() => IsDocumentOpen && Capabilities.CanShrink && !Busy.IsBusy;

    [RelayCommand(CanExecute = nameof(CanShrink))]
    private async Task ShrinkForEmailAsync()
    {
        if (DocumentPath is null || Busy.IsBusy)
            return;
        if (HasUnsavedChanges)
        {
            await ShowErrorAsync(Strings.SaveFirstTitle, Strings.SaveFirstBody);
            return;
        }

        var sourcePath = DocumentPath;
        var original = _document;
        if (original is null)
            return;
        var originalBytes = new FileInfo(sourcePath).Length;

        // Work on a fresh copy from disk so the open document is never degraded. Opened
        // like the document, so a protected one opens with its own password (#134).
        IPdfDocument copy;
        try
        {
            using (Busy.Begin(Strings.BusyShrinking))
                copy = await Task.Run(() => Engine.OpenLike(original, sourcePath));
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Strings.CouldNotShrinkTitle, UserFacing.Describe(ex));
            return;
        }

        try
        {
            var replaced = 0;
            // "Making a smaller copy…", with editing and file commands waiting (#145).
            using (Busy.Begin(Strings.BusyShrinking))
            {
                foreach (var image in await Task.Run(copy.GetImages))
                {
                    var targetWidth = (int)Math.Round(image.DisplayWidthPoints / 72 * EmailTargetDpi);
                    var targetHeight = (int)Math.Round(image.DisplayHeightPoints / 72 * EmailTargetDpi);
                    var oversized = image.PixelWidth > targetWidth * 1.2;
                    if ((!oversized && image.StoredByteLength < 100_000) || image.StoredByteLength < 8_000)
                        continue;
                    targetWidth = Math.Clamp(targetWidth, 8, image.PixelWidth);
                    targetHeight = Math.Clamp(targetHeight, 8, image.PixelHeight);

                    var img = image;
                    var pixels = await Task.Run(() => copy.RenderImageAt(img, targetWidth, targetHeight));
                    var jpeg = await SignatureImageProcessor.EncodeJpegAsync(
                        new SignatureImage(pixels.Bgra, pixels.PixelWidth, pixels.PixelHeight), JpegQuality);
                    if (jpeg.Length >= image.StoredByteLength * 0.9)
                        continue; // not worth it

                    await Task.Run(() => copy.ReplaceImageWithJpeg(img, jpeg));
                    replaced++;
                }
            }

            if (replaced == 0)
            {
                await ShowErrorAsync(Strings.NothingToShrinkTitle, Strings.NothingToShrinkBody);
                return;
            }

            var picker = new FileSavePicker();
            picker.FileTypeChoices.Add(Strings.PdfDocumentFilter, [".pdf"]);
            picker.SuggestedFileName = Strings.SmallerFileName(Path.GetFileNameWithoutExtension(sourcePath));
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;

            try
            {
                using (var busy = Busy.Begin(Strings.BusySaving))
                    await Task.Run(() => VerifiedSave.ToPath(Engine, copy, file.Path, stage => busy.SetLabel(SaveStageLabel(stage))));
            }
            catch (Exception ex)
            {
                // Save and Save As both catch here; this one did not, and the
                // difference was not cosmetic. App.UnhandledException sets
                // Handled = false, so a throw after the save dialog terminated
                // the process — no message, no file, and nothing in WER because
                // the crash log goes to GetTempPath(). Observed 2026-07-24 in the
                // packaged build and mistaken for a shrink-specific failure.
                await ShowErrorAsync(Strings.CouldNotShrinkTitle, UserFacing.Describe(ex));
                return;
            }

            var newBytes = new FileInfo(file.Path).Length;
            await ShowErrorAsync(Strings.SmallerCopySavedTitle,
                Strings.SmallerCopySavedBody(originalBytes / 1024.0 / 1024, newBytes / 1024.0 / 1024, Path.GetFileName(file.Path)));
        }
        finally
        {
            copy.Dispose();
        }
    }

    private bool CanUndo() => _undoStack.CanUndo && !Busy.IsBusy;
    private bool CanRedo() => _undoStack.CanRedo && !Busy.IsBusy;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (Busy.IsBusy || !_undoStack.CanUndo)
            return;
        var op = _undoStack.PeekUndo;
        using (Busy.Begin(Strings.BusyApplying, scope: BusyScope.Page, pageIndex: (op as IPageEditOperation)?.PageIndex ?? -1))
        {
            try
            {
                await Task.Run(() => _undoStack.Undo());
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(Strings.CouldNotEditTitle, UserFacing.Describe(ex));
                return;
            }
        }
        _editCount++;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        HasUnsavedChanges = true;
        if (op is IPageEditOperation pageEdit)
        {
            _journal.Record(pageEdit.ToJournalEntry(inverse: true));
            await RefreshPageAsync(pageEdit.PageIndex);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task RedoAsync()
    {
        if (Busy.IsBusy || !_undoStack.CanRedo)
            return;
        var op = _undoStack.PeekRedo;
        using (Busy.Begin(Strings.BusyApplying, scope: BusyScope.Page, pageIndex: (op as IPageEditOperation)?.PageIndex ?? -1))
        {
            try
            {
                await Task.Run(() => _undoStack.Redo());
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(Strings.CouldNotEditTitle, UserFacing.Describe(ex));
                return;
            }
        }
        _editCount++;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        HasUnsavedChanges = true;
        if (op is IPageEditOperation pageEdit)
        {
            _journal.Record(pageEdit.ToJournalEntry(inverse: false));
            await RefreshPageAsync(pageEdit.PageIndex);
        }
    }

    // --- Crash recovery (SDD §3.4) ---

    /// <summary>Crashed sessions found on disk, newest first.</summary>
    public IReadOnlyList<RecoverableSession> FindRecoverableSessions() => _journal.FindRecoverableSessions();

    /// <summary>Reopens the crashed session's document and replays its journal.</summary>
    public async Task RestoreSessionAsync(RecoverableSession session)
    {
        // Load before OpenDocumentAsync — BeginSession truncates this same file.
        IReadOnlyList<JournalEntry> entries;
        try
        {
            entries = RecoveryJournal.LoadEntries(session.JournalPath);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Strings.CouldNotRestoreTitle, UserFacing.Describe(ex));
            return;
        }

        await OpenDocumentAsync(session.DocumentPath);
        if (_document is null || DocumentPath != session.DocumentPath || entries.Count == 0)
            return;

        var doc = _document;
        // Re-journalled before the replay, not after (#145): the open truncated the journal
        // they came from, so a replay that fails part way must not lose them a second time.
        foreach (var entry in entries)
            _journal.Record(entry);

        int applied;
        try
        {
            using (Busy.Begin(Strings.BusyRestoring))
                applied = await Task.Run(() => JournalReplayer.Replay(doc, entries));
        }
        catch (Exception ex)
        {
            // This used to escape as an unhandled exception and end the app (#145).
            applied = entries.Count;
            await ShowErrorAsync(Strings.CouldNotRestoreTitle, UserFacing.Describe(ex));
        }
        if (!ReferenceEquals(doc, _document))
            return;

        _editCount++;
        HasUnsavedChanges = applied > 0;
        _keyboardMaps.Clear(); // the replay changed what is on the pages (#2)
        // Drop any already-rendered bitmaps (they predate the replay) and re-render the viewport.
        for (var i = 0; i < Pages.Count; i++)
        {
            if (Pages[i].Source is not null)
                Pages[i] = Placeholder(i, Pages[i].PointsWidth, Pages[i].PointsHeight);
        }
        await UpdateViewportAsync(_viewFirst, _viewLast);
    }

    /// <summary>Called when the window closes with the user's consent — nothing left to recover.</summary>
    public void EndJournalSession() => _journal.EndSession();

    // --- Password: unlock, set, change, remove (#131, ADR-004 §3, §5, §6) ---

    /// <summary>
    /// Offers the owner password for a restricted document and reopens it with full
    /// access. A password that opens it but not as its owner (the user password) is
    /// wrong for this purpose, and says so like any other wrong password.
    /// </summary>
    public async Task UnlockAsync()
    {
        if (_document is null || DocumentPath is null || Busy.IsBusy)
            return;
        // Unlocking reopens the document from its file: unsaved changes would stay behind (#145).
        if (!await ConfirmSaveChangesAsync())
            return;

        var path = DocumentPath;
        var fileName = Path.GetFileName(path);
        var wrong = false;
        while (true)
        {
            var ownerPassword = await ShowPasswordPromptAsync(Strings.UnlockTitle, Strings.UnlockPrompt(fileName),
                Strings.UnlockWrong, Strings.Unlock, wrongPassword: wrong);
            if (ownerPassword is null)
                return; // cancelled: the document stays open, restricted

            IPdfDocument doc;
            try
            {
                using (Busy.Begin(Strings.BusyOpening))
                    doc = await Task.Run(() => Engine.Open(path, ownerPassword));
            }
            catch (PdfLoadException ex) when (ex.IsPasswordError)
            {
                wrong = true;
                continue;
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(Strings.CouldNotOpenTitle, UserFacing.Describe(ex));
                return;
            }

            if (!doc.Security.HasFullAccess)
            {
                doc.Dispose();
                wrong = true;
                continue;
            }

            if (DocumentPath != path)
            {
                doc.Dispose(); // another document opened while the prompt was up
                return;
            }

            SaveViewState();
            await AdoptDocumentAsync(doc, path, openedWithPassword: true, ++_openGeneration);
            return;
        }
    }

    /// <summary>
    /// The toolbar's Password command. Without full access it can only offer the owner
    /// password; otherwise it sets a password on an unprotected document, or changes or
    /// removes the one it has (ADR-004 §5: one password, every permission).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SecurityAsync()
    {
        if (_document is null || DocumentPath is null)
            return;

        var security = _document.Security;
        if (!security.HasFullAccess)
        {
            await UnlockAsync();
            return;
        }

        var fileName = Path.GetFileName(DocumentPath);
        if (!security.IsEncrypted)
        {
            if (await ShowNewPasswordDialogAsync(Strings.SetPasswordTitle, Strings.SetPasswordButton, fileName) is { } password)
                await ApplySecurityAsync(password, Strings.PasswordSetNotice);
            return;
        }

        switch (await ShowProtectedDialogAsync(fileName))
        {
            case ContentDialogResult.Primary:
                if (await ShowNewPasswordDialogAsync(Strings.ChangePasswordTitle, Strings.ChangePasswordButton, fileName) is { } changed)
                    await ApplySecurityAsync(changed, Strings.PasswordChangedNotice);
                break;
            case ContentDialogResult.Secondary:
                await ApplySecurityAsync(null, Strings.PasswordRemovedNotice);
                break;
        }
    }

    /// <summary>
    /// Setting, changing or removing security is a save (ADR-004 §6): the document,
    /// unsaved edits included, goes to its own file through the same atomic, verified
    /// write Save uses — checked by opening the copy with the new password, or without one
    /// — and the saved file is reopened, so the open document matches what is on disk.
    /// A refusal or failed write leaves the original untouched.
    /// </summary>
    /// <param name="newPassword">The new password, or null to remove security.</param>
    private async Task ApplySecurityAsync(string? newPassword, string doneMessage)
    {
        if (_document is null || DocumentPath is null || Busy.IsBusy)
            return;

        var document = _document;
        var path = DocumentPath;
        var flattened = false;
        try
        {
            // A save like any other (#145): "Saving…", "Checking the saved file…", editing and file commands waiting.
            using var busy = Busy.Begin(Strings.BusySaving);
            flattened = await FlattenIfConfiguredAsync(document);
            await Task.Run(() =>
            {
                if (newPassword is null)
                    VerifiedSave.ToPathWithoutSecurity(Engine, document, path, stage => busy.SetLabel(SaveStageLabel(stage)));
                else
                    VerifiedSave.ToPathWithSecurity(Engine, document, path, newPassword, ownerPassword: null, PdfPermissions.All,
                        stage => busy.SetLabel(SaveStageLabel(stage)));
            });
        }
        catch (Exception ex)
        {
            if (flattened)
                await OnDocumentFlattenedAsync();
            await ShowErrorAsync(Strings.CouldNotChangeSecurityTitle, UserFacing.Describe(ex));
            return;
        }

        HasUnsavedChanges = false;
        _journal.MarkSaved(path);
        await OpenDocumentAsync(path, newPassword);
        if (_document is not null && DocumentPath == path)
        {
            SecurityNotice = doneMessage;
            IsSecurityNoticeOpen = true;
        }
    }

    /// <summary>New password and its confirmation; null on cancel. Validates before closing.</summary>
    private async Task<string?> ShowNewPasswordDialogAsync(string title, string primaryText, string fileName)
    {
        if (window.Content?.XamlRoot is not { } xamlRoot)
            return null;

        var first = new PasswordBox { PlaceholderText = Strings.NewPasswordPlaceholder };
        var second = new PasswordBox { PlaceholderText = Strings.ConfirmPasswordPlaceholder };
        AutomationProperties.SetName(first, Strings.NewPasswordPlaceholder);
        AutomationProperties.SetName(second, Strings.ConfirmPasswordPlaceholder);
        var problem = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetLiveSetting(problem, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = Strings.SetPasswordBody(fileName), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(first);
        panel.Children.Add(second);
        panel.Children.Add(problem);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = primaryText,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var message = first.Password.Length == 0 ? Strings.PasswordEmpty
                : first.Password != second.Password ? Strings.PasswordsDontMatch
                : null;
            if (message is null)
                return;
            args.Cancel = true;
            problem.Text = message;
            problem.Visibility = Visibility.Visible;
        };
        first.Loaded += (_, _) => first.Focus(FocusState.Programmatic);

        return await dialog.ShowOneAtATimeAsync() == ContentDialogResult.Primary ? first.Password : null;
    }

    /// <summary>A protected document with full access: Primary changes, Secondary removes.</summary>
    private async Task<ContentDialogResult> ShowProtectedDialogAsync(string fileName)
    {
        if (window.Content?.XamlRoot is not { } xamlRoot)
            return ContentDialogResult.None;

        var dialog = new ContentDialog
        {
            Title = Strings.DocumentPasswordTitle,
            Content = new TextBlock { Text = Strings.ProtectedBody(fileName), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Strings.ChangePasswordEllipsis,
            SecondaryButtonText = Strings.RemovePassword,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        return await dialog.ShowOneAtATimeAsync();
    }

    /// <summary>Password prompt: opening a protected PDF, or unlocking a restricted one. Returns null on cancel.</summary>
    private async Task<string?> ShowPasswordPromptAsync(string title, string prompt, string wrongPrompt, string primaryText, bool wrongPassword)
    {
        if (window.Content?.XamlRoot is not { } xamlRoot)
            return null;

        var box = new PasswordBox { PlaceholderText = Strings.PasswordPlaceholder };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = wrongPassword ? wrongPrompt : prompt,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        });
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = primaryText,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);

        return await dialog.ShowOneAtATimeAsync() == ContentDialogResult.Primary && box.Password.Length > 0
            ? box.Password
            : null;
    }

    internal async Task ShowErrorAsync(string title, string message)
    {
        if (window.Content?.XamlRoot is not { } xamlRoot)
            return;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = Strings.OK,
            XamlRoot = xamlRoot,
        };
        await dialog.ShowOneAtATimeAsync();
    }
}

/// <summary>The size and face an added text box is being given (#43).</summary>
public sealed record TextStyleChoice(double FontSize, string FontName);
