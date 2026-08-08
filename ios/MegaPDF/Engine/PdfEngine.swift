import CoreGraphics
import CPdfium
import Foundation

// Engine facade over PDFium via Swift C interop — the iOS counterpart of
// Android's engine module and, like it, a reimplementation of the desktop
// `PdfiumEngine.cs` behavior (contracts: SDD §6.2, engine choice: ADR-001).
//
// PDFium is not thread-safe. `PdfEngine` is an actor and the app uses the
// single `PdfEngine.shared` instance, so every native call is serialized —
// the Swift analog of Android's single-threaded dispatcher.

enum PdfError: Error, Equatable {
    case passwordRequired
    case load(code: Int)
    case pageLoad(index: Int)
    case renderFailed
    case saveFailed
    case editFailed
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

    private init() {
        FPDF_InitLibrary()
    }

    func open(_ bytes: Data, password: String? = nil) throws -> PdfDocument {
        // PDFium requires the buffer to outlive the document; PdfDocument owns it.
        let buffer = UnsafeMutableRawPointer.allocate(byteCount: bytes.count, alignment: 8)
        bytes.copyBytes(to: buffer.assumingMemoryBound(to: UInt8.self), count: bytes.count)

        let doc = password.map { pw in
            pw.withCString { FPDF_LoadMemDocument64(buffer, bytes.count, $0) }
        } ?? FPDF_LoadMemDocument64(buffer, bytes.count, nil)

        guard let doc else {
            let code = Int(FPDF_GetLastError())
            buffer.deallocate()
            if code == FPDF_ERR_PASSWORD { throw PdfError.passwordRequired }
            throw PdfError.load(code: code)
        }

        let ffi = UnsafeMutablePointer<FPDF_FORMFILLINFO>.allocate(capacity: 1)
        ffi.initialize(to: Self.makeFormFillInfo())
        let form = FPDFDOC_InitFormFillEnvironment(doc, ffi)
        return PdfDocument(doc: doc, form: form, buffer: buffer, ffi: ffi)
    }

    func close(_ document: PdfDocument) {
        document.destroy()
    }

    func pageCount(_ document: PdfDocument) -> Int {
        Int(FPDF_GetPageCount(document.doc))
    }

    func pageSize(_ document: PdfDocument, index: Int) throws -> CGSize {
        let page = try loadPage(document, index: index)
        defer { closePage(document, page) }
        return CGSize(width: FPDF_GetPageWidth(page), height: FPDF_GetPageHeight(page))
    }

    /// Renders page `index` at `pixelWidth` x `pixelHeight`: white ground, page
    /// content, then live form-field values — the shared render recipe.
    func render(_ document: PdfDocument, index: Int,
                pixelWidth: Int, pixelHeight: Int) throws -> CGImage {
        let page = try loadPage(document, index: index)
        defer { closePage(document, page) }

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

    /// Serializes the document (`FPDF_SaveAsCopy`, full rewrite). Atomicity is
    /// the caller's job, as on the other platforms.
    func save(_ document: PdfDocument) throws -> Data {
        if let form = document.form { FORM_ForceToKillFocus(form) }
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
        let page = try loadPage(document, index: pageIndex)
        defer { closePage(document, page) }

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
                              right: Double(r.right), top: Double(r.top))))
        }
        return result
    }

    // MARK: - internals

    // WriteBlock is a C function pointer with no user-context slot; the actor
    // serializes saves, so a static sink is safe.
    nonisolated(unsafe) static var saveSink = Data()

    /// Loads a page, runs `body`, and closes the page — the standard access
    /// pattern for all page-scoped operations (form lifecycle included).
    func withPage<T>(_ document: PdfDocument, index: Int,
                     _ body: (FPDF_PAGE) throws -> T) throws -> T {
        let page = try loadPage(document, index: index)
        defer { closePage(document, page) }
        return try body(page)
    }

    private func loadPage(_ document: PdfDocument, index: Int) throws -> FPDF_PAGE {
        guard let page = FPDF_LoadPage(document.doc, Int32(index)) else {
            throw PdfError.pageLoad(index: index)
        }
        if let form = document.form { FORM_OnAfterLoadPage(page, form) }
        return page
    }

    private func closePage(_ document: PdfDocument, _ page: FPDF_PAGE) {
        if let form = document.form { FORM_OnBeforeClosePage(page, form) }
        FPDF_ClosePage(page)
    }

    private static func makeFormFillInfo() -> FPDF_FORMFILLINFO {
        var ffi = FPDF_FORMFILLINFO()
        ffi.version = 1
        // Same no-op callback set as desktop and Android (no JS, no XFA).
        ffi.FFI_Invalidate = { _, _, _, _, _, _ in }
        ffi.FFI_OutputSelectedRect = { _, _, _, _, _, _ in }
        ffi.FFI_SetCursor = { _, _ in }
        ffi.FFI_SetTimer = { _, _, _ in 0 }
        ffi.FFI_KillTimer = { _, _ in }
        ffi.FFI_GetLocalTime = { _ in FPDF_SYSTEMTIME() }
        ffi.FFI_OnChange = { _ in }
        ffi.FFI_GetPage = { _, _, _ in nil }
        ffi.FFI_GetCurrentPage = { _, _ in nil }
        ffi.FFI_GetRotation = { _, _ in 0 }
        ffi.FFI_ExecuteNamedAction = { _, _ in }
        ffi.FFI_SetTextFieldFocus = { _, _, _, _ in }
        ffi.FFI_DoURIAction = { _, _ in }
        ffi.FFI_DoGoToAction = { _, _, _, _, _ in }
        return ffi
    }
}

/// Opaque handle owning the native document, its backing buffer, and the
/// form-fill environment. Create via `PdfEngine.open`; destroy via `close`.
final class PdfDocument {
    fileprivate let doc: FPDF_DOCUMENT
    fileprivate let form: FPDF_FORMHANDLE?

    /// Engine-internal access for the surface extensions (checkboxes, stamps).
    var formHandle: FPDF_FORMHANDLE? { form }
    var docHandle: FPDF_DOCUMENT { doc }
    private let buffer: UnsafeMutableRawPointer
    private let ffi: UnsafeMutablePointer<FPDF_FORMFILLINFO>
    private var destroyed = false

    fileprivate init(doc: FPDF_DOCUMENT, form: FPDF_FORMHANDLE?,
                     buffer: UnsafeMutableRawPointer,
                     ffi: UnsafeMutablePointer<FPDF_FORMFILLINFO>) {
        self.doc = doc
        self.form = form
        self.buffer = buffer
        self.ffi = ffi
    }

    fileprivate func destroy() {
        guard !destroyed else { return }
        destroyed = true
        if let form { FPDFDOC_ExitFormFillEnvironment(form) }
        FPDF_CloseDocument(doc)
        ffi.deallocate()
        buffer.deallocate()
    }
}
