import CoreGraphics
import Foundation
import UIKit

enum ViewerState {
    case home(recents: [RecentEntry], error: String?)
    case loading
    case passwordNeeded(bytes: Data, displayName: String, sourceURL: URL?, wrongPassword: Bool)
    case viewing(displayName: String, pageSizes: [CGSize])
}

/// Owns the engine document and the ±2-page render window — the iOS port of
/// Android's `ViewerViewModel` (same virtualization, same eviction policy).
/// A signature stamp currently selected for move/resize/remove.
struct SelectedStamp: Equatable {
    let pageIndex: Int
    let annotIndex: Int
    let id: String
    let rect: PdfRect
}

/// A text box currently selected for drag/correct/remove (#36).
struct SelectedTextBox: Equatable {
    let pageIndex: Int
    let id: String
    let text: String
    let fontSize: Double
    let fontName: String
    let rect: PdfRect
}

/// Sizes offered for added text (#43). A short list, not a free-entry number box:
/// the job is "match the form I am filling in", and six presets cover it.
let textSizes: [Double] = [8, 10, 12, 14, 18, 24]

/// What a new box starts at, before the user has chosen anything this session.
let defaultTextSize: Double = 12

/// A tap that is waiting for the text the user is about to type (#34). When
/// `editingId` is set the tap re-opened an existing box to correct it (#36), and
/// (`x`, `y`) is that box's bounds lower-left rather than the raw tap point.
struct PendingText: Identifiable {
    let id = UUID()
    let pageIndex: Int
    let x: Double
    let y: Double
    var editingId: String?
    var fontSize: Double = defaultTextSize
    var fontName: String = PdfEngine.defaultFont
    var initialText: String = ""
}

/// One search hit in the document-wide flat match list (#26).
struct SearchMatch: Equatable {
    let pageIndex: Int
    let rects: [PdfRect]
}

@MainActor
final class ViewerModel: ObservableObject {
    @Published private(set) var state: ViewerState
    @Published private(set) var pageImages: [Int: CGImage] = [:]
    @Published private(set) var isDirty = false
    @Published private(set) var isSaving = false
    @Published var statusMessage: String?
    @Published private(set) var signatures: [SignatureEntry] = []
    @Published private(set) var pendingSignature: SignatureEntry?
    @Published private(set) var selectedStamp: SelectedStamp?
    /// The text box currently selected for drag/correct/remove (#36).
    @Published private(set) var selectedTextBox: SelectedTextBox?
    @Published private(set) var searchMatches: [SearchMatch] = []
    @Published private(set) var currentMatchIndex: Int?
    @Published private(set) var isSearching = false
    /// Undo/redo availability (#34) — mirrored out of the history for the toolbar.
    @Published private(set) var canUndo = false
    @Published private(set) var canRedo = false
    /// True between "Add text" and the tap that says where it goes.
    @Published private(set) var isPlacingText = false
    /// Set by that tap; ViewerView presents the text field for it.
    @Published var pendingText: PendingText?
    /// What the text sheet is bound to. Seeded here rather than in the view, so a
    /// correction's prefill (#36/#43) lands in the same update as `pendingText`
    /// instead of racing the sheet's presentation.
    @Published var draftText = ""
    @Published var draftSize = defaultTextSize
    @Published var draftFont = PdfEngine.defaultFont

    /// The size and face the last box was given (#43). Sticky for the session, so
    /// filling six fields on one form is not six trips through the pickers. Not
    /// persisted: a new document is usually a new job.
    private var lastFontSize = defaultTextSize
    private var lastFontName = PdfEngine.defaultFont

    /// Set only by `-screenshot sign|draw` launches; ViewerView opens the sheet.
    enum ScreenshotSheet { case signatures, draw }
    @Published private(set) var screenshotSheet: ScreenshotSheet?

    /// Set only by `-screenshot search` launches; ViewerView opens the find
    /// bar with this term already entered and runs the search immediately.
    @Published private(set) var screenshotSearchTerm: String?

    private let recents = RecentsStore()
    private let signatureStore = SignatureStore()
    private let history = EditHistory()
    private var document: PdfDocument?
    private var sourceURL: URL?
    private var renderedWidths: [Int: Int] = [:]
    private var renderTask: Task<Void, Never>?
    private var searchTask: Task<Void, Never>?
    private var lastWindow: (first: Int, last: Int, widthPx: Int)?

    /// Margin added to a text box's tight glyph rect when hit-testing a tap (#36).
    private static let tapSlopPoints: Double = 6

    private static let renderMargin = 2
    private static let maxPixelDim = 2048

    init() {
        state = .home(recents: recents.load(), error: nil)
        signatures = signatureStore.load()
        applyScreenshotModeIfNeeded()
    }

    private func applyScreenshotModeIfNeeded() {
        guard let mode = DemoContent.requestedState else { return }
        if signatures.isEmpty, let image = DemoContent.signatureImage(),
           let entry = signatureStore.add(displayName: "Mega W.", image: image) {
            signatures.append(entry)
        }
        switch mode {
        case "home":
            state = .home(recents: DemoContent.demoRecents(), error: nil)
        case "viewer", "sign", "draw", "search", "text":
            if let url = Bundle.main.url(forResource: "demo", withExtension: "pdf"),
               let bytes = try? Data(contentsOf: url) {
                if mode == "sign" { screenshotSheet = .signatures }
                if mode == "draw" { screenshotSheet = .draw }
                if mode == "search" { screenshotSearchTerm = DemoContent.searchTerm }
                Task {
                    await open(bytes: bytes, password: nil,
                               displayName: "Rental Agreement.pdf", sourceURL: nil)
                    if mode == "text" {
                        // The Add text sheet, open on a typed name with the size
                        // and face pickers showing (#43). Armed after the open so
                        // it cannot be cleared by the state change, and with a
                        // chosen tap point rather than a synthesised one: just
                        // under the signature rule, where a printed name belongs.
                        draftText = DemoContent.printedName
                        pendingText = PendingText(pageIndex: 0,
                                                  x: DemoContent.printedNameX,
                                                  y: DemoContent.printedNameY,
                                                  initialText: DemoContent.printedName)
                    }
                }
            }
        default:
            break
        }
    }

    // MARK: - opening

    func openPicked(url: URL) {
        state = .loading
        Task {
            let scoped = url.startAccessingSecurityScopedResource()
            defer { if scoped { url.stopAccessingSecurityScopedResource() } }
            guard let bytes = try? Data(contentsOf: url) else {
                toHome("Couldn't read that file.")
                return
            }
            await open(bytes: bytes, password: nil,
                       displayName: url.lastPathComponent, sourceURL: url)
        }
    }

    func openRecent(_ entry: RecentEntry) {
        state = .loading
        Task {
            guard let bookmark = entry.bookmarkData else {
                recents.remove(id: entry.id)
                toHome("That entry was unreadable and has been removed.")
                return
            }
            var stale = false
            guard let url = try? URL(
                resolvingBookmarkData: bookmark, options: [],
                relativeTo: nil, bookmarkDataIsStale: &stale)
            else {
                recents.remove(id: entry.id)
                toHome("That file is no longer accessible. Pick it again to reopen it.")
                return
            }
            openPicked(url: url)
        }
    }

    func submitPassword(_ password: String) {
        guard case let .passwordNeeded(bytes, displayName, url, _) = state else { return }
        state = .loading
        Task { await open(bytes: bytes, password: password,
                          displayName: displayName, sourceURL: url) }
    }

    private func open(bytes: Data, password: String?,
                      displayName: String, sourceURL: URL?) async {
        do {
            let doc = try await PdfEngine.shared.open(bytes, password: password)
            let count = await PdfEngine.shared.pageCount(doc)
            var sizes: [CGSize] = []
            for i in 0..<count {
                sizes.append(try await PdfEngine.shared.pageSize(doc, index: i))
            }
            closeCurrent()
            document = doc
            self.sourceURL = sourceURL
            if let sourceURL,
               let bookmark = try? sourceURL.bookmarkData() {
                recents.add(RecentEntry(
                    bookmarkBase64: bookmark.base64EncodedString(),
                    displayName: displayName,
                    lastOpenedEpochMs: Int64(Date().timeIntervalSince1970 * 1000)))
            }
            state = .viewing(displayName: displayName, pageSizes: sizes)
        } catch PdfError.passwordRequired {
            state = .passwordNeeded(bytes: bytes, displayName: displayName,
                                    sourceURL: sourceURL, wrongPassword: password != nil)
        } catch {
            toHome("Couldn't open this file: \(error)")
        }
    }

    // MARK: - rendering

    /// Visible range changed: render visible ± margin at `widthPx` (capped),
    /// keep already-sharp pages, evict the rest.
    func updateRenderWindow(first: Int, last: Int, widthPx: Int) {
        guard case let .viewing(_, pageSizes) = state, let doc = document else { return }
        lastWindow = (first, last, widthPx)
        let window = max(0, first - Self.renderMargin)...min(pageSizes.count - 1, last + Self.renderMargin)

        for index in pageImages.keys where !window.contains(index) {
            pageImages.removeValue(forKey: index)
            renderedWidths.removeValue(forKey: index)
        }

        renderTask?.cancel()
        renderTask = Task {
            for index in window {
                if Task.isCancelled { return }
                let size = pageSizes[index]
                let width = min(max(widthPx, 1), Self.maxPixelDim)
                let height = min(Int(Double(width) * size.height / size.width), Self.maxPixelDim)
                if renderedWidths[index] == width { continue }
                if let image = try? await PdfEngine.shared.render(
                    doc, index: index, pixelWidth: width, pixelHeight: height) {
                    pageImages[index] = image
                    renderedWidths[index] = width
                }
            }
        }
    }

    // MARK: - editing

    /// Tap dispatch — same ordering as desktop and Android: form fields win
    /// over page content, then existing marks (tap to remove), then drawn
    /// squares (tap to place). Fractions are tap position / page view size,
    /// top-left origin.
    func onPageTapped(index: Int, xFraction: Double, yFraction: Double) {
        guard case let .viewing(_, pageSizes) = state, let doc = document else { return }
        let size = pageSizes[index]
        let x = xFraction * size.width
        let y = (1 - yFraction) * size.height  // view top-left → PDF bottom-left

        if let entry = pendingSignature {
            pendingSignature = nil
            placeSignature(entry, doc: doc, pageIndex: index, pageSize: size, x: x, y: y)
            return
        }

        if isPlacingText {
            isPlacingText = false
            statusMessage = nil
            draftText = ""
            draftSize = lastFontSize
            draftFont = lastFontName
            pendingText = PendingText(pageIndex: index, x: x, y: y,
                                      fontSize: lastFontSize, fontName: lastFontName)
            return
        }

        Task {
            do {
                let engine = PdfEngine.shared

                let allStamps = try await engine.stamps(doc, pageIndex: index)
                if let sig = allStamps.first(where: {
                    $0.id.hasPrefix("sig:") && $0.rect.contains(x: x, y: y)
                }) {
                    selectedStamp = SelectedStamp(pageIndex: index, annotIndex: sig.annotIndex,
                                                  id: sig.id, rect: sig.rect)
                    selectedTextBox = nil
                    return
                }
                selectedStamp = nil

                // Text boxes (#36) rank with signatures: both are things the user
                // put on the page, so they win over the document underneath. Last
                // match wins — later page objects paint on top. The rect is tight
                // around the glyphs, and a 12 pt line is a few points tall, so the
                // hit test gets `tapSlopPoints` of margin.
                let boxes = try await engine.textBoxes(doc, pageIndex: index)
                if let box = boxes.last(where: {
                    $0.rect.grown(by: Self.tapSlopPoints).contains(x: x, y: y)
                }) {
                    if box.id.hasPrefix(PdfEngine.untaggedPrefix) {
                        // A box written by MegaPDF for Windows 1.6.x, before boxes
                        // carried an id. Its only handle is its page-object index,
                        // which the history would replay against a page whose
                        // indices had since shifted — so it would eventually move
                        // or delete the wrong box. Swallow the tap rather than let
                        // it fall through and toggle whatever is underneath.
                        selectedTextBox = nil
                        statusMessage = "This text was added by an older version and can't be edited here."
                        return
                    }
                    let wasSelected = selectedTextBox?.id == box.id
                    selectedTextBox = SelectedTextBox(pageIndex: index, id: box.id,
                                                      text: box.text,
                                                      fontSize: box.fontSize,
                                                      fontName: box.fontName,
                                                      rect: box.rect)
                    if wasSelected {
                        // A second tap on the selected box also opens the editor.
                        // The overlay's pencil is the discoverable way in, because
                        // a *quick* second tap is claimed by double-tap-to-zoom —
                        // this path only fires after that disambiguation lapses.
                        editSelectedTextBox()
                    }
                    return
                }
                selectedTextBox = nil

                // Whichever edit the tap lands on, it goes through the history so
                // it can be taken back (#34).
                var operation: PdfEditOperation?
                let fields = try await engine.formFields(doc, pageIndex: index)
                if let field = fields.first(where: { $0.rect.contains(x: x, y: y) }) {
                    operation = FieldToggleOperation(pageIndex: index,
                                                     x: field.rect.centerX,
                                                     y: field.rect.centerY)
                } else if let mark = allStamps.first(where: {
                    $0.id.hasPrefix("mark:") && $0.rect.contains(x: x, y: y)
                }) {
                    operation = MarkOperation(
                        pageIndex: index,
                        square: MarkOperation.square(fromMark: mark.rect),
                        id: mark.id, adding: false)
                } else if let square = try await engine
                    .detectCheckboxSquares(doc, pageIndex: index)
                    .first(where: { $0.contains(x: x, y: y) }) {
                    operation = MarkOperation(pageIndex: index, square: square,
                                              id: "mark:\(UUID().uuidString)", adding: true)
                }
                if let operation {
                    try await perform(operation, doc: doc)
                }
            } catch {
                // Edits are tap-driven; a failure just leaves the page unchanged.
            }
        }
    }

    // MARK: - added text (#34)

    /// Arms the next tap to place text. Tapping the page opens the text field.
    func startTextPlacement() {
        cancelPlacement()
        selectedStamp = nil
        selectedTextBox = nil
        isPlacingText = true
        statusMessage = "Tap the page where the text should go"
    }

    func cancelTextPlacement() {
        isPlacingText = false
        pendingText = nil
        draftText = ""
        statusMessage = nil
    }

    /// Commits what the text sheet was left holding — a new box, or a change to
    /// one already on the page. Text, size and face all arrive together, so
    /// restyling and correcting a typo are the same single undoable edit.
    func commitText(_ text: String, fontSize: Double, fontName: String) {
        guard let pending = pendingText, let doc = document else { return }
        pendingText = nil
        draftText = ""
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        lastFontSize = fontSize
        lastFontName = fontName
        let style = TextBoxStyle(text: trimmed, fontSize: fontSize, fontName: fontName)
        Task {
            do {
                if let editingId = pending.editingId {
                    let before = TextBoxStyle(text: pending.initialText,
                                              fontSize: pending.fontSize,
                                              fontName: pending.fontName)
                    guard before != style else { return }
                    try await perform(
                        EditTextBoxOperation(pageIndex: pending.pageIndex, id: editingId,
                                             from: before, to: style,
                                             x: pending.x, y: pending.y),
                        doc: doc)
                    await reselectTextBox(doc, pageIndex: pending.pageIndex, id: editingId)
                } else {
                    try await perform(
                        TextBoxOperation(pageIndex: pending.pageIndex,
                                         id: "text:\(UUID().uuidString)",
                                         text: trimmed, fontSize: fontSize,
                                         x: pending.x, y: pending.y, adding: true,
                                         fontName: fontName),
                        doc: doc)
                }
            } catch {
                statusMessage = pending.editingId != nil
                    ? "Couldn't change that text."
                    : "Couldn't add that text."
            }
        }
    }

    // MARK: - selected text box: drag, correct, remove (#36)

    /// Commits a drag from the selection overlay. Only the position changes — a
    /// text box has no resize handle, because resizing one would mean changing
    /// its font size, and SDD §3.1 keeps formatting controls out of the app.
    func commitTextBoxRect(_ newRect: PdfRect) {
        guard let sel = selectedTextBox, let doc = document,
              case let .viewing(_, pageSizes) = state else { return }
        let rect = clampToPage(newRect, pageSize: pageSizes[sel.pageIndex])
        // A tap that slipped into a drag can land a sub-point move; don't put a
        // no-op on the undo stack for it.
        guard abs(rect.left - sel.rect.left) >= 0.01
                || abs(rect.bottom - sel.rect.bottom) >= 0.01 else { return }
        Task {
            do {
                try await perform(
                    MoveTextBoxOperation(pageIndex: sel.pageIndex, id: sel.id,
                                         from: (x: sel.rect.left, y: sel.rect.bottom),
                                         to: (x: rect.left, y: rect.bottom)),
                    doc: doc)
                await reselectTextBox(doc, pageIndex: sel.pageIndex, id: sel.id)
            } catch {
                statusMessage = "Couldn't move that text."
            }
        }
    }

    /// Opens the text field on the selected box so a typo can be corrected. The
    /// anchor handed to the edit is the box's bounds lower-left, not a tap point.
    func editSelectedTextBox() {
        guard let sel = selectedTextBox else { return }
        selectedTextBox = nil
        draftText = sel.text
        draftSize = sel.fontSize
        draftFont = sel.fontName
        pendingText = PendingText(pageIndex: sel.pageIndex,
                                  x: sel.rect.left, y: sel.rect.bottom,
                                  editingId: sel.id, fontSize: sel.fontSize,
                                  fontName: sel.fontName, initialText: sel.text)
    }

    func removeSelectedTextBox() {
        guard let sel = selectedTextBox, let doc = document else { return }
        Task {
            do {
                // boundsAnchored: the coordinates are the box's reported rect, so
                // an undo must re-add against bounds, not the baseline.
                try await perform(
                    TextBoxOperation(pageIndex: sel.pageIndex, id: sel.id, text: sel.text,
                                     fontSize: sel.fontSize,
                                     x: sel.rect.left, y: sel.rect.bottom,
                                     adding: false, boundsAnchored: true,
                                     fontName: sel.fontName),
                    doc: doc)
            } catch {
                statusMessage = "Couldn't remove that text."
            }
        }
    }

    /// Re-reads the box after an edit and keeps it selected, so the handles stay
    /// on it. The rect must be read back rather than reused: correcting the text
    /// changes the box's width.
    private func reselectTextBox(_ doc: PdfDocument, pageIndex: Int, id: String) async {
        guard let box = try? await PdfEngine.shared.textBoxes(doc, pageIndex: pageIndex)
            .first(where: { $0.id == id }) else { return }
        selectedTextBox = SelectedTextBox(pageIndex: pageIndex, id: id, text: box.text,
                                          fontSize: box.fontSize, fontName: box.fontName,
                                          rect: box.rect)
    }

    // MARK: - undo / redo (#34)

    func undo() {
        guard let doc = document else { return }
        Task {
            do {
                if let page = try await history.undo(PdfEngine.shared, doc) {
                    afterHistoryChange(page)
                }
            } catch {
                statusMessage = "Couldn't undo that."
            }
        }
    }

    func redo() {
        guard let doc = document else { return }
        Task {
            do {
                if let page = try await history.redo(PdfEngine.shared, doc) {
                    afterHistoryChange(page)
                }
            } catch {
                statusMessage = "Couldn't redo that."
            }
        }
    }

    /// Applies an edit through the history and refreshes everything that depends
    /// on it. The single funnel for every reversible change.
    private func perform(_ operation: PdfEditOperation, doc: PdfDocument) async throws {
        try await history.perform(operation, PdfEngine.shared, doc)
        afterHistoryChange(operation.pageIndex)
    }

    private func afterHistoryChange(_ pageIndex: Int) {
        // Deliberately conservative: any history movement leaves the document
        // possibly different from the bytes on disk, so it stays dirty.
        isDirty = true
        canUndo = history.canUndo
        canRedo = history.canRedo
        selectedStamp = nil
        selectedTextBox = nil
        invalidatePage(pageIndex)
    }

    func invalidatePage(_ index: Int) {
        renderedWidths.removeValue(forKey: index)
        if let w = lastWindow { updateRenderWindow(first: w.first, last: w.last, widthPx: w.widthPx) }
    }

    // MARK: - search (#26)

    /// As-you-type search: brief debounce, then a whole-document scan for
    /// case-insensitive literal matches, aggregated into the flat match list.
    /// An empty term just clears the results. `debounce` is only turned off by
    /// the `-screenshot search` seeding, which supplies the whole term at once.
    func search(term: String, debounce: Bool = true) {
        searchTask?.cancel()
        searchMatches = []
        currentMatchIndex = nil
        guard !term.isEmpty, case let .viewing(_, pageSizes) = state,
              let doc = document else {
            isSearching = false
            return
        }
        isSearching = true
        searchTask = Task {
            if debounce {
                try? await Task.sleep(nanoseconds: 250_000_000)
                if Task.isCancelled { return }
            }
            var matches: [SearchMatch] = []
            for index in 0..<pageSizes.count {
                if Task.isCancelled { return }
                let hits = (try? await PdfEngine.shared.search(
                    doc, pageIndex: index, term: term)) ?? []
                matches.append(contentsOf: hits.map {
                    SearchMatch(pageIndex: index, rects: $0.rects)
                })
            }
            if Task.isCancelled { return }
            searchMatches = matches
            currentMatchIndex = matches.isEmpty ? nil : 0
            isSearching = false
        }
    }

    func nextMatch() {
        guard let current = currentMatchIndex, !searchMatches.isEmpty else { return }
        currentMatchIndex = (current + 1) % searchMatches.count
    }

    func previousMatch() {
        guard let current = currentMatchIndex, !searchMatches.isEmpty else { return }
        currentMatchIndex = (current + searchMatches.count - 1) % searchMatches.count
    }

    /// Search bar dismissed: drop highlights and any in-flight scan.
    func clearSearch() {
        searchTask?.cancel()
        searchMatches = []
        currentMatchIndex = nil
        isSearching = false
    }

    // MARK: - signatures (#22)

    /// Imports a picked photo: decode, contract cleanup, store.
    func importSignature(imageData: Data) {
        guard let ui = UIImage(data: imageData), let cg = ui.cgImage,
              var (pixels, w, h) = PixelBuffers.argbPixels(from: downscaled(cg))
        else {
            statusMessage = "Couldn't decode that image."
            return
        }
        if !SignatureProcessor.hasTransparency(pixels) {
            pixels = SignatureProcessor.removeWhiteBackground(pixels)
        }
        let trimmed = SignatureProcessor.trimToInk(pixels, width: w, height: h)
        (pixels, w, h) = (trimmed.pixels, trimmed.width, trimmed.height)
        storeSignature(pixels: pixels, width: w, height: h)
    }

    /// Stores a drawn signature: already transparent, so only trim applies.
    func addDrawnSignature(image: CGImage) {
        guard var (pixels, w, h) = PixelBuffers.argbPixels(from: image) else {
            statusMessage = "Couldn't capture the drawing."
            return
        }
        let trimmed = SignatureProcessor.trimToInk(pixels, width: w, height: h)
        (pixels, w, h) = (trimmed.pixels, trimmed.width, trimmed.height)
        storeSignature(pixels: pixels, width: w, height: h)
    }

    func deleteSignature(id: String) {
        signatureStore.delete(id: id)
        signatures = signatureStore.load()
    }

    func startPlacement(_ entry: SignatureEntry) {
        selectedTextBox = nil
        pendingSignature = entry
        statusMessage = "Tap the page where the signature should go"
    }

    func cancelPlacement() {
        pendingSignature = nil
    }

    func commitStampRect(_ newRect: PdfRect) {
        guard let sel = selectedStamp, let doc = document,
              case let .viewing(_, pageSizes) = state else { return }
        let rect = clampToPage(newRect, pageSize: pageSizes[sel.pageIndex])
        Task {
            do {
                let engine = PdfEngine.shared
                guard let image = try await engine.stampImage(
                    doc, pageIndex: sel.pageIndex, annotIndex: sel.annotIndex) else {
                    statusMessage = "Couldn't read this signature's image."
                    return
                }
                try await perform(
                    MoveStampOperation(pageIndex: sel.pageIndex, id: sel.id,
                                       pixels: image.pixels,
                                       pixelWidth: image.width, pixelHeight: image.height,
                                       from: sel.rect, to: rect),
                    doc: doc)
                // Keep the moved stamp selected so the handles stay put.
                if let placed = try await engine.stamps(doc, pageIndex: sel.pageIndex)
                    .first(where: { $0.id == sel.id }) {
                    selectedStamp = SelectedStamp(pageIndex: sel.pageIndex,
                                                  annotIndex: placed.annotIndex,
                                                  id: sel.id, rect: placed.rect)
                }
            } catch {
                statusMessage = "Couldn't move the signature."
            }
        }
    }

    func removeSelectedStamp() {
        guard let sel = selectedStamp, let doc = document else { return }
        Task {
            do {
                let engine = PdfEngine.shared
                // Read the image back first: without it, undo could not put the
                // same signature back.
                guard let image = try await engine.stampImage(
                    doc, pageIndex: sel.pageIndex, annotIndex: sel.annotIndex) else {
                    statusMessage = "Couldn't remove this signature."
                    return
                }
                try await perform(
                    StampOperation(pageIndex: sel.pageIndex, id: sel.id,
                                   pixels: image.pixels,
                                   pixelWidth: image.width, pixelHeight: image.height,
                                   rect: sel.rect, adding: false),
                    doc: doc)
            } catch {
                statusMessage = "Couldn't remove this signature."
            }
        }
    }

    func deselectStamp() {
        selectedStamp = nil
    }

    private func placeSignature(_ entry: SignatureEntry, doc: PdfDocument,
                                pageIndex: Int, pageSize: CGSize, x: Double, y: Double) {
        guard let image = signatureStore.loadImage(entry),
              let (pixels, w, h) = PixelBuffers.argbPixels(from: image) else {
            statusMessage = "That signature's image is missing."
            return
        }
        // Default size: a third of the page width, aspect preserved.
        var wPt = Double(pageSize.width) / 3.0
        var hPt = wPt * Double(h) / Double(w)
        let maxH = Double(pageSize.height) / 3.0
        if hPt > maxH {
            hPt = maxH
            wPt = hPt * Double(w) / Double(h)
        }
        let rect = clampToPage(
            PdfRect(left: x - wPt / 2, bottom: y - hPt / 2,
                    right: x + wPt / 2, top: y + hPt / 2),
            pageSize: pageSize)
        let id = "sig:\(UUID().uuidString)"
        Task {
            do {
                let engine = PdfEngine.shared
                try await perform(
                    StampOperation(pageIndex: pageIndex, id: id, pixels: pixels,
                                   pixelWidth: w, pixelHeight: h,
                                   rect: rect, adding: true),
                    doc: doc)
                if let placed = try await engine.stamps(doc, pageIndex: pageIndex)
                    .first(where: { $0.id == id }) {
                    selectedStamp = SelectedStamp(pageIndex: pageIndex,
                                                  annotIndex: placed.annotIndex,
                                                  id: id, rect: placed.rect)
                }
            } catch {
                statusMessage = "Couldn't place the signature."
            }
        }
    }

    private func storeSignature(pixels: [UInt32], width: Int, height: Int) {
        guard let image = PixelBuffers.image(from: pixels, width: width, height: height),
              let entry = signatureStore.add(
                  displayName: "Signature \(signatures.count + 1)", image: image) else {
            statusMessage = "Couldn't save the signature."
            return
        }
        signatures.append(entry)
        statusMessage = "Signature added"
    }

    private func downscaled(_ image: CGImage, maxDim: Int = 1500) -> CGImage {
        guard image.width > maxDim || image.height > maxDim else { return image }
        let scale = Double(maxDim) / Double(max(image.width, image.height))
        let w = max(Int(Double(image.width) * scale), 1)
        let h = max(Int(Double(image.height) * scale), 1)
        guard let ctx = CGContext(
            data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue |
                CGBitmapInfo.byteOrder32Little.rawValue) else { return image }
        ctx.interpolationQuality = .high
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        return ctx.makeImage() ?? image
    }

    private func clampToPage(_ rect: PdfRect, pageSize: CGSize) -> PdfRect {
        let w = min(rect.right - rect.left, Double(pageSize.width))
        let h = min(rect.top - rect.bottom, Double(pageSize.height))
        let left = min(max(rect.left, 0), Double(pageSize.width) - w)
        let bottom = min(max(rect.bottom, 0), Double(pageSize.height) - h)
        return PdfRect(left: left, bottom: bottom, right: left + w, top: bottom + h)
    }

    // MARK: - save (#23)

    /// Save = write back to the opened document's URL. Same guarantees as the
    /// other platforms: serialize first, verify the output reopens in the
    /// engine, only then touch the destination (coordinated, atomic).
    func save() {
        guard let doc = document, let url = sourceURL, !isSaving else { return }
        isSaving = true
        Task {
            defer { isSaving = false }
            do {
                let engine = PdfEngine.shared
                let data = try await engine.save(doc)
                let verify = try await engine.open(data)
                await engine.close(verify)

                let scoped = url.startAccessingSecurityScopedResource()
                defer { if scoped { url.stopAccessingSecurityScopedResource() } }
                var coordError: NSError?
                var writeError: Error?
                NSFileCoordinator().coordinate(
                    writingItemAt: url, options: .forReplacing, error: &coordError
                ) { target in
                    do { try data.write(to: target, options: .atomic) }
                    catch { writeError = error }
                }
                if let error = coordError { throw error }
                if let error = writeError { throw error }
                isDirty = false
                statusMessage = "Saved"
            } catch {
                statusMessage = "Save failed — use Save a copy. (\(error.localizedDescription))"
            }
        }
    }

    /// Serialized (and engine-verified) bytes for the Save-a-copy exporter.
    func exportData() async -> Data? {
        guard let doc = document else { return nil }
        do {
            let engine = PdfEngine.shared
            let data = try await engine.save(doc)
            let verify = try await engine.open(data)
            await engine.close(verify)
            return data
        } catch {
            statusMessage = "Couldn't prepare the copy."
            return nil
        }
    }

    func markSavedCopy() {
        isDirty = false
        statusMessage = "Saved"
    }

    // MARK: - closing

    func close() {
        closeCurrent()
        state = .home(recents: recents.load(), error: nil)
    }

    private func toHome(_ error: String) {
        state = .home(recents: recents.load(), error: error)
    }

    private func closeCurrent() {
        renderTask?.cancel()
        clearSearch()
        pageImages = [:]
        renderedWidths = [:]
        lastWindow = nil
        sourceURL = nil
        isDirty = false
        pendingSignature = nil
        selectedStamp = nil
        selectedTextBox = nil
        // History belongs to the open document — never offer to undo an edit
        // made to a file that is no longer on screen.
        history.clear()
        canUndo = false
        canRedo = false
        isPlacingText = false
        pendingText = nil
        draftText = ""
        if let doc = document {
            document = nil
            Task { await PdfEngine.shared.close(doc) }
        }
    }
}
