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
    @State private var searchOpen = false
    @State private var searchText = ""
    @FocusState private var searchFocused: Bool
    @State private var aboutOpen = false
    /// The rubber band a redaction drag is drawing; nil the rest of the time (#173).
    @State private var redactBand: RedactBand?
    /// The redaction confirmation is up: marks are on the document and a save was asked for.
    @State private var redactConfirm: RedactSaveChoice?
    /// The More button's on-screen frame (#378): iPad's `UIActivityViewController` needs a
    /// popover source or it crashes, and it has to point at wherever the button actually is
    /// rather than a guessed coordinate — `MoreMenuAnchorKey` below reports it here.
    @State private var moreMenuAnchor: CGRect = .zero
    @Environment(\.displayScale) private var displayScale

    private var effectiveZoom: CGFloat { min(max(zoom * gestureZoom, 1), 4) }

    /// Identity of the zero-size view pinned to the current search match.
    private let matchAnchorID = "megapdf.current-match"

    /// Typed as a key so both branches are looked up in the catalog.
    private var saveLabel: LocalizedStringKey { model.isSaving ? "Saving…" : "Save" }

    var body: some View {
        GeometryReader { geo in
            ScrollViewReader { proxy in
                // The axes follow the COMMITTED zoom, not the one the fingers are
                // mid-way through (#336). Derived from `effectiveZoom`, the axis set
                // changed on every tick of a pinch — and reconfiguring a scroll view's
                // axes rebuilds it, which ends the very gesture asking for the change.
                // The content is still laid out at `effectiveZoom`, so the page grows
                // under the fingers and picks up its horizontal scrolling the moment the
                // gesture ends.
                ScrollView(zoom > 1 ? [.vertical, .horizontal] : .vertical) {
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
                // Simultaneous, not exclusive (#336): a plain .gesture on a ScrollView
                // competes with the scroll view's own pan gesture, and the pinch was
                // losing that race — the magnify never started. Running alongside it lets
                // the pinch scale while the scroll view keeps its scrolling.
                //
                // MagnificationGesture rather than iOS 17's MagnifyGesture: the
                // deployment target is 16.0, and the old type is only deprecated, not
                // removed. Switching means raising the floor, which is not this fix.
                .simultaneousGesture(
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
        .toolbar {
            // #145: while a save, a password change or an open runs, Close and the file commands
            // are disabled; the model ignores them too while a change is being applied.
            ToolbarItem(placement: .navigationBarLeading) {
                Button("Close") {
                    guard !model.closeBlocked else { return }
                    if model.isDirty { model.unsavedChangesFollowUp = .close } else { onClose() }
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
                    Divider()
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
