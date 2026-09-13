import CPdfium
import Foundation

// Editing text that is already in the document (#113). The policy — which
// runs make a visual line, whether the run's own font can carry the new text
// or the closest standard face has to stand in, and the byte-identical undo —
// lives in the shared core (#106, #109, #112, #116). This file only marshals.

/// One run of body text: a single text object on the page, crop space.
struct PdfTextRun: Equatable {
    let objectIndex: Int
    /// What the run says. PDFium's generated separator before the next run on the
    /// line is trimmed off, so this is the text a user would see and retype.
    let text: String
    let rect: PdfRect
    let fontSize: Double
    let fontName: String
    let isTextBox: Bool
    /// Whether the page reads a separator after this run — PDFium's generated space
    /// before the next word. Runs split mid-word (kerning, font changes) carry none.
    let endsWithSeparator: Bool
}

/// A visual line: same-baseline runs, left to right, as the core merges them.
struct PdfTextLine: Equatable {
    let runs: [PdfTextRun]
    let rect: PdfRect

    /// The line as one string: runs joined with a space only where the page separates them.
    var text: String {
        var out = ""
        for (i, run) in runs.enumerated() {
            out += run.text
            if run.endsWithSeparator && i < runs.count - 1 { out += " " }
        }
        return out
    }
}

/// How an edit landed.
enum PdfTextEditOutcome: Equatable {
    /// Tier 1: the document's own font drew the new text.
    case inPlace
    /// Tier 2: that font could not carry it, so a similar standard face was used.
    case substituted
}

/// A page object removed from its page and kept alive by the core for undo.
/// Restoring consumes it; the document frees any still held when it closes.
final class PdfDetachedObject {
    fileprivate var handle: OpaquePointer?
    fileprivate init(handle: OpaquePointer) { self.handle = handle }
}

extension PdfEngine {

    /// The page's visual lines of body text, top to bottom. MegaPDF text boxes
    /// are left out — they have their own editing path (#36).
    func textLines(_ document: PdfDocument, pageIndex: Int) throws -> [PdfTextLine] {
        try withCorePage(document, index: pageIndex) { page in
            guard let text = megapdf_text_load(page, UInt32(MEGAPDF_TEXT_ALL)) else { return [] }
            defer { megapdf_text_free(text) }

            var runs: [PdfTextRun] = []
            for i in 0..<megapdf_text_run_count(text) {
                var r = megapdf_text_run()
                guard megapdf_text_run_get(text, i, &r) == MEGAPDF_OK else {
                    runs.append(PdfTextRun(objectIndex: -1, text: "", rect: PdfRect(left: 0, bottom: 0, right: 0, top: 0),
                                           fontSize: 0, fontName: "", isTextBox: false, endsWithSeparator: false))
                    continue
                }
                let raw = Self.runString(text, i, MEGAPDF_TEXT_RUN_TEXT)
                runs.append(PdfTextRun(
                    objectIndex: Int(r.object_index),
                    text: raw.trimmingCharacters(in: .whitespacesAndNewlines),
                    rect: PdfRect(left: r.bounds.left, bottom: r.bounds.bottom,
                                  right: r.bounds.right, top: r.bounds.top),
                    fontSize: r.font_size,
                    fontName: Self.runString(text, i, MEGAPDF_TEXT_RUN_FONT),
                    isTextBox: r.is_text_box != 0,
                    endsWithSeparator: raw.last.map { $0.isWhitespace } ?? false))
            }

            var lines: [PdfTextLine] = []
            for j in 0..<megapdf_text_line_count(text) {
                let n = megapdf_text_line_runs(text, j, nil, 0)
                var indices = [Int](repeating: 0, count: n)
                _ = indices.withUnsafeMutableBufferPointer { megapdf_text_line_runs(text, j, $0.baseAddress, n) }
                let members = indices.map { runs[$0] }.filter { !$0.isTextBox && $0.objectIndex >= 0 }
                guard !members.isEmpty else { continue }
                var b = megapdf_rect()
                megapdf_text_line_get(text, j, &b)
                lines.append(PdfTextLine(runs: members,
                                         rect: PdfRect(left: b.left, bottom: b.bottom, right: b.right, top: b.top)))
            }
            return lines
        }
    }

    /// Replaces the text of the run at `objectIndex`. The edited run is a new object at
    /// the same index; the untouched original comes back for undo, which puts it back
    /// byte-identical with `restoreOriginal` whichever tier the edit took (#117).
    /// Throws `PdfError.editFailed` when the object is no longer text, or when not even
    /// a standard face can draw the new text.
    func setText(_ document: PdfDocument, pageIndex: Int, objectIndex: Int,
                 text: String) throws -> (outcome: PdfTextEditOutcome, original: PdfDetachedObject) {
        guard !text.isEmpty else { throw PdfError.editFailed }
        return try withCorePage(document, index: pageIndex) { page in
            let wide = Array(text.utf16) + [0]
            var outcome: Int32 = -1
            var replaced: OpaquePointer?
            let status = wide.withUnsafeBufferPointer {
                megapdf_set_text(page, Int32(objectIndex), $0.baseAddress, 0, &outcome, &replaced)
            }
            if status == MEGAPDF_ERR_LAYOUT { throw PdfError.layoutWouldChange }
            guard status == MEGAPDF_OK, let replaced else { throw PdfError.editFailed }
            return (outcome == MEGAPDF_EDIT_SUBSTITUTED ? .substituted : .inPlace,
                    PdfDetachedObject(handle: replaced))
        }
    }

    /// Whether the run at `objectIndex` can be changed without PDFium disturbing the rest of
    /// the page when it rewrites it (#118). Asked before the editor opens.
    func textEditable(_ document: PdfDocument, pageIndex: Int, objectIndex: Int) throws -> Bool {
        try withCorePage(document, index: pageIndex) { page in
            megapdf_text_editable(page, Int32(objectIndex)) == 1
        }
    }

    /// Undoes `setText`: takes the edited run at `objectIndex` off the page and puts the
    /// original back where it was.
    func restoreOriginal(_ document: PdfDocument, pageIndex: Int,
                         _ original: PdfDetachedObject, objectIndex: Int) throws {
        guard let handle = original.handle else { throw PdfError.editFailed }
        try withCorePage(document, index: pageIndex) { page in
            guard let edited = megapdf_detach_object(page, Int32(objectIndex)) else { throw PdfError.editFailed }
            megapdf_discard_detached(edited)
            guard megapdf_restore_object(page, handle, Int32(objectIndex)) == MEGAPDF_OK else {
                throw PdfError.editFailed
            }
            original.handle = nil
        }
    }

    /// Removes the page object at `objectIndex` and keeps it for undo.
    func detachObject(_ document: PdfDocument, pageIndex: Int,
                      objectIndex: Int) throws -> PdfDetachedObject {
        try withCorePage(document, index: pageIndex) { page in
            guard let handle = megapdf_detach_object(page, Int32(objectIndex)) else {
                throw PdfError.editFailed
            }
            return PdfDetachedObject(handle: handle)
        }
    }

    /// Puts a detached object back at `objectIndex`, byte-identical.
    func restoreObject(_ document: PdfDocument, pageIndex: Int,
                       _ detached: PdfDetachedObject, objectIndex: Int) throws {
        guard let handle = detached.handle else { throw PdfError.editFailed }
        try withCorePage(document, index: pageIndex) { page in
            guard megapdf_restore_object(page, handle, Int32(objectIndex)) == MEGAPDF_OK else {
                throw PdfError.editFailed
            }
            detached.handle = nil   // the page owns it again
        }
    }

    /// Frees a detached object that will never be restored.
    func discardDetached(_ detached: PdfDetachedObject) {
        guard let handle = detached.handle else { return }
        megapdf_discard_detached(handle)
        detached.handle = nil
    }

    private static func runString(_ text: OpaquePointer, _ index: Int, _ field: megapdf_text_field) -> String {
        let n = megapdf_text_run_string(text, index, field, nil, 0)
        guard n > 0 else { return "" }
        var units = [UInt16](repeating: 0, count: n)
        _ = units.withUnsafeMutableBufferPointer { megapdf_text_run_string(text, index, field, $0.baseAddress, n) }
        return String(utf16CodeUnits: units, count: n)
    }
}
