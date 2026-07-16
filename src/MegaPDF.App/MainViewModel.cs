using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Recovery;
using MegaPDF.Core.Services;
using Microsoft.UI.Xaml;
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
    public string AccessibleName => $"Page {Index + 1}";
}

/// <summary>A library signature shown in the flyout.</summary>
public sealed record SignatureItem(Guid Id, string Name, string PngPath, ImageSource Thumbnail);

/// <summary>A recent document shown on the empty state.</summary>
public sealed record RecentDocument(string Name, string Path);

public partial class MainViewModel(Window window) : ObservableObject
{
    private static readonly IPdfEngine Engine = new PdfiumEngine();

    private readonly UndoStack _undoStack = new();
    private readonly RecoveryJournal _journal = new();
    private readonly RecentFiles _recentFiles = new();
    private readonly AppSettings _settings = new();

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

    public string? MostRecentDocument => _recentFiles.All.Count > 0 ? _recentFiles.All[0] : null;

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

    // --- Update bar states: available → downloading → staged (restart) ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateMessage), nameof(UpdateActionLabel))]
    private string? _updateAvailableVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateMessage), nameof(UpdateActionLabel))]
    private bool _updateDownloading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateMessage), nameof(UpdateActionLabel))]
    private bool _updateStaged;

    public bool IsUpdateBarOpen => UpdateAvailableVersion is not null;

    public string UpdateMessage =>
        UpdateStaged ? $"MegaPDF {UpdateAvailableVersion} is ready — it will be used the next time you open MegaPDF."
        : UpdateDownloading ? $"Getting MegaPDF {UpdateAvailableVersion}…"
        : $"A new version of MegaPDF is available ({UpdateAvailableVersion}).";

    public string UpdateActionLabel => UpdateStaged ? "Restart now" : "Update";

    public bool CheckForUpdates
    {
        get => _settings.CheckForUpdates;
        set => _settings.CheckForUpdates = value;
    }

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

    public void LoadRecentDocuments()
    {
        RecentDocuments.Clear();
        foreach (var path in _recentFiles.All)
            RecentDocuments.Add(new RecentDocument(Path.GetFileName(path), path));
        OnPropertyChanged(nameof(RecentDocumentsVisibility));
    }

    public ObservableCollection<PageView> Pages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(OpenDocumentName), nameof(EmptyStateVisibility), nameof(DocumentVisibility), nameof(IsDocumentOpen))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(SaveAsCommand), nameof(ShrinkForEmailCommand))]
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

    public string OpenDocumentName => DocumentPath is null ? "" : Path.GetFileName(DocumentPath);

    // Unsaved-changes dot convention (SDD §2.2).
    public string WindowTitle =>
        DocumentPath is null ? "MegaPDF"
        : $"{(HasUnsavedChanges ? "● " : "")}{OpenDocumentName} — MegaPDF";

    public string SaveButtonLabel => HasUnsavedChanges ? "Save ●" : "Save";

    public string PageIndicator => PageCount > 0 ? $"Page {CurrentPage} of {PageCount}" : "";

    public Visibility EmptyStateVisibility => IsDocumentOpen ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DocumentVisibility => IsDocumentOpen ? Visibility.Visible : Visibility.Collapsed;

    [RelayCommand]
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

    public async Task OpenDocumentAsync(string path)
    {
        SaveViewState(); // remember where we left the previous document
        var generation = ++_openGeneration;

        IPdfDocument doc;
        string? password = null;
        while (true)
        {
            try
            {
                var attempt = password;
                doc = await Task.Run(() => Engine.Open(path, attempt));
                break;
            }
            catch (PdfLoadException ex) when (ex.IsPasswordError)
            {
                password = await ShowPasswordPromptAsync(Path.GetFileName(path), wrongPassword: password is not null);
                if (password is null)
                    return; // user cancelled
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Couldn't open that file", ex.Message);
                return;
            }
        }

        if (generation != _openGeneration)
        {
            // A newer open superseded this one while it loaded.
            doc.Dispose();
            return;
        }

        var rememberedView = _recentFiles.FindEntry(path);
        _document?.Dispose();
        _document = doc;
        DocumentPath = path;
        HasUnsavedChanges = false;
        _undoStack.Clear();
        _journal.BeginSession(path);
        _recentFiles.Add(path);
        if (rememberedView is not null)
            ZoomPercent = Math.Clamp(rememberedView.ZoomPercent, MinZoom, MaxZoom);
        LoadRecentDocuments();
        OnPropertyChanged(nameof(RecentDocumentsVisibility));
        Pages.Clear();
        PageCount = doc.PageCount;
        CurrentPage = 1;

        // Fast size-only pass: geometry for every page, no rendering (SDD §4.2).
        var sizes = await Task.Run(() =>
        {
            var list = new List<(double W, double H)>(doc.PageCount);
            for (var i = 0; i < doc.PageCount; i++)
            {
                using var page = doc.GetPage(i);
                list.Add((page.Width, page.Height));
            }
            return list;
        });
        if (generation != _openGeneration)
            return;

        for (var i = 0; i < sizes.Count; i++)
            Pages.Add(Placeholder(i, sizes[i].W, sizes[i].H));

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
            pointsWidth * 96 / 72 * ZoomFactor, pointsHeight * 96 / 72 * ZoomFactor, []);

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
                foreach (var i in Enumerable.Range(lo, hi - lo + 1).OrderBy(i => Math.Abs(i - center)))
                {
                    if (generation != _openGeneration)
                        return;
                    if (Pages[i].Source is null)
                        Pages[i] = await RenderPageAsync(doc, i);
                    if (_viewportDirty)
                        break; // the window moved — restart with the new one
                }
            } while (_viewportDirty);
        }
        finally
        {
            _viewportUpdateRunning = false;
        }
    }

    private async Task<PageView> RenderPageAsync(IPdfDocument doc, int pageIndex)
    {
        // Render at monitor rasterization scale × zoom so pages stay crisp.
        var scale = (window.Content?.XamlRoot?.RasterizationScale ?? 1.0) * ZoomFactor;
        var zoom = ZoomFactor;

        var (rendered, pointsW, pointsH, regions) = await Task.Run(() =>
        {
            using var page = doc.GetPage(pageIndex);
            var pixels = page.Render((int)(page.Width * 96 / 72 * scale), (int)(page.Height * 96 / 72 * scale));
            return (pixels, page.Width, page.Height, BuildRegions(page));
        });

        var bitmap = new WriteableBitmap(rendered.PixelWidth, rendered.PixelHeight);
        using (var pixelStream = bitmap.PixelBuffer.AsStream())
            pixelStream.Write(rendered.Bgra, 0, rendered.Bgra.Length);
        bitmap.Invalidate();

        return new PageView(pageIndex, bitmap, pointsW, pointsH,
            pointsW * 96 / 72 * zoom, pointsH * 96 / 72 * zoom, regions);
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
    public string ZoomLabel => $"{ZoomPercent}%";

    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private async Task ZoomInAsync() => await SetZoomAsync(ZoomPercent + ZoomStep);

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private async Task ZoomOutAsync() => await SetZoomAsync(ZoomPercent - ZoomStep);

    private bool CanZoomIn() => ZoomPercent < MaxZoom;
    private bool CanZoomOut() => ZoomPercent > MinZoom;

    private async Task SetZoomAsync(int percent)
    {
        ZoomPercent = Math.Clamp(percent, MinZoom, MaxZoom);
        if (_document is null)
            return;
        // Resize every slot (placeholders included) and re-render just the viewport.
        for (var i = 0; i < Pages.Count; i++)
            Pages[i] = Placeholder(i, Pages[i].PointsWidth, Pages[i].PointsHeight);
        await UpdateViewportAsync(_viewFirst, _viewLast);
    }

    private async Task RefreshPageAsync(int pageIndex)
    {
        if (_document is null || pageIndex < 0 || pageIndex >= Pages.Count)
            return;
        Pages[pageIndex] = await RenderPageAsync(_document, pageIndex);
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

    public Visibility PlacementHintVisibility =>
        PendingSignature is not null || IsWhiteoutMode || IsTextBoxMode ? Visibility.Visible : Visibility.Collapsed;

    public string PlacementHint =>
        PendingSignature is not null ? $"Click on the page to place “{PendingSignature.Name}”"
        : IsWhiteoutMode ? "Drag across the area you want to cover — Esc cancels"
        : IsTextBoxMode ? "Click where the new text should go — Esc cancels"
        : "";

    public void StartWhiteoutMode()
    {
        CancelPlacementModes();
        IsWhiteoutMode = true;
    }

    public void StartTextBoxMode()
    {
        CancelPlacementModes();
        IsTextBoxMode = true;
    }

    public void CancelPlacementModes()
    {
        PendingSignature = null;
        IsWhiteoutMode = false;
        IsTextBoxMode = false;
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

    public async Task AddTextBoxAsync(int pageIndex, PdfPoint topLeft, string text)
    {
        if (_document is null || string.IsNullOrWhiteSpace(text))
            return;
        await DoEditAsync(new AddTextBoxOperation(_document, pageIndex, text.Trim(), 12, topLeft));
    }

    /// <summary>Repositions an added text box (drag/nudge, SDD §3.3).</summary>
    public async Task MoveTextBoxAsync(int pageIndex, int objectIndex, PdfRect oldBounds, PdfRect newBounds)
    {
        if (_document is null || oldBounds == newBounds)
            return;
        await DoEditAsync(new MoveTextBoxOperation(_document, pageIndex, objectIndex, oldBounds, newBounds));
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
            await ShowErrorAsync("Couldn't add that signature", ex.Message);
        }
    }

    public void RemoveSignatureFromLibrary(SignatureItem item)
    {
        _signatureLibrary.Remove(item.Id);
        Signatures.Remove(item);
    }

    public void SelectSignatureForPlacement(SignatureItem item) => PendingSignature = item;

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
            await ShowErrorAsync("Couldn't place the signature", ex.Message);
        }
    }

    private async Task DoEditAsync(IPageEditOperation op)
    {
        await Task.Run(() => _undoStack.Do(op));
        _journal.Record(op.ToJournalEntry(inverse: false));
        HasUnsavedChanges = true;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        await RefreshPageAsync(op.PageIndex);
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
            await ShowErrorAsync("Can't edit this text", ex.Message);
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
        await DoEditAsync(new DeleteLineOperation(_document, pageIndex, line));
    }

    [ObservableProperty]
    private bool _isFontNoticeOpen;

    /// <summary>SDD §3.1 tier 3: clicking a scanned page explains why nothing is editable.</summary>
    [ObservableProperty]
    private bool _isScannedHintOpen;

    private bool CanSave() => IsDocumentOpen;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_document is null || DocumentPath is null)
            return;

        var document = _document;
        var path = DocumentPath;
        try
        {
            var flattened = await FlattenIfConfiguredAsync(document);
            // Atomic save protocol (SDD §3.4): temp file in place, flush, swap.
            await Task.Run(() => AtomicFileWriter.Write(path, stream => document.Save(stream)));
            HasUnsavedChanges = false;
            _journal.MarkSaved(path);
            if (flattened)
                await OnDocumentFlattenedAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Couldn't save", $"{ex.Message}\n\nTry \"Save As\" to save a copy instead.");
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

        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("PDF document", [".pdf"]);
        picker.SuggestedFileName = $"{Path.GetFileNameWithoutExtension(DocumentPath)} - edited";
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));

        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;

        var document = _document;
        try
        {
            var flattened = await FlattenIfConfiguredAsync(document);
            await Task.Run(() => AtomicFileWriter.Write(file.Path, stream => document.Save(stream)));
            // The newly saved file becomes the active document (SDD §3.4).
            DocumentPath = file.Path;
            HasUnsavedChanges = false;
            _journal.MarkSaved(file.Path);
            if (flattened)
                await OnDocumentFlattenedAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Couldn't save", ex.Message);
        }
    }

    // --- Shrink for email: a smaller COPY, original untouched ---

    private const double EmailTargetDpi = 150;
    private const double JpegQuality = 0.75;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task ShrinkForEmailAsync()
    {
        if (DocumentPath is null)
            return;
        if (HasUnsavedChanges)
        {
            await ShowErrorAsync("Save first", "Save your changes, then shrink the saved file.");
            return;
        }

        var sourcePath = DocumentPath;
        var originalBytes = new FileInfo(sourcePath).Length;

        // Work on a fresh copy from disk so the open document is never degraded.
        IPdfDocument copy;
        try
        {
            copy = await Task.Run(() => Engine.Open(sourcePath));
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Couldn't shrink", ex.Message);
            return;
        }

        try
        {
            var replaced = 0;
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

            if (replaced == 0)
            {
                await ShowErrorAsync("Nothing to shrink", "The pictures in this document are already small.");
                return;
            }

            var picker = new FileSavePicker();
            picker.FileTypeChoices.Add("PDF document", [".pdf"]);
            picker.SuggestedFileName = $"{Path.GetFileNameWithoutExtension(sourcePath)} - smaller";
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;

            await Task.Run(() => AtomicFileWriter.Write(file.Path, stream => copy.Save(stream)));

            var newBytes = new FileInfo(file.Path).Length;
            await ShowErrorAsync("Smaller copy saved",
                $"Was {originalBytes / 1024.0 / 1024:F1} MB, now {newBytes / 1024.0 / 1024:F1} MB.\n\nSaved as {Path.GetFileName(file.Path)} — ready to email.");
        }
        finally
        {
            copy.Dispose();
        }
    }

    private bool CanUndo() => _undoStack.CanUndo;
    private bool CanRedo() => _undoStack.CanRedo;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        var op = _undoStack.PeekUndo;
        await Task.Run(() => _undoStack.Undo());
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
        var op = _undoStack.PeekRedo;
        await Task.Run(() => _undoStack.Redo());
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
        var entries = RecoveryJournal.LoadEntries(session.JournalPath);

        await OpenDocumentAsync(session.DocumentPath);
        if (_document is null || entries.Count == 0)
            return;

        var doc = _document;
        var applied = await Task.Run(() => JournalReplayer.Replay(doc, entries));

        // Re-journal the restored edits so a second crash before save is still covered.
        foreach (var entry in entries)
            _journal.Record(entry);

        HasUnsavedChanges = applied > 0;
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

    /// <summary>Password prompt for protected PDFs. Returns null on cancel.</summary>
    private async Task<string?> ShowPasswordPromptAsync(string fileName, bool wrongPassword)
    {
        if (window.Content?.XamlRoot is not { } xamlRoot)
            return null;

        var box = new PasswordBox { PlaceholderText = "Password" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = wrongPassword
                ? "That password wasn't right — try again."
                : $"“{fileName}” is protected. Enter its password to open it.",
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        });
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            Title = "Password required",
            Content = panel,
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);

        return await dialog.ShowAsync() == ContentDialogResult.Primary && box.Password.Length > 0
            ? box.Password
            : null;
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        if (window.Content?.XamlRoot is not { } xamlRoot)
            return;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = xamlRoot,
        };
        await dialog.ShowAsync();
    }
}
