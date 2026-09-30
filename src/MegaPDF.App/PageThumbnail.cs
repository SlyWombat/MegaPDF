using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using MegaPDF.Core.Engine;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MegaPDF.App;

/// <summary>
/// One tile in the Pages pane (#174): a small raster of the page, its number, and the
/// index it currently stands at.
///
/// A separate object from <see cref="PageView"/>, and not an extra field on it, because the
/// two have opposite lifetimes. A <see cref="PageView"/> is an immutable record the viewport
/// loop <i>replaces</i> — on every scroll, zoom, tint change and edit — and the Pages pane's
/// <c>GridView</c> keeps its selection and its drag state on the item instances it was given.
/// Binding a grid's <c>SelectedItems</c> to a collection whose items are swapped out from
/// under it loses the selection every time a page renders. So the tile is a long-lived
/// <see cref="ObservableObject"/> that is created once per page and renumbered in place, the
/// way the Avalonia leg's page view model already is; what is index-keyed here is the
/// <see cref="Index"/>, and <see cref="Renumber"/> is what contract 10 says the app owes.
///
/// Its raster is its own and not a scaled copy of the page's: the page's bitmap exists only
/// while the page is inside the render window and is up to 128 MB when it is, where a tile is
/// ~40 KB, drawn once, and survives scrolling the document. That is what makes a pane of them
/// affordable on #147's thousand-page file — together with drawing them lazily, only for the
/// tiles the grid has actually realised.
/// </summary>
public sealed partial class PageThumbnail(IPdfDocument document, int index, double pointsWidth, double pointsHeight)
    : ObservableObject
{
    /// <summary>The tile's long edge in device-independent pixels; the short one follows the page.</summary>
    public const double BoxLongEdge = 112;

    private bool _pending;
    private bool _disposed;

    /// <summary>Where this page stands in the document now. Follows a page operation, never remembered across one.</summary>
    public int Index { get; private set; } = index;

    public double PointsWidth { get; private set; } = pointsWidth;
    public double PointsHeight { get; private set; } = pointsHeight;

    /// <summary>Laid out before the raster exists, so the grid's extent is right from the first frame.</summary>
    public double BoxWidth => PointsWidth >= PointsHeight ? BoxLongEdge : BoxLongEdge * PointsWidth / PointsHeight;

    public double BoxHeight => PointsWidth >= PointsHeight ? BoxLongEdge * PointsHeight / PointsWidth : BoxLongEdge;

    [ObservableProperty]
    private ImageSource? _source;

    /// <summary>The number under the tile — the number alone, which is what a page thumbnail wears.</summary>
    public string PageNumberLabel => (Index + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>
    /// What Narrator reads for the tile. "3" on its own is what a sighted person needs under a
    /// picture of the page and is useless without it, so the tile says "Page 3".
    /// </summary>
    public string AccessibleName => Strings.PageThumbnailName(Index + 1);

    /// <summary>Its new index after a page operation (#174). Nothing else about the page changed.</summary>
    internal void Renumber(int index)
    {
        if (Index == index)
            return;
        Index = index;
        OnPropertyChanged(nameof(Index));
        OnPropertyChanged(nameof(PageNumberLabel));
        OnPropertyChanged(nameof(AccessibleName));
    }

    /// <summary>
    /// The page's size after a rotation (#174). The core reports the <i>rotated</i> size, so a
    /// quarter turn swaps the tile's width and height; the raster is dropped rather than
    /// stretched, because its pixels are of the other way up.
    /// </summary>
    internal void Resize(double pointsWidth, double pointsHeight)
    {
        if (Math.Abs(PointsWidth - pointsWidth) < 0.01 && Math.Abs(PointsHeight - pointsHeight) < 0.01)
            return;
        PointsWidth = pointsWidth;
        PointsHeight = pointsHeight;
        OnPropertyChanged(nameof(PointsWidth));
        OnPropertyChanged(nameof(PointsHeight));
        OnPropertyChanged(nameof(BoxWidth));
        OnPropertyChanged(nameof(BoxHeight));
    }

    /// <summary>Throws the raster away so the next realisation draws it again (a rotation, an edit).</summary>
    internal void Invalidate() => Source = null;

    /// <summary>
    /// Draws the tile if it has no raster yet. Called from the UI thread when the grid realises
    /// the tile, and cheap to call again — which is what lets one call serve both a realisation
    /// and a rotation. The awaited continuation lands back on the UI thread, where
    /// <see cref="WriteableBitmap"/> may be built.
    /// </summary>
    internal async Task EnsureAsync(double rasterScale)
    {
        if (_disposed || _pending || Source is not null)
            return;
        _pending = true;
        try
        {
            var index = Index;
            var pixelWidth = Math.Max(1, (int)Math.Round(BoxWidth * rasterScale));
            var pixelHeight = Math.Max(1, (int)Math.Round(BoxHeight * rasterScale));
            // Untinted, always: the pane is a map of the document, not a second view of it, and
            // a sepia map of a sepia document says nothing the page itself does not.
            var rendered = await Task.Run(() =>
            {
                using var page = document.GetPage(index);
                return page.Render(pixelWidth, pixelHeight);
            });
            if (_disposed)
                return;
            var bitmap = new WriteableBitmap(rendered.PixelWidth, rendered.PixelHeight);
            using (var pixels = bitmap.PixelBuffer.AsStream())
                pixels.Write(rendered.Bgra, 0, rendered.Bgra.Length);
            bitmap.Invalidate();
            Source = bitmap;
        }
        catch (Exception ex)
        {
            // A tile that cannot be drawn is an empty tile with its number on it, never a
            // crash: this runs from a realisation handler, where a throw is unhandled. The
            // page itself says so in its own slot (#93); the pane does not need to repeat it.
            System.Diagnostics.Debug.WriteLine($"thumbnail {Index + 1} could not be drawn: {ex}");
        }
        finally
        {
            _pending = false;
        }
    }

    internal void Dispose()
    {
        _disposed = true;
        Source = null;
    }
}
