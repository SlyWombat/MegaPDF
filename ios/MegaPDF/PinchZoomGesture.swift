import SwiftUI
import UIKit

/// Pinch to zoom, anchored on the point between the fingers, and the scroll offset that
/// keeps it there (#530).
///
/// ## Why this is UIKit and not a SwiftUI gesture
///
/// `MagnificationGesture`'s value is a bare `CGFloat`: **it reports no location at all**,
/// so nothing downstream of it can know where to anchor. iOS 17's `MagnifyGesture` does
/// carry `startLocation`, but the deployment target is 16.0 (`ios/project.yml`) and raising
/// it is not a fix's decision to take — and on its own it would not be enough. Zoom here
/// is a layout change inside a `ScrollView`, and iOS 16's SwiftUI gives no way to move a
/// `ScrollView`'s offset to a point: `ScrollViewReader` scrolls to a *view*, and
/// `scrollPosition` is iOS 17 as well. So this fix needs the enclosing `UIScrollView`
/// whatever reports the gesture, and one `UIPinchGestureRecognizer` on that scroll view
/// answers both halves at once — `location(in:)` for the anchor, `contentOffset` to put
/// the anchor back.
///
/// ## How it is wired
///
/// The view is a zero-size probe sitting at the **page stack's top-leading corner**
/// (`ViewerView.document` overlays it on the `LazyVStack`), which is two jobs in one: it
/// is inside the scroll view's content, so walking up its superviews finds the scroll
/// view; and its own position in content coordinates *is* the stack's origin, which is
/// what the anchor arithmetic needs measured before and after a zoom rather than derived
/// from a second copy of the view's layout.
///
/// It takes no touch of its own (`isUserInteractionEnabled = false`) — the recogniser goes
/// on the scroll view, which sees every touch on the page — so the page keeps every pixel
/// and every gesture it had.
struct PinchZoomGesture: UIViewRepresentable {
    /// Shared with `ViewerView`, which drives the same corrections from its double-tap and
    /// from reading mode's zoom buttons.
    let controller: ZoomAnchorController
    /// The zoom already committed — what a pinch multiplies.
    let zoom: CGFloat
    /// The floor the result is clamped to: 1 (fit width) until reading mode's *Fit page*
    /// lowers it (#512). A pinch must not undercut whatever the person last asked for.
    let zoomFloor: CGFloat
    /// The live scale, as a multiple of `zoom` — the page is laid out at `zoom * this`.
    let onGestureScale: (CGFloat) -> Void
    /// The zoom the gesture ended on, already clamped.
    let onCommit: (CGFloat) -> Void

    func makeUIView(context: Context) -> PinchZoomHost {
        let host = PinchZoomHost()
        host.isUserInteractionEnabled = false
        push(to: host)
        return host
    }

    func updateUIView(_ host: PinchZoomHost, context: Context) { push(to: host) }

    private func push(to host: PinchZoomHost) {
        host.controller = controller
        host.zoom = zoom
        host.zoomFloor = zoomFloor
        host.onGestureScale = onGestureScale
        host.onCommit = onCommit
    }

    static func dismantleUIView(_ host: PinchZoomHost, coordinator: ()) {
        host.detach()
    }
}

/// `PinchZoomGesture`'s view. Its own type because the recogniser can only be added once
/// there is a scroll view above it to add it to, and `didMoveToWindow` is where that is
/// first known — the same shape as `EdgeGestureHost` (#506).
final class PinchZoomHost: UIView, UIGestureRecognizerDelegate {
    var controller: ZoomAnchorController? {
        didSet { controller?.attach(probe: self, scrollView: scrollView) }
    }
    var zoom: CGFloat = 1
    var zoomFloor: CGFloat = ReadingZoom.fitWidth
    var onGestureScale: ((CGFloat) -> Void)?
    var onCommit: ((CGFloat) -> Void)?

    private var pinch: UIPinchGestureRecognizer?
    private weak var scrollView: UIScrollView?
    /// The committed zoom the gesture started from. A pinch reports its scale from its own
    /// start, so this is the one number the whole gesture is measured against.
    private var zoomAtStart: CGFloat = 1
    private var panWasEnabled = true

    override func didMoveToWindow() {
        super.didMoveToWindow()
        guard let window else {
            detach()
            return
        }
        let scroll = enclosingScrollView()
        scrollView = scroll
        controller?.attach(probe: self, scrollView: scroll)
        // The scroll view when there is one, so the recogniser only ever sees touches on
        // the page. The window is the fallback and nothing more: if some future SwiftUI
        // stops backing `ScrollView` with a `UIScrollView`, a pinch that still zooms
        // without anchoring is a great deal better than a pinch that silently does
        // nothing at all, which is what #336 and #436 both were.
        let target: UIView = scroll ?? window
        if let pinch, pinch.view === target { return }
        if let pinch { pinch.view?.removeGestureRecognizer(pinch) }
        let recognizer = UIPinchGestureRecognizer(target: self, action: #selector(handle(_:)))
        recognizer.delegate = self
        target.addGestureRecognizer(recognizer)
        pinch = recognizer
    }

    func detach() {
        if let pinch {
            pinch.view?.removeGestureRecognizer(pinch)
            self.pinch = nil
        }
        // Never leave the page unable to scroll because a gesture ended in an odd way.
        scrollView?.panGestureRecognizer.isEnabled = true
        controller?.detach(probe: self)
    }

    /// The `UIScrollView` this probe sits inside, or nil.
    private func enclosingScrollView() -> UIScrollView? {
        var view = superview
        while let current = view {
            if let scroll = current as? UIScrollView { return scroll }
            view = current.superview
        }
        return nil
    }

    @objc private func handle(_ recognizer: UIPinchGestureRecognizer) {
        switch recognizer.state {
        case .began:
            zoomAtStart = zoom
            if let scrollView {
                controller?.begin(atContentPoint: recognizer.location(in: scrollView))
                // The scroll view's own pan recognises alongside this one (#336), and
                // while it does it rewrites `contentOffset` every frame from where the
                // pan began — which is precisely the offset the correction is moving away
                // from, so every correction would be undone as fast as it was made. A
                // pinch is not a scroll: the pan is off for the length of one and back on
                // the moment it ends.
                panWasEnabled = scrollView.panGestureRecognizer.isEnabled
                scrollView.panGestureRecognizer.isEnabled = false
            }
        case .changed:
            let committed = ReadingZoom.clamped(zoomAtStart * recognizer.scale, floor: zoomFloor)
            onGestureScale?(committed / zoomAtStart)
            controller?.reanchor(ratio: committed / zoomAtStart)
        case .ended, .cancelled, .failed:
            // `onCommit` puts the live scale back to 1 as it commits the zoom, so the two
            // land in one SwiftUI update and the page is never laid out at the product of
            // both.
            let committed = ReadingZoom.clamped(zoomAtStart * recognizer.scale, floor: zoomFloor)
            onCommit?(committed)
            // The ratio the zoom **actually took**, never the one the fingers asked for:
            // the two part company the moment the clamp bites, and correcting by the
            // request there walks the page sideways while the zoom itself holds still.
            controller?.reanchor(ratio: committed / zoomAtStart)
            scrollView?.panGestureRecognizer.isEnabled = panWasEnabled
        default:
            break
        }
    }

    /// Alongside the scroll view's own pan, for the reason #336 recorded: left exclusive,
    /// whichever recogniser claimed the touches first would decide whether a pinch worked
    /// at all, and on a scrolling page that is the pan.
    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer,
                           shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool {
        true
    }
}

/// The scroll offset half of the fix, shared by every way the viewer changes zoom (#530).
///
/// It holds no zoom of its own: `ViewerView`'s `zoom` is still the only source of truth.
/// What it holds is the measurement a correction needs — where the offset, the anchor and
/// the page stack's origin were *before* the zoom — and the scroll view to write the
/// answer back to.
final class ZoomAnchorController: ObservableObject {
    /// What one zoom change is anchored against.
    private struct Session {
        let offset: CGPoint
        /// In the viewport's own coordinates, so it can be compared with a content point
        /// minus the offset at any later zoom.
        let focus: CGPoint
        /// The page stack's top-left in content coordinates, before the zoom.
        let origin: CGPoint
    }

    private weak var scrollView: UIScrollView?
    private weak var probe: UIView?
    private var session: Session?
    private var ratio: CGFloat = 1
    /// Bumped by every request, so a correction scheduled by an earlier one cannot land
    /// after a later one has already had its say.
    private var generation = 0
    /// `applyPending` forces a layout pass, which a scroll view can answer by calling back
    /// into this object. Re-entering would read a half-applied state as if it were the
    /// state before the zoom — the shape of the bug #534's self-test caught on Avalonia.
    private var isApplying = false

    func attach(probe: UIView, scrollView: UIScrollView?) {
        self.probe = probe
        self.scrollView = scrollView
    }

    func detach(probe: UIView) {
        guard self.probe === probe else { return }
        self.probe = nil
        self.scrollView = nil
        self.session = nil
    }

    /// Start anchoring around a point in the scroll view's **content** coordinates, which
    /// is what `UIGestureRecognizer.location(in: scrollView)` reports.
    @discardableResult
    func begin(atContentPoint point: CGPoint) -> Bool {
        guard let scrollView, let probe, probe.window != nil else {
            session = nil
            return false
        }
        let offset = scrollView.contentOffset
        session = Session(offset: offset,
                          focus: CGPoint(x: point.x - offset.x, y: point.y - offset.y),
                          origin: probe.convert(.zero, to: scrollView))
        return true
    }

    /// Start anchoring around a point in window coordinates — SwiftUI's `.global` space —
    /// or, with nil, around the middle of what is actually on screen.
    @discardableResult
    func begin(atWindowPoint point: CGPoint?) -> Bool {
        guard let scrollView else {
            session = nil
            return false
        }
        if let point {
            return begin(atContentPoint: scrollView.convert(point, from: nil))
        }
        // The visible middle, not `bounds.midY`: the top chrome is a safe-area inset, so
        // the top of the scroll view's bounds is behind it.
        let inset = scrollView.adjustedContentInset
        let centre = CGPoint(x: (inset.left + scrollView.bounds.width - inset.right) / 2,
                             y: (inset.top + scrollView.bounds.height - inset.bottom) / 2)
        return begin(atContentPoint: CGPoint(x: centre.x + scrollView.contentOffset.x,
                                             y: centre.y + scrollView.contentOffset.y))
    }

    /// Correct the offset for a zoom that has just been committed at `ratio` times the zoom
    /// the session began at.
    ///
    /// The correction cannot be applied now: the page is laid out at the new zoom by
    /// SwiftUI, and until that pass has run the new content size does not exist yet — an
    /// offset written into the old one is simply clamped away, which is the same stale-
    /// layout trap #527 hit on Android. So the answer is applied a frame later, and again
    /// shortly after in case a frame was not enough. Both reads are of the **latest**
    /// requested ratio, so a fast pinch converges rather than replaying its own history.
    func reanchor(ratio: CGFloat) {
        guard session != nil else { return }
        self.ratio = ratio
        generation &+= 1
        let mine = generation
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.0 / 60.0) { [weak self] in
            self?.applyPending(generation: mine)
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { [weak self] in
            self?.applyPending(generation: mine)
        }
    }

    /// Anchor a zoom the view commits itself — a double tap, or reading mode's own zoom
    /// buttons — around where it was aimed, or around the viewport's middle for the ones
    /// that carry no position at all.
    ///
    /// The measurement is taken, then `commit` runs, then the correction is scheduled: the
    /// order is the whole point, and having it in one place is why this takes a closure
    /// rather than leaving each call site to remember it.
    func anchor(aroundWindowPoint point: CGPoint?,
                from oldZoom: CGFloat, to newZoom: CGFloat,
                commit: () -> Void) {
        let started = begin(atWindowPoint: point)
        commit()
        guard started, oldZoom > 0, newZoom > 0, oldZoom != newZoom else { return }
        reanchor(ratio: newZoom / oldZoom)
    }

    private func applyPending(generation: Int) {
        guard generation == self.generation, !isApplying,
              let session, let scrollView, let probe, probe.window != nil else { return }
        isApplying = true
        defer { isApplying = false }
        // The new layout has to exist before its origin and its extent can be read. Every
        // number the correction is built from was captured in `session` before the zoom,
        // so nothing this pass does can overwrite the "before" it is measured against.
        scrollView.layoutIfNeeded()
        let inset = scrollView.adjustedContentInset
        let minOffset = CGPoint(x: -inset.left, y: -inset.top)
        let maxOffset = CGPoint(
            x: max(-inset.left,
                   scrollView.contentSize.width + inset.right - scrollView.bounds.width),
            y: max(-inset.top,
                   scrollView.contentSize.height + inset.bottom - scrollView.bounds.height))
        let target = ZoomAnchor.reanchor(offset: session.offset,
                                        focus: session.focus,
                                        oldOrigin: session.origin,
                                        newOrigin: probe.convert(.zero, to: scrollView),
                                        ratio: ratio,
                                        minOffset: minOffset, maxOffset: maxOffset)
        guard abs(target.x - scrollView.contentOffset.x) > 0.5
                || abs(target.y - scrollView.contentOffset.y) > 0.5 else { return }
        scrollView.setContentOffset(target, animated: false)
    }
}
