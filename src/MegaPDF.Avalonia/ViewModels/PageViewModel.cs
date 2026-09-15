using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MegaPDF.Avalonia.Rendering;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Viewing;

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
///
/// Rasterising happens off the UI thread (#95): a poster-sized scan takes seconds
/// to decode, and doing that inside <c>ContainerPrepared</c> froze the window for
/// the whole of it. A render request carries a generation number; whichever
/// request is newest when a raster arrives is the one that gets shown, and a
/// raster for a zoom that has since changed triggers the next render itself.
/// </summary>
public sealed partial class PageViewModel : ObservableObject, IDisposable
{
    private readonly IPdfDocument _document;
    private double _renderedZoom;
    private double _renderedDpiScale;
    private double _pendingZoom = -1;
    private double _pendingDpiScale = -1;
    private int _renderGeneration;
    private bool _disposed;

    /// <summary>
    /// The raster of a page too large to render at its ideal size (see
    /// <see cref="RenderLimits"/>), kept across zoom steps and scroll-outs: past the
    /// clamp every zoom asks for the same pixels, and below it the view scales the
    /// same raster down, so decoding the page once is enough until an edit (#94).
    /// </summary>
    private WriteableBitmap? _cappedImage;

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
    partial void OnZoomChanged(double value)
    {
        RebuildHighlights();
        PlaceBusy();
    }

    // --- Page-level busy work (#145): the #139 check, the text-edit check, applying a change ---

    private PdfRect? _busyArea;

    /// <summary>A spinner on this page, with no particular line: at the top of the page.</summary>
    [ObservableProperty]
    private bool _showsBusyOnPage;

    /// <summary>A spinner under one line: the text-edit check, or the edit it gates.</summary>
    [ObservableProperty]
    private bool _showsBusyOnLine;

    [ObservableProperty]
    private string _busyLabel = "";

    [ObservableProperty]
    private global::Avalonia.Thickness _busyMargin;

    [ObservableProperty]
    private double _busyWidth = 48;

    /// <summary>Set by the main view model from its busy state.</summary>
    internal void ShowBusy(bool show, PdfRect? area, string label)
    {
        _busyArea = area;
        BusyLabel = label;
        ShowsBusyOnLine = show && area is not null;
        ShowsBusyOnPage = show && area is null;
        PlaceBusy();
    }

    /// <summary>Under the line, in the page surface's device-independent pixels.</summary>
    private void PlaceBusy()
    {
        if (_busyArea is not { } area)
            return;
        var scale = PageBitmap.PointsToPixels * Zoom;
        BusyMargin = new global::Avalonia.Thickness(area.X * scale, (area.Y + area.Height) * scale + 2, 0, 0);
        BusyWidth = Math.Max(48, area.Width * scale);
    }

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

    /// <summary>
    /// The engine could not rasterise this page (#93). The slot says so instead of
    /// staying a blank sheet, and stops asking until something changes.
    /// </summary>
    [ObservableProperty]
    private bool _renderFailed;

    /// <summary>Whether the view has realised this page and it currently holds a raster.</summary>
    internal bool IsRealised => Image is not null;

    /// <summary>Whether a raster is on its way for the current zoom (tests and diagnostics).</summary>
    internal bool IsRenderPending => _pendingZoom >= 0;

    /// <summary>
    /// Rasterises at the current zoom if what we have isn't already that, or already
    /// on its way. Cheap to call on every scroll or zoom tick — the equality guards
    /// are what make it so.
    /// </summary>
    internal void EnsureRendered(double dpiScale)
    {
        if (Image is not null && _renderedZoom == Zoom && _renderedDpiScale == dpiScale)
            return;
        if (_pendingZoom == Zoom && _pendingDpiScale == dpiScale)
            return;
        StartRender(dpiScale, invalidate: false);
    }

    /// <summary>
    /// What is interactive on this page, in HitTest priority order. Built once per
    /// raster and consulted in memory, so telling the cursor what it is over costs
    /// nothing — hit-testing the engine on every pointer move would mean opening a
    /// page per mouse movement (SDD §2.2: the cursor is the mode).
    /// </summary>
    internal IReadOnlyList<(PdfRect Bounds, PageHitKind Kind)> Regions { get; private set; } = [];

    /// <summary>
    /// Builds the interaction map if it is missing, WITHOUT rasterising.
    ///
    /// The two were coupled at first — the map was a by-product of Rerender — which
    /// made keyboard traversal (#2) force a full page render just to learn what was
    /// on a page, and made it impossible to test at all without a graphics stack.
    /// The map only needs the engine; the raster is a separate concern.
    /// </summary>
    internal void EnsureRegions()
    {
        if (Regions.Count > 0)
            return;
        using var page = _document.GetPage(Index);
        Regions = BuildRegions(page);
    }

    private static List<(PdfRect, PageHitKind)> BuildRegions(IPdfPage page)
    {
        var regions = new List<(PdfRect, PageHitKind)>();

        foreach (var stamp in page.GetStamps())
            regions.Add((stamp.Bounds, PageHitKind.StampAnnotation));
        // Before body text, so hovering added text offers the move affordance rather
        // than a caret.
        foreach (var box in page.GetTextBoxes())
            regions.Add((box.Bounds, PageHitKind.TextBox));
        foreach (var field in page.GetFormFields())
        {
            var kind = field.Kind switch
            {
                FormFieldKind.Text => PageHitKind.FormTextField,
                FormFieldKind.Checkbox or FormFieldKind.RadioButton => PageHitKind.FormCheckbox,
                _ => PageHitKind.None,
            };
            if (kind != PageHitKind.None)
                regions.Add((field.Bounds, kind));
        }
        foreach (var line in page.GetTextLines())
            regions.Add((line.Bounds, PageHitKind.TextRun));
        foreach (var whiteout in page.GetWhiteouts())
            regions.Add((whiteout.Bounds, PageHitKind.Whiteout));
        foreach (var square in page.DetectCheckboxSquares())
            regions.Add((square, PageHitKind.DrawnCheckbox));

        return regions;
    }

    /// <summary>
    /// The same regions in reading order — top to bottom, then left to right within
    /// a line — which is what Tab traversal needs (#2). <see cref="Regions"/> is
    /// ordered by hit-test priority instead, because a click wants the topmost
    /// thing and the keyboard wants the next thing.
    ///
    /// The ordering itself lives in Core (<see cref="PageReadingOrder"/>) so the
    /// Windows app tabs through a page in exactly the same order.
    /// </summary>
    internal IReadOnlyList<(PdfRect Bounds, PageHitKind Kind)> RegionsInReadingOrder() =>
        PageReadingOrder.Order(Regions);

    /// <summary>What is under this point, in page space. Empty means bare page.</summary>
    internal PageHitKind KindAt(PdfPoint point)
    {
        foreach (var (bounds, kind) in Regions)
        {
            if (point.X >= bounds.X && point.X <= bounds.X + bounds.Width
                && point.Y >= bounds.Y && point.Y <= bounds.Y + bounds.Height)
                return kind;
        }
        return PageHitKind.None;
    }

    /// <summary>
    /// Unconditional re-raster — what an edit needs, since the zoom hasn't changed.
    /// The current raster stays on screen until the new one arrives.
    /// </summary>
    internal void Rerender(double dpiScale) => StartRender(dpiScale, invalidate: true);

    private void StartRender(double dpiScale, bool invalidate)
    {
        if (_disposed)
            return;

        var zoom = Zoom;
        var generation = ++_renderGeneration;
        _pendingZoom = zoom;
        _pendingDpiScale = dpiScale;

        if (invalidate)
        {
            // The page changed, so a retained raster is wrong now. It may still be
            // the one on screen; it is disposed when the replacement lands.
            _cappedImage = null;
        }
        else if (_cappedImage is { } kept)
        {
            // Served from the retained raster at any zoom (#94); the view scales it.
            Apply(kept, Regions, zoom, dpiScale, generation, wasCapped: true);
            return;
        }

        var idealWidth = PointWidth * PageBitmap.PointsToPixels * zoom * dpiScale;
        var idealHeight = PointHeight * PageBitmap.PointsToPixels * zoom * dpiScale;
        var capped = RenderLimits.IsCapped(idealWidth, idealHeight);
        var (pixelWidth, pixelHeight) = RenderLimits.Fit(idealWidth, idealHeight);
        var document = _document;
        var index = Index;
        // An empty slot gets a quarter-size preview first (#94): a sixteenth of the
        // work for anything fill-rate bound, so a heavy page shows something within a
        // frame or two. A slot that already has a raster needs none — the item
        // template stretches the old raster to the new layout size until the full
        // render lands, which is the same effect for free.
        var wantPreview = Image is null && !capped;
        var (previewWidth, previewHeight) = RenderLimits.Fit(idealWidth / 4, idealHeight / 4);

        Task.Run(() =>
        {
            using var page = document.GetPage(index);
            if (wantPreview)
            {
                var small = page.Render(previewWidth, previewHeight);
                Dispatcher.UIThread.Post(() => ApplyPreview(small, dpiScale, generation));
            }
            var rendered = page.Render(pixelWidth, pixelHeight);
            // Refreshed alongside the raster, because an edit changes both.
            var regions = BuildRegions(page);
            return (rendered, regions);
        }).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || generation != _renderGeneration)
                return; // superseded by a newer request, or the document is gone
            if (task.IsFaulted)
            {
                Console.Error.WriteLine($"page {index + 1}: render failed: {task.Exception?.GetBaseException().Message}");
                _pendingZoom = -1;
                _pendingDpiScale = -1;
                RenderFailed = true;
                return;
            }
            var (rendered, regions) = task.Result;
            Apply(PageBitmap.FromRenderedPage(rendered), regions, zoom, dpiScale, generation, capped);
        }), TaskScheduler.Default);
    }

    /// <summary>UI thread: the stand-in raster, shown only while the slot is still empty.</summary>
    private void ApplyPreview(RenderedPage small, double dpiScale, int generation)
    {
        if (_disposed || generation != _renderGeneration || Image is not null)
            return;
        // Not recorded as rendered: EnsureRendered keeps treating the page as pending
        // until the full raster arrives through Apply.
        Image = PageBitmap.FromRenderedPage(small);
    }

    /// <summary>UI thread: shows a raster, then chases the zoom if it moved while rendering.</summary>
    private void Apply(WriteableBitmap next, IReadOnlyList<(PdfRect, PageHitKind)> regions,
                       double zoom, double dpiScale, int generation, bool wasCapped)
    {
        if (_disposed || generation != _renderGeneration)
            return;

        var previous = Image;
        Image = next;
        Regions = regions;
        RenderFailed = false;
        _renderedZoom = zoom;
        _renderedDpiScale = dpiScale;
        _pendingZoom = -1;
        _pendingDpiScale = -1;
        if (wasCapped)
        {
            var displaced = _cappedImage;
            _cappedImage = next;
            if (!ReferenceEquals(displaced, next) && !ReferenceEquals(displaced, previous))
                displaced?.Dispose();
            RetainedRasters.Touch(this);
        }
        if (!ReferenceEquals(previous, next) && !ReferenceEquals(previous, _cappedImage))
            previous?.Dispose();

        // The zoom moved while this was rendering; the page is on screen, so it
        // follows up itself rather than waiting for a scroll to notice.
        if (Zoom != zoom)
            EnsureRendered(dpiScale);
    }

    /// <summary>Drops the raster but keeps the page, for when it scrolls out of view.</summary>
    internal void Unrender()
    {
        _renderGeneration++; // an in-flight raster for a page nobody is looking at is dropped
        _pendingZoom = -1;
        _pendingDpiScale = -1;
        var previous = Image;
        Image = null;
        _renderedZoom = 0;
        if (!ReferenceEquals(previous, _cappedImage))
            previous?.Dispose();
    }

    /// <summary>Lets go of the retained raster; what is on screen stays until it is replaced.</summary>
    private void ReleaseRetained()
    {
        var capped = _cappedImage;
        _cappedImage = null;
        if (capped is not null && !ReferenceEquals(capped, Image))
            capped.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        _renderGeneration++;
        RetainedRasters.Forget(this);
        var image = Image;
        Image = null;
        var capped = _cappedImage;
        _cappedImage = null;
        image?.Dispose();
        if (!ReferenceEquals(capped, image))
            capped?.Dispose();
    }

    /// <summary>
    /// Bounds how many clamped rasters stay alive across the whole app: each is up
    /// to 128 MB, and a scanned book could otherwise retain one per page. UI thread only.
    /// </summary>
    private static class RetainedRasters
    {
        private const int Capacity = 2;
        private static readonly LinkedList<PageViewModel> Recent = new();

        public static void Touch(PageViewModel page)
        {
            Recent.Remove(page);
            Recent.AddFirst(page);
            while (Recent.Count > Capacity)
            {
                var oldest = Recent.Last!.Value;
                Recent.RemoveLast();
                oldest.ReleaseRetained();
            }
        }

        public static void Forget(PageViewModel page) => Recent.Remove(page);
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
}
