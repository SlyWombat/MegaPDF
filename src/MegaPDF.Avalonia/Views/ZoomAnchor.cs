using Avalonia;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// The one piece of arithmetic every zoom entry point shares (#528). Zoom here is
/// re-layout, not a transform: <c>PageViewModel.LayoutWidth/Height</c> grow with
/// <c>Zoom</c> and the pages panel grows from its own origin inside
/// <c>PageScroller</c>. Nothing about that layout adjusts the scroll offset, which
/// is why every zoom used to anchor at the top-left corner regardless of where the
/// gesture or the pointer was. This is the correction: given where a fixed
/// viewport point sat over the content before the zoom step, and the zoom this
/// app actually committed to, it returns the offset that puts the same content
/// back under that viewport point.
///
/// Deliberately free of any Avalonia control beyond the two primitives — Offset
/// and Viewport are already expressed in the pixel space <c>LayoutWidth/Height</c>
/// scales, so this is arithmetic on two numbers, not on a visual tree, and it is
/// exercised directly by the self-test as well as through a real window.
/// </summary>
internal static class ZoomAnchor
{
    /// <param name="oldOffset"><c>PageScroller.Offset</c> before the zoom step.</param>
    /// <param name="oldZoom"><c>DocumentViewModel.Zoom</c> before the step.</param>
    /// <param name="newZoom">
    /// <c>DocumentViewModel.Zoom</c> AFTER the step was applied and clamped — the
    /// ratio used here is <c>newZoom / oldZoom</c>, the ratio the layout actually
    /// used, not whatever ratio a wheel notch or a pinch asked for. Passing the
    /// requested ratio instead of the committed one is exactly the trap #528 warns
    /// about: at the minimum or maximum stop the two differ, and correcting by the
    /// requested one drifts the page while the zoom itself sits still. A caller
    /// that always reads this from the view model after setting <c>Zoom</c> gets
    /// the committed value automatically, clamp or no clamp.
    /// </param>
    /// <param name="anchorViewport">
    /// The point, in viewport coordinates (unaffected by zoom), that must not
    /// move: the pointer for a wheel step or a trackpad pinch, the viewport's own
    /// centre for a menu or keyboard command, since there is no pointer position
    /// to anchor one of those on.
    /// </param>
    public static Vector Reanchor(Vector oldOffset, double oldZoom, double newZoom, Point anchorViewport)
    {
        if (oldZoom <= 0 || newZoom <= 0)
            return oldOffset;

        var ratio = newZoom / oldZoom;
        var anchorContentX = oldOffset.X + anchorViewport.X;
        var anchorContentY = oldOffset.Y + anchorViewport.Y;
        return new Vector(
            (anchorContentX * ratio) - anchorViewport.X,
            (anchorContentY * ratio) - anchorViewport.Y);
    }
}
