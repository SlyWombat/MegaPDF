import CoreGraphics
import Foundation

enum ViewerState {
    case home(recents: [RecentEntry], error: String?)
    case loading
    case passwordNeeded(bytes: Data, displayName: String, sourceURL: URL?, wrongPassword: Bool)
    case viewing(displayName: String, pageSizes: [CGSize])
}

/// Owns the engine document and the ±2-page render window — the iOS port of
/// Android's `ViewerViewModel` (same virtualization, same eviction policy).
@MainActor
final class ViewerModel: ObservableObject {
    @Published private(set) var state: ViewerState
    @Published private(set) var pageImages: [Int: CGImage] = [:]

    private let recents = RecentsStore()
    private var document: PdfDocument?
    private var sourceURL: URL?
    private var renderedWidths: [Int: Int] = [:]
    private var renderTask: Task<Void, Never>?
    private var lastWindow: (first: Int, last: Int, widthPx: Int)?

    private static let renderMargin = 2
    private static let maxPixelDim = 2048

    init() {
        state = .home(recents: recents.load(), error: nil)
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

    func invalidatePage(_ index: Int) {
        renderedWidths.removeValue(forKey: index)
        if let w = lastWindow { updateRenderWindow(first: w.first, last: w.last, widthPx: w.widthPx) }
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
        if let doc = document {
            document = nil
            Task { await PdfEngine.shared.close(doc) }
        }
    }
}
