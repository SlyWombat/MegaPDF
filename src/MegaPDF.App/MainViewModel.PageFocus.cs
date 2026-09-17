using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Services;
using MegaPDF.Core.Viewing;

namespace MegaPDF.App;

/// <summary>
/// Keyboard traversal of the page (SDD §2.2 — required, #2).
///
/// The toolbar was already reachable; the page was not. Nothing on it could be
/// reached without a pointer, which made the whole fill-check-sign task impossible
/// for anyone who does not use one. This is the model the macOS app has had since
/// its port, on the same per-page interaction maps the cursor reads, so moving
/// focus costs a list walk rather than an engine hit-test. The reading order and
/// the walk across pages are shared in Core (<see cref="PageReadingOrder"/>).
///
/// One Windows difference: once focus is on the page, Tab walks off the end of the
/// document and on to the next control instead of wrapping, so the toolbar stays
/// reachable with Tab alone. Focus arriving on the page does wrap, so it always
/// finds something when there is anything to find.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// Where keyboard focus is on the page: which page, and which of its regions in
    /// reading order. Null means focus is not on the page at all.
    /// </summary>
    public sealed record FocusedRegion(int PageIndex, int RegionIndex, PdfRect Bounds, PageHitKind Kind, bool IsChecked, string? Content = null)
    {
        /// <summary>What the region is, for a screen reader.</summary>
        public string What => DescribeRegion(Kind, IsChecked);

        /// <summary>
        /// "Page 2, Checkbox, ticked: Damage insurance accepted" — the ring's UIA name and
        /// what a screen reader announces.
        ///
        /// The kind alone was not enough (#190): NVDA read "Page 1, Text, editable" for
        /// every line on the page and "Page 1, Box to tick" for every box, so nothing told
        /// them apart. What the region says comes with it now.
        /// </summary>
        public string AccessibleName => string.IsNullOrWhiteSpace(Content)
            ? Strings.PageRegionDescription(PageIndex + 1, What)
            : Strings.PageRegionDescriptionWithContent(PageIndex + 1, What, Content);
    }

    /// <summary>
    /// What the focused region says, for the announcement: the words of a line of text, a
    /// form field's name and value, the label beside a box. Trimmed to a sentence's worth —
    /// a screen reader repeats this on every Tab.
    /// </summary>
    internal static string? DescribeContent(PageHit hit)
    {
        var text = hit.Kind switch
        {
            PageHitKind.FormTextField or PageHitKind.FormCheckbox => hit.Field is { } field
                ? (string.IsNullOrWhiteSpace(field.Value) ? field.Name : $"{field.Name}: {field.Value}")
                : null,
            _ => hit.TextRun?.Text ?? hit.TextLine?.Text,
        };
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
        return text.Length <= 80 ? text : text[..79] + "…";
    }

    public static string DescribeRegion(PageHitKind kind, bool isChecked) => kind switch
    {
        PageHitKind.FormCheckbox => isChecked ? Strings.RegionCheckboxTicked : Strings.RegionCheckboxNotTicked,
        PageHitKind.DrawnCheckbox => Strings.RegionBoxToTick,
        PageHitKind.FormTextField => Strings.RegionFormField,
        PageHitKind.TextRun => Strings.RegionTextEditable,
        PageHitKind.TextBox => Strings.RegionAddedText,
        PageHitKind.StampAnnotation => Strings.RegionSignatureOrMark,
        PageHitKind.Whiteout => Strings.RegionCover,
        _ => Strings.RegionPage,
    };

    public enum FocusMove
    {
        /// <summary>Focus landed on a region.</summary>
        Moved,

        /// <summary>Tab ran off the end of the document (or Shift+Tab off the start).</summary>
        LeftDocument,

        /// <summary>Nothing on the document can take focus.</summary>
        NothingToFocus,
    }

    [ObservableProperty]
    private FocusedRegion? _pageFocus;

    /// <summary>
    /// Keyboard maps for pages with no rendered map: never rendered, evicted from the
    /// render window, or still showing a preview. Built from the engine without
    /// rasterising — Tab must reach page 40 without drawing pages 4 to 39. Already
    /// in reading order and filtered to what the document allows. An edit forgets
    /// its page's entry; opening a document forgets them all.
    /// </summary>
    private readonly ConcurrentDictionary<int, IReadOnlyList<(PdfRect Bounds, PageHitKind Kind)>> _keyboardMaps = new();

    /// <summary>One focus change at a time: Tab pressed twice quickly moves twice, in order.</summary>
    private readonly SemaphoreSlim _pageFocusGate = new(1, 1);

    /// <summary>
    /// Moves keyboard focus to the next (or previous) interactive region, crossing
    /// pages. Entering the page starts on the page that is on screen.
    /// </summary>
    public async Task<FocusMove> MovePageFocusAsync(bool forward)
    {
        await _pageFocusGate.WaitAsync();
        try
        {
            if (_document is not { } doc || Pages.Count == 0)
                return FocusMove.NothingToFocus;

            var maps = SnapshotKeyboardMaps(doc);
            var current = PageFocus is { } focus ? new RegionPosition(focus.PageIndex, focus.RegionIndex) : (RegionPosition?)null;
            var entryPage = Math.Clamp(CurrentPage - 1, 0, Pages.Count - 1);

            var found = await Task.Run(() =>
            {
                var position = PageReadingOrder.Step(maps.PageCount, p => maps.RegionsFor(p).Count,
                    current, forward, entryPage, wrap: current is null);
                return position is { } at ? maps.Focus(at) : null;
            });
            if (maps.Generation != _openGeneration)
                return FocusMove.NothingToFocus; // a different document opened meanwhile

            PageFocus = found;
            return found is not null ? FocusMove.Moved
                : current is null ? FocusMove.NothingToFocus
                : FocusMove.LeftDocument;
        }
        finally
        {
            _pageFocusGate.Release();
        }
    }

    /// <summary>
    /// Re-reads the focused region after the page changed underneath it — a ticked
    /// box, an edited line, a placed signature. Prefers the same kind of thing still
    /// under the old centre, because an edit can add or remove a region earlier in
    /// the reading order and the index alone would then land on a neighbour.
    /// </summary>
    public async Task<FocusedRegion?> RereadPageFocusAsync()
    {
        await _pageFocusGate.WaitAsync();
        try
        {
            if (PageFocus is not { } focus || _document is not { } doc || focus.PageIndex >= Pages.Count)
                return PageFocus;

            var maps = SnapshotKeyboardMaps(doc);
            var updated = await Task.Run(() =>
            {
                var regions = maps.RegionsFor(focus.PageIndex);
                if (regions.Count == 0)
                    return null;
                var index = -1;
                for (var i = 0; i < regions.Count && index < 0; i++)
                {
                    if (regions[i].Kind == focus.Kind && regions[i].Bounds.Contains(focus.Bounds.Center))
                        index = i;
                }
                if (index < 0)
                    index = Math.Min(focus.RegionIndex, regions.Count - 1);
                return maps.Focus(new RegionPosition(focus.PageIndex, index));
            });

            // Superseded: another document, or focus moved or cleared while this ran.
            if (maps.Generation != _openGeneration || PageFocus != focus)
                return PageFocus;
            PageFocus = updated;
            return updated;
        }
        finally
        {
            _pageFocusGate.Release();
        }
    }

    public void ClearPageFocus() => PageFocus = null;

    /// <summary>A new document: the old maps and focus describe pages that are gone.</summary>
    private void ResetPageFocus()
    {
        _keyboardMaps.Clear();
        PageFocus = null;
    }

    /// <summary>
    /// UI thread: which pages already have a trustworthy map, so the walk off the UI
    /// thread never reads <see cref="Pages"/> while it is being replaced. A first-pass
    /// preview carries no map; a zoom step's stretched raster keeps the old one,
    /// which is still right because maps are in points.
    /// </summary>
    private KeyboardMaps SnapshotKeyboardMaps(IPdfDocument doc)
    {
        var rendered = new IReadOnlyList<InteractiveRegion>?[Pages.Count];
        for (var i = 0; i < Pages.Count; i++)
        {
            var slot = Pages[i];
            if (slot.Source is not null && (!slot.IsPreview || slot.Regions.Count > 0))
                rendered[i] = slot.Regions;
        }
        return new KeyboardMaps(this, doc, rendered, Capabilities, _openGeneration);
    }

    /// <summary>The walk's view of every page's regions, in reading order. Safe off the UI thread.</summary>
    private sealed class KeyboardMaps(
        MainViewModel owner, IPdfDocument doc, IReadOnlyList<InteractiveRegion>?[] rendered,
        DocumentCapabilities capabilities, int generation)
    {
        private readonly Dictionary<int, IReadOnlyList<(PdfRect Bounds, PageHitKind Kind)>> _ordered = [];

        public int PageCount => rendered.Length;
        public int Generation => generation;

        public IReadOnlyList<(PdfRect Bounds, PageHitKind Kind)> RegionsFor(int pageIndex)
        {
            if (_ordered.TryGetValue(pageIndex, out var known))
                return known;

            IReadOnlyList<(PdfRect Bounds, PageHitKind Kind)> ordered;
            if (rendered[pageIndex] is { } map)
            {
                ordered = Prepare(map);
            }
            else if (!owner._keyboardMaps.TryGetValue(pageIndex, out ordered!))
            {
                // A page the view has never rendered has no map. Tab must still reach
                // it, so build one — which needs the engine, not a raster.
                using (var page = doc.GetPage(pageIndex))
                    ordered = Prepare(BuildRegions(page));
                if (generation == owner._openGeneration)
                    owner._keyboardMaps[pageIndex] = ordered;
            }
            _ordered[pageIndex] = ordered;
            return ordered;
        }

        /// <summary>
        /// Skips what the document's owner does not allow (#131) — the same rule the
        /// hover affordance follows, so Tab never stops on something a click refuses.
        /// </summary>
        private IReadOnlyList<(PdfRect Bounds, PageHitKind Kind)> Prepare(IEnumerable<InteractiveRegion> regions) =>
            PageReadingOrder.Order(regions
                .Where(r => capabilities.Allows(r.Kind))
                .Select(r => (r.Bounds, r.Kind)));

        public FocusedRegion Focus(RegionPosition at)
        {
            var (bounds, kind) = RegionsFor(at.PageIndex)[at.RegionIndex];
            // The map knows where a region is and what kind it is, not what it says or
            // whether a box is ticked. One hit-test at its centre answers both (#190).
            using var page = doc.GetPage(at.PageIndex);
            var hit = page.HitTest(bounds.Center);
            var isChecked = kind == PageHitKind.FormCheckbox && hit.Field is { IsChecked: true };
            var content = DescribeContent(hit);
            if (content is null && kind == PageHitKind.DrawnCheckbox)
            {
                // A drawn box is just ink: nothing under it says what ticking it means. Its
                // label is the text beside it, which is what a sighted person reads (#190).
                var beside = new PdfPoint(bounds.X + bounds.Width * 1.5 + 4, bounds.Y + bounds.Height / 2);
                content = DescribeContent(page.HitTest(beside));
            }
            return new FocusedRegion(at.PageIndex, at.RegionIndex, bounds, kind, isChecked, content);
        }
    }
}
