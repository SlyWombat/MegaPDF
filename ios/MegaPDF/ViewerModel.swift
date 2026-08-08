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

    /// Set only by `-screenshot sign|draw` launches; ViewerView opens the sheet.
    enum ScreenshotSheet { case signatures, draw }
    @Published private(set) var screenshotSheet: ScreenshotSheet?

    private let recents = RecentsStore()
    private let signatureStore = SignatureStore()
    private var document: PdfDocument?
    private var sourceURL: URL?
    private var renderedWidths: [Int: Int] = [:]
    private var renderTask: Task<Void, Never>?
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
           let entry = signatureStore.add(displayName: "MegaWoman", image: image) {
            signatures.append(entry)
        }
        switch mode {
        case "home":
            state = .home(recents: DemoContent.demoRecents(), error: nil)
        case "viewer", "sign", "draw":
            if let url = Bundle.main.url(forResource: "demo", withExtension: "pdf"),
               let bytes = try? Data(contentsOf: url) {
                if mode == "sign" { screenshotSheet = .signatures }
                if mode == "draw" { screenshotSheet = .draw }
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

        Task {
            do {
                var edited = false
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

                let fields = try await engine.formFields(doc, pageIndex: index)
                if let field = fields.first(where: { $0.rect.contains(x: x, y: y) }) {
                    try await engine.clickAt(doc, pageIndex: index,
                                             x: field.rect.centerX, y: field.rect.centerY)
                    edited = true
                } else {
                    let stamps = try await engine.stamps(doc, pageIndex: index)
                    if let mark = stamps.first(where: {
                        $0.id.hasPrefix("mark:") && $0.rect.contains(x: x, y: y)
                    }) {
                        try await engine.removeAnnot(doc, pageIndex: index,
                                                     annotIndex: mark.annotIndex)
                        edited = true
                    } else if let square = try await engine
                        .detectCheckboxSquares(doc, pageIndex: index)
                        .first(where: { $0.contains(x: x, y: y) }) {
                        try await engine.addCheckMark(
                            doc, pageIndex: index, square: square,
                            id: "mark:\(UUID().uuidString)")
                        edited = true
                    }
                }
                if edited {
                    isDirty = true
                    invalidatePage(index)
                }
            } catch {
                // Edits are tap-driven; a failure just leaves the page unchanged.
            }
        }
    }

    func invalidatePage(_ index: Int) {
        renderedWidths.removeValue(forKey: index)
        if let w = lastWindow { updateRenderWindow(first: w.first, last: w.last, widthPx: w.widthPx) }
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
                try await engine.removeAnnot(doc, pageIndex: sel.pageIndex,
                                             annotIndex: sel.annotIndex)
                try await engine.addImageStamp(
                    doc, pageIndex: sel.pageIndex, pixels: image.pixels,
                    pixelWidth: image.width, pixelHeight: image.height,
                    rect: rect, id: sel.id)
                let placed = try await engine.stamps(doc, pageIndex: sel.pageIndex)
                    .first { $0.id == sel.id }
                if let placed {
                    selectedStamp = SelectedStamp(pageIndex: sel.pageIndex,
                                                  annotIndex: placed.annotIndex,
                                                  id: sel.id, rect: placed.rect)
                }
                isDirty = true
                invalidatePage(sel.pageIndex)
            } catch {
                statusMessage = "Couldn't move the signature."
            }
        }
    }

    func removeSelectedStamp() {
        guard let sel = selectedStamp, let doc = document else { return }
        Task {
            try? await PdfEngine.shared.removeAnnot(
                doc, pageIndex: sel.pageIndex, annotIndex: sel.annotIndex)
            selectedStamp = nil
            isDirty = true
            invalidatePage(sel.pageIndex)
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
                try await engine.addImageStamp(doc, pageIndex: pageIndex, pixels: pixels,
                                               pixelWidth: w, pixelHeight: h,
                                               rect: rect, id: id)
                if let placed = try await engine.stamps(doc, pageIndex: pageIndex)
                    .first(where: { $0.id == id }) {
                    selectedStamp = SelectedStamp(pageIndex: pageIndex,
                                                  annotIndex: placed.annotIndex,
                                                  id: id, rect: placed.rect)
                }
                isDirty = true
                invalidatePage(pageIndex)
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
        pageImages = [:]
        renderedWidths = [:]
        lastWindow = nil
        sourceURL = nil
        isDirty = false
        pendingSignature = nil
        selectedStamp = nil
        if let doc = document {
            document = nil
            Task { await PdfEngine.shared.close(doc) }
        }
    }
}
