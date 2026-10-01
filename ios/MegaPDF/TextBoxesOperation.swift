import Foundation

// A note of more than one line (#4) — the phone counterpart of the desktops'
// `AddTextBoxesOperation` (#535, #564) and of Android's (#565).
//
// There is no multi-line text object in the format this app writes: a "line" of added text
// was always the whole of what the single-line operation placed. So a two-line note is two
// ordinary text boxes, stacked, made and taken back as **one** history entry. That is also
// what keeps #4's acceptance test true — "each line remains individually editable
// afterwards" — because each line never stopped being an ordinary, separately-selectable box.

/// Folds every line ending a text view might hand back into one form, and drops the lines
/// that are only whitespace.
///
/// **Measured, not assumed.** Windows' `TextBox` handed back a lone `"\r"` and Android's field
/// needed the same fold; both platforms shipped a check that caught it, because without the
/// fold a multi-line note silently stayed *one* text box with a control character inside it
/// (#564, #565). Two platforms in a row makes it a known trap rather than a surprise. On iOS
/// the field is SwiftUI's own, and `MultilineTextUITests` reads the code points an actual
/// running app hands back rather than trusting this comment.
///
/// `U+2028` and `U+2029` are in the list for a reason of this platform's own: a UIKit text
/// view inserts LINE SEPARATOR rather than a newline on some soft-return paths — and a paste
/// from another app carries whatever that app used, whichever way the keyboard behaves.
func textBoxLines(_ text: String) -> [String] {
    text.replacingOccurrences(of: "\r\n", with: "\n")
        .replacingOccurrences(of: "\r", with: "\n")
        .replacingOccurrences(of: "\u{2028}", with: "\n")
        .replacingOccurrences(of: "\u{2029}", with: "\n")
        .split(separator: "\n", omittingEmptySubsequences: false)
        .map { $0.trimmingCharacters(in: .whitespaces) }
        .filter { !$0.isEmpty }
}

/// Places a note of several lines as one undoable step.
///
/// The tap point is the **first line's baseline**, as it is for a single box, and each line
/// after it drops by `leading` — 1.2× the chosen size, the same stack Android uses, so a note
/// reads the same on both phones. Ids are minted up front and the boxes are addressed by them,
/// so unlike a whiteout nothing here needs re-anchoring: an id outlives every index shift.
final class AddTextBoxesOperation: PdfEditOperation {
    let pageIndex: Int
    private let lines: [String]
    private let ids: [String]
    private let style: TextBoxStyle
    private let x: Double
    private let baselineY: Double

    /// The gap between one line's baseline and the next.
    static func leading(forSize size: Double) -> Double { size * 1.2 }

    init(pageIndex: Int, lines: [String], ids: [String], style: TextBoxStyle,
         x: Double, y: Double) {
        self.pageIndex = pageIndex
        self.lines = lines
        self.ids = ids
        self.style = style
        self.x = x
        self.baselineY = y
    }

    /// One name for the whole note: Undo takes back the gesture, not a line of it.
    var name: String { "text" }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        guard lines.count == ids.count, !lines.isEmpty else { throw PdfError.editFailed }
        let leading = Self.leading(forSize: style.fontSize)
        for (i, line) in lines.enumerated() {
            try await engine.addTextBox(document, pageIndex: pageIndex, text: line,
                                        fontSize: style.fontSize, x: x,
                                        y: baselineY - Double(i) * leading,
                                        id: ids[i], fontName: style.fontName)
        }
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        // Backwards, so each removal is of the last box added and the indices of the ones
        // still to go are untouched. They are addressed by id, so this is belt and braces —
        // but it costs nothing and it is the order the apply has to be undone in.
        for id in ids.reversed() {
            try await engine.removeTextBox(document, pageIndex: pageIndex, id: id)
        }
    }
}
