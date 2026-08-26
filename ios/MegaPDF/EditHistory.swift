import Foundation

// Undo/redo (#34) — the mobile port of the desktop `IEditOperation` + `UndoStack`
// (SDD §4.2, command pattern). Two rules make it safe on a document that other
// edits are reshaping underneath it:
//
//   1. **Operations address their target by id, never by index.** Annotation and
//      page-object indices shift as things are added and removed; `MegaPDF_Id`
//      and the text box's mark id do not.
//   2. **Revert restores the same id.** Undoing a delete and redoing it must not
//      invent a new handle, or a second undo would miss.

/// A reversible edit. Reference type: an operation may learn things when applied
/// (an image read back before deletion) that its revert needs.
protocol PdfEditOperation: AnyObject {
    /// Plain-language name for the UI ("Undo mark"), per SDD §2.2.
    var name: String { get }
    var pageIndex: Int { get }
    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws
    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws
}

/// Bounded undo/redo stack. Mobile documents are single-session, so the desktop's
/// crash-recovery journal has no counterpart here — only the in-memory history.
@MainActor
final class EditHistory {
    static let capacity = 200

    private var done: [PdfEditOperation] = []
    private var undone: [PdfEditOperation] = []

    var canUndo: Bool { !done.isEmpty }
    var canRedo: Bool { !undone.isEmpty }
    var undoName: String? { done.last?.name }
    var redoName: String? { undone.last?.name }

    /// Applies the operation and records it, clearing the redo history.
    func perform(_ operation: PdfEditOperation,
                 _ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await operation.apply(engine, document)
        done.append(operation)
        if done.count > Self.capacity { done.removeFirst() }
        undone.removeAll()
    }

    /// Reverts the last operation; returns the page that needs re-rendering.
    func undo(_ engine: PdfEngine, _ document: PdfDocument) async throws -> Int? {
        guard let operation = done.popLast() else { return nil }
        do {
            try await operation.revert(engine, document)
        } catch {
            done.append(operation)   // keep the history honest if the revert failed
            throw error
        }
        undone.append(operation)
        return operation.pageIndex
    }

    /// Re-applies the last undone operation; returns the page that needs re-rendering.
    func redo(_ engine: PdfEngine, _ document: PdfDocument) async throws -> Int? {
        guard let operation = undone.popLast() else { return nil }
        do {
            try await operation.apply(engine, document)
        } catch {
            undone.append(operation)
            throw error
        }
        done.append(operation)
        return operation.pageIndex
    }

    func clear() {
        done.removeAll()
        undone.removeAll()
    }
}

// MARK: - Operations

/// Marking a drawn square, or clearing a mark — one type, because they are each
/// other's inverse. `square` is the detected square in crop space; the mark drawn
/// inside it is inset 10% per side (SDD §6.2).
final class MarkOperation: PdfEditOperation {
    let pageIndex: Int
    private let square: PdfRect
    private let id: String
    private let adding: Bool

    init(pageIndex: Int, square: PdfRect, id: String, adding: Bool) {
        self.pageIndex = pageIndex
        self.square = square
        self.id = id
        self.adding = adding
    }

    /// Rebuilds the detected square from a placed mark's rect. `addCheckMark`
    /// insets 10% per side, so the mark is 80% of the square, concentric.
    static func square(fromMark rect: PdfRect) -> PdfRect {
        let cx = (rect.left + rect.right) / 2, cy = (rect.bottom + rect.top) / 2
        let w = (rect.right - rect.left) / 0.8, h = (rect.top - rect.bottom) / 0.8
        return PdfRect(left: cx - w / 2, bottom: cy - h / 2,
                       right: cx + w / 2, top: cy + h / 2)
    }

    var name: String { adding ? "mark" : "clear mark" }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        if adding {
            try await add(engine, document)
        } else {
            try await remove(engine, document)
        }
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        if adding {
            try await remove(engine, document)
        } else {
            try await add(engine, document)
        }
    }

    private func add(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await engine.addCheckMark(document, pageIndex: pageIndex, square: square, id: id)
    }

    private func remove(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await engine.removeAnnot(document, pageIndex: pageIndex, id: id)
    }
}

/// Toggling an AcroForm checkbox or radio button. A toggle is its own inverse,
/// exactly as on the desktop (`CheckboxToggleOperation`).
final class FieldToggleOperation: PdfEditOperation {
    let pageIndex: Int
    private let x: Double
    private let y: Double

    init(pageIndex: Int, x: Double, y: Double) {
        self.pageIndex = pageIndex
        self.x = x
        self.y = y
    }

    var name: String { "checkbox" }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await engine.clickAt(document, pageIndex: pageIndex, x: x, y: y)
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await apply(engine, document)
    }
}

/// Placing or removing a signature stamp. The pixels are held so an undone
/// removal can put the same image back under the same id.
final class StampOperation: PdfEditOperation {
    let pageIndex: Int
    private let id: String
    private let pixels: [UInt32]
    private let pixelWidth: Int
    private let pixelHeight: Int
    private let rect: PdfRect
    private let adding: Bool

    init(pageIndex: Int, id: String, pixels: [UInt32], pixelWidth: Int, pixelHeight: Int,
         rect: PdfRect, adding: Bool) {
        self.pageIndex = pageIndex
        self.id = id
        self.pixels = pixels
        self.pixelWidth = pixelWidth
        self.pixelHeight = pixelHeight
        self.rect = rect
        self.adding = adding
    }

    var name: String { adding ? "signature" : "remove signature" }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        if adding {
            try await add(engine, document)
        } else {
            try await remove(engine, document)
        }
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        if adding {
            try await remove(engine, document)
        } else {
            try await add(engine, document)
        }
    }

    private func add(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await engine.addImageStamp(document, pageIndex: pageIndex, pixels: pixels,
                                       pixelWidth: pixelWidth, pixelHeight: pixelHeight,
                                       rect: rect, id: id)
    }

    private func remove(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await engine.removeAnnot(document, pageIndex: pageIndex, id: id)
    }
}

/// Moving or resizing a placed stamp: remove and re-place under the same id, which
/// is what the drag already does — this only makes it reversible.
final class MoveStampOperation: PdfEditOperation {
    let pageIndex: Int
    private let id: String
    private let pixels: [UInt32]
    private let pixelWidth: Int
    private let pixelHeight: Int
    private let from: PdfRect
    private let to: PdfRect

    init(pageIndex: Int, id: String, pixels: [UInt32], pixelWidth: Int, pixelHeight: Int,
         from: PdfRect, to: PdfRect) {
        self.pageIndex = pageIndex
        self.id = id
        self.pixels = pixels
        self.pixelWidth = pixelWidth
        self.pixelHeight = pixelHeight
        self.from = from
        self.to = to
    }

    var name: String { "move signature" }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await place(engine, document, at: to)
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await place(engine, document, at: from)
    }

    private func place(_ engine: PdfEngine, _ document: PdfDocument, at rect: PdfRect) async throws {
        try await engine.removeAnnot(document, pageIndex: pageIndex, id: id)
        try await engine.addImageStamp(document, pageIndex: pageIndex, pixels: pixels,
                                       pixelWidth: pixelWidth, pixelHeight: pixelHeight,
                                       rect: rect, id: id)
    }
}

/// Places a text box so its **bounds** lower-left lands on (`x`, `y`).
///
/// `addTextBox` puts the text *baseline* on the point given — right for the tap
/// that creates a box, since the text should sit on the printed rule the user
/// tapped. But `textBoxes()` reports **bounds**, and `moveTextBox` anchors
/// bounds. So any operation that has to put a box back where a rect said it was
/// must normalize through a move: adding at the reported rect alone leaves the
/// box a descender's depth too high, and undo would not restore the position.
private func placeTextBox(_ engine: PdfEngine, _ document: PdfDocument, pageIndex: Int,
                          id: String, style: TextBoxStyle,
                          x: Double, y: Double) async throws {
    try await engine.addTextBox(document, pageIndex: pageIndex, text: style.text,
                                fontSize: style.fontSize, x: x, y: y, id: id,
                                fontName: style.fontName)
    try await engine.moveTextBox(document, pageIndex: pageIndex, id: id, x: x, y: y)
}

/// How a text box is styled: what it says, how big, in which face.
struct TextBoxStyle: Equatable {
    var text: String
    var fontSize: Double = 12
    var fontName: String = PdfEngine.defaultFont
}

/// Adding or removing a text box (#34).
///
/// `boundsAnchored` picks what (`x`, `y`) means: false for the tap that creates a
/// box (baseline, so the text sits on the tapped rule), true when the coordinates
/// came from a box's reported rect — a removal, whose undo has to put the box
/// back exactly where it was.
final class TextBoxOperation: PdfEditOperation {
    let pageIndex: Int
    private let id: String
    private let text: String
    private let fontSize: Double
    private let x: Double
    private let y: Double
    private let adding: Bool
    private let boundsAnchored: Bool
    private let fontName: String

    init(pageIndex: Int, id: String, text: String, fontSize: Double,
         x: Double, y: Double, adding: Bool, boundsAnchored: Bool = false,
         fontName: String = PdfEngine.defaultFont) {
        self.pageIndex = pageIndex
        self.id = id
        self.text = text
        self.fontSize = fontSize
        self.x = x
        self.y = y
        self.adding = adding
        self.boundsAnchored = boundsAnchored
        self.fontName = fontName
    }

    var name: String { adding ? "text" : "remove text" }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        if adding {
            try await add(engine, document)
        } else {
            try await remove(engine, document)
        }
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        if adding {
            try await remove(engine, document)
        } else {
            try await add(engine, document)
        }
    }

    private func add(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        if boundsAnchored {
            try await placeTextBox(
                engine, document, pageIndex: pageIndex, id: id,
                style: TextBoxStyle(text: text, fontSize: fontSize, fontName: fontName),
                x: x, y: y)
        } else {
            try await engine.addTextBox(document, pageIndex: pageIndex, text: text,
                                        fontSize: fontSize, x: x, y: y, id: id,
                                        fontName: fontName)
        }
    }

    private func remove(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await engine.removeTextBox(document, pageIndex: pageIndex, id: id)
    }
}

/// Restyling a placed box (#36 for the text, #43 for the size and face) — remove
/// and re-add under the same id, anchored to the rect it occupied.
///
/// Anything about the box's appearance can change at once, and all of it is one
/// undo. The box's width and height change with the new style; its lower-left
/// corner does not, so the box stays where the user put it. That anchor choice
/// matters more for a size change than for a typo fix: going 12 pt → 18 pt grows
/// the glyphs, and the box grows upward from the corner it sits on — right for
/// text sitting on a printed rule.
final class EditTextBoxOperation: PdfEditOperation {
    let pageIndex: Int
    private let id: String
    private let from: TextBoxStyle
    private let to: TextBoxStyle
    private let x: Double
    private let y: Double

    init(pageIndex: Int, id: String, from: TextBoxStyle, to: TextBoxStyle,
         x: Double, y: Double) {
        self.pageIndex = pageIndex
        self.id = id
        self.from = from
        self.to = to
        self.x = x
        self.y = y
    }

    var name: String { from.text == to.text ? "restyle text" : "edit text" }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await replace(engine, document, with: to)
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await replace(engine, document, with: from)
    }

    private func replace(_ engine: PdfEngine, _ document: PdfDocument,
                         with style: TextBoxStyle) async throws {
        try await engine.removeTextBox(document, pageIndex: pageIndex, id: id)
        try await placeTextBox(engine, document, pageIndex: pageIndex, id: id,
                               style: style, x: x, y: y)
    }
}

/// Moving a text box to a new crop-space corner.
final class MoveTextBoxOperation: PdfEditOperation {
    let pageIndex: Int
    private let id: String
    private let from: (x: Double, y: Double)
    private let to: (x: Double, y: Double)

    init(pageIndex: Int, id: String, from: (x: Double, y: Double), to: (x: Double, y: Double)) {
        self.pageIndex = pageIndex
        self.id = id
        self.from = from
        self.to = to
    }

    var name: String { "move text" }

    func apply(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await engine.moveTextBox(document, pageIndex: pageIndex, id: id, x: to.x, y: to.y)
    }

    func revert(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await engine.moveTextBox(document, pageIndex: pageIndex, id: id, x: from.x, y: from.y)
    }
}
