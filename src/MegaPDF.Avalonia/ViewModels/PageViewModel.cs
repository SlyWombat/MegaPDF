using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MegaPDF.Avalonia.Rendering;
using MegaPDF.Core.Engine;

namespace MegaPDF.Avalonia.ViewModels;

/// <summary>
/// One page. Knows its size in points from the moment the document opens, so the
/// scroll extent is right before anything rasterises, and rasterises only when the
/// view actually realises it (SDD §4.5: opening a long document must not cost a
/// render per page).
///
/// Deliberately does NOT hold a live <see cref="IPdfPage"/>. Every edit operation
/// opens its own page handle and mutates the document underneath, so a cached
/// handle goes stale the moment a checkbox is ticked. Opening one per render is the
/// cost of being able to edit at all.
/// </summary>
public sealed partial class PageViewModel : ObservableObject, IDisposable
{
    private readonly IPdfDocument _document;
    private double _renderedZoom;
    private double _renderedDpiScale;

    internal PageViewModel(IPdfDocument document, int index, double pointWidth, double pointHeight)
    {
        _document = document;
        Index = index;
        PointWidth = pointWidth;
        PointHeight = pointHeight;
    }

    public int Index { get; }

    /// <summary>Page size in PDF points — the intrinsic size, before zoom.</summary>
    public double PointWidth { get; }
    public double PointHeight { get; }

    /// <summary>
    /// Layout size in device-independent pixels. Bound by the item template so a page
    /// that has not been rasterised yet still occupies the right space — without this,
    /// virtualization collapses the scrollbar as you scroll.
    /// </summary>
    public double LayoutWidth => PointWidth * PageBitmap.PointsToPixels * Zoom;
    public double LayoutHeight => PointHeight * PageBitmap.PointsToPixels * Zoom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LayoutWidth))]
    [NotifyPropertyChangedFor(nameof(LayoutHeight))]
    private double _zoom = 1.0;

    // Highlights are positioned in the same space as the page surface rather than
    // baked into the raster, so a zoom change repositions them without forcing a
    // re-render — and searching never invalidates a single bitmap.
    partial void OnZoomChanged(double value) => RebuildHighlights();

    /// <summary>Search hits on this page, in device-independent pixels.</summary>
    public ObservableCollection<Highlight> Highlights { get; } = [];

    private IReadOnlyList<PdfRect> _matchRects = [];
    private int _currentMatch = -1;

    internal void SetMatches(IReadOnlyList<PdfRect> rects, int currentIndex)
    {
        _matchRects = rects;
        _currentMatch = currentIndex;
        RebuildHighlights();
    }

    private void RebuildHighlights()
    {
        Highlights.Clear();
        var scale = PageBitmap.PointsToPixels * Zoom;
        for (var i = 0; i < _matchRects.Count; i++)
        {
            var r = _matchRects[i];
            Highlights.Add(new Highlight(
                r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale, i == _currentMatch));
        }
    }

    [ObservableProperty]
    private WriteableBitmap? _image;

    /// <summary>Whether the view has realised this page and it currently holds a raster.</summary>
    internal bool IsRealised => Image is not null;

    /// <summary>
    /// Rasterises at the current zoom if what we have isn't already that. Cheap to call
    /// on every scroll or zoom tick — the equality guard is what makes it so.
    /// </summary>
    internal void EnsureRendered(double dpiScale)
    {
        if (Image is not null && _renderedZoom == Zoom && _renderedDpiScale == dpiScale)
            return;

        Rerender(dpiScale);
    }

    /// <summary>Unconditional re-raster — what an edit needs, since the zoom hasn't changed.</summary>
    internal void Rerender(double dpiScale)
    {
        using var page = _document.GetPage(Index);
        var next = PageBitmap.Render(page, Zoom, dpiScale);

        var previous = Image;
        Image = next;
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
    }
}

/// <summary>
/// One search hit, already in device-independent pixels relative to the page
/// surface. <paramref name="IsCurrent"/> distinguishes the hit the user is on from
/// the rest, which is the difference between "there are 40 matches" and "you are
/// looking at match 7".
/// </summary>
public sealed record Highlight(double X, double Y, double Width, double Height, bool IsCurrent)
{
    /// <summary>
    /// Position expressed as a margin inside a top-left aligned panel, rather than
    /// Canvas.Left/Top. An attached property set from an ItemContainerTheme resolves
    /// its binding against the enclosing x:DataType — the page, not the hit — so
    /// compiled bindings reject it. A margin is bound on the item itself and needs
    /// no container theme at all.
    /// </summary>
    public global::Avalonia.Thickness Margin => new(X, Y, 0, 0);

    /// <summary>The hit you are on is stronger than the ones you are not.</summary>
    public double Opacity => IsCurrent ? 0.55 : 0.28;
}
