import CPdfium
import Foundation

// Added text (#34) — new text placed on the page, not edits to existing body
// text (which iOS deliberately does not do; see SDD §4.4 and #33).
//
// The representation is the desktop's, byte for byte: a page **text object** in
// a standard font carrying the `MegaPDFTextBox` page-object mark, exactly what
// `PdfiumEngine.AppendTextBox` writes. A box added on a phone is therefore a
// movable text box in MegaPDF for Windows and ordinary selectable text in
// Acrobat. The mark additionally carries an `id` string param — a stable handle
// that survives the index shifts page-object edits cause, the page-object
// equivalent of the `MegaPDF_Id` annotation contract (SDD §6.2).

/// A MegaPDF-placed text box on a page. `rect` is crop space (#30).
struct PdfTextBox: Equatable {
    let id: String
    let objectIndex: Int
    let text: String
    let rect: PdfRect
    let fontSize: Double
    /// The face it was written in; Helvetica for boxes that predate #43.
    let fontName: String
}

extension PdfEngine {

    /// The page-object mark that distinguishes our text from the document's own.
    static let textBoxMark = "MegaPDFTextBox"

    /// The face the user picked (#43), carried as a mark param beside the id
    /// rather than read back off the font resource: pdfium is free to normalise
    /// a standard font's reported name, and the cross-platform contract has to
    /// be exactly what was chosen.
    static let textBoxFontKey = "font"

    /// The base-14 faces a text box may be written in (#43).
    ///
    /// Three, not fourteen: SDD §3.1 keeps formatting controls out of the app,
    /// and a choice between serif, sans and monospace is what "make this match
    /// the form I am filling in" actually needs. These are the exact names
    /// `FPDFText_LoadStandardFont` takes, so nothing has to be mapped.
    static let standardFonts = ["Helvetica", "Times-Roman", "Courier"]

    /// What a box with no recorded face is, and what a new one defaults to.
    static let defaultFont = "Helvetica"

    /// Places `text` with its baseline starting at the crop-space point
    /// (`x`, `y`) — text sits *on* the point tapped, which is what putting it on
    /// a printed rule needs. Returns the new box's stable id.
    /// `fontName` must be one of `standardFonts`; the face is recorded on the
    /// mark so every platform reads back exactly what was chosen (#43).
    @discardableResult
    func addTextBox(_ document: PdfDocument, pageIndex: Int, text: String,
                    fontSize: Double, x: Double, y: Double,
                    id: String = "text:\(UUID().uuidString)",
                    fontName: String = PdfEngine.defaultFont) throws -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw PdfError.editFailed }
        guard Self.standardFonts.contains(fontName) else { throw PdfError.editFailed }

        try withPage(document, index: pageIndex) { page in
            let crop = cropOrigin(page)
            guard let font = FPDFText_LoadStandardFont(document.docHandle, fontName) else {
                throw PdfError.editFailed
            }
            defer { FPDFFont_Close(font) }

            guard let obj = FPDFPageObj_CreateTextObj(
                document.docHandle, font, Float(fontSize)) else { throw PdfError.editFailed }
            // pdfium takes ownership in FPDFPage_InsertObject — and frees the
            // object itself if that call fails — so once it is handed over we
            // must not destroy it, success or not.
            var handedToPdfium = false
            defer { if !handedToPdfium { FPDFPageObj_Destroy(obj) } }

            let wide = Array(trimmed.utf16) + [0]
            let didSet = wide.withUnsafeBufferPointer { FPDFText_SetText(obj, $0.baseAddress) }
            guard didSet != 0 else { throw PdfError.editFailed }

            var m = FS_MATRIX(a: 1, b: 0, c: 0, d: 1,
                              e: Float(x + crop.x), f: Float(y + crop.y))
            guard FPDFPageObj_SetMatrix(obj, &m) != 0 else { throw PdfError.editFailed }

            guard FPDFPageObj_AddMark(obj, Self.textBoxMark) != nil else {
                throw PdfError.editFailed
            }
            // Re-read the mark we just added so the params land on the object
            // pdfium owns, then hand the object to the page.
            guard Self.tagLastMark(document, obj, id: id, fontName: fontName) else {
                throw PdfError.editFailed
            }
            handedToPdfium = true
            guard FPDFPage_InsertObject(page, obj) != 0,
                  FPDFPage_GenerateContent(page) != 0 else { throw PdfError.editFailed }
        }
        return id
    }

    /// Every MegaPDF text box on the page, in page-object order.
    func textBoxes(_ document: PdfDocument, pageIndex: Int) throws -> [PdfTextBox] {
        try withPage(document, index: pageIndex) { page in
            let crop = cropOrigin(page)
            let textPage = FPDFText_LoadPage(page)
            defer { if let textPage { FPDFText_ClosePage(textPage) } }

            var result: [PdfTextBox] = []
            for i in 0..<FPDFPage_CountObjects(page) {
                guard let obj = FPDFPage_GetObject(page, i),
                      FPDFPageObj_GetType(obj) == FPDF_PAGEOBJ_TEXT,
                      let id = Self.textBoxId(obj, objectIndex: Int(i)) else { continue }
                var l: Float = 0, b: Float = 0, r: Float = 0, t: Float = 0
                guard FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) != 0 else { continue }
                var size: Float = 0
                FPDFTextObj_GetFontSize(obj, &size)
                result.append(PdfTextBox(
                    id: id, objectIndex: Int(i),
                    text: Self.textOf(obj, textPage),
                    rect: PdfRect(left: Double(l), bottom: Double(b),
                                  right: Double(r), top: Double(t)).toCrop(crop),
                    fontSize: Double(size),
                    fontName: Self.textBoxFont(obj)))
            }
            return result
        }
    }

    /// Translates the box with the given id so its lower-left corner lands on the
    /// crop-space point (`x`, `y`). Scale and rotation are left untouched.
    func moveTextBox(_ document: PdfDocument, pageIndex: Int, id: String,
                     x: Double, y: Double) throws {
        try withPage(document, index: pageIndex) { page in
            let crop = cropOrigin(page)
            guard let obj = Self.findTextBox(page, id: id) else { throw PdfError.editFailed }
            var l: Float = 0, b: Float = 0, r: Float = 0, t: Float = 0
            var m = FS_MATRIX()
            guard FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) != 0,
                  FPDFPageObj_GetMatrix(obj, &m) != 0 else { throw PdfError.editFailed }
            m.e += Float(x + crop.x) - l
            m.f += Float(y + crop.y) - b
            guard FPDFPageObj_SetMatrix(obj, &m) != 0,
                  FPDFPage_GenerateContent(page) != 0 else { throw PdfError.editFailed }
        }
    }

    /// Removes the box with the given id. Silently succeeds if it is already gone,
    /// so an undo that races a re-render cannot throw.
    func removeTextBox(_ document: PdfDocument, pageIndex: Int, id: String) throws {
        try withPage(document, index: pageIndex) { page in
            guard let obj = Self.findTextBox(page, id: id) else { return }
            guard FPDFPage_RemoveObject(page, obj) != 0 else { throw PdfError.editFailed }
            FPDFPageObj_Destroy(obj)
            guard FPDFPage_GenerateContent(page) != 0 else { throw PdfError.editFailed }
        }
    }

    // MARK: - internals

    private static func findTextBox(_ page: FPDF_PAGE, id: String) -> FPDF_PAGEOBJECT? {
        for i in 0..<FPDFPage_CountObjects(page) {
            guard let obj = FPDFPage_GetObject(page, i),
                  FPDFPageObj_GetType(obj) == FPDF_PAGEOBJ_TEXT,
                  textBoxId(obj, objectIndex: Int(i)) == id else { continue }
            return obj
        }
        return nil
    }

    /// The id carried by the object's `MegaPDFTextBox` mark, or nil when the
    /// object is not one of ours. Desktop boxes predate the id param, so a marked
    /// object with no id still reads as a text box — it just gets a derived handle.
    private static func textBoxId(_ obj: FPDF_PAGEOBJECT, objectIndex: Int) -> String? {
        for m in 0..<FPDFPageObj_CountMarks(obj) {
            guard let mark = FPDFPageObj_GetMark(obj, UInt(m)),
                  readWide({ FPDFPageObjMark_GetName(mark, $0, $1, $2) }) == textBoxMark
            else { continue }
            if let id = readWide({ FPDFPageObjMark_GetParamStringValue(mark, "id", $0, $1, $2) }),
               !id.isEmpty {
                return id
            }
            // A box written before the id param existed (shipping Windows 1.6.x).
            // It still reads as a text box, but its handle can only be its
            // position — and it must be *unique*, or a document with two of them
            // would let removeTextBox delete an arbitrary one. Position-derived
            // handles are not stable across edits, which is fine: nothing here
            // creates untagged boxes, it only has to read them coherently.
            return "\(untaggedPrefix)\(objectIndex)"
        }
        return nil
    }

    /// Prefix for the handle given to a marked box that carries no id.
    static let untaggedPrefix = "text:untagged#"

    /// The face named by the box's `font` param, or Helvetica when it carries
    /// none — which is every box written before #43, and what they all are.
    private static func textBoxFont(_ obj: FPDF_PAGEOBJECT) -> String {
        for m in 0..<FPDFPageObj_CountMarks(obj) {
            guard let mark = FPDFPageObj_GetMark(obj, UInt(m)),
                  readWide({ FPDFPageObjMark_GetName(mark, $0, $1, $2) }) == textBoxMark
            else { continue }
            if let face = readWide({
                FPDFPageObjMark_GetParamStringValue(mark, textBoxFontKey, $0, $1, $2)
            }), !face.isEmpty {
                return face
            }
            break
        }
        return defaultFont
    }

    private static func tagLastMark(_ document: PdfDocument, _ obj: FPDF_PAGEOBJECT,
                                    id: String, fontName: String) -> Bool {
        let count = FPDFPageObj_CountMarks(obj)
        guard count > 0, let mark = FPDFPageObj_GetMark(obj, UInt(count - 1)) else { return false }
        return FPDFPageObjMark_SetStringParam(document.docHandle, obj, mark, "id", id) != 0
            && FPDFPageObjMark_SetStringParam(
                document.docHandle, obj, mark, textBoxFontKey, fontName) != 0
    }

    /// Despite the headers saying FPDF_WCHARs, these lengths are in **BYTES**
    /// (including the UTF-16 NUL) — the same pdfium 152 quirk the desktop engine
    /// documents in `ReadTextObjectText`.
    private static func readWide(
        _ call: (UnsafeMutablePointer<FPDF_WCHAR>?, UInt, UnsafeMutablePointer<UInt>?) -> Int32
    ) -> String? {
        var needed: UInt = 0
        guard call(nil, 0, &needed) != 0, needed > 2 else { return nil }
        var buf = [UInt16](repeating: 0, count: Int(needed) / 2)
        var written = needed
        guard call(&buf, needed, &written) != 0 else { return nil }
        return String(utf16CodeUnits: buf, count: buf.count - 1)
    }

    private static func textOf(_ obj: FPDF_PAGEOBJECT, _ textPage: FPDF_TEXTPAGE?) -> String {
        guard let textPage else { return "" }
        let lengthInBytes = FPDFTextObj_GetText(obj, textPage, nil, 0)
        guard lengthInBytes > 2 else { return "" }
        var buf = [UInt16](repeating: 0, count: Int(lengthInBytes) / 2)
        _ = FPDFTextObj_GetText(obj, textPage, &buf, lengthInBytes)
        return String(utf16CodeUnits: buf, count: buf.count - 1)
    }
}
