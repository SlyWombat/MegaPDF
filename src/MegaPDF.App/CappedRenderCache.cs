using MegaPDF.Core.Engine;

namespace MegaPDF.App;

/// <summary>
/// A few page rasters that were rendered at the <see cref="Core.Viewing.RenderLimits"/>
/// clamp rather than at their ideal size, kept by page index so a zoom step can reuse
/// them instead of decoding the page again (#94). Least-recently-used eviction with a
/// tiny capacity: each entry is up to 128 MB.
///
/// Thread-safe, because renders run on the thread pool while the view model clears
/// entries from the UI thread.
/// </summary>
internal sealed class CappedRenderCache(int capacity)
{
    private readonly Dictionary<int, RenderedPage> _pages = new();
    private readonly LinkedList<int> _order = new();
    private readonly object _lock = new();

    public bool TryGet(int pageIndex, out RenderedPage rendered)
    {
        lock (_lock)
        {
            if (_pages.TryGetValue(pageIndex, out var found))
            {
                _order.Remove(pageIndex);
                _order.AddFirst(pageIndex);
                rendered = found;
                return true;
            }
            rendered = null!;
            return false;
        }
    }

    public void Put(int pageIndex, RenderedPage rendered)
    {
        lock (_lock)
        {
            _pages[pageIndex] = rendered;
            _order.Remove(pageIndex);
            _order.AddFirst(pageIndex);
            while (_order.Count > capacity)
            {
                var oldest = _order.Last!.Value;
                _order.RemoveLast();
                _pages.Remove(oldest);
            }
        }
    }

    public void Remove(int pageIndex)
    {
        lock (_lock)
        {
            _pages.Remove(pageIndex);
            _order.Remove(pageIndex);
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
