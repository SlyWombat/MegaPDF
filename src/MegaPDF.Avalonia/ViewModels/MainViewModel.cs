using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;

namespace MegaPDF.Avalonia.ViewModels;

/// <summary>
/// The viewer shell. Deliberately NOT a port of src/MegaPDF.App/MainViewModel.cs
/// yet: that file is 1,199 lines in a WinUI project this one cannot reference, so
/// sharing it means either copying it (two drifting copies of the editing
/// behaviour) or extracting it into a net8.0 project both UIs reference. ADR-002
/// records that as an open decision — this class covers open/view/zoom only, so
/// the decision can be made on its own merits rather than under this increment.
///
/// File dialogs live in the view: Avalonia reaches them through the TopLevel's
/// IStorageProvider, so a path comes in and the view model stays UI-free.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly double[] ZoomStops =
        [0.5, 0.67, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0];

    private readonly IPdfEngine _engine = new PdfiumEngine();
    private IPdfDocument? _document;

    public ObservableCollection<PageViewModel> Pages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDocumentOpen))]
    private string? _documentPath;

    public bool IsDocumentOpen => _document is not null;

    [ObservableProperty]
    private string _status = "Open a PDF to get started.";

    /// <summary>
    /// Display scale of the monitor the window is on. Set by the view; feeding it
    /// into the raster is what keeps a page sharp on a retina Mac instead of
    /// upscaled (SDD §4.5).
    /// </summary>
    [ObservableProperty]
    private double _dpiScale = 1.0;

    [ObservableProperty]
    private double _zoom = 1.0;

    partial void OnZoomChanged(double value)
    {
        // Push zoom down so each page can report its layout size; the scroll
        // extent has to change even for pages that are not realised.
        foreach (var page in Pages)
            page.Zoom = value;
        RerenderRealisedPages();
    }

    partial void OnDpiScaleChanged(double value) => RerenderRealisedPages();

    public void Open(string path)
    {
        CloseDocument();

        try
        {
            _document = _engine.Open(path);
        }
        catch (PdfLoadException ex)
        {
            // Engine messages are already written for the person holding the
            // document, not the developer (SDD §2.2) — surface as-is.
            Status = ex.Message;
            return;
        }

        for (var i = 0; i < _document.PageCount; i++)
            Pages.Add(new PageViewModel(_document.GetPage(i)) { Zoom = Zoom });

        DocumentPath = path;
        OnPropertyChanged(nameof(IsDocumentOpen));
        Status = $"{Path.GetFileName(path)} — {_document.PageCount} page"
                 + (_document.PageCount == 1 ? "" : "s");
    }

    [RelayCommand]
    private void ZoomIn() => Zoom = NextStop(Zoom, forward: true);

    [RelayCommand]
    private void ZoomOut() => Zoom = NextStop(Zoom, forward: false);

    [RelayCommand]
    private void ZoomReset() => Zoom = 1.0;

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

    /// <summary>
    /// Only pages the view has realised carry a raster, so this re-renders exactly
    /// those and leaves the rest to render when they scroll in.
    /// </summary>
    private void RerenderRealisedPages()
    {
        foreach (var page in Pages)
            if (page.Image is not null)
                page.EnsureRendered(DpiScale);
    }

    private void CloseDocument()
    {
        foreach (var page in Pages)
            page.Dispose();
        Pages.Clear();

        _document?.Dispose();
        _document = null;
        DocumentPath = null;
    }

    public void Dispose()
    {
        CloseDocument();
        _engine.Dispose();
    }
}
