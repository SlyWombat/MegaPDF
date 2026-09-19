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
    let onClose: () -> Void

    @State private var zoom: CGFloat = 1
    @State private var gestureZoom: CGFloat = 1
    @State private var visible: Set<Int> = []
    @State private var signaturesOpen = false
    @State private var confirmDiscard = false
    @State private var searchOpen = false
    @State private var searchText = ""
    @FocusState private var searchFocused: Bool
    @State private var aboutOpen = false
    /// The rubber band a redaction drag is drawing; nil the rest of the time (#173).
    @State private var redactBand: RedactBand?
    /// The redaction confirmation is up: marks are on the document and a save was asked for.
    @State private var redactConfirm: RedactSaveChoice?
    @Environment(\.displayScale) private var displayScale

    private var effectiveZoom: CGFloat { min(max(zoom * gestureZoom, 1), 4) }

    /// Identity of the zero-size view pinned to the current search match.
    private let matchAnchorID = "megapdf.current-match"

    /// Typed as a key so both branches are looked up in the catalog.
    private var saveLabel: LocalizedStringKey { model.isSaving ? "Saving…" : "Save" }

    var body: some View {
        GeometryReader { geo in
            ScrollViewReader { proxy in
                ScrollView(effectiveZoom > 1 ? [.vertical, .horizontal] : .vertical) {
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
                .background(Brand.backdrop)
                .gesture(
                    MagnificationGesture()
                        .onChanged { gestureZoom = $0 }
                        .onEnded { value in
                            zoom = min(max(zoom * value, 1), 4)
                            gestureZoom = 1
                            pushWindow(containerWidth: geo.size.width)
                        }
                )
                .safeAreaInset(edge: .top, spacing: 0) {
                    VStack(spacing: 0) {
                        if searchOpen { searchBar }
                        // Document-level work (#145): a strip under the top chrome.
                        if let work = busy.strip {
                            BusyStrip(label: work.label.text)
                                .transition(.opacity)
                        }
                    }
                }
                .onChange(of: searchText) { term in
                    model.search(term: term)
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
        .navigationTitle((model.isDirty ? "• " : "") + displayName)
        .navigationBarTitleDisplayMode(.inline)
        // #144: the navigation bar holds what is done to the file as a whole — Close,
        // Save, and a More menu — and the bottom toolbar holds the everyday tools, as
        // the HIG lays out an iPhone document viewer. Everything else stays in More,
        // so the page keeps the screen.
        .toolbar {
            // #145: while a save, a password change or an open runs, Close and the file commands
            // are disabled; the model ignores them too while a change is being applied.
            ToolbarItem(placement: .navigationBarLeading) {
                Button("Close") {
                    guard !model.closeBlocked else { return }
                    if model.isDirty { confirmDiscard = true } else { onClose() }
                }
                .disabled(model.fileCommandsBlocked)
            }
            ToolbarItemGroup(placement: .navigationBarTrailing) {
                Button(saveLabel) {
                    // Marks on the document mean the question comes first (#173): nothing
                    // is written until it has been answered.
                    if model.redactionMarkCount > 0 { redactConfirm = .overwrite } else { model.save() }
                }
                    // Marks count as something to save, even though they are not a change
                    // to the document — nothing is written until the question above is
                    // answered, so marking deliberately leaves it clean. Asking isDirty
                    // alone left Save greyed out with areas marked, which made the branch
                    // inside this very button unreachable and left the ⋯ menu as the only
                    // way to finish a redaction (#173).
                    .disabled((!model.isDirty && model.redactionMarkCount == 0)
                              || model.isSaving || model.fileCommandsBlocked)
                Menu {
                    Button("Save a copy") {
                        if model.redactionMarkCount > 0 { redactConfirm = .copy } else { onSaveCopy() }
                    }
                        .disabled(model.isSaving || model.fileCommandsBlocked)
                    Button("Password…", action: model.showPasswordCommand)
                        .disabled(!model.canUsePasswordCommand || model.fileCommandsBlocked)
                    if model.capabilities.isRestricted {
                        Button("Unlock with owner password…", action: model.showUnlock)
                            .disabled(model.isUnlocking || model.fileCommandsBlocked)
                    }
                    Divider()
                    Button("About MegaPDF") { aboutOpen = true }
                } label: {
                    Label("More", systemImage: "ellipsis.circle")
                }
                .accessibilityIdentifier("viewerMore")
            }
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
                // Redact with the creating tools (#173). The phone has no Whiteout —
                // mobile's set is fill, check, sign, find, add text — so this arrives on
                // its own, and its label says what it does: it removes.
                Button { model.toggleRedactMode() } label: {
                    toolLabel("Redact", systemImage: model.redactMode
                        ? "rectangle.fill.badge.xmark" : "rectangle.badge.xmark")
                }
                .disabled(!model.capabilities.canEditContent || model.fileCommandsBlocked)
                .accessibilityLabel("Redact")
                .accessibilityHint("Remove content from the file")
                // The armed state, as a value rather than a trait (#173). .isSelected is
                // the right thing to say and it does not arrive: measured on a device
                // element, the trait is dropped by the bottom-bar bridge whether it is
                // added here or inside the label, while the label, hint, identifier and
                // value all come through. The trait stays because it is correct and
                // costs nothing if the bridge ever carries it; the value is what a
                // screen reader actually reads today — "Redact, On".
                .accessibilityValue(model.redactMode ? "On" : "Off")
                .accessibilityAddTraits(model.redactMode ? .isSelected : [])
                .accessibilityIdentifier("viewerRedact")
                Button {
                    if searchOpen { closeSearch() } else { searchOpen = true }
                } label: {
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
        .sheet(isPresented: $aboutOpen) {
            AboutView()
        }
        // The confirmation #173 asks for, before either save path writes anything: what
        // redaction does, that it cannot be undone once saved, and Save a copy as the
        // DEFAULT action — the reversible choice, because the other cannot be taken back.
        .confirmationDialog(
            "Remove the marked content?",
            isPresented: Binding(get: { redactConfirm != nil },
                                 set: { if !$0 { redactConfirm = nil } }),
            titleVisibility: .visible
        ) {
            Button("Save as a copy") {
                redactConfirm = nil
                Task { if await model.applyRedactions(reportWithSave: true) { onSaveCopy() } }
            }
            Button("Overwrite the original") {
                redactConfirm = nil
                Task { if await model.applyRedactions(reportWithSave: true) { model.save() } }
            }
            Button("Cancel", role: .cancel) { redactConfirm = nil }
        } message: {
            Text("Redaction permanently removes the marked content. This can't be undone after saving.")
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
        .sheet(isPresented: $signaturesOpen) {
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
        .onAppear {
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
        .sheet(item: $model.pendingText) { pending in
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
        .alert("Unsaved changes", isPresented: $confirmDiscard) {
            // Saves, then closes once the document is saved (#145).
            Button("Save") { model.save(thenClose: true) }
            Button("Discard", role: .destructive, action: onClose)
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This document has unsaved changes.")
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
                    Rectangle()
                        .fill(Brand.redactionMark)
                        .overlay(Rectangle().stroke(Brand.redactionMarkOutline, lineWidth: 1))
                        .frame(width: CGFloat(mark.rect.right - mark.rect.left) * scaleX,
                               height: CGFloat(mark.rect.top - mark.rect.bottom) * scaleY)
                        .offset(x: CGFloat(mark.rect.left) * scaleX,
                                y: CGFloat(Double(size.height) - mark.rect.top) * scaleY)
                        .onTapGesture { model.removeRedactionMark(pageIndex: index, markId: mark.markId) }
                        .accessibilityLabel("Marked for redaction")
                        .accessibilityHint("Double tap to remove this mark")
                }
            }
            if model.redactMode, let band = redactBand, band.pageIndex == index {
                Rectangle()
                    .fill(Brand.redactionMark)
                    .overlay(Rectangle().stroke(Brand.redactionMarkOutline, lineWidth: 1))
                    .frame(width: abs(band.current.x - band.origin.x),
                           height: abs(band.current.y - band.origin.y))
                    .offset(x: min(band.origin.x, band.current.x),
                            y: min(band.origin.y, band.current.y))
                    .allowsHitTesting(false)
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
        // While Redact is armed a drag marks an area instead of scrolling (#173). The
        // gesture is attached only when the tool is on, so the scroll view keeps its
        // scrolling the rest of the time — and `minimumDistance` keeps a tap a tap.
        .simultaneousGesture(
            model.redactMode
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
                        model.markForRedaction(
                            pageIndex: index,
                            rect: PdfRect(left: Double(left) * size.width,
                                          bottom: Double(1 - bottom) * size.height,
                                          right: Double(right) * size.width,
                                          top: Double(1 - top) * size.height))
                    }
                : nil
        )
        // Double-tap zoom is checked first; a lone tap (deferred briefly by
        // the exclusivity) dispatches to the model — as on Android.
        .gesture(
            SpatialTapGesture(count: 2)
                .onEnded { _ in
                    zoom = zoom < 1.5 ? 2 : 1
                    pushWindow(containerWidth: containerWidth)
                }
                .exclusively(before: SpatialTapGesture()
                    .onEnded { value in
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

/// Document-level work in progress (#145): a label over an indeterminate bar, under the
/// navigation bar. VoiceOver reads the label; the model announces it when the strip appears.
struct BusyStrip: View {
    let label: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label)
                .font(.footnote)
                .foregroundColor(.secondary)
            ProgressView()
                .progressViewStyle(.linear)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.bar)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(label)
        .accessibilityAddTraits(.updatesFrequently)
        .accessibilityIdentifier("busyStrip")
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
enum RedactSaveChoice { case overwrite, copy }
