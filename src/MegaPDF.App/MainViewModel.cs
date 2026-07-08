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

/// <summary>One rendered page: bitmap, display size in DIPs, and its interaction map.</summary>
public sealed record PageView(int Index, ImageSource Source, double Width, double Height, IReadOnlyList<InteractiveRegion> Regions);

/// <summary>A library signature shown in the flyout.</summary>
public sealed record SignatureItem(Guid Id, string Name, string PngPath, ImageSource Thumbnail);

public partial class MainViewModel(Window window) : ObservableObject
{
    private static readonly IPdfEngine Engine = new PdfiumEngine();

    private readonly UndoStack _undoStack = new();
    private readonly RecoveryJournal _journal = new();
    private IPdfDocument? _document;
    private int _openGeneration;

    public ObservableCollection<PageView> Pages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(OpenDocumentName), nameof(EmptyStateVisibility), nameof(DocumentVisibility))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(SaveAsCommand))]
    private string? _documentPath;

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
        var generation = ++_openGeneration;

        IPdfDocument doc;
        try
        {
            doc = await Task.Run(() => Engine.Open(path));
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Couldn't open that file", ex.Message);
            return;
        }

        if (generation != _openGeneration)
        {
            // A newer open superseded this one while it loaded.
            doc.Dispose();
            return;
        }

        _document?.Dispose();
        _document = doc;
        DocumentPath = path;
        HasUnsavedChanges = false;
        _undoStack.Clear();
        _journal.BeginSession(path);
        Pages.Clear();
        PageCount = doc.PageCount;
        CurrentPage = 1;

        for (var i = 0; i < doc.PageCount && generation == _openGeneration; i++)
        {
            var pageView = await RenderPageAsync(doc, i);
            if (generation != _openGeneration)
                return;
            Pages.Add(pageView);
        }
    }

    private async Task<PageView> RenderPageAsync(IPdfDocument doc, int pageIndex)
    {
        // Render at monitor rasterization scale × zoom so pages stay crisp.
        var scale = (window.Content?.XamlRoot?.RasterizationScale ?? 1.0) * ZoomFactor;
        var zoom = ZoomFactor;

        var (rendered, widthDips, heightDips, regions) = await Task.Run(() =>
        {
            using var page = doc.GetPage(pageIndex);
            var wDips = page.Width * 96 / 72 * zoom;
            var hDips = page.Height * 96 / 72 * zoom;
            var pixels = page.Render((int)(page.Width * 96 / 72 * scale), (int)(page.Height * 96 / 72 * scale));
            return (pixels, wDips, hDips, BuildRegions(page));
        });

        var bitmap = new WriteableBitmap(rendered.PixelWidth, rendered.PixelHeight);
        using (var pixelStream = bitmap.PixelBuffer.AsStream())
            pixelStream.Write(rendered.Bgra, 0, rendered.Bgra.Length);
        bitmap.Invalidate();

        return new PageView(pageIndex, bitmap, widthDips, heightDips, regions);
    }

    /// <summary>Interaction map in HitTest priority order: stamps, form fields, squares, text.</summary>
    private static List<InteractiveRegion> BuildRegions(IPdfPage page)
    {
        var regions = new List<InteractiveRegion>();
        foreach (var stamp in page.GetStamps())
            regions.Add(new InteractiveRegion(stamp.Bounds, PageHitKind.StampAnnotation));
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
        foreach (var square in page.DetectCheckboxSquares())
            regions.Add(new InteractiveRegion(square, PageHitKind.DrawnCheckbox));
        foreach (var run in page.GetTextRuns())
            regions.Add(new InteractiveRegion(run.Bounds, PageHitKind.TextRun));
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
        var generation = _openGeneration;
        for (var i = 0; i < Pages.Count && generation == _openGeneration; i++)
            await RefreshPageAsync(i);
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
        await DoEditAsync(new AddMarkOperation(_document, pageIndex, squareBounds));
    }

    /// <summary>Removes a MegaPDF stamp — routes by id prefix (mark vs. signature).</summary>
    public async Task RemoveStampAsync(int pageIndex, string annotationId, PdfRect bounds)
    {
        if (_document is null)
            return;
        IPageEditOperation op = annotationId.StartsWith("sig:", StringComparison.Ordinal)
            ? new RemoveSignatureOperation(_document, pageIndex, annotationId, bounds)
            : new RemoveMarkOperation(_document, pageIndex, annotationId, bounds);
        await DoEditAsync(op);
    }

    // --- Signature library & placement (SDD §3.3) ---

    private readonly SignatureLibrary _signatureLibrary = new();

    public ObservableCollection<SignatureItem> Signatures { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlacementHintVisibility), nameof(PlacementHint))]
    private SignatureItem? _pendingSignature;

    public Visibility PlacementHintVisibility => PendingSignature is null ? Visibility.Collapsed : Visibility.Visible;
    public string PlacementHint => PendingSignature is null ? "" : $"Click on the page to place “{PendingSignature.Name}”";

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

    public async Task ApplyTextEditAsync(int pageIndex, PdfTextRun run, string newText)
    {
        if (_document is null || string.IsNullOrEmpty(newText) || newText == run.Text)
            return;

        var op = new TextEditOperation(_document, pageIndex, run, newText);
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

    [ObservableProperty]
    private bool _isFontNoticeOpen;

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
            // Atomic save protocol (SDD §3.4): temp file in place, flush, swap.
            await Task.Run(() => AtomicFileWriter.Write(path, stream => document.Save(stream)));
            HasUnsavedChanges = false;
            _journal.MarkSaved(path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Couldn't save", $"{ex.Message}\n\nTry \"Save As\" to save a copy instead.");
        }
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
            await Task.Run(() => AtomicFileWriter.Write(file.Path, stream => document.Save(stream)));
            // The newly saved file becomes the active document (SDD §3.4).
            DocumentPath = file.Path;
            HasUnsavedChanges = false;
            _journal.MarkSaved(file.Path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Couldn't save", ex.Message);
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
        var generation = _openGeneration;
        for (var i = 0; i < Pages.Count && generation == _openGeneration; i++)
            await RefreshPageAsync(i);
    }

    /// <summary>Called when the window closes with the user's consent — nothing left to recover.</summary>
    public void EndJournalSession() => _journal.EndSession();

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
