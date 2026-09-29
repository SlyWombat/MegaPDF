using Windows.Foundation;

namespace MegaPDF.App;

/// <summary>
/// The one piece of arithmetic every zoom entry point in this app shares (#528). Zoom
/// here is re-layout, not a transform: <see cref="DocumentViewModel.SetZoomAsync"/>
/// (by way of <c>OnZoomPercentChanged</c>) grows every <see cref="PageView"/>'s own
/// <c>Width</c>/<c>Height</c>, and <c>PagesScroll</c> — the <c>ScrollViewer</c> around
/// the page list in <c>DocumentView.xaml</c> — grows from its own origin, because
/// nothing about that layout adjusts the scroll offset. That is why every zoom used to
/// anchor on the top-left corner no matter where the pointer, the menu click or the
/// keyboard shortcut was aimed. This is the correction: given where a fixed viewport
/// point sat over the content before the zoom step, and the zoom this app actually
/// committed to, it returns the offset that puts the same content back under that
/// viewport point.
///
/// Deliberately free of any WinUI control beyond the one primitive point type —
/// <c>HorizontalOffset</c>/<c>VerticalOffset</c> and the viewport are already
/// expressed in the DIP space page sizes scale in, so this is arithmetic on two
/// numbers and a point, not on a visual tree, and it is exercised directly by the
/// screenshot self-test (<c>--screenshot-state zoom-anchor</c>, see Screenshot.cs) as
/// well as through a real window. Mirrors the Avalonia leg's own
/// <c>Views.ZoomAnchor.Reanchor</c> (#534) — same shape of bug, same shape of fix.
/// </summary>
internal static class ZoomAnchor
{
    /// <param name="oldOffset">
    /// <c>PagesScroll.HorizontalOffset</c>/<c>VerticalOffset</c> before the zoom step.
    /// </param>
    /// <param name="oldZoomFactor"><see cref="DocumentViewModel.ZoomFactor"/> before the step.</param>
    /// <param name="newZoomFactor">
    /// <see cref="DocumentViewModel.ZoomFactor"/> AFTER the step was applied and
    /// clamped — the ratio used here is <c>newZoomFactor / oldZoomFactor</c>, the
    /// ratio the layout actually used, not whatever ratio a wheel notch or a menu
    /// click asked for. Passing the requested ratio instead of the committed one is
    /// exactly the trap #528 warns about: at <see cref="DocumentViewModel.MinZoom"/>
    /// or <see cref="DocumentViewModel.MaxZoom"/> the two differ, and correcting by
    /// the requested one drifts the page while the zoom itself sits still or lands
    /// somewhere other than where it was asked to. A caller that always reads this
    /// from the view model after changing <c>ZoomPercent</c> gets the committed value
    /// automatically, clamp or no clamp.
    /// </param>
    /// <param name="anchorViewport">
    /// The point, in viewport coordinates (unaffected by zoom), that must not move:
    /// the pointer for Ctrl+wheel, the viewport's own centre for a menu, toolbar,
    /// keyboard or reading-mode command, since there is no pointer position to anchor
    /// one of those on.
    /// </param>
    public static Point Reanchor(Point oldOffset, double oldZoomFactor, double newZoomFactor, Point anchorViewport)
    {
        if (oldZoomFactor <= 0 || newZoomFactor <= 0)
            return oldOffset;

        var ratio = newZoomFactor / oldZoomFactor;
        var anchorContentX = oldOffset.X + anchorViewport.X;
        var anchorContentY = oldOffset.Y + anchorViewport.Y;
        return new Point(
            (anchorContentX * ratio) - anchorViewport.X,
            (anchorContentY * ratio) - anchorViewport.Y);
    }
}
