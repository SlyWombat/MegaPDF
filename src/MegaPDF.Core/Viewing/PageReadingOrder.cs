using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Viewing;

/// <summary>Where keyboard focus sits on a document: a page, and a region's index in that page's reading order.</summary>
public readonly record struct RegionPosition(int PageIndex, int RegionIndex);

/// <summary>
/// The order Tab walks a page's interactive regions in (SDD §2.2 — required, #2).
///
/// Shared because both desktops need exactly the same answer: the macOS port had
/// it first, inside its page view model, and the Windows app would otherwise have
/// grown a second copy that drifts. Pure, so the reasoning is pinned by tests
/// rather than by someone tabbing through a form.
/// </summary>
public static class PageReadingOrder
{
    /// <summary>Points; a line's worth of baseline wobble.</summary>
    public const double RowBand = 6;

    /// <summary>
    /// The regions in reading order — top to bottom, then left to right within a
    /// line. The input is usually in hit-test priority order instead, because a
    /// click wants the topmost thing and the keyboard wants the next thing.
    ///
    /// Rows are banded rather than sorted on raw Y: glyphs on one line rarely share
    /// an exact baseline, and sorting on Y alone would zig-zag across a row. The
    /// sort is stable, so regions at the same place keep their priority order.
    /// </summary>
    public static IReadOnlyList<(PdfRect Bounds, PageHitKind Kind)> Order(
        IEnumerable<(PdfRect Bounds, PageHitKind Kind)> regions) =>
        regions
            .OrderBy(r => Math.Round(r.Bounds.Y / RowBand))
            .ThenBy(r => r.Bounds.X)
            .ToList();

    /// <summary>
    /// The next region Tab (or Shift+Tab) lands on, or null when there is none.
    ///
    /// <paramref name="regionCount"/> is asked for a page's count only when the walk
    /// reaches that page, so a caller can build a never-rendered page's map on demand.
    /// </summary>
    /// <param name="pageCount">Pages in the document.</param>
    /// <param name="regionCount">How many regions a page has, in reading order.</param>
    /// <param name="current">Where focus is now; null when it is not on the page yet.</param>
    /// <param name="forward">Tab (true) or Shift+Tab (false).</param>
    /// <param name="entryPage">Where a walk with no <paramref name="current"/> starts.</param>
    /// <param name="wrap">
    /// Whether the walk may run off one end of the document and continue at the
    /// other. Either way it covers at most one lap, so a document with nothing
    /// interactive ends rather than spinning. Without it, running off the end
    /// returns null — how the Windows app hands Tab on to the next control instead
    /// of trapping focus on the page.
    /// </param>
    public static RegionPosition? Step(int pageCount, Func<int, int> regionCount,
                                       RegionPosition? current, bool forward,
                                       int entryPage = 0, bool wrap = true)
    {
        if (pageCount <= 0)
            return null;

        // FromEnd means "start at this page's last region" — the count is not known
        // until the page is reached, and a sentinel of int.MaxValue does not work:
        // MaxValue - 1 is never a valid index, so going backwards would skip whole
        // pages instead of entering them at the end.
        const int FromEnd = -2;

        var pageIndex = current?.PageIndex ?? Math.Clamp(entryPage, 0, pageCount - 1);
        var regionIndex = current?.RegionIndex ?? (forward ? -1 : FromEnd);

        for (var visited = 0; visited <= pageCount; visited++)
        {
            var count = regionCount(pageIndex);
            var next = regionIndex == FromEnd
                ? count - 1
                : forward ? regionIndex + 1 : regionIndex - 1;

            if (next >= 0 && next < count)
                return new RegionPosition(pageIndex, next);

            pageIndex += forward ? 1 : -1;
            if (pageIndex < 0 || pageIndex >= pageCount)
            {
                if (!wrap)
                    return null;
                pageIndex = (pageIndex + pageCount) % pageCount;
            }
            regionIndex = forward ? -1 : FromEnd;
        }

        return null;
    }
}
