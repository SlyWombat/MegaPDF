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

/// Adding or removing a text box (#34).
final class TextBoxOperation: PdfEditOperation {
    let pageIndex: Int
    private let id: String
    private let text: String
    private let fontSize: Double
    private let x: Double
    private let y: Double
    private let adding: Bool

    init(pageIndex: Int, id: String, text: String, fontSize: Double,
         x: Double, y: Double, adding: Bool) {
        self.pageIndex = pageIndex
        self.id = id
        self.text = text
        self.fontSize = fontSize
        self.x = x
        self.y = y
        self.adding = adding
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
        try await engine.addTextBox(document, pageIndex: pageIndex, text: text,
                                    fontSize: fontSize, x: x, y: y, id: id)
    }

    private func remove(_ engine: PdfEngine, _ document: PdfDocument) async throws {
        try await engine.removeTextBox(document, pageIndex: pageIndex, id: id)
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
