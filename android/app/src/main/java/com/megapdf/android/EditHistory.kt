package com.megapdf.android

import com.megapdf.engine.PdfDocument
import com.megapdf.engine.DEFAULT_FONT
import com.megapdf.engine.PdfRect
import com.megapdf.engine.DetachedObject
import com.megapdf.engine.TextEditOutcome
import com.megapdf.engine.TextLine

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

    /**
     * True when applying this changes the file: the document becomes unsaved and the page is
     * re-rendered. False for the redaction marks (#329), which the core keeps and never
     * writes — so a mark must not make the document unsaved, must not write a journal entry,
     * and must not spend a re-render, because the overlay draw *is* the visible change.
     */
    val changesDocument: Boolean get() = true

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
        record(operation)
    }

    /**
     * Records an operation the caller has already applied.
     *
     * Marking for redaction is the one edit that cannot go through [perform]: it is the
     * core's *text selection* that decides which marks a drag makes and where, and it makes
     * them as it answers, so the caller applies it and hands back what it made (#329).
     */
    fun record(operation: PdfEditOperation) {
        done.addLast(operation)
        if (done.size > capacity) done.removeFirst()
        undone.clear()
    }

    /** Reverts the last operation; returns it — the caller needs to know if it changed the file. */
    suspend fun undo(doc: PdfDocument): PdfEditOperation? {
        val operation = done.removeLastOrNull() ?: return null
        try {
            operation.revert(doc)
        } catch (e: Exception) {
            done.addLast(operation)   // keep the history honest if the revert failed
            throw e
        }
        undone.addLast(operation)
        return operation
    }

    /** Re-applies the last undone operation; returns it. */
    suspend fun redo(doc: PdfDocument): PdfEditOperation? {
        val operation = undone.removeLastOrNull() ?: return null
        try {
            operation.apply(doc)
        } catch (e: Exception) {
            undone.addLast(operation)
            throw e
        }
        done.addLast(operation)
        return operation
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
    pageIndex: Int, id: String, text: String, fontSize: Double, fontName: String,
    x: Double, y: Double,
) = onPage(pageIndex) {
    it.addTextBox(text, fontSize, x, y, id, fontName)
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
    private val fontName: String = DEFAULT_FONT,
) : PdfEditOperation {

    override val name: String get() = if (adding) "text" else "remove text"

    override suspend fun apply(doc: PdfDocument) = if (adding) add(doc) else remove(doc)
    override suspend fun revert(doc: PdfDocument) = if (adding) remove(doc) else add(doc)

    private suspend fun add(doc: PdfDocument) =
        if (boundsAnchored) doc.placeTextBoxAt(pageIndex, id, text, fontSize, fontName, x, y)
        else doc.onPage(pageIndex) { it.addTextBox(text, fontSize, x, y, id, fontName) }

    private suspend fun remove(doc: PdfDocument) = doc.onPage(pageIndex) { it.removeTextBox(id) }
}

// ---- Redaction marks (#329) -------------------------------------------------
//
// A mark is the core's own and is never written to the file, so every operation here says
// `changesDocument = false`: a mark leaves the document clean, writes no journal entry and
// re-renders nothing — the overlay draw is the visible change. They are in the history
// because Undo has to be able to take a mark back, which is the whole of #329.
//
// Inverse pairs, one type each, as everywhere above.

/**
 * Marking areas for redaction, and removing them — each other's inverse.
 *
 * [rects] are the areas as they now exist in crop space: the marks a drag made, already
 * grown to whole glyphs by the core, or the marks being removed. [adding] says which way
 * round the operation goes.
 *
 * Redo replays the recorded rectangles rather than re-running the text selection: that
 * would re-derive glyph runs from the page as it is *now*, and the person is owed the
 * rectangle they saw. Re-marking a rectangle goes through the plain rect path — a mark is
 * an area, and the glyph snapping only decided what the area was. The core never reuses an
 * id for the life of a document, so a redo takes fresh ids; [ids] is what the page carries
 * at this moment and is re-read after every apply.
 */
class RedactMarkOperation(
    override val pageIndex: Int,
    private val rects: List<PdfRect>,
    ids: List<Int>,
    private val adding: Boolean,
) : PdfEditOperation {

    private var ids: List<Int> = ids

    override val name: String get() = if (adding) "redact" else "remove mark"
    override val changesDocument: Boolean get() = false

    override suspend fun apply(doc: PdfDocument) {
        if (adding) mark(doc) else remove(doc)
    }

    override suspend fun revert(doc: PdfDocument) {
        if (adding) remove(doc) else mark(doc)
    }

    private suspend fun mark(doc: PdfDocument) = doc.onPage(pageIndex) { page ->
        ids = rects.mapNotNull { rect -> page.markForRedaction(rect).takeIf { it >= 0 } }
    }

    private suspend fun remove(doc: PdfDocument) = doc.onPage(pageIndex) { page ->
        // Already gone counts as success on the core side, so an undo cannot fail.
        ids.forEach { page.removeRedactionMark(it) }
        ids = emptyList()
    }
}

/**
 * Moving or resizing a mark. The core moves an id in place, so undo and redo keep the same
 * id — which is why this is not the remove-and-re-place that [MoveStampOperation] needs.
 */
class MoveRedactionMarkOperation(
    override val pageIndex: Int,
    private val markId: Int,
    private val from: PdfRect,
    private val to: PdfRect,
) : PdfEditOperation {

    override val name: String get() = "move mark"
    override val changesDocument: Boolean get() = false

    override suspend fun apply(doc: PdfDocument) {
        doc.onPage(pageIndex) { it.moveRedactionMark(markId, to) }
    }

    override suspend fun revert(doc: PdfDocument) {
        doc.onPage(pageIndex) { it.moveRedactionMark(markId, from) }
    }
}

/**
 * Clearing every mark on the document, as **one** undo step: a person who says "clear all
 * marks" means one action, not one per mark or one per page, and Undo puts every one of
 * them back where it was.
 *
 * [marksByPage] is the whole document's marks as they were. [pageIndex] is the page the UI
 * treats as this operation's own — a clear can span pages, and the history wants one page.
 */
class ClearRedactionMarksOperation(
    override val pageIndex: Int,
    private val marksByPage: Map<Int, List<PdfRect>>,
) : PdfEditOperation {

    override val name: String get() = "clear marks"
    override val changesDocument: Boolean get() = false

    override suspend fun apply(doc: PdfDocument) {
        doc.clearRedactionMarks()
    }

    override suspend fun revert(doc: PdfDocument) {
        for ((page, rects) in marksByPage) {
            doc.onPage(page) { p -> rects.forEach { p.markForRedaction(it) } }
        }
    }
}

/** How a text box is styled: what it says, how big, in which face. */
data class TextBoxStyle(
    val text: String,
    val fontSize: Double = 12.0,
    val fontName: String = DEFAULT_FONT,
)

/**
 * Restyling a placed box (#36 for the text, #43 for the size and face) — remove
 * and re-add under the same id, anchored to the rect it occupied.
 *
 * Anything about the box's appearance can change at once, and all of it is one
 * undo. The box's width and height change with the new style; its lower-left
 * corner does not, so the box stays where the user put it. That anchor choice
 * matters more for a size change than for a typo fix: going 12 pt → 18 pt grows
 * the glyphs, and the box grows upward from the corner it sits on — right for
 * text sitting on a printed rule.
 */
class EditTextBoxOperation(
    override val pageIndex: Int,
    private val id: String,
    private val from: TextBoxStyle,
    private val to: TextBoxStyle,
    private val x: Double,
    private val y: Double,
) : PdfEditOperation {

    override val name: String
        get() = if (from.text == to.text) "restyle text" else "edit text"

    override suspend fun apply(doc: PdfDocument) = replace(doc, to)
    override suspend fun revert(doc: PdfDocument) = replace(doc, from)

    // One page load for the whole swap, as MoveStampOperation does — pdfium has
    // no in-place text edit, so restyling means rebuilding the object.
    private suspend fun replace(doc: PdfDocument, style: TextBoxStyle) = doc.onPage(pageIndex) {
        it.removeTextBox(id)
        it.addTextBox(style.text, style.fontSize, x, y, id, style.fontName)
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

// ---- The document's own text (#114) ----------------------------------------

/**
 * Retyping a visual line: the Android twin of iOS's BodyTextEditOperation and the
 * desktop LineEditOperation. The new text goes into the line's first run; the other
 * runs leave the page but are kept, and so are the hidden copies a producer drew under
 * any run for fake bold, an outline or a shadow (#136). The core hands everything back
 * with the first run's untouched original, so undo restores the line byte-identical (#117).
 */
class BodyTextEditOperation(
    override val pageIndex: Int,
    private val line: TextLine,
    private val newText: String,
) : PdfEditOperation {
    private var originals: DetachedObject? = null

    /** How the last apply landed; SUBSTITUTED means the UI owes the user a notice. */
    var lastOutcome: TextEditOutcome? = null
        private set

    override val name: String get() = "edit text"

    override suspend fun apply(doc: PdfDocument) = doc.onPage(pageIndex) { page ->
        // One core call for the whole line, hidden copies included (#136): taking runs one
        // at a time moves the indices of those still to be taken.
        val edit = page.setLineText(line.runs.map { it.objectIndex }, newText)
        originals = edit.original
        lastOutcome = edit.outcome
    }

    override suspend fun revert(doc: PdfDocument) = doc.onPage(pageIndex) { page ->
        // Every object goes back at its own index, the edited run off first.
        page.restoreDetached(checkNotNull(originals) { "nothing to undo" })
        originals = null
    }
}

/** Clearing a line in the editor removes it, hidden copies and all (#136); undo brings every object back exactly. */
class BodyTextDeleteOperation(
    override val pageIndex: Int,
    private val line: TextLine,
) : PdfEditOperation {
    private var held: DetachedObject? = null

    override val name: String get() = "delete text"

    override suspend fun apply(doc: PdfDocument) = doc.onPage(pageIndex) { page ->
        held = page.detachTextRuns(line.runs.map { it.objectIndex })
    }

    override suspend fun revert(doc: PdfDocument) = doc.onPage(pageIndex) { page ->
        page.restoreDetached(checkNotNull(held) { "nothing to undo" })
        held = null
    }
}
