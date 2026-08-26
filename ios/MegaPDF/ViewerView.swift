import SwiftUI

/// Scrolling page list with pinch/double-tap zoom, tap-to-edit dispatch,
/// signature placement chrome, search bar, and save/sign toolbar.
struct ViewerView: View {
    @ObservedObject var model: ViewerModel
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
    @Environment(\.displayScale) private var displayScale

    private var effectiveZoom: CGFloat { min(max(zoom * gestureZoom, 1), 4) }

    /// Identity of the zero-size view pinned to the current search match.
    private let matchAnchorID = "megapdf.current-match"

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
                    .frame(minWidth: geo.size.width)
                }
                .background(Color(white: 0.25))
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
                    if searchOpen { searchBar }
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
        .toolbar {
            ToolbarItemGroup(placement: .navigationBarLeading) {
                Button("Close") {
                    if model.isDirty { confirmDiscard = true } else { onClose() }
                }
                Button {
                    model.undo()
                } label: {
                    Image(systemName: "arrow.uturn.backward")
                }
                .accessibilityLabel("Undo")
                .disabled(!model.canUndo)
            }
            ToolbarItemGroup(placement: .navigationBarTrailing) {
                Button {
                    if searchOpen { closeSearch() } else { searchOpen = true }
                } label: {
                    Image(systemName: "magnifyingglass")
                }
                .accessibilityLabel("Find in document")
                Button("Sign") { signaturesOpen = true }
                Button("Text") { model.startTextPlacement() }
                    .accessibilityLabel("Add text")
                Button(model.isSaving ? "Saving…" : "Save") { model.save() }
                    .disabled(!model.isDirty || model.isSaving)
                Menu {
                    Button("Save a copy", action: onSaveCopy)
                        .disabled(model.isSaving)
                    Button("Redo", action: model.redo)
                        .disabled(!model.canRedo)
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
            }
        }
        .sheet(isPresented: $signaturesOpen) {
            SignaturesSheet(
                signatures: model.signatures,
                startDrawing: model.screenshotSheet == .draw,
                onPick: { entry in
                    signaturesOpen = false
                    model.startPlacement(entry)
                },
                onDrawn: model.addDrawnSignature,
                onPhoto: model.importSignature,
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
            Button("Save") { model.save() }
            Button("Discard", role: .destructive, action: onClose)
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This document has unsaved changes.")
        }
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
        guard let current = model.currentMatchIndex else { return "No results" }
        return "\(current + 1) of \(model.searchMatches.count)"
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
                    context.fill(
                        Path(r),
                        with: .color(Color.accentColor.opacity(isCurrent ? 0.45 : 0.25)))
                    if isCurrent {
                        context.stroke(Path(r), with: .color(Color.accentColor),
                                       lineWidth: 2)
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
