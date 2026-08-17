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

/// A tap that is waiting for the text the user is about to type (#34).
struct PendingText: Identifiable {
    let id = UUID()
    let pageIndex: Int
    let x: Double
    let y: Double
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
        case "viewer", "sign", "draw", "search":
            if let url = Bundle.main.url(forResource: "demo", withExtension: "pdf"),
               let bytes = try? Data(contentsOf: url) {
                if mode == "sign" { screenshotSheet = .signatures }
                if mode == "draw" { screenshotSheet = .draw }
                if mode == "search" { screenshotSearchTerm = DemoContent.searchTerm }
                Task {
                    await open(bytes: bytes, password: nil,
                               displayName: "Rental Agreement.pdf", sourceURL: nil)
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
            pendingText = PendingText(pageIndex: index, x: x, y: y)
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
                    return
                }
                selectedStamp = nil

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
        isPlacingText = true
        statusMessage = "Tap the page where the text should go"
    }

    func cancelTextPlacement() {
        isPlacingText = false
        pendingText = nil
        statusMessage = nil
    }

    /// Commits the text the user typed for the pending tap.
    func commitText(_ text: String) {
        guard let pending = pendingText, let doc = document else { return }
        pendingText = nil
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        Task {
            do {
                try await perform(
                    TextBoxOperation(pageIndex: pending.pageIndex,
                                     id: "text:\(UUID().uuidString)",
                                     text: trimmed, fontSize: 12,
                                     x: pending.x, y: pending.y, adding: true),
                    doc: doc)
            } catch {
                statusMessage = "Couldn't add that text."
            }
        }
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
        // History belongs to the open document — never offer to undo an edit
        // made to a file that is no longer on screen.
        history.clear()
        canUndo = false
        canRedo = false
        isPlacingText = false
        pendingText = nil
        if let doc = document {
            document = nil
            Task { await PdfEngine.shared.close(doc) }
        }
    }
}
