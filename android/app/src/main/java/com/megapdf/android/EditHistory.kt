package com.megapdf.android

import com.megapdf.engine.PdfDocument
import com.megapdf.engine.PdfRect

// Undo/redo (#34) — the mobile port of the desktop `IEditOperation` + `UndoStack`
// (SDD §4.2, command pattern), and the twin of iOS's EditHistory.swift. Two rules
// make it safe on a document other edits are reshaping underneath it:
//
//   1. Operations address their target by id, never by index. Annotation and
//      page-object indices shift; `MegaPDF_Id` and the text box mark id do not.
//   2. Revert restores the same id, so a second undo still finds it.

/** A reversible edit. */
interface PdfEditOperation {
    /** Plain-language name for the UI ("Undo mark"), per SDD §2.2. */
    val name: String
    val pageIndex: Int
    suspend fun apply(doc: PdfDocument)
    suspend fun revert(doc: PdfDocument)
}

/** Bounded undo/redo stack. Single-session, so there is no recovery journal. */
class EditHistory(private val capacity: Int = 200) {
    private val done = ArrayDeque<PdfEditOperation>()
    private val undone = ArrayDeque<PdfEditOperation>()

    val canUndo: Boolean get() = done.isNotEmpty()
    val canRedo: Boolean get() = undone.isNotEmpty()
    val undoName: String? get() = done.lastOrNull()?.name
    val redoName: String? get() = undone.lastOrNull()?.name

    /** Applies the operation and records it, clearing the redo history. */
    suspend fun perform(operation: PdfEditOperation, doc: PdfDocument) {
        operation.apply(doc)
        done.addLast(operation)
        if (done.size > capacity) done.removeFirst()
        undone.clear()
    }

    /** Reverts the last operation; returns the page that needs re-rendering. */
    suspend fun undo(doc: PdfDocument): Int? {
        val operation = done.removeLastOrNull() ?: return null
        try {
            operation.revert(doc)
        } catch (e: Exception) {
            done.addLast(operation)   // keep the history honest if the revert failed
            throw e
        }
        undone.addLast(operation)
        return operation.pageIndex
    }

    /** Re-applies the last undone operation; returns the page to re-render. */
    suspend fun redo(doc: PdfDocument): Int? {
        val operation = undone.removeLastOrNull() ?: return null
        try {
            operation.apply(doc)
        } catch (e: Exception) {
            undone.addLast(operation)
            throw e
        }
        done.addLast(operation)
        return operation.pageIndex
    }

    fun clear() {
        done.clear()
        undone.clear()
    }
}

private suspend fun <T> PdfDocument.onPage(index: Int, body: suspend (com.megapdf.engine.PdfPage) -> T): T {
    val page = openPage(index)
    try {
        return body(page)
    } finally {
        page.close()
    }
}

/**
 * Marking a drawn square, or clearing a mark — one type, because they are each
 * other's inverse. [square] is the detected square; the mark drawn inside it is
 * inset 10% per side (SDD §6.2).
 */
class MarkOperation(
    override val pageIndex: Int,
    private val square: PdfRect,
    private val id: String,
    private val adding: Boolean,
) : PdfEditOperation {

    override val name: String get() = if (adding) "mark" else "clear mark"

    override suspend fun apply(doc: PdfDocument) = if (adding) add(doc) else remove(doc)
    override suspend fun revert(doc: PdfDocument) = if (adding) remove(doc) else add(doc)

    private suspend fun add(doc: PdfDocument) = doc.onPage(pageIndex) { it.addCheckMark(square, id) }
    private suspend fun remove(doc: PdfDocument) = doc.onPage(pageIndex) { it.removeAnnot(id) }

    companion object {
        /**
         * Rebuilds the detected square from a placed mark's rect: `addCheckMark`
         * insets 10% per side, so the mark is 80% of the square, concentric.
         */
        fun squareFromMark(rect: PdfRect): PdfRect {
            val cx = (rect.left + rect.right) / 2
            val cy = (rect.bottom + rect.top) / 2
            val w = (rect.right - rect.left) / 0.8
            val h = (rect.top - rect.bottom) / 0.8
            return PdfRect(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2)
        }
    }
}

/** Toggling an AcroForm checkbox or radio button — its own inverse. */
class FieldToggleOperation(
    override val pageIndex: Int,
    private val x: Double,
    private val y: Double,
) : PdfEditOperation {

    override val name: String get() = "checkbox"

    override suspend fun apply(doc: PdfDocument) = doc.onPage(pageIndex) { it.clickAt(x, y) }
    override suspend fun revert(doc: PdfDocument) = apply(doc)
}

/** Placing or removing a signature stamp; the pixels let an undone removal return. */
class StampOperation(
    override val pageIndex: Int,
    private val id: String,
    private val pixels: IntArray,
    private val pixelWidth: Int,
    private val pixelHeight: Int,
    private val rect: PdfRect,
    private val adding: Boolean,
) : PdfEditOperation {

    override val name: String get() = if (adding) "signature" else "remove signature"

    override suspend fun apply(doc: PdfDocument) = if (adding) add(doc) else remove(doc)
    override suspend fun revert(doc: PdfDocument) = if (adding) remove(doc) else add(doc)

    private suspend fun add(doc: PdfDocument) = doc.onPage(pageIndex) {
        it.addImageStamp(pixels, pixelWidth, pixelHeight, rect, id)
    }
    private suspend fun remove(doc: PdfDocument) = doc.onPage(pageIndex) { it.removeAnnot(id) }
}

/** Moving or resizing a placed stamp: remove and re-place under the same id. */
class MoveStampOperation(
    override val pageIndex: Int,
    private val id: String,
    private val pixels: IntArray,
    private val pixelWidth: Int,
    private val pixelHeight: Int,
    private val from: PdfRect,
    private val to: PdfRect,
) : PdfEditOperation {

    override val name: String get() = "move signature"

    override suspend fun apply(doc: PdfDocument) = place(doc, to)
    override suspend fun revert(doc: PdfDocument) = place(doc, from)

    private suspend fun place(doc: PdfDocument, rect: PdfRect) = doc.onPage(pageIndex) {
        it.removeAnnot(id)
        it.addImageStamp(pixels, pixelWidth, pixelHeight, rect, id)
    }
}

/**
 * Places a text box so its **bounds** lower-left lands on ([x], [y]).
 *
 * `addTextBox` puts the text *baseline* on the point given — right for the tap
 * that creates a box, since the text should sit on the printed rule the user
 * tapped. But `textBoxes()` reports **bounds**, and `moveTextBox` anchors
 * bounds. So any operation that has to put a box back where a rect said it was
 * must normalize through a move: adding at the reported rect alone leaves the
 * box a descender's depth too high, and undo would not restore the position.
 */
private suspend fun PdfDocument.placeTextBoxAt(
    pageIndex: Int, id: String, text: String, fontSize: Double, x: Double, y: Double,
) = onPage(pageIndex) {
    it.addTextBox(text, fontSize, x, y, id)
    it.moveTextBox(id, x, y)
}

/**
 * Adding or removing a text box (#34).
 *
 * [boundsAnchored] picks what ([x], [y]) means: false for the tap that creates a
 * box (baseline, so the text sits on the tapped rule), true when the coordinates
 * came from a box's reported rect — a removal, whose undo has to put the box
 * back exactly where it was.
 */
class TextBoxOperation(
    override val pageIndex: Int,
    private val id: String,
    private val text: String,
    private val fontSize: Double,
    private val x: Double,
    private val y: Double,
    private val adding: Boolean,
    private val boundsAnchored: Boolean = false,
) : PdfEditOperation {

    override val name: String get() = if (adding) "text" else "remove text"

    override suspend fun apply(doc: PdfDocument) = if (adding) add(doc) else remove(doc)
    override suspend fun revert(doc: PdfDocument) = if (adding) remove(doc) else add(doc)

    private suspend fun add(doc: PdfDocument) =
        if (boundsAnchored) doc.placeTextBoxAt(pageIndex, id, text, fontSize, x, y)
        else doc.onPage(pageIndex) { it.addTextBox(text, fontSize, x, y, id) }

    private suspend fun remove(doc: PdfDocument) = doc.onPage(pageIndex) { it.removeTextBox(id) }
}

/**
 * Correcting the text of a placed box (#36) — remove and re-add under the same
 * id, anchored to the rect it occupied. The width changes with the new text;
 * the lower-left corner does not, so the correction stays where the user put it.
 */
class EditTextBoxOperation(
    override val pageIndex: Int,
    private val id: String,
    private val oldText: String,
    private val newText: String,
    private val fontSize: Double,
    private val x: Double,
    private val y: Double,
) : PdfEditOperation {

    override val name: String get() = "edit text"

    override suspend fun apply(doc: PdfDocument) = replace(doc, newText)
    override suspend fun revert(doc: PdfDocument) = replace(doc, oldText)

    // One page load for the whole swap, as MoveStampOperation does — pdfium has
    // no in-place text edit, so replacing the text means rebuilding the object.
    private suspend fun replace(doc: PdfDocument, text: String) = doc.onPage(pageIndex) {
        it.removeTextBox(id)
        it.addTextBox(text, fontSize, x, y, id)
        it.moveTextBox(id, x, y)
    }
}

/** Moving a text box to a new lower-left corner. */
class MoveTextBoxOperation(
    override val pageIndex: Int,
    private val id: String,
    private val fromX: Double,
    private val fromY: Double,
    private val toX: Double,
    private val toY: Double,
) : PdfEditOperation {

    override val name: String get() = "move text"

    override suspend fun apply(doc: PdfDocument) =
        doc.onPage(pageIndex) { it.moveTextBox(id, toX, toY) }

    override suspend fun revert(doc: PdfDocument) =
        doc.onPage(pageIndex) { it.moveTextBox(id, fromX, fromY) }
}
