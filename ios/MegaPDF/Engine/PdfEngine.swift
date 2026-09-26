import CoreGraphics
import CPdfium
import Foundation

// Engine facade over the shared engine core (ADR-003) — the iOS counterpart of
// Android's engine module and the desktop `PdfiumEngine.cs` (contracts: SDD §6.2,
// engine choice: ADR-001).
//
// Every contract lives in core/ (#105–#112): bytes go in, opaque handles come
// out, and coordinates come back in crop space. Nothing here calls PDFium.
//
// The core serialises its own calls; `PdfEngine` is an actor and the app uses the
// single `PdfEngine.shared` instance, which keeps multi-call sequences (count,
// then fill) from interleaving.

enum PdfError: Error, Equatable {
    case passwordRequired
    case load(code: Int)
    case pageLoad(index: Int)
    case renderFailed
    case saveFailed
    case editFailed
    /// PDFium would change the rest of the page if it rewrote this text (#118), and why (#128).
    case layoutWouldChange(PdfLayoutCause)
    /// The document's security doesn't allow it; its owner password would (#131).
    case restricted
    /// A security handler PDFium can't open, such as a certificate handler (#131, ADR-004 §8).
    case unsupportedSecurity
    /// The file is past what the platform can address (#147). Not reachable on 64-bit iOS today.
    case tooLarge
    /// `megapdf_write_text` failed (#386) -- the Markdown/text export, not a document save.
    case textExportFailed
}

/// What `error.localizedDescription` says for an engine failure — short and
/// plain, because it reaches the user (the save-failed status shows it).
extension PdfError: LocalizedError {
    var errorDescription: String? {
        switch self {
        case .passwordRequired:
            return String(localized: "Password required.")
        case let .load(code):
            return String(localized: "Couldn't open that file (error \(code)).")
        case let .pageLoad(index):
            return String(localized: "Couldn't load page \(index + 1).")
        case .renderFailed:
            return String(localized: "Couldn't draw the page.")
        case .saveFailed:
            return String(localized: "Couldn't save the document.")
        case .editFailed:
            return String(localized: "Couldn't change the document.")
        case let .layoutWouldChange(cause):
            return cause.notice
        case .restricted:
            return String(localized: "This document's security doesn't allow that without its owner password.")
        case .unsupportedSecurity:
            return String(localized: "This PDF uses a kind of protection MegaPDF can't open.")
        case .tooLarge:
            return String(localized: "This file is too large for MegaPDF to open.")
        case .textExportFailed:
            return String(localized: "Couldn't export the text.")
        }
    }
}

/// Rectangle in PDF points, bottom-left origin.
struct PdfRect: Equatable {
    var left: Double
    var bottom: Double
    var right: Double
    var top: Double
}

struct PdfStamp: Equatable {
    let annotIndex: Int
    let id: String
    let rect: PdfRect
}

actor PdfEngine {
    static let shared = PdfEngine()

    private init() {}   // the core initialises PDFium on its first open

    func open(_ bytes: Data, password: String? = nil) throws -> PdfDocument {
        // The core copies the bytes, so `bytes` is only borrowed for the call.
        let core: OpaquePointer? = bytes.withUnsafeBytes { raw in
            let base = raw.baseAddress
            if let pw = password {
                return pw.withCString { megapdf_open(base, raw.count, $0) }
            }
            return megapdf_open(base, raw.count, nil)
        }
        guard let core else {
            let code = Int(megapdf_last_error())
            if code == FPDF_ERR_PASSWORD { throw PdfError.passwordRequired }
            if code == FPDF_ERR_SECURITY { throw PdfError.unsupportedSecurity }
            throw PdfError.load(code: code)
        }
        return PdfDocument(core: core)
    }

    /// Opens a document from its file, read on demand for as long as it is open (#147, #148):
    /// nothing holds the file in memory, so a document costs what PDFium parses rather than its
    /// size. The caller must be able to open the file now (security-scoped access, for a picked
    /// one); reads after that go through the descriptor the core keeps.
    ///
    /// Replacing the file (an atomic write) leaves the document reading what it was opened on.
    /// Writing it in place would not: see `readFromCopy`.
    func open(file url: URL, password: String? = nil) throws -> PdfDocument {
        let core: OpaquePointer? = url.withUnsafeFileSystemRepresentation { path in
            guard let path else { return nil }
            if let pw = password {
                return pw.withCString { megapdf_open_file(path, $0) }
            }
            return megapdf_open_file(path, nil)
        }
        return try opened(core)
    }

    /// `open(file:)` with the credentials `like` was opened with (#132): how a save is checked
    /// without reading it into memory (#147).
    func open(file url: URL, like: PdfDocument) throws -> PdfDocument {
        guard !like.isDestroyed else { throw PdfError.load(code: 0) }
        let core: OpaquePointer? = url.withUnsafeFileSystemRepresentation { path in
            guard let path else { return nil }
            return megapdf_open_file_like(like.core, path)
        }
        return try opened(core)
    }

    private func opened(_ core: OpaquePointer?) throws -> PdfDocument {
        guard let core else {
            let code = Int(megapdf_last_error())
            if code == FPDF_ERR_PASSWORD { throw PdfError.passwordRequired }
            if code == FPDF_ERR_SECURITY { throw PdfError.unsupportedSecurity }
            if code == Int(MEGAPDF_OPEN_ERR_TOO_LARGE) { throw PdfError.tooLarge }
            throw PdfError.load(code: code)
        }
        return PdfDocument(core: core)
    }

    /// Whether `document` reads the file at `url` (the same file, not the same name) (#147).
    func reads(_ document: PdfDocument, file url: URL) -> Bool {
        guard !document.isDestroyed else { return false }
        return url.withUnsafeFileSystemRepresentation { path in
            guard let path else { return false }
            return megapdf_reads_file(document.core, path) == 1
        }
    }

    /// Moves `document` onto a private copy of the file it reads, so that file can be written in
    /// place without changing what the document reads (#147). On APFS the copy is a clone: no
    /// time, no space until the original is written. The copy is in the temporary directory and
    /// its name is gone before this returns.
    func readFromCopy(_ document: PdfDocument) throws {
        guard !document.isDestroyed else { throw PdfError.saveFailed }
        let copy = FileManager.default.temporaryDirectory.appendingPathComponent("reading-\(UUID().uuidString).pdf")
        let status = copy.withUnsafeFileSystemRepresentation { path -> Int32 in
            guard let path else { return Int32(MEGAPDF_ERR_ARGUMENT) }
            return megapdf_read_from_copy(document.core, path)
        }
        guard status == MEGAPDF_OK else { throw PdfError.saveFailed }
    }

    /// Opens `bytes` with the credentials `like` was opened with (#132): a saved copy of a
    /// protected document is still protected, so reading it back needs the same password.
    func open(_ bytes: Data, like: PdfDocument) throws -> PdfDocument {
        guard !like.isDestroyed else { throw PdfError.load(code: 0) }
        let core: OpaquePointer? = bytes.withUnsafeBytes { raw in
            megapdf_open_like(like.core, raw.baseAddress, raw.count)
        }
        guard let core else {
            let code = Int(megapdf_last_error())
            if code == FPDF_ERR_PASSWORD { throw PdfError.passwordRequired }
            if code == FPDF_ERR_SECURITY { throw PdfError.unsupportedSecurity }
            throw PdfError.load(code: code)
        }
        return PdfDocument(core: core)
    }

    /// Closes the document. Page checks running off the actor (#145) are told to stop first, and
    /// the close waits until they have, so none of them can touch the document once it is freed.
    func close(_ document: PdfDocument) async {
        guard !document.isDestroyed else { return }
        await document.stopChecks()
        document.destroy()
    }

    func pageCount(_ document: PdfDocument) -> Int {
        guard !document.isDestroyed else { return 0 }
        return Int(megapdf_page_count(document.core))
    }

    /// The CropBox size, which is what a viewer shows.
    func pageSize(_ document: PdfDocument, index: Int) throws -> CGSize {
        try withCorePage(document, index: index) { page in
            CGSize(width: megapdf_page_width(page), height: megapdf_page_height(page))
        }
    }

    /// Renders page `index` at up to `pixelWidth` x `pixelHeight`: white ground, page
    /// content, then live form-field values — the shared render recipe, drawn by the
    /// core, which also applies the render clamp (#93/#111): a request past 16,384 px
    /// a side or 32 MP comes back smaller, aspect preserved, and the view scales it up.
    func render(_ document: PdfDocument, index: Int,
                pixelWidth: Int, pixelHeight: Int) throws -> CGImage {
        var width: Int32 = 0, height: Int32 = 0
        megapdf_render_size(Double(pixelWidth), Double(pixelHeight), &width, &height)
        let w = Int(width), h = Int(height)
        return try withCorePage(document, index: index) { page in
            let stride = w * 4
            var pixels = Data(count: stride * h)
            let status = pixels.withUnsafeMutableBytes { raw -> Int32 in
                guard let base = raw.baseAddress else { return Int32(MEGAPDF_ERR_PDFIUM) }
                return megapdf_render(page, base, width, height, Int32(stride), UInt32(MEGAPDF_RENDER_BGRA))
            }
            guard status == MEGAPDF_OK,
                  let provider = CGDataProvider(data: pixels as CFData),
                  let cgImage = CGImage(
                      width: w, height: h,
                      bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: stride,
                      space: CGColorSpaceCreateDeviceRGB(),
                      bitmapInfo: CGBitmapInfo(
                          rawValue: CGImageAlphaInfo.premultipliedFirst.rawValue |
                              CGBitmapInfo.byteOrder32Little.rawValue),
                      provider: provider, decode: nil,
                      shouldInterpolate: true, intent: .defaultIntent)
            else { throw PdfError.renderFailed }
            return cgImage
        }
    }

    /// Serializes the document (full rewrite, #97) through the core, which commits
    /// any in-progress form edit first. Atomicity is the caller's job, as on the
    /// other platforms.
    func save(_ document: PdfDocument) throws -> Data {
        guard !document.isDestroyed else { throw PdfError.saveFailed }
        return try serialize { write, context in megapdf_save(document.core, write, context) }
    }

    /// Whether the document is encrypted and what this open may do (#131).
    func security(_ document: PdfDocument) -> PdfSecurity {
        guard !document.isDestroyed else { return .unprotected }
        var s = megapdf_security()
        _ = megapdf_security_info(document.core, &s)
        return PdfSecurity(isEncrypted: s.encrypted != 0, revision: Int(s.revision),
                           permissions: PdfPermissions(rawValue: s.permissions),
                           hasFullAccess: s.full_access != 0)
    }

    /// A copy encrypted with AES-256 under new passwords, in place of any security the
    /// document had (#131). The owner password opens it with every permission; nil means
    /// the same as the user password. The copy no longer opens like `document`: verify it
    /// with the new password. Throws `PdfError.restricted` without full access.
    func save(_ document: PdfDocument, userPassword: String, ownerPassword: String?,
              permissions: PdfPermissions) throws -> Data {
        guard !document.isDestroyed else { throw PdfError.saveFailed }
        return try userPassword.withCString { user in
            try (ownerPassword ?? "").withCString { owner in
                try serialize { write, context in
                    megapdf_save_with_security(document.core, user, owner, permissions.rawValue, write, context)
                }
            }
        }
    }

    /// `save` into a new file at `url`, streamed, so a large document is never in memory (#147).
    func save(_ document: PdfDocument, to url: URL) throws {
        guard !document.isDestroyed else { throw PdfError.saveFailed }
        try serialize(to: url) { write, context in megapdf_save(document.core, write, context) }
    }

    /// `save(_:userPassword:ownerPassword:permissions:)` into a new file at `url` (#147).
    func save(_ document: PdfDocument, userPassword: String, ownerPassword: String?,
              permissions: PdfPermissions, to url: URL) throws {
        guard !document.isDestroyed else { throw PdfError.saveFailed }
        try userPassword.withCString { user in
            try (ownerPassword ?? "").withCString { owner in
                try serialize(to: url) { write, context in
                    megapdf_save_with_security(document.core, user, owner, permissions.rawValue, write, context)
                }
            }
        }
    }

    /// `saveWithoutSecurity` into a new file at `url` (#147).
    func saveWithoutSecurity(_ document: PdfDocument, to url: URL) throws {
        guard !document.isDestroyed else { throw PdfError.saveFailed }
        try serialize(to: url) { write, context in megapdf_save_without_security(document.core, write, context) }
    }

    /// A copy with no security (#131). Throws `PdfError.restricted` without full access.
    func saveWithoutSecurity(_ document: PdfDocument) throws -> Data {
        guard !document.isDestroyed else { throw PdfError.saveFailed }
        return try serialize { write, context in megapdf_save_without_security(document.core, write, context) }
    }

    /// Runs one of the core's saves, collecting its blocks.
    private func serialize(_ save: (megapdf_write_fn, UnsafeMutableRawPointer) -> Int32) throws -> Data {
        final class Sink { var data = Data() }
        let sink = Sink()
        let write: megapdf_write_fn = { context, bytes, count in
            guard let context, let bytes, count > 0 else { return 1 }
            Unmanaged<Sink>.fromOpaque(context).takeUnretainedValue().data.append(Data(bytes: bytes, count: count))
            return 1
        }
        let status = withExtendedLifetime(sink) { () -> Int32 in
            save(write, Unmanaged.passUnretained(sink).toOpaque())
        }
        if status == MEGAPDF_ERR_RESTRICTED { throw PdfError.restricted }
        guard status == MEGAPDF_OK, !sink.data.isEmpty else { throw PdfError.saveFailed }
        return sink.data
    }

    /// Runs one of the core's saves into a new file, block by block (#147). The file is removed
    /// again if the save fails.
    private func serialize(to url: URL, _ save: (megapdf_write_fn, UnsafeMutableRawPointer) -> Int32) throws {
        guard FileManager.default.createFile(atPath: url.path, contents: nil),
              let handle = try? FileHandle(forWritingTo: url) else { throw PdfError.saveFailed }
        final class Sink {
            let handle: FileHandle
            var written = 0
            var failed = false
            init(handle: FileHandle) { self.handle = handle }
        }
        let sink = Sink(handle: handle)
        let write: megapdf_write_fn = { context, bytes, count in
            guard let context, let bytes, count > 0 else { return 1 }
            let sink = Unmanaged<Sink>.fromOpaque(context).takeUnretainedValue()
            do {
                try sink.handle.write(contentsOf: UnsafeRawBufferPointer(start: bytes, count: count))
                sink.written += count
                return 1
            } catch {
                sink.failed = true
                return 0
            }
        }
        let status = withExtendedLifetime(sink) { () -> Int32 in
            save(write, Unmanaged.passUnretained(sink).toOpaque())
        }
        let closed = (try? handle.close()) != nil
        guard status == MEGAPDF_OK, !sink.failed, sink.written > 0, closed else {
            try? FileManager.default.removeItem(at: url)
            if status == MEGAPDF_ERR_RESTRICTED { throw PdfError.restricted }
            throw PdfError.saveFailed
        }
    }

    /// All MegaPDF-placed stamps on the page (`MegaPDF_Id`-tagged, any platform),
    /// from the core's stamp list (#108).
    func stamps(_ document: PdfDocument, pageIndex: Int) throws -> [PdfStamp] {
        try withCorePage(document, index: pageIndex) { page in
            guard let stamps = megapdf_stamps_load(page) else { return [] }
            defer { megapdf_stamps_free(stamps) }
            var result: [PdfStamp] = []
            for i in 0..<megapdf_stamp_count(stamps) {
                var s = megapdf_stamp()
                guard megapdf_stamp_get(stamps, i, &s) == MEGAPDF_OK else { continue }
                let n = megapdf_stamp_id(stamps, i, nil, 0)
                var units = [UInt16](repeating: 0, count: n)
                if n > 0 { _ = units.withUnsafeMutableBufferPointer { megapdf_stamp_id(stamps, i, $0.baseAddress, n) } }
                result.append(PdfStamp(
                    annotIndex: Int(s.annot_index), id: String(utf16CodeUnits: units, count: n),
                    rect: PdfRect(left: s.bounds.left, bottom: s.bounds.bottom,
                                  right: s.bounds.right, top: s.bounds.top)))
            }
            return result
        }
    }

    // MARK: - internals

    /// Loads a page through the core, runs `body` with the core's handle, and
    /// closes it — the access pattern for the migrated contracts.
    func withCorePage<T>(_ document: PdfDocument, index: Int,
                         _ body: (OpaquePointer) throws -> T) throws -> T {
        // A call queued behind a close must not reach a freed document.
        guard !document.isDestroyed,
              let page = megapdf_load_page(document.core, Int32(index)) else {
            throw PdfError.pageLoad(index: index)
        }
        defer { megapdf_close_page(page) }
        return try body(page)
    }
}

/// Opaque handle over the core's document, which owns the bytes, the PDFium
/// document and the form-fill environment. Create via `PdfEngine.open`;
/// destroy via `close`.
final class PdfDocument: @unchecked Sendable {
    /// The core's handle.
    let core: OpaquePointer
    /// Only read and written on the engine actor.
    private(set) var isDestroyed = false

    /// Page checks running off the actor (#145), guarded by `lock`: `close` raises their flags
    /// and waits for them before megapdf_close(), and no check may begin once closing started.
    private let lock = NSLock()
    private var closing = false
    private var checks: [ObjectIdentifier: PageCheckFlag] = [:]
    private var drained: [CheckedContinuation<Void, Never>] = []

    fileprivate init(core: OpaquePointer) {
        self.core = core
    }

    fileprivate func destroy() {
        guard !isDestroyed else { return }
        isDestroyed = true
        megapdf_close(core)   // form environment, any page still open, then the document
    }

    /// Registers a check before its first core call. False once the document is closing.
    func beginCheck(_ flag: PageCheckFlag) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard !closing else { return false }
        checks[ObjectIdentifier(flag)] = flag
        return true
    }

    /// The check's last core call has returned (its page handle closed too).
    func endCheck(_ flag: PageCheckFlag) {
        lock.lock()
        checks[ObjectIdentifier(flag)] = nil
        var waiting: [CheckedContinuation<Void, Never>] = []
        if checks.isEmpty {
            waiting = drained
            drained = []
        }
        lock.unlock()
        waiting.forEach { $0.resume() }
    }

    /// Stops new checks, raises every running check's flag, and returns once they have all ended.
    fileprivate func stopChecks() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            lock.lock()
            closing = true
            let running = Array(checks.values)
            if running.isEmpty {
                lock.unlock()
                continuation.resume()
                return
            }
            drained.append(continuation)
            lock.unlock()
            running.forEach { $0.raise() }
        }
    }
}
