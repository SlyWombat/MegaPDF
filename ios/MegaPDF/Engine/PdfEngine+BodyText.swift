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

/// Which check of the layout guard refused (#128). Mirrors MEGAPDF_LAYOUT_* in megapdf_core.h.
enum PdfLayoutCause: Equatable {
    /// Editable: the rewrite changed nothing past the guard's budgets.
    case ok
    /// More than 0.05% of the page's pixels would look different.
    case render
    /// Some text object's bounds would move by more than 0.5 pt.
    case textMoved
    /// The page would have a different number of text objects, or different text.
    case textChanged
    /// PDFium could not rewrite, save or reopen the copy of the page.
    case rewriteFailed

    init(core cause: Int32) {
        // Compared the way the other core constants are (`status == MEGAPDF_ERR_LAYOUT`).
        if cause == MEGAPDF_LAYOUT_OK {
            self = .ok
        } else if cause == MEGAPDF_LAYOUT_RENDER {
            self = .render
        } else if cause == MEGAPDF_LAYOUT_TEXT_MOVED {
            self = .textMoved
        } else if cause == MEGAPDF_LAYOUT_TEXT_CHANGED {
            self = .textChanged
        } else {
            self = .rewriteFailed
        }
    }

    /// What the person is told: text elsewhere would move, or other parts of the page would look different.
    var notice: String {
        switch self {
        case .textMoved, .textChanged:
            return String(localized: "This line can't be changed without moving text elsewhere on the page.")
        case .render:
            return String(localized: "This line can't be changed without making other parts of the page look different.")
        case .ok, .rewriteFailed:
            return String(localized: "This page's text can't be changed without disturbing its layout.")
        }
    }
}

/// The layout guard's verdict on one text object (#118, #128), with the numbers the dry run saw.
struct PdfLayoutVerdict: Equatable {
    let editable: Bool
    let cause: PdfLayoutCause
    /// MEGAPDF_LAYOUT_WHERE_* bits: 1 on the judged text, 2 on other text, 4 off text.
    let areas: Int
    let changedPixels: Int
    let totalPixels: Int
    let maxShiftPoints: Double

    init(_ v: megapdf_layout_verdict) {
        editable = v.editable != 0
        cause = PdfLayoutCause(core: v.cause)
        areas = Int(v.`where`)
        changedPixels = Int(v.changed_pixels)
        totalPixels = Int(v.total_pixels)
        maxShiftPoints = v.max_shift_pt
    }
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
            if status == MEGAPDF_ERR_LAYOUT { throw PdfError.layoutWouldChange(Self.lastLayoutCause()) }
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

    /// `textEditable` with its reason (#128): the same cached dry run. nil when the object
    /// is not text, which `textEditable` answers with false.
    func layoutVerdict(_ document: PdfDocument, pageIndex: Int, objectIndex: Int) throws -> PdfLayoutVerdict? {
        try withCorePage(document, index: pageIndex) { page in
            var verdict = megapdf_layout_verdict()
            guard megapdf_text_editable_reason(page, Int32(objectIndex), &verdict) >= 0 else { return nil }
            return PdfLayoutVerdict(verdict)
        }
    }

    /// Whether regenerating the page's content, with nothing changed, would change how it looks
    /// (#139). Whiteouts, text boxes, signatures and removals regenerate the page too and are never
    /// refused; ask before the first such change on a page and warn when `editable` is false.
    /// The same dry run and budgets as `layoutVerdict`, cached per page until the page changes.
    /// nil when the page cannot be judged.
    func pageRegenerationVerdict(_ document: PdfDocument, pageIndex: Int) throws -> PdfLayoutVerdict? {
        try withCorePage(document, index: pageIndex) { page in
            var verdict = megapdf_layout_verdict()
            guard megapdf_page_regeneration_verdict(page, &verdict) >= 0 else { return nil }
            return PdfLayoutVerdict(verdict)
        }
    }

    /// Why the core's last call on this thread was refused by the layout guard. Read straight
    /// after the refused call, inside the same `withCorePage`.
    private static func lastLayoutVerdict() -> PdfLayoutVerdict {
        var verdict = megapdf_layout_verdict()
        _ = megapdf_last_layout_verdict(&verdict)
        return PdfLayoutVerdict(verdict)
    }

    private static func lastLayoutCause() -> PdfLayoutCause { lastLayoutVerdict().cause }

    /// Undoes `setText`: takes the edited run at `objectIndex` off the page and puts the
    /// original back where it was, with any hidden copy of the run the edit took along (#136).
    /// The core knows where each object goes; it refuses when the page is not as the edit left it.
    func restoreOriginal(_ document: PdfDocument, pageIndex: Int,
                         _ original: PdfDetachedObject, objectIndex: Int) throws {
        try restoreDetached(document, pageIndex: pageIndex, original)
    }

    /// Retypes a visual line (#136): `text` goes into the run at the first of `objectIndices`,
    /// the line's other runs leave the page, and so do the hidden copies a producer drew under
    /// any of them for fake bold, an outline or a shadow, all in one core call. Taking them one
    /// at a time would move the indices of those still to be taken. `restoreDetached` undoes it
    /// byte-identical.
    func setLineText(_ document: PdfDocument, pageIndex: Int, objectIndices: [Int],
                     text: String) throws -> (outcome: PdfTextEditOutcome, original: PdfDetachedObject) {
        guard !text.isEmpty, !objectIndices.isEmpty else { throw PdfError.editFailed }
        return try withCorePage(document, index: pageIndex) { page in
            let wide = Array(text.utf16) + [0]
            let indices = objectIndices.map { Int32($0) }
            var outcome: Int32 = -1
            var replaced: OpaquePointer?
            let status = wide.withUnsafeBufferPointer { chars in
                indices.withUnsafeBufferPointer {
                    megapdf_set_line_text(page, $0.baseAddress, $0.count, chars.baseAddress, 0, &outcome, &replaced)
                }
            }
            if status == MEGAPDF_ERR_LAYOUT { throw PdfError.layoutWouldChange(Self.lastLayoutCause()) }
            guard status == MEGAPDF_OK, let replaced else { throw PdfError.editFailed }
            return (outcome == MEGAPDF_EDIT_SUBSTITUTED ? .substituted : .inPlace,
                    PdfDetachedObject(handle: replaced))
        }
    }

    /// Removes a visual line's runs with the hidden copies drawn under them (#136), all at once,
    /// and keeps them for undo with `restoreDetached`.
    func detachTextRuns(_ document: PdfDocument, pageIndex: Int,
                        objectIndices: [Int]) throws -> PdfDetachedObject {
        guard !objectIndices.isEmpty else { throw PdfError.editFailed }
        return try withCorePage(document, index: pageIndex) { page in
            let indices = objectIndices.map { Int32($0) }
            let handle = indices.withUnsafeBufferPointer {
                megapdf_detach_text_runs(page, $0.baseAddress, $0.count)
            }
            guard let handle else {
                // A layout refusal says so on this thread (#128); anything else is a failed edit.
                let refusal = Self.lastLayoutVerdict()
                throw refusal.editable ? PdfError.editFailed : PdfError.layoutWouldChange(refusal.cause)
            }
            return PdfDetachedObject(handle: handle)
        }
    }

    /// Undoes `setLineText`, `setText` or `detachTextRuns`: every object goes back at its own
    /// index, after the edited run is taken off.
    func restoreDetached(_ document: PdfDocument, pageIndex: Int, _ detached: PdfDetachedObject) throws {
        guard let handle = detached.handle else { throw PdfError.editFailed }
        try withCorePage(document, index: pageIndex) { page in
            guard megapdf_restore_detached(page, handle) == MEGAPDF_OK else {
                throw PdfError.editFailed
            }
            detached.handle = nil   // the page owns them again
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
