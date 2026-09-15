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
    @Environment(\.displayScale) private var displayScale
    @Environment(\.horizontalSizeClass) private var horizontalSizeClass

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
                Button(saveLabel) { model.save() }
                    .disabled(!model.isDirty || model.isSaving || model.fileCommandsBlocked)
                Menu {
                    Button("Save a copy", action: onSaveCopy)
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

    /// A bottom-toolbar label (#144): icons alone on an iPhone; a regular-width window
    /// (iPad) has the room to name them. Styled here rather than on the whole view so
    /// the sheets the viewer presents keep their own label layout.
    private func toolLabel(_ title: LocalizedStringKey, systemImage: String) -> some View {
        Label(title, systemImage: systemImage)
            .labelStyle(ToolbarLabelStyle(showsTitle: horizontalSizeClass == .regular))
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
            if let image = model.pageImages[index] {
                Image(uiImage: UIImage(cgImage: image))
                    .resizable()
                    .interpolation(.high)
            } else {
                Color.white  // placeholder keeps layout stable until the render lands
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
        .accessibilityLabel("Page \(index + 1)")
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

/// The viewer toolbar's labels (#144): the icon alone where space is tight, icon and
/// title side by side where it is not. Either way the title stays the button's
/// accessibility label, so VoiceOver reads "Sign", never "signature".
struct ToolbarLabelStyle: LabelStyle {
    let showsTitle: Bool

    func makeBody(configuration: Configuration) -> some View {
        if showsTitle {
            Label(configuration).labelStyle(.titleAndIcon)
        } else {
            Label(configuration).labelStyle(.iconOnly)
        }
    }
}
