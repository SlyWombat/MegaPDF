import CoreGraphics

/// Where a scroll offset has to go for a zoom to grow about a point instead of about the
/// page's own top-left corner (#530).
///
/// The same arithmetic as `ZoomAnchor` in the Windows app (#546) and the Avalonia one
/// (#534), and as Android's `anchoredScrollDelta` (#527), because it is the same bug on
/// every platform: zoom is a **layout** change — the page is laid out at
/// `containerWidth * zoom` — so the content grows from its own origin, and nothing but a
/// matching move of the scroll offset can keep a point of the page where it was.
///
/// Pure on purpose. `PinchZoomGesture` measures the scroll view, this decides, and
/// `ZoomAnchorTests` checks the decision with no window, no gesture and no simulator in
/// the room.
enum ZoomAnchor {

    /// The offset a scroll view must take so that whatever was under `focus` stays there.
    ///
    /// Everything is in the scroll view's own points.
    ///
    /// - Parameters:
    ///   - offset: the content offset the zoom started from.
    ///   - focus: the point to hold still, in the viewport's own coordinates — that is,
    ///     `location(in: scrollView) - contentOffset`.
    ///   - oldOrigin: the page stack's top-left corner in content coordinates, before.
    ///   - newOrigin: the same corner after the new zoom has been laid out. **Measured,
    ///     not computed**: the stack is centred while it is smaller than the viewport
    ///     (#48) and packed to the top once it is taller, so its origin moves with the
    ///     zoom as well as its size, and a formula here would be a second copy of the
    ///     view's layout waiting to drift from it.
    ///   - ratio: the new zoom over the old one — **the zoom actually committed**, never
    ///     the one the gesture asked for. The two differ the moment the zoom clamps at
    ///     the floor or the ceiling, and correcting by the requested ratio there slides
    ///     the page while the zoom itself holds perfectly still.
    ///   - minOffset: the smallest offset the scroll view will hold once laid out at the
    ///     new zoom (`-adjustedContentInset` on each axis).
    ///   - maxOffset: the largest.
    static func reanchor(offset: CGPoint, focus: CGPoint,
                         oldOrigin: CGPoint, newOrigin: CGPoint,
                         ratio: CGFloat,
                         minOffset: CGPoint, maxOffset: CGPoint) -> CGPoint {
        guard ratio.isFinite, ratio > 0 else { return offset }
        // Where the anchored point sits inside the stack, in the stack's pre-zoom points.
        let inStack = CGPoint(x: offset.x + focus.x - oldOrigin.x,
                             y: offset.y + focus.y - oldOrigin.y)
        // The same point of the page, in the stack it has just been laid out as.
        let moved = CGPoint(x: newOrigin.x + inStack.x * ratio,
                            y: newOrigin.y + inStack.y * ratio)
        return CGPoint(x: clamp(moved.x - focus.x, minOffset.x, maxOffset.x),
                       y: clamp(moved.y - focus.y, minOffset.y, maxOffset.y))
    }

    private static func clamp(_ value: CGFloat, _ low: CGFloat, _ high: CGFloat) -> CGFloat {
        guard high > low else { return low }
        return min(max(value, low), high)
    }
}
