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

    /// Opens `bytes` with the credentials `like` was opened with (#132): a saved copy of a
    /// protected document is still protected, so reading it back needs the same password.
    func open(_ bytes: Data, like: PdfDocument) throws -> PdfDocument {
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

    func close(_ document: PdfDocument) {
        document.destroy()
    }

    func pageCount(_ document: PdfDocument) -> Int {
        Int(megapdf_page_count(document.core))
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
        try serialize { write, context in megapdf_save(document.core, write, context) }
    }

    /// Whether the document is encrypted and what this open may do (#131).
    func security(_ document: PdfDocument) -> PdfSecurity {
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
        try userPassword.withCString { user in
            try (ownerPassword ?? "").withCString { owner in
                try serialize { write, context in
                    megapdf_save_with_security(document.core, user, owner, permissions.rawValue, write, context)
                }
            }
        }
    }

    /// A copy with no security (#131). Throws `PdfError.restricted` without full access.
    func saveWithoutSecurity(_ document: PdfDocument) throws -> Data {
        try serialize { write, context in megapdf_save_without_security(document.core, write, context) }
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
        guard let page = megapdf_load_page(document.core, Int32(index)) else {
            throw PdfError.pageLoad(index: index)
        }
        defer { megapdf_close_page(page) }
        return try body(page)
    }
}

/// Opaque handle over the core's document, which owns the bytes, the PDFium
/// document and the form-fill environment. Create via `PdfEngine.open`;
/// destroy via `close`.
final class PdfDocument {
    /// The core's handle.
    let core: OpaquePointer
    private var destroyed = false

    fileprivate init(core: OpaquePointer) {
        self.core = core
    }

    fileprivate func destroy() {
        guard !destroyed else { return }
        destroyed = true
        megapdf_close(core)   // form environment, any page still open, then the document
    }
}
