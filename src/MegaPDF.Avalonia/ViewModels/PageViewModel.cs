using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MegaPDF.Avalonia.Rendering;
using MegaPDF.Core.Engine;

namespace MegaPDF.Avalonia.ViewModels;

/// <summary>
/// One page. Knows its size in points from the moment the document opens, so the
/// scroll extent is right before anything is rasterised, and rasterises only when
/// the view actually realises it (SDD §4.5: opening a long document must not cost
/// a render per page).
/// </summary>
public sealed partial class PageViewModel : ObservableObject, IDisposable
{
    private readonly IPdfPage _page;
    private double _renderedZoom;
    private double _renderedDpiScale;

    internal PageViewModel(IPdfPage page)
    {
        _page = page;
        PointWidth = page.Width;
        PointHeight = page.Height;
    }

    internal int Index => _page.Index;

    /// <summary>Page size in PDF points — the intrinsic size, before zoom.</summary>
    internal double PointWidth { get; }
    internal double PointHeight { get; }

    /// <summary>
    /// Layout size in device-independent pixels. Bound by the item template so a
    /// page that has not been rasterised yet still occupies the right space —
    /// without this, virtualization collapses the scrollbar as you scroll.
    /// </summary>
    public double LayoutWidth => PointWidth * PageBitmap.PointsToPixels * Zoom;
    public double LayoutHeight => PointHeight * PageBitmap.PointsToPixels * Zoom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LayoutWidth))]
    [NotifyPropertyChangedFor(nameof(LayoutHeight))]
    private double _zoom = 1.0;

    [ObservableProperty]
    private WriteableBitmap? _image;

    /// <summary>
    /// Rasterises at the current zoom if what we have isn't already that. Cheap to
    /// call on every scroll or zoom tick — the equality guard is what makes it so.
    /// </summary>
    internal void EnsureRendered(double dpiScale)
    {
        if (Image is not null && _renderedZoom == Zoom && _renderedDpiScale == dpiScale)
            return;

        var previous = Image;
        Image = PageBitmap.Render(_page, Zoom, dpiScale);
        _renderedZoom = Zoom;
        _renderedDpiScale = dpiScale;
        previous?.Dispose();
    }

    /// <summary>Drops the raster but keeps the page, for when it scrolls out of view.</summary>
    internal void Unrender()
    {
        var previous = Image;
        Image = null;
        _renderedZoom = 0;
        previous?.Dispose();
    }

    public void Dispose()
    {
        var image = Image;
        Image = null;
        image?.Dispose();
        _page.Dispose();
    }
}
