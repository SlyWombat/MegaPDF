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
    /// a printed rule needs. Returns the new box's stable id. The core writes the
    /// mark with the id and the face exactly as chosen (#43, #109) and rejects any
    /// face outside `standardFonts`.
    @discardableResult
    func addTextBox(_ document: PdfDocument, pageIndex: Int, text: String,
                    fontSize: Double, x: Double, y: Double,
                    id: String = "text:\(UUID().uuidString)",
                    fontName: String = PdfEngine.defaultFont) throws -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw PdfError.editFailed }
        guard Self.standardFonts.contains(fontName) else { throw PdfError.editFailed }

        try withCorePage(document, index: pageIndex) { page in
            let wideText = Array(trimmed.utf16) + [0]
            let wideId = Array(id.utf16) + [0]
            var index: Int32 = -1
            let status = wideText.withUnsafeBufferPointer { t in
                wideId.withUnsafeBufferPointer { i in
                    megapdf_add_text_box(page, -1, t.baseAddress, fontName, fontSize, x, y, i.baseAddress, &index)
                }
            }
            guard status == MEGAPDF_OK else { throw PdfError.editFailed }
        }
        return id
    }

    /// Every MegaPDF text box on the page, in page-object order.
    func textBoxes(_ document: PdfDocument, pageIndex: Int) throws -> [PdfTextBox] {
        try withCorePage(document, index: pageIndex) { page in
            guard let text = megapdf_text_load(page, UInt32(MEGAPDF_TEXT_BOXES_ONLY)) else { return [] }
            defer { megapdf_text_free(text) }
            var result: [PdfTextBox] = []
            for i in 0..<megapdf_text_run_count(text) {
                var r = megapdf_text_run()
                guard megapdf_text_run_get(text, i, &r) == MEGAPDF_OK else { continue }
                var id = Self.coreString(text, i, MEGAPDF_TEXT_RUN_BOX_ID)
                if id.isEmpty {
                    // A box written before the id param existed (shipping Windows 1.6.x).
                    // Its handle is its position, which the core's find understands too.
                    id = "\(Self.untaggedPrefix)\(r.object_index)"
                }
                var face = Self.coreString(text, i, MEGAPDF_TEXT_RUN_BOX_FONT)
                if face.isEmpty { face = Self.defaultFont }   // every box written before #43
                result.append(PdfTextBox(
                    id: id, objectIndex: Int(r.object_index),
                    text: Self.coreString(text, i, MEGAPDF_TEXT_RUN_TEXT),
                    rect: PdfRect(left: r.bounds.left, bottom: r.bounds.bottom,
                                  right: r.bounds.right, top: r.bounds.top),
                    fontSize: r.font_size,
                    fontName: face))
            }
            return result
        }
    }

    /// Translates the box with the given id so its lower-left corner lands on the
    /// crop-space point (`x`, `y`). Scale and rotation are left untouched.
    func moveTextBox(_ document: PdfDocument, pageIndex: Int, id: String,
                     x: Double, y: Double) throws {
        try withCorePage(document, index: pageIndex) { page in
            let wide = Array(id.utf16) + [0]
            let index = wide.withUnsafeBufferPointer { megapdf_find_text_box(page, $0.baseAddress) }
            guard index >= 0, megapdf_move_text_box(page, index, x, y) == MEGAPDF_OK else {
                throw PdfError.editFailed
            }
        }
    }

    /// Removes the box with the given id. Silently succeeds if it is already gone,
    /// so an undo that races a re-render cannot throw.
    func removeTextBox(_ document: PdfDocument, pageIndex: Int, id: String) throws {
        try withCorePage(document, index: pageIndex) { page in
            let wide = Array(id.utf16) + [0]
            let status = wide.withUnsafeBufferPointer { megapdf_remove_text_box(page, $0.baseAddress) }
            guard status == MEGAPDF_OK else { throw PdfError.editFailed }
        }
    }

    /// Prefix for the handle given to a marked box that carries no id.
    static let untaggedPrefix = "text:untagged#"

    // MARK: - internals

    private static func coreString(_ text: OpaquePointer, _ index: Int, _ field: megapdf_text_field) -> String {
        let n = megapdf_text_run_string(text, index, field, nil, 0)
        guard n > 0 else { return "" }
        var units = [UInt16](repeating: 0, count: n)
        _ = units.withUnsafeMutableBufferPointer { megapdf_text_run_string(text, index, field, $0.baseAddress, n) }
        return String(utf16CodeUnits: units, count: n)
    }
}
