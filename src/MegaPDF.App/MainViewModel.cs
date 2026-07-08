using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;

namespace MegaPDF.App;

/// <summary>One rendered page: bitmap plus display size in DIPs.</summary>
public sealed record PageView(int Index, ImageSource Source, double Width, double Height);

public partial class MainViewModel(Window window) : ObservableObject
{
    private static readonly IPdfEngine Engine = new PdfiumEngine();

    private readonly UndoStack _undoStack = new();
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

    public bool IsDocumentOpen => DocumentPath is not null;

    public string OpenDocumentName => DocumentPath is null ? "" : Path.GetFileName(DocumentPath);

    // Unsaved-changes dot convention (SDD §2.2).
    public string WindowTitle =>
        DocumentPath is null ? "MegaPDF"
        : $"{(HasUnsavedChanges ? "● " : "")}{OpenDocumentName} — MegaPDF";

    public string SaveButtonLabel => HasUnsavedChanges ? "Save ●" : "Save";

    public string PageIndicator => PageCount > 0 ? $"Page 1 of {PageCount}" : "";

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
        Pages.Clear();
        PageCount = doc.PageCount;

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
        // Render at the monitor's rasterization scale so pages are crisp at 100% zoom.
        var scale = window.Content?.XamlRoot?.RasterizationScale ?? 1.0;

        var (rendered, widthDips, heightDips) = await Task.Run(() =>
        {
            using var page = doc.GetPage(pageIndex);
            var wDips = page.Width * 96 / 72;
            var hDips = page.Height * 96 / 72;
            var pixels = page.Render((int)(wDips * scale), (int)(hDips * scale));
            return (pixels, wDips, hDips);
        });

        var bitmap = new WriteableBitmap(rendered.PixelWidth, rendered.PixelHeight);
        using (var pixelStream = bitmap.PixelBuffer.AsStream())
            pixelStream.Write(rendered.Bgra, 0, rendered.Bgra.Length);
        bitmap.Invalidate();

        return new PageView(pageIndex, bitmap, widthDips, heightDips);
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

    public async Task RemoveMarkAsync(int pageIndex, string annotationId, PdfRect markBounds)
    {
        if (_document is null)
            return;
        await DoEditAsync(new RemoveMarkOperation(_document, pageIndex, annotationId, markBounds));
    }

    private async Task DoEditAsync(IPageEditOperation op)
    {
        await Task.Run(() => _undoStack.Do(op));
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
            await Task.Run(() => _undoStack.Do(op));
        }
        catch (TextEditException ex)
        {
            await ShowErrorAsync("Can't edit this text", ex.Message);
            return;
        }

        HasUnsavedChanges = true;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        // Non-modal, non-blocking notice per SDD §3.1 tier 2.
        IsFontNoticeOpen = op.LastOutcome == TextEditOutcome.EditedWithSubstitutedFont;
        await RefreshPageAsync(pageIndex);
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
            await RefreshPageAsync(pageEdit.PageIndex);
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
            await RefreshPageAsync(pageEdit.PageIndex);
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
