using MegaPDF.Core.Engine;

namespace MegaPDF.App;

/// <summary>
/// A few page rasters that were rendered at the <see cref="Core.Viewing.RenderLimits"/>
/// clamp rather than at their ideal size, kept by page index so a zoom step can reuse
/// them instead of decoding the page again (#94). Least-recently-used eviction with a
/// tiny capacity: each entry is up to 128 MB.
///
/// Keyed on the page colours as well as the page (#510): the tint is baked into the
/// raster by the core (<c>MEGAPDF_RENDER_SEPIA</c> / <c>_NIGHT</c>, #509), so a raster
/// kept from before the choice changed holds the *old* tint's pixels. Keying rather
/// than clearing is what makes switching back and forth free, and it is why a tint
/// change costs the visible window and nothing more.
///
/// Thread-safe, because renders run on the thread pool while the view model clears
/// entries from the UI thread.
/// </summary>
internal sealed class CappedRenderCache(int capacity)
{
    /// <summary>A page raster is only the same raster if it was drawn in the same colours.</summary>
    private readonly record struct Key(int PageIndex, PageTint Tint);

    private readonly Dictionary<Key, RenderedPage> _pages = new();
    private readonly LinkedList<Key> _order = new();
    private readonly object _lock = new();

    public bool TryGet(int pageIndex, PageTint tint, out RenderedPage rendered)
    {
        var key = new Key(pageIndex, tint);
        lock (_lock)
        {
            if (_pages.TryGetValue(key, out var found))
            {
                _order.Remove(key);
                _order.AddFirst(key);
                rendered = found;
                return true;
            }
            rendered = null!;
            return false;
        }
    }

    public void Put(int pageIndex, PageTint tint, RenderedPage rendered)
    {
        var key = new Key(pageIndex, tint);
        lock (_lock)
        {
            _pages[key] = rendered;
            _order.Remove(key);
            _order.AddFirst(key);
            while (_order.Count > capacity)
            {
                var oldest = _order.Last!.Value;
                _order.RemoveLast();
                _pages.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// Drops every tint's copy of one page — an edit changed what the page *is*, which
    /// no colour choice makes stale differently.
    /// </summary>
    public void Remove(int pageIndex)
    {
        lock (_lock)
        {
            foreach (var key in _order.Where(k => k.PageIndex == pageIndex).ToList())
            {
                _pages.Remove(key);
                _order.Remove(key);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _pages.Clear();
            _order.Clear();
        }
    }
}
