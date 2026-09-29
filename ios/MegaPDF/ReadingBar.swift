import SwiftUI
import UIKit

/// What the reading bar can do, handed over as one value rather than nine parameters
/// (#506). The bar draws controls; `ViewerView` owns every piece of state they change.
struct ReadingBarActions {
    var previousPage: () -> Void = {}
    var nextPage: () -> Void = {}
    var goToPage: () -> Void = {}
    var fitWidth: () -> Void = {}
    var fitPage: () -> Void = {}
    var zoomOut: () -> Void = {}
    var zoomIn: () -> Void = {}
    var find: () -> Void = {}
    var exit: () -> Void = {}
}

/// The floating bar inside reading mode (#506, #512): the only chrome left on screen.
///
/// An `.ultraThinMaterial` capsule at `radius.control` 10 (`docs/design-tokens.md` §3, the
/// iOS column), bottom centre, over the page rather than beside it — the same shape the
/// other three platforms' pills have, in this platform's own material. It carries what the
/// plan's bar carries everywhere: previous and next page with the page number between
/// them, the two fit presets, zoom out and in, and the way out. The magnifier is here
/// because on a phone there is nowhere else for it once the bottom bar is gone, and
/// `docs/reading-mode-plan.md` §2 asks that Find still be reachable from inside.
///
/// **It is one accessibility group, not nine loose buttons.** `children: .contain` under a
/// name of its own means VoiceOver announces "Reading controls" and then its buttons,
/// instead of dropping the user among unnamed chevrons floating over a page; the summary
/// trait is what makes it announce itself when it appears, which is the moment a reader
/// most needs to know it is there.
///
/// It never fades while VoiceOver is running — that rule is `ReadingBarFade`, and the
/// timer is armed by `ViewerView`, because it is the bar's *presence* that is pinned, not
/// anything this view draws.
struct ReadingBar: View {
    /// One-based, for the label and for what "previous" and "next" mean.
    let page: Int
    let pageCount: Int
    let tint: PageTint
    var actions = ReadingBarActions()

    /// Titles go first; a narrow phone in French falls back to the icon alone, exactly as
    /// the iPad tool strip does (`ViewerView.regularToolStrip`).
    var body: some View {
        ViewThatFits(in: .horizontal) {
            row(titledExit: true)
            row(titledExit: false)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
        .background(background)
        .overlay(
            RoundedRectangle(cornerRadius: 10, style: .continuous)
                .strokeBorder(.white.opacity(0.12), lineWidth: 0.5)
        )
        .shadow(radius: 8, y: 2)
        .foregroundStyle(Brand.Reading.ink(tint) ?? Color.primary)
        // One named group, not nine anonymous controls on top of a page (see above).
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Reading controls")
        .accessibilityAddTraits(.isSummaryElement)
        .accessibilityIdentifier("readingBar")
    }

    @ViewBuilder
    private var background: some View {
        let shape = RoundedRectangle(cornerRadius: 10, style: .continuous)
        if let surface = Brand.Reading.surface(tint) {
            // A tinted page gets a tinted bar: the system material would take its colour
            // from the appearance, which is the one thing the page colours are not
            // following (`Brand.Reading`).
            shape.fill(surface)
        } else {
            shape.fill(.ultraThinMaterial)
        }
    }

    private func row(titledExit: Bool) -> some View {
        HStack(spacing: 2) {
            control("Previous page", systemImage: "chevron.left",
                    identifier: "readingPreviousPage", action: actions.previousPage)
                .disabled(page <= 1)
            Button(action: actions.goToPage) {
                Text(verbatim: "\(page)/\(pageCount)")
                    .font(.footnote.monospacedDigit())
                    .padding(.horizontal, 6)
                    .padding(.vertical, 6)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            // The digits are the label on screen; what a screen reader hears is the
            // sentence, and the hint says the number can be typed.
            .accessibilityLabel(Text("Page \(page) of \(pageCount)"))
            .accessibilityHint(Text("Go to page"))
            .accessibilityIdentifier("readingPageNumber")
            control("Next page", systemImage: "chevron.right",
                    identifier: "readingNextPage", action: actions.nextPage)
                .disabled(page >= pageCount)
            separator
            control("Fit width", systemImage: "arrow.left.and.right",
                    identifier: "readingFitWidth", action: actions.fitWidth)
            control("Fit page", systemImage: "arrow.up.and.down",
                    identifier: "readingFitPage", action: actions.fitPage)
            control("Zoom out", systemImage: "minus.magnifyingglass",
                    identifier: "readingZoomOut", action: actions.zoomOut)
            control("Zoom in", systemImage: "plus.magnifyingglass",
                    identifier: "readingZoomIn", action: actions.zoomIn)
            separator
            control("Find in document", systemImage: "magnifyingglass",
                    identifier: "readingFind", action: actions.find)
            Button(action: actions.exit) {
                Label("Exit", systemImage: "arrow.down.right.and.arrow.up.left")
                    .labelStyle(ToolStripLabelStyle(titled: titledExit))
                    .font(.footnote.weight(.semibold))
                    .padding(.horizontal, 8)
                    .padding(.vertical, 6)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel(Text("Exit reading mode"))
            .accessibilityIdentifier("readingExit")
        }
    }

    private var separator: some View {
        Rectangle()
            .fill(.primary.opacity(0.15))
            .frame(width: 0.5, height: 18)
            .padding(.horizontal, 3)
            .accessibilityHidden(true)
    }

    private func control(_ title: LocalizedStringKey, systemImage: String,
                         identifier: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .font(.footnote.weight(.semibold))
                .frame(width: 30, height: 28)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel(Text(title))
        .accessibilityIdentifier(identifier)
    }
}

/// The swipe-back edge gesture, as the second way out of reading mode (#506).
///
/// Reading mode hides the navigation bar, and the viewer is the navigation stack's root
/// anyway, so there is no system back gesture to inherit: the way out that iOS users
/// already know has to be provided. A `UIScreenEdgePanGestureRecognizer` is that gesture —
/// the same recogniser `UINavigationController` uses — rather than a `DragGesture` on a
/// strip down the leading edge, which would have to swallow touches the page needs for
/// scrolling a zoomed page sideways.
///
/// Two details that are load-bearing:
///
/// - **The recogniser goes on the window, not on this view.** The host view has
///   `isUserInteractionEnabled = false` and never takes a touch, so it can sit over the
///   page without costing the page anything; the recogniser still sees every touch
///   because the window does. Attached on `didMoveToWindow` and taken off again when the
///   view leaves, so nothing outlives reading mode.
/// - **It recognises alongside the scroll view's pan.** Left exclusive, whichever
///   recogniser happened to claim the touch first would decide whether the swipe worked,
///   and a zoomed page scrolling horizontally is exactly the case where the scroll view
///   claims it. Being simultaneous makes the way out reliable, at the cost of the page
///   moving a little under a swipe that is about to leave anyway.
///
/// It exits nothing by itself: `onSwipe` is called and `ViewerView` decides what one step
/// back means — the find bar first, if it is open, then reading mode.
struct ScreenEdgeBackGesture: UIViewRepresentable {
    let isEnabled: Bool
    let onSwipe: () -> Void

    func makeUIView(context: Context) -> EdgeGestureHost {
        let host = EdgeGestureHost()
        host.isUserInteractionEnabled = false
        host.onEdgeSwipe = onSwipe
        host.isGestureEnabled = isEnabled
        return host
    }

    func updateUIView(_ host: EdgeGestureHost, context: Context) {
        host.onEdgeSwipe = onSwipe
        host.isGestureEnabled = isEnabled
    }

    static func dismantleUIView(_ host: EdgeGestureHost, coordinator: ()) {
        host.detach()
    }
}

/// `ScreenEdgeBackGesture`'s view. Its own type because the recogniser can only be added
/// once there is a window to add it to, and `didMoveToWindow` is where that is known.
final class EdgeGestureHost: UIView, UIGestureRecognizerDelegate {
    var onEdgeSwipe: (() -> Void)?
    var isGestureEnabled = true {
        didSet { recognizer?.isEnabled = isGestureEnabled }
    }

    private var recognizer: UIScreenEdgePanGestureRecognizer?

    override func didMoveToWindow() {
        super.didMoveToWindow()
        guard let window else {
            detach()
            return
        }
        guard recognizer == nil else { return }
        let pan = UIScreenEdgePanGestureRecognizer(target: self, action: #selector(handle(_:)))
        // The back edge is the leading one, which is the right-hand side in a
        // right-to-left layout — the same flip `UINavigationController` makes.
        pan.edges = effectiveUserInterfaceLayoutDirection == .rightToLeft ? .right : .left
        pan.delegate = self
        pan.isEnabled = isGestureEnabled
        window.addGestureRecognizer(pan)
        recognizer = pan
    }

    func detach() {
        guard let pan = recognizer else { return }
        pan.view?.removeGestureRecognizer(pan)
        recognizer = nil
    }

    @objc private func handle(_ pan: UIScreenEdgePanGestureRecognizer) {
        guard pan.state == .ended else { return }
        onEdgeSwipe?()
    }

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer,
                           shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool {
        true
    }
}
