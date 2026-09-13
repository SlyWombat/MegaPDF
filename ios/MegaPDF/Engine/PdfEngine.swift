import CoreGraphics
import CPdfium
import Foundation

// Engine facade over the shared engine core (ADR-003) and, for the contracts
// that have not migrated yet, PDFium via Swift C interop — the iOS counterpart
// of Android's engine module and the desktop `PdfiumEngine.cs` (contracts:
// SDD §6.2, engine choice: ADR-001).
//
// Since #105 the core owns the document: bytes go in, opaque handles come out,
// and the form-fill environment, page lifecycle and crop-origin bookkeeping live
// in core/. `PdfDocument` wraps the core's handle and exposes the raw PDFium
// handles beside it for the extensions still bound directly.
//
// PDFium is not thread-safe. The core serialises its own calls; `PdfEngine` is
// an actor and the app uses the single `PdfEngine.shared` instance, so the
// direct PDFium calls are serialized too — the Swift analog of Android's
// single-threaded dispatcher.

enum PdfError: Error, Equatable {
    case passwordRequired
    case load(code: Int)
    case pageLoad(index: Int)
    case renderFailed
    case saveFailed
    case editFailed
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

/// The CropBox origin in user space (#28/#30).
///
/// pdfium reports page content in user space, whose origin is the **MediaBox**,
/// but it renders and measures the **CropBox**. Where the two differ — imposed
/// pages, trimmed scans — every coordinate handed to the UI is out by that
/// difference. Every coordinate crossing this engine's boundary is therefore
/// shifted into crop-relative space, which is a no-op on the usual page whose
/// crop origin is already (0,0). The core owns the origin; the contracts it has
/// absorbed return crop space already, and the extensions still bound directly
/// convert through this.
struct CropOrigin: Equatable {
    var x: Double = 0
    var y: Double = 0
}

extension PdfRect {
    /// User space (pdfium) → crop space (what the UI draws in).
    func toCrop(_ crop: CropOrigin) -> PdfRect {
        PdfRect(left: left - crop.x, bottom: bottom - crop.y,
                right: right - crop.x, top: top - crop.y)
    }

    /// Crop space (what the UI hands us) → user space (pdfium).
    func toUser(_ crop: CropOrigin) -> PdfRect {
        PdfRect(left: left + crop.x, bottom: bottom + crop.y,
                right: right + crop.x, top: top + crop.y)
    }
}

struct PdfStamp: Equatable {
    let annotIndex: Int
    let id: String
    let rect: PdfRect
}

actor PdfEngine {
    static let shared = PdfEngine()

    private init() {
        // The core initialises PDFium on first open; the direct calls below need it too.
        FPDF_InitLibrary()
    }

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

    /// Renders page `index` at `pixelWidth` x `pixelHeight`: white ground, page
    /// content, then live form-field values — the shared render recipe.
    func render(_ document: PdfDocument, index: Int,
                pixelWidth: Int, pixelHeight: Int) throws -> CGImage {
        try withPage(document, index: index) { page in
            let stride = pixelWidth * 4
            var pixels = Data(count: stride * pixelHeight)
            let rendered = pixels.withUnsafeMutableBytes { raw -> Bool in
                guard let base = raw.baseAddress,
                      let bmp = FPDFBitmap_CreateEx(
                          Int32(pixelWidth), Int32(pixelHeight),
                          Int32(FPDFBitmap_BGRA), base, Int32(stride)) else { return false }
                FPDFBitmap_FillRect(bmp, 0, 0, Int32(pixelWidth), Int32(pixelHeight), 0xFFFFFFFF)
                let flags = Int32(FPDF_ANNOT | FPDF_LCD_TEXT)
                FPDF_RenderPageBitmap(bmp, page, 0, 0, Int32(pixelWidth), Int32(pixelHeight), 0, flags)
                if let form = document.form {
                    FPDF_FFLDraw(form, bmp, page, 0, 0, Int32(pixelWidth), Int32(pixelHeight), 0, flags)
                }
                FPDFBitmap_Destroy(bmp)
                return true
        }

        guard rendered,
              let provider = CGDataProvider(data: pixels as CFData),
              let cgImage = CGImage(
                  width: pixelWidth, height: pixelHeight,
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

    /// Serializes the document (`FPDF_SaveAsCopy`, full rewrite). Atomicity is
    /// the caller's job, as on the other platforms.
    func save(_ document: PdfDocument) throws -> Data {
        megapdf_form_commit(document.core)   // commit any in-progress field edit (#107)
        Self.saveSink = Data()
        var writer = FPDF_FILEWRITE(version: 1, WriteBlock: { _, data, size in
            guard let data, size > 0 else { return 1 }
            PdfEngine.saveSink.append(
                Data(bytes: data, count: Int(size)))
            return 1
        })
        let ok = FPDF_SaveAsCopy(document.doc, &writer, 0)
        let result = Self.saveSink
        Self.saveSink = Data()
        guard ok != 0, !result.isEmpty else { throw PdfError.saveFailed }
        return result
    }

    /// All MegaPDF-placed stamps on the page (`MegaPDF_Id`-tagged, any platform).
    func stamps(_ document: PdfDocument, pageIndex: Int) throws -> [PdfStamp] {
        try withPage(document, index: pageIndex) { page in
            let crop = cropOrigin(page)
            var result: [PdfStamp] = []
            for i in 0..<FPDFPage_GetAnnotCount(page) {
                guard let annot = FPDFPage_GetAnnot(page, i) else { continue }
                defer { FPDFPage_CloseAnnot(annot) }
                let bytes = FPDFAnnot_GetStringValue(annot, "MegaPDF_Id", nil, 0)
                guard bytes > 2 else { continue }
                var buf = [UInt16](repeating: 0, count: Int(bytes) / 2)
                FPDFAnnot_GetStringValue(annot, "MegaPDF_Id", &buf, bytes)
                let id = String(utf16CodeUnits: buf, count: buf.count - 1)
                var r = FS_RECTF()
                FPDFAnnot_GetRect(annot, &r)
                result.append(PdfStamp(
                    annotIndex: Int(i), id: id,
                    rect: PdfRect(left: Double(r.left), bottom: Double(r.bottom),
                                  right: Double(r.right), top: Double(r.top)).toCrop(crop)))
        }
        return result
        }
    }

    // MARK: - internals

    // WriteBlock is a C function pointer with no user-context slot; the actor
    // serializes saves, so a static sink is safe.
    nonisolated(unsafe) static var saveSink = Data()

    /// The core's page handles for the raw pages currently inside a `withPage`
    /// body, so the extensions still written against `FPDF_PAGE` can ask the core
    /// for the crop origin without changing shape. Actor-isolated, and gone once
    /// the last contract migrates (#106–#110).
    private var corePages: [FPDF_PAGE: OpaquePointer] = [:]

    /// The page's CropBox origin, or (0,0) when it has none (#30), as recorded by
    /// the core when it loaded the page. Every coordinate-returning entry point
    /// still bound directly converts through it.
    func cropOrigin(_ page: FPDF_PAGE) -> CropOrigin {
        var crop = CropOrigin()
        megapdf_page_crop_origin(corePages[page], &crop.x, &crop.y)
        return crop
    }

    /// Loads a page through the core, runs `body` with the raw `FPDF_PAGE`, and
    /// closes it — the access pattern for the operations still bound to PDFium
    /// directly (form-fill hooks applied by the core on load and close).
    func withPage<T>(_ document: PdfDocument, index: Int,
                     _ body: (FPDF_PAGE) throws -> T) throws -> T {
        try withCorePage(document, index: index) { core in
            let raw = OpaquePointer(megapdf_page_raw(core))!
            corePages[raw] = core
            defer { corePages[raw] = nil }
            return try body(raw)
        }
    }

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
    /// Raw PDFium handles, for the extensions still bound directly (stamps, text, forms, save).
    let docHandle: FPDF_DOCUMENT
    let formHandle: FPDF_FORMHANDLE?
    fileprivate var doc: FPDF_DOCUMENT { docHandle }
    fileprivate var form: FPDF_FORMHANDLE? { formHandle }
    private var destroyed = false

    fileprivate init(core: OpaquePointer) {
        self.core = core
        docHandle = OpaquePointer(megapdf_document_raw(core))!
        formHandle = megapdf_document_form_raw(core).map { OpaquePointer($0) }
    }

    fileprivate func destroy() {
        guard !destroyed else { return }
        destroyed = true
        megapdf_close(core)   // form environment, any page still open, then the document
    }
}
