import SwiftUI
import UIKit

// The on-page face of text selection (#710): the highlight, the two handles and the system
// edit menu with Copy.
//
// WHY NOT `UITextInteraction`. That is where the system's own handles, loupe and menu come
// from, but it needs a `UITextInput` — a text model with positions, ranges, a tokenizer and
// the geometry of every character — and a UIKit view that owns the touches. Here the pages are
// SwiftUI images in a lazy scroll view, owned by the page's own tap, double-tap and pinch, and
// the text model is the core's snapshot, not a text storage. A hand-written `UITextInput` over
// a snapshot, with a hit-testable overlay that would have to hand every tap back to SwiftUI,
// is a lot of surface for a feature whose visible parts are three things. So the highlight and
// handles are drawn here in the system's selection colour, and the menu is the system's own:
// `UIEditMenuInteraction`, the same menu every iOS text selection shows, which also gives
// VoiceOver, Reduce Motion and the iPad pointer menu for free.

/// Page crop space (bottom-left, points) to the page view's space (top-left, points).
struct PageSpace {
    let pageSize: CGSize
    let viewSize: CGSize

    var scaleX: CGFloat { viewSize.width / pageSize.width }
    var scaleY: CGFloat { viewSize.height / pageSize.height }

    func point(_ p: PdfPoint) -> CGPoint {
        CGPoint(x: p.x * scaleX, y: (Double(pageSize.height) - p.y) * scaleY)
    }

    func rect(_ r: PdfRect) -> CGRect {
        CGRect(x: r.left * scaleX, y: (Double(pageSize.height) - r.top) * scaleY,
               width: (r.right - r.left) * scaleX, height: (r.top - r.bottom) * scaleY)
    }
}

/// The selection tint: the app's tint (the colour iOS draws a text selection in) at the
/// system's selection opacity. There is no public constant for it, so this is the tint at 0.25.
enum SelectionTint {
    static let fill = Color.accentColor.opacity(0.25)
    static let handle = Color.accentColor
}

struct TextSelectionOverlay: View {
    let rects: [PdfRect]
    let space: PageSpace
    let onDragHandle: (PageTextSelection.Handle, CGPoint) -> Void
    let onEndDrag: () -> Void

    var body: some View {
        ZStack(alignment: .topLeading) {
            Canvas { context, _ in
                for rect in rects {
                    context.fill(Path(space.rect(rect)), with: .color(SelectionTint.fill))
                }
            }
            .allowsHitTesting(false)
            .accessibilityHidden(true)

            if let handles = SelectionGeometry.handlePoints(for: rects) {
                handle(.start, at: space.point(handles.start))
                handle(.end, at: space.point(handles.end))
            }
        }
    }

    private static let ball: CGFloat = 10

    /// A stem along the line's edge with a ball on its far end: above the line for the start,
    /// below it for the end — iOS's own shape. The touch target is 44 points square.
    private func handle(_ which: PageTextSelection.Handle, at point: CGPoint) -> some View {
        let isStart = which == .start
        let lineHeight = (rects.first.map { space.rect($0).height } ?? 12)
        return ZStack {
            Rectangle()
                .fill(SelectionTint.handle)
                .frame(width: 2, height: lineHeight)
                .offset(y: isStart ? lineHeight / 2 : -lineHeight / 2)
            Circle()
                .fill(SelectionTint.handle)
                .frame(width: Self.ball, height: Self.ball)
                .offset(y: isStart ? -Self.ball / 2 : Self.ball / 2)
        }
        .frame(width: 44, height: 44)
        .contentShape(Rectangle())
        .position(point)
        .gesture(
            DragGesture(minimumDistance: 0, coordinateSpace: .named(PageSpace.name))
                .onChanged { value in
                    // The finger is on the ball, a line's height from the text it means: aim
                    // at the line, not at the ball.
                    let aim = CGPoint(x: value.location.x,
                                      y: value.location.y + (isStart ? 1 : -1) * (lineHeight / 2 + Self.ball))
                    onDragHandle(which, aim)
                }
                .onEnded { _ in onEndDrag() })
        .accessibilityLabel(isStart ? Text("Start of selection") : Text("End of selection"))
        .accessibilityIdentifier(isStart ? "selectionHandleStart" : "selectionHandleEnd")
    }
}

extension PageSpace {
    /// The name of the page view's coordinate space, which the handles' drags read.
    static let name = "megapdfPageSpace"
}

/// Presents the system edit menu over the selection. A zero-interaction `UIView` laid exactly
/// over the selection's box, so the menu points at it; `request` going up asks it to appear.
struct SelectionMenuHost: UIViewRepresentable {
    let request: Int
    let canCopy: Bool
    let onCopy: () -> Void

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeUIView(context: Context) -> UIView {
        let view = UIView()
        view.isUserInteractionEnabled = false   // the interaction presents without touches
        view.backgroundColor = .clear
        let interaction = UIEditMenuInteraction(delegate: context.coordinator)
        view.addInteraction(interaction)
        context.coordinator.interaction = interaction
        // Zero, not `request`: this view is made when a selection appears, and the request for
        // its menu was raised in the same update, so it has to find the request unanswered.
        context.coordinator.shown = 0
        return view
    }

    func updateUIView(_ view: UIView, context: Context) {
        let coordinator = context.coordinator
        coordinator.canCopy = canCopy
        coordinator.onCopy = onCopy
        guard coordinator.shown != request else { return }
        coordinator.shown = request
        // After this layout pass, and again if the view is not in a window yet: it is created in
        // the same update that asks for its menu.
        coordinator.present(in: view, attemptsLeft: 8)
    }

    static func dismantleUIView(_ view: UIView, coordinator: Coordinator) {
        coordinator.interaction?.dismissMenu()
    }

    final class Coordinator: NSObject, UIEditMenuInteractionDelegate {
        var interaction: UIEditMenuInteraction?
        var shown = 0
        var canCopy = true
        var onCopy: () -> Void = {}

        func present(in view: UIView, attemptsLeft: Int) {
            DispatchQueue.main.async { [weak self, weak view] in
                guard let self, let view else { return }
                guard view.window != nil, view.bounds.width > 0 else {
                    if attemptsLeft > 0 { self.present(in: view, attemptsLeft: attemptsLeft - 1) }
                    return
                }
                let point = CGPoint(x: view.bounds.midX, y: view.bounds.minY)
                let configuration = UIEditMenuConfiguration(identifier: nil, sourcePoint: point)
                configuration.preferredArrowDirection = .down
                self.interaction?.presentEditMenu(with: configuration)
            }
        }

        func editMenuInteraction(_ interaction: UIEditMenuInteraction,
                                 menuFor configuration: UIEditMenuConfiguration,
                                 suggestedActions: [UIMenuElement]) -> UIMenu? {
            // Just Copy: the page is not editable, and the system's other suggestions (Look Up,
            // Translate, Share) are not on a selection this app cannot read back as a string.
            // Disabled, not absent, when the document forbids copying: the notice says why.
            let copy = UIAction(title: String(localized: "Copy"),
                                image: UIImage(systemName: "doc.on.doc"),
                                attributes: canCopy ? [] : [.disabled]) { [weak self] _ in
                self?.onCopy()
            }
            return UIMenu(children: [copy])
        }
    }
}

/// The long press that starts a selection. A zero-size-input probe laid over the page, with a
/// `UILongPressGestureRecognizer` on the enclosing scroll view — the shape `PinchZoomHost`
/// already uses for the same reason.
///
/// It is UIKit because a SwiftUI long press reports no location, and the fix that does (a long
/// press sequenced before a zero-distance drag) was tried first and measured to swallow the
/// page's taps: `FormTextUITests` could no longer open a field. The recogniser here is
/// simultaneous with everything and cancels no touches, so a tap, a double tap, the scroll
/// pan and the pinch are exactly as they were; it sees a press only inside its own page.
struct PageLongPress: UIViewRepresentable {
    let enabled: Bool
    /// Where the press began, as fractions of the page view, top-left origin.
    let onPress: (_ xFraction: Double, _ yFraction: Double) -> Void
    /// The finger came up (or the press was cancelled): the menu waits for this, since a menu
    /// shown under a finger that is still down is taken away again when it lifts.
    let onEnd: () -> Void

    func makeUIView(context: Context) -> PageLongPressHost {
        let host = PageLongPressHost()
        host.isUserInteractionEnabled = false
        host.backgroundColor = .clear
        push(to: host)
        return host
    }

    func updateUIView(_ host: PageLongPressHost, context: Context) { push(to: host) }

    private func push(to host: PageLongPressHost) {
        host.enabled = enabled
        host.onPress = onPress
        host.onEnd = onEnd
    }

    static func dismantleUIView(_ host: PageLongPressHost, coordinator: ()) { host.detach() }
}

final class PageLongPressHost: UIView, UIGestureRecognizerDelegate {
    var enabled = true
    var onPress: ((Double, Double) -> Void)?
    var onEnd: (() -> Void)?
    private var began = false
    private var recognizer: UILongPressGestureRecognizer?

    override func didMoveToWindow() {
        super.didMoveToWindow()
        guard window != nil else { detach(); return }
        guard recognizer == nil, let scroll = enclosingScrollView() else { return }
        let press = UILongPressGestureRecognizer(target: self, action: #selector(handle(_:)))
        press.minimumPressDuration = 0.45
        press.allowableMovement = 12
        press.numberOfTouchesRequired = 1
        press.cancelsTouchesInView = false
        press.delaysTouchesBegan = false
        press.delegate = self
        scroll.addGestureRecognizer(press)
        recognizer = press
    }

    func detach() {
        if let recognizer { recognizer.view?.removeGestureRecognizer(recognizer) }
        recognizer = nil
    }

    private func enclosingScrollView() -> UIScrollView? {
        var view = superview
        while let current = view {
            if let scroll = current as? UIScrollView { return scroll }
            view = current.superview
        }
        return nil
    }

    @objc private func handle(_ press: UILongPressGestureRecognizer) {
        switch press.state {
        case .began:
            guard bounds.width > 0, bounds.height > 0 else { return }
            let p = press.location(in: self)
            guard bounds.contains(p) else { return }
            began = true
            onPress?(Double(p.x / bounds.width), Double(p.y / bounds.height))
        case .ended, .cancelled, .failed:
            guard began else { return }
            began = false
            onEnd?()
        default:
            break
        }
    }

    // UIView already has this selector (for its own recognisers), and the delegate call lands on
    // the same one, so it is an override.
    override func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        guard gestureRecognizer === recognizer else { return super.gestureRecognizerShouldBegin(gestureRecognizer) }
        guard enabled, window != nil else { return false }
        return bounds.contains(gestureRecognizer.location(in: self))
    }

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer,
                           shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool { true }
}
