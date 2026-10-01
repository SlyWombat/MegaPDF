import SwiftUI

/// Scrolling page list with pinch/double-tap zoom, tap-to-edit dispatch,
/// signature placement chrome, search bar, and save/sign toolbar.
struct ViewerView: View {
    @ObservedObject var model: ViewerModel
    /// The model's busy state (#145), observed here so the strip, the page spinner and the
    /// disabled controls follow it.
    @ObservedObject var busy: BusyState
    let displayName: String
    let pageSizes: [CGSize]
    let onSaveCopy: () -> Void
    /// A Markdown export of the document's text (#386) -- a one-way, lossy export, not another
    /// Save-a-copy format; see `ViewerModel.exportMarkdownFile`.
    let onExportMarkdown: () -> Void
    let onClose: () -> Void

    @State private var zoom: CGFloat = 1
    @State private var gestureZoom: CGFloat = 1
    /// Holds the scroll offset's end of every zoom change (#530): a zoom here is a layout
    /// change, so without a matching move of the offset the page grows about its own
    /// top-left corner and takes whatever was under the fingers away with it.
    @StateObject private var zoomAnchor = ZoomAnchorController()
    @State private var visible: Set<Int> = []
    @State private var signaturesOpen = false
    @State private var searchOpen = false
    @State private var searchText = ""
    @FocusState private var searchFocused: Bool
    @State private var aboutOpen = false
    /// The rubber band a redaction drag is drawing; nil the rest of the time (#173).
    @State private var redactBand: RedactBand?
    /// The redaction confirmation is up: marks are on the document and a save was asked for.
    @State private var redactConfirm: RedactSaveChoice?
    /// The signature confirmation is up (#481): Save was tapped on a signed document with
    /// no redaction marks pending -- those ask their own question, which already mentions
    /// the signature too (`redactConfirmMessage`).
    @State private var signatureConfirm = false
    /// The More button's on-screen frame (#378): iPad's `UIActivityViewController` needs a
    /// popover source or it crashes, and it has to point at wherever the button actually is
    /// rather than a guessed coordinate — `MoreMenuAnchorKey` below reports it here.
    @State private var moreMenuAnchor: CGRect = .zero
    @State private var settingsOpen = false

    // MARK: - reading mode's view state (#506, #512)

    /// Whether the floating bar is on screen. It is *removed* when it is not, never merely
    /// faded to nothing: an invisible bar left in place would still be a row of VoiceOver
    /// stops over the page, which is the focus trap reading mode exists to avoid.
    @State private var readingBarVisible = true
    /// Bumped by every show, so a fade armed by an earlier tap cannot hide a bar a later
    /// tap has just brought back.
    @State private var readingFadeGeneration = 0
    /// The "go to page" box the bar's page number opens.
    @State private var goToPageOpen = false
    @State private var goToPageText = ""
    /// A page the bar has asked to scroll to; consumed by the `ScrollViewReader` inside
    /// `document`, which is the only thing that holds a proxy.
    @State private var pendingScrollTarget: Int?
    /// Read once and then kept current from the notification, rather than asked for on
    /// every layout pass: what it gates is whether a *timer* is armed, and a timer is
    /// armed at a moment, not continuously.
    @State private var voiceOverRunning = ViewerView.screenReaderRunning()

    /// The page colours, as the Settings sheet stores them (#512); pushed into the model,
    /// which is what actually renders, by `syncPageTint`.
    @AppStorage(ReadingDefaults.pageColoursKey)
    private var pageColours: String = PageTint.normal.rawValue

    @Environment(\.displayScale) private var displayScale
    /// Which layout this is (#172). Regular width — an iPad full screen, or the wider
    /// side of a Split View — gets its own toolbar, `regularToolStrip`; compact width,
    /// which is every iPhone and an iPad in Slide Over or a narrow Split View, keeps the
    /// bottom bar. Read live, so dragging the divider re-lays the chrome out.
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass
    private var isRegular: Bool { horizontalSizeClass == .regular }
    private var isPad: Bool { UIDevice.current.userInterfaceIdiom == .pad }

    /// The floor the zoom is clamped to. 1 — fit width — for the whole of the app's life
    /// before #512, and still 1 until *Fit page* is chosen: the phones' zoom is
    /// width-relative, so a portrait page only fits the screen's height below 1, and a
    /// preset that could not go there would not be a preset. Lowered only by Fit page
    /// itself and put back by Fit width, so pinching in still stops at fit width unless
    /// the person has asked for something smaller.
    @State private var zoomFloor: CGFloat = ReadingZoom.fitWidth

    private var effectiveZoom: CGFloat {
        ReadingZoom.clamped(zoom * gestureZoom, floor: zoomFloor)
    }

    /// Identity of the zero-size view pinned to the current search match.
    private let matchAnchorID = "megapdf.current-match"

    /// Typed as a key so both branches are looked up in the catalog.
    private var saveLabel: LocalizedStringKey { model.isSaving ? "Saving…" : "Save" }

    /// The page list itself: pinch and double-tap zoom, the find bar and busy strip over
    /// it, and the scroll to the current match. Its own property so the body below is a
    /// chain of presentations the type-checker can still get through (#172 tipped it).
    private var document: some View {
        GeometryReader { geo in
            ScrollViewReader { proxy in
                // Both axes, always, and never derived from the zoom (#336, #530).
                // Reconfiguring a scroll view's axes rebuilds it, so an axis set that
                // followed `effectiveZoom` ended the very pinch asking for the change; and
                // one that followed the COMMITTED zoom still rebuilt it at the moment a
                // pinch crossed 1x — on a fresh scroll view, at offset zero, which is one
                // of the ways #530's page snapped back to its own left edge. Held constant
                // there is nothing to rebuild: at fit width or below the page is no wider
                // than the viewport, so the horizontal axis has no room to scroll in and
                // costs nothing, and one `UIScrollView` lives as long as the viewer for
                // the pinch recogniser and the offset correction to address.
                ScrollView([.vertical, .horizontal]) {
                    LazyVStack(spacing: 8) {
                        ForEach(pageSizes.indices, id: \.self) { index in
                            pageView(index: index, containerWidth: geo.size.width)
                                .onAppear {
                                    visible.insert(index)
                                    pushWindow(containerWidth: geo.size.width)
                                }
                                .onDisappear {
                                    visible.remove(index)
                                    pushWindow(containerWidth: geo.size.width)
                                }
                                .id(index)
                        }
                    }
                    // Pinch to zoom, and the scroll correction that anchors every zoom on
                    // the screen (#530). Overlaid on the stack rather than on the scroll
                    // view because it is two things at once: a probe whose own position is
                    // the stack's top-left corner in content coordinates, which is what
                    // the correction measures, and the only way up the view hierarchy to
                    // the `UIScrollView` whose offset it has to write. It draws nothing and
                    // takes no touch; `PinchZoomGesture` has the long version, including
                    // why this is UIKit and not `MagnificationGesture`.
                    .overlay(alignment: .topLeading) {
                        PinchZoomGesture(
                            controller: zoomAnchor,
                            zoom: zoom,
                            zoomFloor: zoomFloor,
                            onGestureScale: { gestureZoom = $0 },
                            onCommit: { committed in
                                zoom = committed
                                gestureZoom = 1
                                pushWindow(containerWidth: geo.size.width)
                            })
                            .frame(width: 0, height: 0)
                    }
                    // Centred, not top-packed (#48): the grey behind the scroll view
                    // is the document surround every PDF viewer draws so you can see
                    // where the page ends. Top-packed and full-bleed, it could only
                    // ever show below the last page — a third of the viewport in one
                    // dead block on a one-page document. minHeight only bites while
                    // the content is shorter than the viewport, so a multi-page
                    // document still packs from the top and scrolls unchanged.
                    .frame(minWidth: geo.size.width, minHeight: geo.size.height,
                           alignment: .center)
                }
                // The wall the pages sit on follows the page colours, not the system
                // appearance (#512, `Brand.Reading`): a sepia page framed in the dark
                // grey this shows the rest of the time would be a sepia page in the
                // wrong room.
                .background(Brand.Reading.gutter(model.pageTint))
                .overlay(alignment: .topLeading) { zoomProbes(geo: geo) }
                // The pinch itself lives in the overlay inside the stack above, not
                // here: `MagnificationGesture` reports a bare scale and no location at
                // all, so nothing hung off it could know where to anchor (#530).
                // The iPad's pages sidebar (#174), inset *before* the top chrome so the tool
                // strip spans the whole width above both of them — the arrangement the
                // desktops use, in this platform's own controls. Inset rather than an HStack
                // around `document` so the scroll view keeps its own safe area, its own
                // scrolling and the pinch recogniser #530 works through.
                .safeAreaInset(edge: .leading, spacing: 0) { pagesSidebar }
                .safeAreaInset(edge: .top, spacing: 0) { topChrome }
                // Reading mode's own chrome: the floating bar, the way out by the edge,
                // and the box the page number opens (#506).
                .overlay(alignment: .bottom) { readingChrome(geo: geo) }
                .onChange(of: searchText) { term in
                    model.search(term: term)
                }
                // The reading bar's previous / next / go-to-page (#506). It is drawn in an
                // overlay outside this reader, so it asks by setting a page here rather
                // than by holding a proxy of its own.
                .onChange(of: pendingScrollTarget) { target in
                    guard let target else { return }
                    withAnimation { proxy.scrollTo(target, anchor: .top) }
                    pendingScrollTarget = nil
                }
                // Bring the current match's page on screen (wraps included).
                // The screenshot launch skips this: its matches are already at
                // the top of page 1, and centring them would push them under
                // the find bar on the taller iPad capture.
                .onChange(of: model.currentMatchIndex) { newValue in
                    guard model.screenshotSearchTerm == nil else { return }
                    guard let newValue, newValue < model.searchMatches.count else { return }
                    // Two steps, and the order matters: the page scroll materialises
                    // the row in the lazy stack so the anchor exists at all, then
                    // centring the anchor puts the match itself on screen -- on both
                    // axes, which is what a zoomed-in page needs.
                    withAnimation {
                        proxy.scrollTo(model.searchMatches[newValue].pageIndex,
                                       anchor: .center)
                    }
                    DispatchQueue.main.async {
                        withAnimation { proxy.scrollTo(matchAnchorID, anchor: .center) }
                    }
                }
            }
        }
    }

    // MARK: - the pages (#174)

    /// Whether the pages are a sidebar rather than a sheet: regular width, open, and not in
    /// reading mode — which takes the chrome away and this with it.
    private var showsPagesSidebar: Bool { isRegular && model.pagesOpen && !model.readingMode }

    /// The iPad's pages sidebar. Built at all only when it is showing, so its tiles are out of
    /// the tab order and out of the accessibility tree the rest of the time — the rule #506 wrote
    /// down for reading mode, applied to the one piece of chrome added since.
    @ViewBuilder
    private var pagesSidebar: some View {
        if showsPagesSidebar {
            HStack(spacing: 0) {
                PagesPanel(model: model, documentName: displayName,
                           presentation: .sidebar, onDismiss: { model.setPagesOpen(false) })
                    .frame(width: 280)
                Divider()
            }
        }
    }

    // MARK: - reading mode (#506, #512)

    /// Everything reading mode puts on screen, and nothing when it is off.
    @ViewBuilder
    private func readingChrome(geo: GeometryProxy) -> some View {
        if model.readingMode {
            ZStack(alignment: .bottom) {
                // Takes no touch and no space of its own: its recogniser lives on the
                // window, so the page keeps every pixel and every gesture it had.
                ScreenEdgeBackGesture(isEnabled: true, onSwipe: stepBackFromReading)
                    .frame(width: 0, height: 0)
                    .allowsHitTesting(false)
                if readingBarVisible {
                    ReadingBar(page: currentPageIndex + 1,
                               pageCount: max(pageSizes.count, 1),
                               tint: model.pageTint,
                               actions: readingBarActions(geo: geo))
                        .padding(.bottom, 16)
                        .transition(.opacity)
                }
            }
        }
    }

    /// The page at the top of the viewport: what "previous", "next" and *fit page* are
    /// about. `visible` is kept by the page rows themselves, so this is the same answer
    /// the render window is built from.
    private var currentPageIndex: Int {
        min(max(visible.min() ?? 0, 0), max(pageSizes.count - 1, 0))
    }

    private func readingBarActions(geo: GeometryProxy) -> ReadingBarActions {
        ReadingBarActions(
            previousPage: { goToPage(currentPageIndex, containerWidth: geo.size.width) },
            nextPage: { goToPage(currentPageIndex + 2, containerWidth: geo.size.width) },
            goToPage: {
                goToPageText = ""
                goToPageOpen = true
                showReadingBar()
            },
            // The two presets are deliberately NOT anchored (#530). Fit width and fit
            // page are answers to "show me the page", not zooms aimed at a point on it,
            // and both land on a zoom at which the page fits an axis of the viewport
            // exactly — so there is nothing the offset could usefully hold still, and
            // holding the middle would scroll a page that has just been made to fit away
            // from its own top. The same reasoning as the `FitOnOpen` guard in #534.
            fitWidth: {
                zoomFloor = ReadingZoom.fitWidth
                zoom = ReadingZoom.fitWidth
                afterReadingBarAction(containerWidth: geo.size.width)
            },
            fitPage: {
                let fit = ReadingZoom.fitPage(viewport: geo.size,
                                              page: pageSizes[currentPageIndex])
                // The floor comes down with it, or the clamp would undo the preset the
                // moment it was applied on any page taller than it is wide.
                zoomFloor = min(ReadingZoom.fitWidth, fit)
                zoom = fit
                afterReadingBarAction(containerWidth: geo.size.width)
            },
            // A button carries no position, so these two anchor on the middle of what
            // is on screen — the same choice the menu and keyboard zooms made on Windows
            // (#546) and in the Avalonia app (#534).
            zoomOut: {
                let target = ReadingZoom.clamped(zoom / 1.25, floor: zoomFloor)
                zoomAnchor.anchor(aroundWindowPoint: nil, from: zoom, to: target) {
                    zoom = target
                }
                afterReadingBarAction(containerWidth: geo.size.width)
            },
            zoomIn: {
                let target = ReadingZoom.clamped(zoom * 1.25, floor: zoomFloor)
                zoomAnchor.anchor(aroundWindowPoint: nil, from: zoom, to: target) {
                    zoom = target
                }
                afterReadingBarAction(containerWidth: geo.size.width)
            },
            // The find bar is the one piece of chrome the plan lets over the reading view
            // (§2), and with the tool bar gone this is the only way to reach it on a phone.
            find: {
                if !searchOpen { searchOpen = true }
                showReadingBar()
            },
            exit: { model.setReadingMode(false) }
        )
    }

    private func afterReadingBarAction(containerWidth: CGFloat) {
        pushWindow(containerWidth: containerWidth)
        showReadingBar()
    }

    /// One step back, which is what both ways out ask for (plan §7, "Esc and Back
    /// ordering"): the find bar first if it is open, then reading mode, and never both at
    /// once. Getting this order wrong is how a user loses the thing they were using — the
    /// desktop's ladder is four levels for the same reason.
    private func stepBackFromReading() {
        if searchOpen {
            closeSearch()
            return
        }
        model.setReadingMode(false)
    }

    /// Shows the bar and, unless a screen reader is running, arms the fade.
    private func showReadingBar() {
        withReadingAnimation { readingBarVisible = true }
        armReadingBarFade()
    }

    /// Arms the idle fade, or deliberately does not (`ReadingBarFade`).
    ///
    /// The generation counter is what makes the timer safe to arm on every tap: a fade
    /// from an earlier tap finds its generation stale and does nothing, so the bar hides
    /// two seconds after the *last* tap rather than the first.
    private func armReadingBarFade() {
        readingFadeGeneration &+= 1
        let generation = readingFadeGeneration
        guard ReadingBarFade.armsTimer(voiceOverRunning: voiceOverRunning) else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + ReadingBarFade.idle) {
            guard generation == readingFadeGeneration, model.readingMode else { return }
            withReadingAnimation { readingBarVisible = false }
        }
    }

    /// Reduced motion is answered by not animating at all, rather than by a shorter
    /// animation: the bar appearing and disappearing is the information, and the fade was
    /// only ever decoration.
    private func withReadingAnimation(_ body: () -> Void) {
        if UIAccessibility.isReduceMotionEnabled {
            body()
        } else {
            withAnimation(.easeInOut(duration: 0.2)) { body() }
        }
    }

    /// Whether the bar must be pinned.
    ///
    /// `-uiTestPinReadingBar` is the same kind of lever as #531's `-uiTestZoomProbes`, and
    /// exists for the same reason: VoiceOver cannot be turned on from inside a UI test, so
    /// without it the one rule that matters most here — the bar never fades for the people
    /// reading mode is for — could only be argued, never run. The flag substitutes the
    /// *reading* of VoiceOver's state and nothing else; every line downstream of it is the
    /// code a real screen reader takes.
    static func screenReaderRunning() -> Bool {
        if ProcessInfo.processInfo.arguments.contains("-uiTestPinReadingBar") { return true }
        return UIAccessibility.isVoiceOverRunning
    }

    /// Scrolls to a one-based page number, if there is one there.
    private func goToPage(_ oneBased: Int, containerWidth: CGFloat) {
        let index = oneBased - 1
        guard pageSizes.indices.contains(index) else { return }
        pendingScrollTarget = index
        showReadingBar()
    }

    /// Pushes the stored page colours into the model, which is what renders with them.
    private func syncPageTint() {
        model.setPageTint(PageTint(rawValue: pageColours) ?? .normal)
    }

    // MARK: - the zoom probes ViewerZoomUITests pinches at (#465)

    /// Built only under `-uiTestZoomProbes`, which one UI test passes and nothing else does:
    /// no ordinary launch, and no screenshot or preview capture, has either of these in its
    /// accessibility tree. Neither one draws a pixel or takes a touch.
    ///
    /// Why the app has to offer them (#465). `XCUIElement.pinch(withScale:velocity:)` places
    /// its two synthetic touches from the element's frame, clipped to the window and inset
    /// 50pt. Once a first pinch has zoomed the page past the viewport, the page's frame IS
    /// the window, so the lower touch lands 50pt from the bottom of the screen — inside the
    /// compact-width bottom toolbar (`ToolbarItemGroup(placement: .bottomBar)`), which takes
    /// it, as a toolbar should. `MagnificationGesture` then only ever sees one finger, and
    /// the second pinch measured as a complete no-op on every iPhone while iPad — regular
    /// width, tools along the top in `regularToolStrip`, nothing 50pt from the bottom —
    /// passed. That was never a zoom bug: it was two fingers, one of them on the toolbar,
    /// which no hand does. `viewerPinchProbe` is a frame and nothing else, well inside the
    /// page; `allowsHitTesting(false)` lets the touches fall through to the gesture below it,
    /// so the test pinches the page the way a hand does.
    ///
    /// `viewerZoomProbe` carries the committed `zoom` in its label. Pixels say whether the
    /// screen changed; this says what the app itself did, which is the difference between
    /// "the gesture never arrived" and "it arrived and did nothing".
    private var zoomProbesRequested: Bool {
        ProcessInfo.processInfo.arguments.contains("-uiTestZoomProbes")
    }

    @ViewBuilder
    private func zoomProbes(geo: GeometryProxy) -> some View {
        if zoomProbesRequested {
            ZStack(alignment: .topLeading) {
                Text("zoom \(String(format: "%.4f", zoom))")
                    .font(.system(size: 6))
                    .foregroundColor(.clear)
                    .accessibilityIdentifier("viewerZoomProbe")
                // Reading mode's own state, for the same reason (#506): "the chrome is
                // gone" is a thing a screenshot can show, but "the app is in reading mode,
                // the bar is up, and page 3 is still the page you were on" is not. Read
                // together with `viewerZoomProbe`, this is what lets the UI tests assert
                // that leaving reading mode came back to the *same place* rather than
                // merely to a page.
                Text("reading \(model.readingMode ? "on" : "off")"
                     + " bar \(readingBarVisible ? "on" : "off")"
                     + " page \(currentPageIndex)"
                     + " tint \(model.pageTint.rawValue.isEmpty ? "Normal" : model.pageTint.rawValue)")
                    .font(.system(size: 6))
                    .foregroundColor(.clear)
                    .accessibilityIdentifier("viewerReadingProbe")
                // #174: what the app itself has, for the tests that drive a page change. The
                // page *sizes* are the identity a reorder is visible in — the fixture's pages
                // are deliberately different sizes — and a rotation shows up in the same string
                // as a swapped width and height, which is how "the rotate arrived and did
                // something" is told from "the rotate arrived and did nothing".
                Text("pages \(model.pagesOpen ? "on" : "off")"
                     + " selecting \(model.pagesSelecting ? "on" : "off")"
                     + " selected \(model.pageSelection.count)"
                     + " count \(pageSizes.count)"
                     + " sizes \(pageSizeFingerprint)")
                    .font(.system(size: 6))
                    .foregroundColor(.clear)
                    .accessibilityIdentifier("viewerPagesProbe")
                Color.clear
                    .frame(width: max(geo.size.width * 0.7, 40),
                           height: max(geo.size.height * 0.4, 40))
                    .contentShape(Rectangle())
                    .accessibilityElement()
                    .accessibilityIdentifier("viewerPinchProbe")
                    .accessibilityLabel("Pinch probe")
                    .padding(.leading, geo.size.width * 0.15)
                    .padding(.top, geo.size.height * 0.2)
            }
            .allowsHitTesting(false)
        }
    }

    /// The pages as the probe reports them: "612x792,400x300,…", in document order.
    private var pageSizeFingerprint: String {
        pageSizes.map { "\(Int($0.width))x\(Int($0.height))" }.joined(separator: ",")
    }

    /// Reading mode's own presentations, sat on top of `presentations` rather than in it.
    ///
    /// Not a style choice: `presentations` was already at the Swift type-checker's
    /// complexity ceiling before this issue — the comment on the redaction
    /// `confirmationDialog` records the last time one more modifier tipped it — and adding
    /// these five to that chain produced exactly the same "unable to type-check this
    /// expression in reasonable time". Two chains of modifiers over one view is the same
    /// view; one chain of thirty-five is a build failure.
    var body: some View {
        presentations
            .sheet(isPresented: $settingsOpen) {
                SettingsView()
            }
            // The bar's page number, as a box to type one into (#506). An alert with a
            // field, not a sheet: it is one number, and the page behind it should stay
            // visible while it is asked for.
            .alert("Go to page", isPresented: $goToPageOpen) {
                TextField("Page", text: $goToPageText)
                    .keyboardType(.numberPad)
                    .accessibilityIdentifier("readingGoToPageField")
                Button("Go") {
                    if let wanted = Int(goToPageText.trimmingCharacters(in: .whitespaces)),
                       pageSizes.indices.contains(wanted - 1) {
                        pendingScrollTarget = wanted - 1
                    }
                    showReadingBar()
                }
                Button("Cancel", role: .cancel) { showReadingBar() }
            }
            // Reading mode's own lifecycle: the bar comes up with the mode, the stored
            // page colours reach the model, and a screen reader turning on mid-session
            // pins the bar where it is (#506, #512).
            .onChange(of: model.readingMode) { on in
                if on { showReadingBar() }
            }
            .onChange(of: pageColours) { _ in syncPageTint() }
            // The phone's pages: a sheet over the document, half height to begin with, so the
            // page being worked on is still on screen above the grid (#174).
            .sheet(isPresented: Binding(get: { model.pagesOpen && !isRegular },
                                        set: { if !$0 { model.setPagesOpen(false) } })) {
                PagesPanel(model: model, documentName: displayName,
                           presentation: .sheet, onDismiss: { model.setPagesOpen(false) })
                    .presentationDetents([.medium, .large])
                    .presentationDragIndicator(.visible)
            }
            // A page change that was refused, in one sentence saying what happened and that
            // nothing changed (#174).
            //
            // Raised from here only when the pages sheet is **not** up: an alert attached below a
            // sheet never appears over it, so in compact width `PagesPanel` presents this same
            // value instead. Two presenters, each answering for its own case and never both live
            // — the shape #173 settled on for the redaction question.
            .alert("Nothing was changed",
                   isPresented: Binding(get: { model.pageToolRefusal != nil && !(model.pagesOpen && !isRegular) },
                                        set: { if !$0 { model.pageToolRefusal = nil } })) {
                Button("OK", role: .cancel) { model.pageToolRefusal = nil }
            } message: {
                Text(model.pageToolRefusal ?? "")
            }
            // A tap on a thumbnail asks for its page; the scroll itself belongs to the reader
            // inside `document`, which already has a way to be asked (#506's `pendingScrollTarget`).
            .onChange(of: model.pageToShow) { target in
                guard let target else { return }
                pendingScrollTarget = target
                model.pageToShow = nil
            }
            .onReceive(NotificationCenter.default.publisher(
                for: UIAccessibility.voiceOverStatusDidChangeNotification)) { _ in
                voiceOverRunning = ViewerView.screenReaderRunning()
                if voiceOverRunning { showReadingBar() }
            }
            // Escape on an iPad keyboard steps back one level, and only one (plan §7).
            // The find bar's own Done already owns `.cancelAction` while it is open
            // (`searchBar`), so this is attached only when it is not: two live
            // `.cancelAction`s would make which one Escape reached a matter of luck, and
            // the whole point of the ladder is that it is not.
            //
            // Zero-sized and hidden from assistive technology on purpose: it is a keyboard
            // affordance, and reading mode's visible way out is the bar's Exit. A hidden
            // *focusable* control over the page would be the focus trap this mode exists
            // to avoid, which is why it is `accessibilityHidden` rather than merely clear.
            .background {
                if model.readingMode && isPad && !searchOpen {
                    Button("Exit reading mode") { stepBackFromReading() }
                        .keyboardShortcut(.cancelAction)
                        .frame(width: 0, height: 0)
                        .opacity(0)
                        .accessibilityHidden(true)
                }
            }
    }

    /// Everything the viewer presents over the page: the toolbar, the sheets, the alerts
    /// and the confirmations, in the order they were added.
    private var presentations: some View {
        document
            .navigationTitle((model.isDirty ? "• " : "") + displayName)
            .navigationBarTitleDisplayMode(.inline)
            // The bar sits on the dark wall, so it takes the dark scheme whatever the
            // system's: in light mode the transparent bar drew the title and the status
            // bar in black on Brand.backdrop, 2.0 : 1. The background is made visible
            // and the wall's own colour because the scheme only applies to a bar whose
            // background is showing, and so a page scrolled up under the bar does not
            // flip the bar back to light halfway through a scroll.
            .toolbarBackground(Brand.backdrop, for: .navigationBar)
            .toolbarBackground(.visible, for: .navigationBar)
            .toolbarColorScheme(.dark, for: .navigationBar)
            // #144: the navigation bar holds what is done to the file as a whole — Close,
            // Save, and a More menu — and the bottom toolbar holds the everyday tools, as
            // the HIG lays out an iPhone document viewer. Everything else stays in More,
            // so the page keeps the screen.
            .toolbar { viewerToolbar }
            // Reading mode takes the chrome away, and takes it out of the accessibility
            // tree with it (#506). Two things do that, on purpose:
            //
            // `.toolbar(.hidden, …)` is what the plan names, and it is what stops the bars
            // being *drawn*. Not composing `viewerToolbar`'s items at all
            // (`showsToolbarItems`) is what makes the promise about VoiceOver true by
            // construction rather than by trusting a visibility modifier to reach the
            // accessibility tree — reading mode is meant to be the screen-reader-friendly
            // view, so a Close button still reachable by swipe under a hidden bar would be
            // worse than not shipping it. `ReadingModeUITests` asserts the buttons are gone
            // from the tree, which is the assertion that would catch either half failing.
            .toolbar(model.readingMode ? .hidden : .visible,
                     for: .navigationBar, .bottomBar)
            // The keyboard's commands (#172, `MegaPDFCommands`): what the buttons above do,
            // reachable from ⌘S, ⌘W, ⌘F and ⌘Z on an iPad keyboard.
            .focusedSceneValue(\.viewerCommands, commandTarget)
            .onPreferenceChange(MoreMenuAnchorKey.self) { moreMenuAnchor = $0 }
            .sheet(isPresented: $aboutOpen) {
                AboutView()
            }
            // #378: the OS share sheet. `isPresented`, not `.sheet(item:)`, because `URL` has no
            // stable identity of its own to key a sheet off — the guard inside re-reads
            // `model.shareURL` for the content.
            .sheet(isPresented: Binding(get: { model.shareURL != nil },
                                        set: { if !$0 { model.shareURL = nil } })) {
                if let url = model.shareURL {
                    ShareSheet(activityItems: [url], anchor: moreMenuAnchor)
                }
            }
            // The confirmation #173 asks for, before either save path writes anything: what
            // redaction does, that it cannot be undone once saved, and Save a copy as the
            // DEFAULT action — the reversible choice, because the other cannot be taken back.
            //
            // #481's signature confirmation shares this one call rather than adding a second
            // `.confirmationDialog` next to it: `body`'s modifier chain is already long enough
            // that one more attached directly hit the type-checker's complexity ceiling
            // ("unable to type-check this expression in reasonable time").
            .confirmationDialog(
                saveConfirmTitle,
                isPresented: saveConfirmPresented(regular: false),
                titleVisibility: .visible
            ) {
                saveConfirmButtons
            } message: {
                saveConfirmMessage
            }
            .alert("", isPresented: Binding(get: { model.redactionSummary != nil },
                                            set: { if !$0 { model.redactionSummary = nil } })) {
                Button("OK", role: .cancel) { model.redactionSummary = nil }
            } message: {
                Text(model.redactionSummary ?? "")
            }
            .alert("Nothing was removed",
                   isPresented: Binding(get: { model.redactionRefusal != nil },
                                        set: { if !$0 { model.redactionRefusal = nil } })) {
                Button("OK", role: .cancel) { model.redactionRefusal = nil }
            } message: {
                Text(model.redactionRefusal ?? "")
            }
            // Compact width only: in regular width the library is a popover on the Sign
            // button in `regularToolStrip` (#172), off the same flag.
            .sheet(isPresented: Binding(get: { signaturesOpen && !isRegular },
                                        set: { if !$0 { signaturesOpen = false } })) {
                signaturesLibrary
            }
            .onAppear {
                // Before anything else: the first render of the first page should already
                // be the colour that was asked for, not a white page that turns sepia.
                syncPageTint()
                if model.readingMode { showReadingBar() }
                if model.screenshotSheet != nil { signaturesOpen = true }
                // `-screenshot search`: open the find bar with the term already
                // typed and start the scan here. This view only exists in the
                // `.viewing` state, so the document is loaded by construction —
                // the seeded search can't fire too early or be lost.
                if let term = model.screenshotSearchTerm, !searchOpen {
                    searchOpen = true
                    searchText = term
                    model.search(term: term, debounce: false)
                }
            }
            // A sheet, not an alert: #43 puts size and face pickers beside the text
            // field, and an alert's content builder ignores everything that is not a
            // button or a text field. The `item:` form also drops the no-op-setter
            // hack the alert needed — it does not flip its own binding when a button
            // is tapped, so the pending tap can only be resolved deliberately.
            //
            // The field and the pickers bind to the model, not to @State, so a
            // correction's prefill lands in the same update as `pendingText` rather
            // than racing the sheet's presentation.
            // The document's own text (#113): one field, the line keeps its size and font.
            .sheet(item: $model.pendingBodyEdit) { _ in
                BodyTextSheet(
                    text: $model.bodyDraft,
                    onSave: { model.commitBodyEdit(model.bodyDraft) },
                    onCancel: model.cancelBodyEdit
                )
            }
            // Unlocking a restricted document; setting, changing or removing its password (#131).
            .sheet(item: $model.securitySheet) { mode in
                DocumentSecuritySheet(
                    mode: mode,
                    savesChanges: model.isDirty,
                    isBusy: model.isSaving || model.isUnlocking,
                    error: model.securityError,
                    documentIsSigned: model.isSignedDocument,
                    documentIsCertified: model.isCertifiedSignature,
                    onUnlock: model.unlock,
                    onSetPassword: model.setPassword,
                    onRemovePassword: model.removePassword,
                    onCancel: model.dismissSecuritySheet
                )
            }
            .overlay(alignment: .bottom) {
                if let notice = model.notice {
                    NoticeBanner(text: notice)
                        .padding(.bottom, 24)
                        .transition(.move(edge: .bottom).combined(with: .opacity))
                }
            }
            .animation(.easeInOut(duration: 0.2), value: model.notice)
            // Compact width only, like the signatures sheet: the iPad's is a popover on
            // the Add text button (#172).
            .sheet(item: Binding(get: { isRegular ? nil : model.pendingText },
                                 set: { model.pendingText = $0 })) { pending in
                textBoxEditor(pending)
            }
            // One alert for all three callers (#145, #377, #378): what Save/Cancel do never
            // changes, kept in the model as `unsavedChangesFollowUp` rather than three separate
            // booleans here. The second button and the message DO change for Share (Fable's
            // #378 review, 2026-09-26): "Discard" read as if the edits themselves were being
            // thrown away, when for Share it only ever meant which file gets shared — Close and
            // an external open really do discard, so they keep the word and the destructive
            // styling.
            .alert("Unsaved changes", isPresented: Binding(
                get: { model.unsavedChangesFollowUp != nil },
                set: { if !$0 { model.unsavedChangesFollowUp = nil } })
            ) {
                Button("Save") {
                    switch model.unsavedChangesFollowUp {
                    case .close: model.save(then: .close)
                    case .share: model.save(then: .share)
                    case let .open(url): model.save(then: .open(url))
                    case nil: break
                    }
                }
                if model.unsavedChangesFollowUp == .share {
                    // Not destructive: nothing is thrown away here, only excluded from what's
                    // about to be shared (#378).
                    Button("Share without saving") { model.share() }
                } else {
                    Button("Discard", role: .destructive) {
                        switch model.unsavedChangesFollowUp {
                        case .close: onClose()
                        // The pending edits to the document being replaced are lost (#377) — this
                        // is only reachable for a document handed over from outside, never the
                        // everyday Close path above.
                        case let .open(url): model.openPicked(url: url)
                        case .share, nil: break
                        }
                    }
                }
                Button("Cancel", role: .cancel) {}
            } message: {
                if model.unsavedChangesFollowUp == .share {
                    Text("This document has unsaved changes. They won't be in the shared copy unless you save first.")
                } else {
                    Text("This document has unsaved changes.")
                }
            }
            // #139: once per page, before a text-box change on a page PDFium's rewrite would alter.
            // The buttons answer; the binding's setter does nothing, so SwiftUI dismissing the alert
            // around a button tap can never answer Cancel ahead of Continue.
            .alert("Change this page?",
                   isPresented: Binding(get: { model.pageRewriteWarning != nil }, set: { _ in })) {
                Button("Continue") { model.answerPageRewriteWarning(true) }
                Button("Cancel", role: .cancel) { model.answerPageRewriteWarning(false) }
            } message: {
                Text("Changing this page may slightly alter parts of it you haven't touched.")
            }
    }

    /// The navigation bar (#144): what is done to the file as a whole — Close, Save and
    /// the ⋯ menu — and, in compact width, the bottom bar of everyday tools.
    @ToolbarContentBuilder
    private var viewerToolbar: some ToolbarContent {
        // #145: while a save, a password change or an open runs, Close and the file commands
        // are disabled; the model ignores them too while a change is being applied.
        if showsToolbarItems {
        ToolbarItem(placement: .navigationBarLeading) {
            Button("Close", action: closeTapped)
                .disabled(model.fileCommandsBlocked)
        }
        ToolbarItemGroup(placement: .navigationBarTrailing) {
            Button(saveLabel, action: saveTapped)
                // Marks count as something to save, even though they are not a change
                // to the document — nothing is written until the question above is
                // answered, so marking deliberately leaves it clean. Asking isDirty
                // alone left Save greyed out with areas marked, which made the branch
                // inside this very button unreachable and left the ⋯ menu as the only
                // way to finish a redaction (#173).
                .disabled(!canSave)
                // On the iPad the redaction question is a popover, and a popover
                // points at something: the Save it was raised from (#172). The
                // compact layouts keep the action sheet on the view, below. #481's
                // signature confirmation shares this call too (see `body`'s twin).
                .confirmationDialog(
                    saveConfirmTitle,
                    isPresented: saveConfirmPresented(regular: true),
                    titleVisibility: .visible
                ) {
                    saveConfirmButtons
                } message: {
                    saveConfirmMessage
                }
            Menu {
                Button("Save a copy") {
                    if model.redactionMarkCount > 0 { redactConfirm = .copy } else { onSaveCopy() }
                }
                    .disabled(model.isSaving || model.fileCommandsBlocked)
                // #386: a Markdown export, alongside Save a copy rather than a variant of
                // it -- it is a one-way, lossy text export (contract 9 drops layout, field
                // interactivity, everything Markdown can't model), and MegaPDF has no
                // Markdown-import path, so the label says "Export", not "Save", and its own
                // "Exported" completion message (ViewerModel.finishMarkdownExport) never
                // clears the "Unsaved changes" state a real Save still needs to answer.
                Button("Export as Markdown") {
                    if model.redactionMarkCount > 0 { redactConfirm = .markdown } else { onExportMarkdown() }
                }
                    .disabled(model.isSaving || model.fileCommandsBlocked)
                    .accessibilityIdentifier("viewerExportMarkdown")
                // #378: hands the document to the OS's own share sheet (Mail, Messages,
                // AirDrop, another PDF app, whatever is installed) rather than a
                // MegaPDF-drawn destination list. Unsaved changes ask first, through the
                // same alert Close uses, wired to share instead of close.
                Button {
                    if model.isDirty { model.unsavedChangesFollowUp = .share } else { model.share() }
                } label: {
                    Label("Share", systemImage: "square.and.arrow.up")
                }
                    .disabled(!model.canShare)
                    .accessibilityIdentifier("viewerShare")
                Button("Password…", action: model.showPasswordCommand)
                    .disabled(!model.canUsePasswordCommand || model.fileCommandsBlocked)
                if model.capabilities.isRestricted {
                    Button("Unlock with owner password…", action: model.showUnlock)
                        .disabled(model.isUnlocking || model.fileCommandsBlocked)
                }
                Divider()
                // Redact lives here rather than on the bottom bar (#328): it is not an
                // everyday tool — it is the one command that destroys content — so it
                // keeps the company of the file-level commands instead of taking a
                // permanent place beside Sign and Add text. It is a labelled row, so
                // its name is in the list rather than guessed at from an icon, and the
                // name does not change with its state, so the row does not move under a
                // finger or rename itself on the way to being tapped.
                //
                // A Toggle, not a Button with a hand-swapped checkmark: in a menu the
                // state is the row's own (UIMenuElement's state, which is what draws the
                // checkmark and what a screen reader announces as selected), so the
                // platform owns both the drawing and the announcement.
                Toggle("Redact", isOn: Binding(get: { model.redactMode },
                                               set: { _ in model.toggleRedactMode() }))
                    .disabled(!model.canRedact || model.fileCommandsBlocked)
                    // And the state in words as well, which is how #173 defined it and
                    // what Android says ("Activé" / "Désactivé"): it words both states,
                    // where a checkmark only marks one. If the menu bridge drops a value
                    // — the way the bottom bar dropped `.isSelected` — the toggle's own
                    // state is what is left, and it says the same thing.
                    .accessibilityValue(model.redactMode ? "On" : "Off")
                    .accessibilityHint("Remove content from the file")
                    .accessibilityIdentifier("viewerRedact")
                if model.redactionMarkCount > 0 {
                    Button("Clear all marks", action: model.clearRedactionMarks)
                        .disabled(!model.canRedact || model.fileCommandsBlocked)
                        .accessibilityIdentifier("viewerClearRedactionMarks")
                }
                // #3: beside Redact, because the two are the pair people confuse and this is
                // where the difference can be read in one glance -- one covers, one removes,
                // and the hints say exactly that. A Toggle for the same reason Redact is one:
                // in a menu the armed state is the row's own, so the platform draws the
                // checkmark and announces it.
                Toggle("Whiteout", isOn: Binding(get: { model.whiteoutMode },
                                                 set: { _ in model.toggleWhiteoutMode() }))
                    .disabled(!model.canWhiteout || model.fileCommandsBlocked)
                    .accessibilityValue(model.whiteoutMode ? "On" : "Off")
                    .accessibilityHint("Cover an area without removing what is under it")
                    .accessibilityIdentifier("viewerWhiteout")
                Divider()
                // #506: reading mode's entry point on every iPhone and iPad, beside Share
                // and Export as Markdown as the plan's §4 asks. A Button, not a Toggle:
                // once reading mode is on this menu is gone with the rest of the chrome,
                // so this row can only ever turn it on, and a checkmark that is never seen
                // ticked would be furniture. The way back is the bar's Exit or the edge
                // swipe.
                // #174: the pages, beside Reading mode, Share and Export as Markdown — where
                // this app keeps what is done to the document as a whole. Never disabled: the
                // grid opens on a restricted document too, and says there what it will not let
                // you change, rather than being a row that cannot be tapped for reasons the
                // menu has no room to give.
                Button("Pages") { model.setPagesOpen(true) }
                    .accessibilityIdentifier("viewerPages")
                Button("Reading mode") { model.setReadingMode(true) }
                    .accessibilityIdentifier("viewerReadingMode")
                Button("Settings…") { settingsOpen = true }
                    .accessibilityIdentifier("viewerSettings")
                Button("About MegaPDF") { aboutOpen = true }
            } label: {
                Label("More", systemImage: "ellipsis.circle")
            }
            .accessibilityIdentifier("viewerMore")
            // Captures where this button actually ends up on screen, for the iPad share
            // popover (#378) — a `GeometryReader` behind a toolbar item still reports real
            // window coordinates, so this needs no fixed guess at the nav bar's geometry.
            .background(
                GeometryReader { geo in
                    Color.clear
                        .preference(key: MoreMenuAnchorKey.self, value: geo.frame(in: .global))
                }
            )
        }
        // The phone's bar. In regular width the same tools are `regularToolStrip`
        // (#172), and nothing is placed here — an empty bottom bar would still be
        // drawn.
        if !isRegular {
            ToolbarItemGroup(placement: .bottomBar) {
                // A restricted open can't use the tools its owner withheld (#131);
                // the notice shown when it opened says why.
                Button { signaturesOpen = true } label: {
                    toolLabel("Sign", systemImage: "signature")
                }
                .disabled(!model.capabilities.canSign || model.fileCommandsBlocked)
                Button { model.startTextPlacement() } label: {
                    toolLabel("Add text", systemImage: "character.textbox")
                }
                .disabled(!model.capabilities.canAddText || model.fileCommandsBlocked)
                // Redact was the third button here; it is in the ⋯ menu now (#328).
                Button(action: toggleSearch) {
                    toolLabel("Search", systemImage: "magnifyingglass")
                }
                .accessibilityLabel("Find in document")
                Spacer()
                Button(action: model.undo) {
                    toolLabel("Undo", systemImage: "arrow.uturn.backward")
                }
                .disabled(!model.canUndo || model.fileCommandsBlocked)
                Button(action: model.redo) {
                    toolLabel("Redo", systemImage: "arrow.uturn.forward")
                }
                .disabled(!model.canRedo || model.fileCommandsBlocked)
            }
        }
        }
    }

    /// Whether the navigation and tool bars have anything in them at all.
    ///
    /// False in reading mode, which is how the Close, Save, ⋯ and tool buttons leave the
    /// accessibility tree rather than merely stop being drawn (#506, and `body`'s
    /// `.toolbar(.hidden, …)` beside it).
    private var showsToolbarItems: Bool { !model.readingMode }

    /// A bottom-toolbar label (#144).
    ///
    /// The title is here for assistive technology, not for the screen: a Label inside a
    /// SwiftUI toolbar item renders icon-only on iOS 26 whatever style it is given, and
    /// wherever it is placed. Measured twice on an iPad Pro 13" (#172) — forcing the old
    /// `showsTitle: true`, and again with the built-in `.labelStyle(.titleAndIcon)` —
    /// and the bar came back identical to the iPhone's both times; the same Label put in
    /// the navigation bar lost its title too. So the size-class read this used to do was
    /// never going to reach the screen, and asking for a style here would only be a line
    /// that reads like a feature. Titles on the iPad need the tools to stop being
    /// toolbar items; that is on #172.
    private func toolLabel(_ title: LocalizedStringKey, systemImage: String) -> some View {
        Label(title, systemImage: systemImage)
    }

    /// What sits between the navigation bar and the page: the iPad's tools (#172), the
    /// find bar, the dynamic-XFA banner (#456, #457), and the document-level busy strip
    /// (#145), in that order.
    private var topChrome: some View {
        VStack(spacing: 0) {
            // The iPad's tool strip goes with the rest of the chrome in reading mode
            // (#506); the find bar below it is the one piece the plan lets stay, and it
            // closes back into the chrome-free view.
            if isRegular && !model.readingMode { regularToolStrip }
            if searchOpen { searchBar }
            // Calm and persistent, not a dialog and not the transient `NoticeBanner`
            // (below, over the page): up for as long as the document is open, in the same
            // place the find bar and the busy strip live, so it reads as part of the
            // document's own chrome rather than a passing message.
            if model.isDynamicXfa {
                DynamicXfaBanner()
                    .transition(.opacity)
            }
            if let work = busy.strip {
                BusyStrip(work: work, canStop: busy.canStop, isStopping: busy.isStopping,
                          onStop: busy.requestStop)
                    .transition(.opacity)
            }
        }
    }

    // MARK: - file commands, shared by the bar and the keyboard (#172)

    /// The commands as the keyboard sees them. A command that cannot be done now is
    /// published as nil, which the menu shows disabled.
    private var commandTarget: ViewerCommandTarget {
        var target = ViewerCommandTarget()
        if canSave { target.save = saveTapped }
        if !model.fileCommandsBlocked { target.close = closeTapped }
        target.find = toggleSearch
        target.pages = model.togglePages
        if model.canUndo && !model.fileCommandsBlocked { target.undo = model.undo }
        if model.canRedo && !model.fileCommandsBlocked { target.redo = model.redo }
        return target
    }

    private var canSave: Bool {
        (model.isDirty || model.redactionMarkCount > 0) && !model.isSaving && !model.fileCommandsBlocked
    }

    private func saveTapped() {
        // Marks on the document mean the question comes first (#173): nothing
        // is written until it has been answered -- and that question already mentions
        // the signature too, if there is one (`redactConfirmMessage`).
        if model.redactionMarkCount > 0 {
            redactConfirm = .overwrite
        } else if model.isSignedDocument {
            // #481: no marks to ask about, but Save would still overwrite a signed
            // original -- ask the same way, on its own.
            signatureConfirm = true
        } else {
            model.save()
        }
    }

    private func closeTapped() {
        guard !model.closeBlocked else { return }
        if model.isDirty { model.unsavedChangesFollowUp = .close } else { onClose() }
    }

    private func toggleSearch() {
        if searchOpen { closeSearch() } else { searchOpen = true }
    }

    /// The redaction question is raised from one place per layout — the view, as an
    /// action sheet, in compact width; the Save button, as a popover, in regular — so
    /// each presenter only answers for its own width and the question is never up twice.
    /// #481's signature confirmation shares this one (`saveConfirmPresented`, below) rather
    /// than getting a presenter and a `confirmationDialog` of its own -- redaction's marks
    /// and Save's own signature check can never both be waiting at once (whichever the tap
    /// found first is answered before the other is even asked), and `body`'s modifier chain
    /// was already at the type-checker's complexity ceiling before this was added.
    private func redactConfirmPresented(regular: Bool) -> Binding<Bool> {
        Binding(get: { redactConfirm != nil && isRegular == regular },
                set: { if !$0 { redactConfirm = nil } })
    }

    /// One `confirmationDialog` for both #173's redaction question and #481's signature
    /// question: `redactConfirm` wins when both could apply (its own message already
    /// mentions the signature too, via `redactConfirmMessageText`), so this only reads
    /// `signatureConfirm` once `redactConfirm` is nil.
    private func saveConfirmPresented(regular: Bool) -> Binding<Bool> {
        Binding(get: { (redactConfirm != nil || signatureConfirm) && isRegular == regular },
                set: { if !$0 { redactConfirm = nil; signatureConfirm = false } })
    }

    private var saveConfirmTitle: String {
        redactConfirm != nil
            ? String(localized: "Remove the marked content?")
            : String(localized: "Save over the signed original?")
    }

    /// The confirmation #173 asks for, before either save path writes anything: what
    /// redaction does, that it cannot be undone once saved, and Save a copy as the
    /// DEFAULT action — the reversible choice, because the other cannot be taken back.
    /// #481: when no marks are pending but Save would still overwrite a signed original,
    /// the same dialog asks that question instead (`signatureConfirmButtons`).
    @ViewBuilder
    private var saveConfirmButtons: some View {
        if redactConfirm != nil {
            redactConfirmButtons
        } else {
            signatureConfirmButtons
        }
    }

    private var saveConfirmMessage: some View {
        Group {
            if redactConfirm != nil {
                redactConfirmMessage
            } else {
                signatureConfirmMessage
            }
        }
    }

    @ViewBuilder
    private var redactConfirmButtons: some View {
        Button("Save as a copy") {
            redactConfirm = nil
            Task { if await model.applyRedactions(reportWithSave: true) { onSaveCopy() } }
        }
        // #386: offered here too -- marked-but-unapplied redactions must actually be
        // removed (applyRedactions) before ANY export reads the document's text, Markdown
        // included, or the marked content would leak into the .md file.
        Button("Export as Markdown") {
            redactConfirm = nil
            Task { if await model.applyRedactions(reportWithSave: true) { onExportMarkdown() } }
        }
        Button("Overwrite the original") {
            redactConfirm = nil
            Task { if await model.applyRedactions(reportWithSave: true) { model.save() } }
        }
        Button("Cancel", role: .cancel) { redactConfirm = nil }
    }

    private var redactConfirmMessage: some View {
        // #481: "Overwrite the original" above would also invalidate an existing signature --
        // said here too, rather than as a second question, since this one already gates every
        // way this dialog's buttons can write over the file.
        Text(redactConfirmMessageText)
    }

    private var redactConfirmMessageText: String {
        let base = String(localized: "Redaction permanently removes the marked content. This can't be undone after saving.")
        guard model.isSignedDocument else { return base }
        let signatureNote = model.isCertifiedSignature
            ? String(localized: "This document is also certified as closed to changes; overwriting it will invalidate that certification too.")
            : String(localized: "This document is also digitally signed; overwriting it will invalidate the signature too.")
        return base + " " + signatureNote
    }

    /// The confirmation #481 asks for, before Save overwrites a signed original with no
    /// redaction marks pending: Save a copy is the prominent, safe choice, and overwriting
    /// needs its own deliberate, destructive-styled tap -- the same shape as
    /// `redactConfirmButtons`.
    @ViewBuilder
    private var signatureConfirmButtons: some View {
        Button("Save a copy") {
            signatureConfirm = false
            onSaveCopy()
        }
        Button(model.isCertifiedSignature
               ? "Overwrite the certified original" : "Overwrite the signed original",
               role: .destructive) {
            signatureConfirm = false
            model.save()
        }
        Button("Cancel", role: .cancel) { signatureConfirm = false }
    }

    /// A certification signature (`/DocMDP`) gets different wording (#481): the document's
    /// own structure declares it closed to changes, not merely invalidated by one --
    /// measured in #476 as the common case (33/33 of a real corpus), not the edge case.
    private var signatureConfirmMessage: some View {
        Text(model.isCertifiedSignature
             ? "This document is certified as closed to changes. Saving here will invalidate that certification. Save a copy to keep the signed original intact."
             : "This document has a digital signature. Saving here will invalidate it. Save a copy to keep the signed original intact.")
    }

    // MARK: - the iPad's toolbar (#172)

    /// The everyday tools in regular width, as ordinary views rather than toolbar items.
    ///
    /// A `Label` in a toolbar item renders icon-only on iOS 26 whatever it is asked
    /// (`toolLabel`); a `Label` in a plain `HStack` renders its title, so this is where the
    /// titles the issue asked for come from. The row sits under the navigation bar, on the
    /// same dark wall and in the same scheme, so the two read as one wide iPad bar with the
    /// file commands above and the tools below — the arrangement the desktops use, in this
    /// platform's own controls. Titles go first; when the width will not hold them (a
    /// French row in the narrower half of a Split View) `ViewThatFits` falls back to the
    /// icons, which is what the phone shows all the time.
    ///
    /// Sign and Add text present as popovers anchored to their buttons, not full-screen
    /// sheets: on an iPad the page stays where it was and the arrow says which tool is
    /// open. Redact is not here — it stays in the ⋯ menu on every layout (#328).
    private var regularToolStrip: some View {
        ViewThatFits(in: .horizontal) {
            toolStripRow(titled: true)
            toolStripRow(titled: false)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 4)
        .frame(maxWidth: .infinity)
        .background(Brand.backdrop)
        .environment(\.colorScheme, .dark)
        .accessibilityIdentifier("viewerToolStrip")
    }

    private func toolStripRow(titled: Bool) -> some View {
        HStack(spacing: 8) {
            // A restricted open can't use the tools its owner withheld (#131).
            toolStripButton("Sign", systemImage: "signature", titled: titled) {
                signaturesOpen = true
            }
            .disabled(!model.capabilities.canSign || model.fileCommandsBlocked)
            .popover(isPresented: $signaturesOpen, arrowEdge: .top) {
                signaturesLibrary
                    .frame(width: 480, height: 520)
            }
            toolStripButton("Add text", systemImage: "character.textbox", titled: titled,
                            selected: model.isPlacingText) {
                model.startTextPlacement()
            }
            .disabled(!model.capabilities.canAddText || model.fileCommandsBlocked)
            .popover(item: $model.pendingText, arrowEdge: .top) { pending in
                textBoxEditor(pending)
                    .frame(width: 400, height: 340)
            }
            toolStripButton("Search", systemImage: "magnifyingglass", titled: titled,
                            accessibilityLabel: "Find in document", selected: searchOpen,
                            action: toggleSearch)
            // #506: the iPad has a bar of its own since #172, so reading mode's entry
            // point belongs on it and not only three taps down the ⋯ menu — that is what
            // the issue means by an iPad-specific placement. The ⋯ row stays on both
            // layouts, because compact width (an iPhone, or an iPad in Slide Over) has no
            // strip to put this on.
            // #174: the iPad has a bar of its own (#172), and the sidebar it toggles is the
            // one piece of chrome that belongs on it rather than three taps down the dot-dot-dot
            // menu — the same argument #506 made for reading mode's second entry point. The
            // wash says whether the sidebar is showing, which is what a toggle on a strip owes.
            toolStripButton("Pages", systemImage: "square.grid.2x2", titled: titled,
                            selected: model.pagesOpen) {
                model.togglePages()
            }
            .accessibilityIdentifier("viewerPagesButton")
            toolStripButton("Reading mode", systemImage: "book", titled: titled) {
                model.setReadingMode(true)
            }
            .accessibilityIdentifier("viewerReadingModeButton")
            Spacer(minLength: 24)
            toolStripButton("Undo", systemImage: "arrow.uturn.backward", titled: titled,
                            action: model.undo)
                .disabled(!model.canUndo || model.fileCommandsBlocked)
            toolStripButton("Redo", systemImage: "arrow.uturn.forward", titled: titled,
                            action: model.redo)
                .disabled(!model.canRedo || model.fileCommandsBlocked)
        }
    }

    /// One tool: icon and title (or the icon alone), a pointer hover highlight, and its
    /// state shown as a wash — the thing the phone's bar could not do (#173).
    private func toolStripButton(_ title: LocalizedStringKey, systemImage: String, titled: Bool,
                                 accessibilityLabel: LocalizedStringKey? = nil,
                                 selected: Bool = false,
                                 action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: systemImage)
                .labelStyle(ToolStripLabelStyle(titled: titled))
                .font(Brand.Text.body)
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .background(selected ? Brand.accentSubtle : .clear,
                            in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                .contentShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
        }
        .buttonStyle(.borderless)
        // The bar's own ink: white on the wall like Close above it, dimmed when disabled;
        // the accent is kept for the wash that says a tool is on.
        .tint(.white)
        .hoverEffect(.highlight)
        .accessibilityLabel(Text(accessibilityLabel ?? title))
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    /// The signature library (#100), in whichever presentation the width calls for.
    private var signaturesLibrary: some View {
        SignaturesSheet(
            signatures: model.signatures,
            startDrawing: model.screenshotSheet == .draw,
            loadImage: { SignatureStore().loadImage($0) },
            onPick: { entry in
                signaturesOpen = false
                model.startPlacement(entry)
            },
            onDrawn: model.addDrawnSignature,
            onPhoto: model.importSignature,
            onRename: model.renameSignature,
            onDelete: model.deleteSignature,
            onDismiss: { signaturesOpen = false }
        )
    }

    /// The one text editor (#34, #36, #43), likewise.
    ///
    /// The field and the pickers bind to the model, not to @State, so a correction's
    /// prefill lands in the same update as `pendingText` rather than racing the
    /// presentation.
    private func textBoxEditor(_ pending: PendingText) -> some View {
        TextBoxSheet(
            isEditing: pending.editingId != nil,
            text: $model.draftText,
            fontSize: $model.draftSize,
            fontName: $model.draftFont,
            onCommit: {
                model.commitText(model.draftText,
                                 fontSize: model.draftSize,
                                 fontName: model.draftFont)
            },
            onCancel: model.cancelTextPlacement
        )
    }

    // MARK: - search (#26)

    /// Inline find bar: as-you-type field, "N of M" count, previous/next
    /// (wrapping), and Done to dismiss and clear highlights.
    private var searchBar: some View {
        HStack(spacing: 12) {
            Image(systemName: "magnifyingglass")
                .foregroundColor(.secondary)
            TextField("Find in document", text: $searchText)
                .textFieldStyle(.plain)
                .autocorrectionDisabled()
                .textInputAutocapitalization(.never)
                .submitLabel(.search)
                .focused($searchFocused)
                .onSubmit { model.nextMatch() }
            Text(matchCountLabel)
                .font(.footnote.monospacedDigit())
                .foregroundColor(.secondary)
                .lineLimit(1)
            Button { model.previousMatch() } label: {
                Image(systemName: "chevron.up")
            }
            .disabled(model.searchMatches.isEmpty)
            .accessibilityLabel("Previous match")
            Button { model.nextMatch() } label: {
                Image(systemName: "chevron.down")
            }
            .disabled(model.searchMatches.isEmpty)
            .accessibilityLabel("Next match")
            Button("Done") { closeSearch() }
                // Escape closes the find bar on an iPad keyboard (#172); nil leaves the
                // phone's button exactly as it was.
                .keyboardShortcut(isPad ? .cancelAction : nil)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(.bar)
        // Screenshot captures keep the keyboard down: the term is already in
        // the field and a keyboard would cover half the page.
        .onAppear { if model.screenshotSearchTerm == nil { searchFocused = true } }
    }

    private var matchCountLabel: String {
        if searchText.isEmpty || model.isSearching { return "" }
        guard let current = model.currentMatchIndex else {
            return String(localized: "No results")
        }
        // Catalog key "%lld of %lld"; the French value reorders positionally.
        return String(localized: "\(current + 1) of \(model.searchMatches.count)")
    }

    private func closeSearch() {
        searchOpen = false
        searchText = ""
        searchFocused = false
        model.clearSearch()
    }

    /// Translucent accent rects over every match on the page; the current
    /// match gets a stronger fill plus an outline.
    private func searchHighlights(index: Int, pageSize: CGSize,
                                  viewSize: CGSize) -> some View {
        Canvas { context, _ in
            let scaleX = Double(viewSize.width / pageSize.width)
            let scaleY = Double(viewSize.height / pageSize.height)
            for (i, match) in model.searchMatches.enumerated()
            where match.pageIndex == index {
                let isCurrent = i == model.currentMatchIndex
                for rect in match.rects {
                    let r = CGRect(
                        x: rect.left * scaleX,
                        y: (Double(pageSize.height) - rect.top) * scaleY,
                        width: (rect.right - rect.left) * scaleX,
                        height: (rect.top - rect.bottom) * scaleY)
                    // Hue, not opacity: cyan for every hit, brand blue for the one
                    // you are on. Two strengths of one colour are hard to tell apart
                    // on a dark scan; two colours are not. The tokens carry their own
                    // alpha, so nothing is layered on here.
                    context.fill(
                        Path(r),
                        with: .color(isCurrent ? Brand.findMatchCurrent : Brand.findMatch))
                    if isCurrent {
                        context.stroke(Path(r), with: .color(Brand.accent), lineWidth: 2)
                    }
                }
            }
        }
        .allowsHitTesting(false)
    }

    @ViewBuilder
    private func pageView(index: Int, containerWidth: CGFloat) -> some View {
        let size = pageSizes[index]
        let width = containerWidth * effectiveZoom
        let height = width * size.height / size.width
        ZStack(alignment: .topLeading) {
            // The page's name goes on the page itself, not on this stack: a label on the
            // stack is stamped over every element inside it, so VoiceOver read a redaction
            // mark and a selected stamp's Remove and Edit buttons all as "Page 1" (#277).
            if let image = model.pageImages[index] {
                Image(uiImage: UIImage(cgImage: image))
                    .resizable()
                    .interpolation(.high)
                    .accessibilityLabel("Page \(index + 1)")
            } else {
                Color.white  // placeholder keeps layout stable until the render lands
                    .accessibilityLabel("Page \(index + 1)")
            }
            if !model.searchMatches.isEmpty {
                searchHighlights(index: index, pageSize: size,
                                 viewSize: CGSize(width: width, height: height))
                // ScrollViewReader can only scroll to a view, so the current match
                // gets one: a zero-size anchor sitting exactly on it. Scrolling to the
                // page instead left the match off screen whenever zoom made the page
                // taller or wider than the viewport (#28).
                if let current = model.currentMatchIndex,
                   current < model.searchMatches.count,
                   model.searchMatches[current].pageIndex == index,
                   let rect = model.searchMatches[current].rects.first {
                    let scaleX = width / size.width
                    let scaleY = height / size.height
                    Color.clear
                        .frame(width: 1, height: 1)
                        .offset(x: CGFloat(rect.left) * scaleX,
                                y: CGFloat(Double(size.height) - rect.top) * scaleY)
                        .id(matchAnchorID)
                }
            }
            // Areas marked for redaction (#173), drawn OVER the page: a mark is never
            // written to the file, so there is nothing in the raster to draw, and marking
            // costs no re-render. Translucent with an outline, so what is about to be
            // removed can still be read — the reason marking and applying are two steps.
            if let marks = model.redactionMarks[index], !marks.isEmpty {
                let scaleX = width / size.width
                let scaleY = height / size.height
                ForEach(marks) { mark in
                    let markWidth = CGFloat(mark.rect.right - mark.rect.left) * scaleX
                    let markHeight = CGFloat(mark.rect.top - mark.rect.bottom) * scaleY
                    let markX = CGFloat(mark.rect.left) * scaleX
                    let markY = CGFloat(Double(size.height) - mark.rect.top) * scaleY
                    Rectangle()
                        .fill(Brand.redactionMark)
                        .overlay(Rectangle().stroke(Brand.redactionMarkOutline, lineWidth: 1))
                        .frame(width: markWidth, height: markHeight)
                        .offset(x: markX, y: markY)
                        // No tap gesture here: the page's own tap does the hit test, so a
                        // tap that lands on a mark cannot also fall through to a form field
                        // or a line of text underneath it (#329). The gesture stays on the
                        // page, and this view is the mark's accessibility node.
                        .accessibilityElement()
                        .accessibilityLabel("Marked for redaction")
                        .accessibilityAddTraits(.isButton)
                        .accessibilityHint("Double tap to select this mark")
                        .accessibilityAction {
                            model.selectRedactionMark(pageIndex: index, markId: mark.markId)
                        }
                        // Text(...) rather than a bare literal: `accessibilityAction(named:)`
                        // has Text, LocalizedStringKey and StringProtocol overloads, and a
                        // string literal matches all three. In a chain this long the compiler
                        // gave up on the whole expression — "unable to type-check this
                        // expression in reasonable time" — which is what the other call site
                        // in SignatureViews.swift avoids the same way.
                        .accessibilityAction(named: Text("Remove mark")) {
                            model.removeRedactionMark(pageIndex: index, markId: mark.markId)
                        }
                        .accessibilityIdentifier("redactionMark-\(mark.markId)")
                }
            }
            // The selected mark's chrome: drag to move, corner grip to resize, ✕ to remove.
            // Not aspect-locked, unlike a signature: a redaction area is a rectangle by
            // nature, and a wide strip of a line is the shape people want.
            if let selected = model.selectedRedactionMark, selected.pageIndex == index {
                SelectionOverlay(
                    rect: selected.rect,
                    pageSize: size,
                    viewSize: CGSize(width: width, height: height),
                    onCommit: { model.commitRedactionMarkRect(pageIndex: index,
                                                              markId: selected.markId, rect: $0) },
                    onRemove: model.removeSelectedRedactionMark,
                    aspectLocked: false,
                    removeLabel: "Remove mark"
                )
            }
            if model.redactMode || model.whiteoutMode, let band = redactBand, band.pageIndex == index {
                // The band shows what the drag is about to do, and the two tools do opposite
                // things (#3): a redaction mark is translucent, so the content you are about
                // to lose can still be read; a cover is opaque white, because that is
                // literally what will be on the page.
                Rectangle()
                    .fill(model.whiteoutMode ? Color.white : Brand.redactionMark)
                    .overlay(Rectangle().stroke(model.whiteoutMode ? Brand.accent
                                                                   : Brand.redactionMarkOutline,
                                                lineWidth: 1))
                    .frame(width: abs(band.current.x - band.origin.x),
                           height: abs(band.current.y - band.origin.y))
                    .offset(x: min(band.origin.x, band.current.x),
                            y: min(band.origin.y, band.current.y))
                    .allowsHitTesting(false)
            }
            // The selected cover's chrome: drag to move, corner grip to resize, X to remove
            // (#3). The same `SelectionOverlay` a signature, a text box and a redaction mark
            // already use -- there was no new interaction model to invent and no new hit
            // target to size, which is the answer to #565's question about this platform.
            // Free-form, like a mark and unlike a signature: a cover is an area.
            if let cover = model.selectedWhiteout, cover.pageIndex == index {
                SelectionOverlay(
                    rect: cover.rect,
                    pageSize: size,
                    viewSize: CGSize(width: width, height: height),
                    onCommit: model.commitWhiteoutRect,
                    onRemove: model.removeSelectedWhiteout,
                    aspectLocked: false,
                    removeLabel: "Remove whiteout"
                )
            }
            if let stamp = model.selectedStamp, stamp.pageIndex == index {
                SelectionOverlay(
                    rect: stamp.rect,
                    pageSize: size,
                    viewSize: CGSize(width: width, height: height),
                    onCommit: model.commitStampRect,
                    onRemove: model.removeSelectedStamp
                )
            }
            if let box = model.selectedTextBox, box.pageIndex == index {
                SelectionOverlay(
                    rect: box.rect,
                    pageSize: size,
                    viewSize: CGSize(width: width, height: height),
                    onCommit: model.commitTextBoxRect,
                    onRemove: model.removeSelectedTextBox,
                    resizable: false,
                    onEdit: model.editSelectedTextBox
                )
            }
            // Page-level work (#145): a small spinner on the line or box it is about, else mid-page.
            if let work = busy.pageIndicator, case let .page(page, rect) = work.scope, page == index {
                let scaleX = width / size.width
                let scaleY = height / size.height
                PageBusyIndicator(label: work.label.text)
                    .position(
                        x: rect.map { CGFloat(($0.left + $0.right) / 2) * scaleX } ?? width / 2,
                        y: rect.map { (size.height - CGFloat(($0.bottom + $0.top) / 2)) * scaleY } ?? height / 2)
            }
        }
        .frame(width: width, height: height)
        .clipped()
        // While Redact or Whiteout is armed a drag draws a rectangle instead of scrolling
        // (#173, #3). One gesture for both tools rather than a second one of its own -- the
        // armed tool decides what the rectangle becomes, and the band above shows which. The
        // gesture is attached only when a tool is on, so the scroll view keeps its scrolling
        // the rest of the time — and `minimumDistance` keeps a tap a tap.
        .simultaneousGesture(
            model.redactMode || model.whiteoutMode
                ? DragGesture(minimumDistance: 8)
                    .onChanged { value in
                        if redactBand?.pageIndex == index {
                            redactBand?.current = value.location
                        } else {
                            redactBand = RedactBand(pageIndex: index,
                                                    origin: value.startLocation,
                                                    current: value.location)
                        }
                    }
                    .onEnded { value in
                        redactBand = nil
                        let left = min(value.startLocation.x, value.location.x) / width
                        let right = max(value.startLocation.x, value.location.x) / width
                        let top = min(value.startLocation.y, value.location.y) / height
                        let bottom = max(value.startLocation.y, value.location.y) / height
                        guard right - left > 0.005, bottom - top > 0.005 else { return }
                        let area = PdfRect(left: Double(left) * size.width,
                                           bottom: Double(1 - bottom) * size.height,
                                           right: Double(right) * size.width,
                                           top: Double(1 - top) * size.height)
                        // Which tool is armed decides what the rectangle becomes. Whiteout is
                        // asked first because arming it disarms Redact, so both can never be
                        // on -- and if they somehow were, covering is the one that can be
                        // undone.
                        if model.whiteoutMode {
                            model.placeWhiteout(pageIndex: index, rect: area)
                        } else {
                            model.markForRedaction(pageIndex: index, rect: area)
                        }
                    }
                : nil
        )
        // Double-tap zoom is checked first; a lone tap (deferred briefly by
        // the exclusivity) dispatches to the model — as on Android.
        .gesture(
            // In the global space, which is the window's: a double tap is aimed
            // somewhere, and #530 is about zooms that ignore where they were aimed. The
            // single tap below stays local — it wants the point on the page, not on the
            // screen.
            SpatialTapGesture(count: 2, coordinateSpace: .global)
                .onEnded { value in
                    let target = ReadingZoom.clamped(zoom < 1.5 ? 2 : ReadingZoom.fitWidth,
                                                     floor: zoomFloor)
                    zoomAnchor.anchor(aroundWindowPoint: value.location,
                                      from: zoom, to: target) { zoom = target }
                    pushWindow(containerWidth: containerWidth)
                }
                .exclusively(before: SpatialTapGesture()
                    .onEnded { value in
                        // Inside reading mode a single tap on the page shows and hides the
                        // floating bar, and does nothing else (#168 decision 2, #506). It
                        // is scoped to reading mode deliberately: the ordinary tap on a
                        // field, a box or a mark is exactly as it was the rest of the time,
                        // and the double-tap zoom above is untouched in both. The model
                        // swallows the tap too (`onPageTapped`), so the page's dispatch
                        // cannot fire even if this branch were ever got wrong.
                        if model.readingMode {
                            if readingBarVisible {
                                withReadingAnimation { readingBarVisible = false }
                                // Cancels a fade that would otherwise hide a bar the next
                                // tap is about to show.
                                readingFadeGeneration &+= 1
                            } else {
                                showReadingBar()
                            }
                            return
                        }
                        model.onPageTapped(
                            index: index,
                            xFraction: Double(value.location.x / width),
                            yFraction: Double(value.location.y / height))
                    })
        )
    }

    private func pushWindow(containerWidth: CGFloat) {
        guard let first = visible.min(), let last = visible.max() else { return }
        let widthPx = Int(containerWidth * effectiveZoom * displayScale)
        model.updateRenderWindow(first: first, last: last, widthPx: widthPx)
    }
}

/// The calm, persistent explanation for a dynamic-XFA document (#456, #457): this form is
/// built to be filled in with Adobe Reader, and everything except filling it in still
/// works. Deliberately not an alert (nothing to confirm) and not `NoticeBanner` (which
/// clears itself after a few seconds and must not be the only place this is said): a fixed
/// strip under the navigation bar, in `topChrome`, for as long as the document stays open.
/// Arming Sign or Add text on this document explains again at the moment it is tapped
/// (`ViewerModel.showDynamicXfaNotice`), so this banner never needs a dismiss button that
/// would have to be reopened.
struct DynamicXfaBanner: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("This form is built to be filled in with Adobe Reader. You can still view, print, save and share it here — only filling it in isn't possible.")
                .font(.footnote)
                .fixedSize(horizontal: false, vertical: true)
            Link("Get Adobe Reader", destination: URL(string: "https://www.adobe.com/go/reader_download")!)
                .font(.footnote.weight(.semibold))
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(.bar)
        .accessibilityIdentifier("dynamicXfaBanner")
    }
}

/// Document-level work in progress (#145): a label over a bar, under the navigation bar —
/// determinate with a count line when the work can say how far it has got, and with a Stop
/// beside it when the work can be stopped. VoiceOver reads the label; the model announces it
/// when the strip appears.
///
/// **The count is its own line, not part of the label.** The label is the strip's polite live
/// region, and folding "Page 312 of 2,000" into it would have a screen reader announce it
/// three hundred times over one search. The count sits inside the element that ignores its
/// children, so it is drawn and not spoken; the label, which does not change, is what is
/// spoken. (#563 made the same call on the desktops, for the same reason.)
///
/// **The button says Stop, not Cancel.** Cancel is the word that abandons a question; this
/// abandons work. It goes insensitive the moment it is pressed, with "Stopping…" in its place,
/// because work does not stop the instant it is asked to.
struct BusyStrip: View {
    let work: BusyWork
    /// Whether there is still running work behind the strip to stop. False while the strip
    /// lives out its last 0.3 s, which is why it is asked of the state rather than of `work`.
    let canStop: Bool
    let isStopping: Bool
    let onStop: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text(work.label.text)
                    .font(.footnote)
                    .foregroundColor(.secondary)
                if let progress = work.progress {
                    ProgressView(value: progress.fraction)
                        .progressViewStyle(.linear)
                } else {
                    ProgressView()
                        .progressViewStyle(.linear)
                }
                if let count = work.progressText {
                    Text(count)
                        .font(.caption.monospacedDigit())
                        .foregroundColor(.secondary)
                        .accessibilityIdentifier("busyCount")
                }
            }
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(work.label.text)
            .accessibilityAddTraits(.updatesFrequently)
            .accessibilityIdentifier("busyStrip")
            if work.cancellable {
                // Typed as keys, two literals rather than a ternary, so the catalog sees both.
                Button(action: onStop) {
                    // The 44-point target is around the LABEL, with a content shape to match,
                    // not a `.frame` on the button: a frame outside it grows the layout and
                    // leaves the hit area the size of the words. Measured on a simulator at
                    // 15.7 points tall that way -- a third of what a finger needs, and
                    // something only a running app would have said.
                    Group {
                        if isStopping {
                            Text("Stopping…")
                        } else {
                            Text("Stop")
                        }
                    }
                    .frame(minWidth: 44, minHeight: 44)
                    .contentShape(Rectangle())
                }
                .font(.footnote.weight(.semibold))
                .disabled(!canStop)
                .accessibilityIdentifier("busyStop")
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.bar)
    }
}

/// Page-level work in progress (#145): a small spinner on the page, named for VoiceOver.
struct PageBusyIndicator: View {
    let label: String

    var body: some View {
        ProgressView()
            .padding(8)
            .background(.regularMaterial, in: Circle())
            .shadow(radius: 2, y: 1)
            .allowsHitTesting(false)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(label)
            .accessibilityIdentifier("pageBusy")
    }
}

/// A launch or reopen in progress with no document on screen yet (#145): "Opening…" once it
/// has taken half a second, nothing before.
struct OpeningView: View {
    @ObservedObject var busy: BusyState

    var body: some View {
        ZStack {
            if let work = busy.strip {
                ProgressView(work.label.text)
                    .accessibilityIdentifier("busyOpening")
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// The rubber band while a redaction drag is in progress, in the page view's own points.
struct RedactBand: Equatable {
    let pageIndex: Int
    var origin: CGPoint
    var current: CGPoint
}

/// Which save the redaction confirmation was raised from (#173).
enum RedactSaveChoice { case overwrite, copy, markdown }

/// Icon beside title, or the icon alone, for the iPad's tool strip (#172). Its own style
/// rather than a ternary between `.titleAndIcon` and `.iconOnly`, which are two types.
/// The button carries the title as its accessibility label either way, so dropping the
/// text drops nothing a screen reader hears.
struct ToolStripLabelStyle: LabelStyle {
    let titled: Bool

    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 4) {
            configuration.icon
            if titled { configuration.title }
        }
    }
}

/// The More button's on-screen frame, reported by the `GeometryReader` behind its label
/// (#378) — read by `ViewerView` so the iPad share popover has a real anchor.
private struct MoreMenuAnchorKey: PreferenceKey {
    static var defaultValue: CGRect = .zero
    static func reduce(value: inout CGRect, nextValue: () -> CGRect) { value = nextValue() }
}

/// The OS share sheet (#378): hands the document's file URL to Mail, Messages, AirDrop,
/// another PDF app, or whatever else is installed, through the platform's own chooser
/// rather than a destination list MegaPDF draws itself.
///
/// iPad presents `UIActivityViewController` as a popover and crashes without a
/// `sourceView`/`sourceRect` — `anchor` is the More button's real frame (`MoreMenuAnchorKey`,
/// above), not a guessed coordinate, so the popover points at where the row actually is.
/// `.zero` (asked before the first layout pass ever ran) falls back to the window's
/// top-trailing corner, roughly the nav bar's trailing end, rather than crashing.
struct ShareSheet: UIViewControllerRepresentable {
    let activityItems: [Any]
    let anchor: CGRect

    func makeUIViewController(context: Context) -> UIActivityViewController {
        let controller = UIActivityViewController(activityItems: activityItems, applicationActivities: nil)
        if let popover = controller.popoverPresentationController {
            let window = UIApplication.shared.connectedScenes
                .compactMap { ($0 as? UIWindowScene)?.keyWindow }
                .first
            popover.sourceView = window
            popover.sourceRect = anchor == .zero
                ? CGRect(x: (window?.bounds.width ?? 0) - 1, y: 0, width: 1, height: 1)
                : anchor
            popover.permittedArrowDirections = [.up]
        }
        return controller
    }

    func updateUIViewController(_ uiViewController: UIActivityViewController, context: Context) {}
}
