package com.megapdf.android

import com.megapdf.engine.DEFAULT_FONT
import com.megapdf.engine.PdfRect
import com.megapdf.engine.DetachedObject
import com.megapdf.engine.RedactionMark
import com.megapdf.engine.TextEditOutcome
import com.megapdf.engine.TextLine

/** Single line spacing at the face's own size (#4) — the same rule a wrapped paragraph reads at. */
private const val TEXT_LINE_HEIGHT_FACTOR = 1.2

// Undo/redo (#34) — the mobile port of the desktop `IEditOperation` + `UndoStack`
// (SDD §4.2, command pattern), and the twin of iOS's EditHistory.swift. Two rules
// make it safe on a document other edits are reshaping underneath it:
//
//   1. Operations address their target by id, never by index. Annotation and
//      page-object indices shift; `MegaPDF_Id` and the text box mark id do not.
//   2. Revert restores the same id, so a second undo still finds it. The redaction marks
//      are the one place this is not in the history's gift: the core hands out a fresh mark
//      id every time an area is marked and never reuses one, so a mark that comes back from
//      an undo comes back under a new id. The history rebinds the operations that named the
//      old one ([RedactionMarkEdit], #429) — without that, the undo of a move recorded
//      before a removal looks for a mark the removal's own undo has already replaced.

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

    /**
     * The pages whose look this changed, for the re-render. Usually just [pageIndex]; a page
     * operation can touch several at once — rotating a selection is one undo step (#174).
     */
    val pagesChanged: List<Int> get() = listOf(pageIndex)

    /**
     * How the last apply or revert renumbered the document's pages (#174), in the order it
     * happened, or empty for an edit that leaves the page order alone — which is every edit but
     * the page tools. The app's own index-keyed state follows these ([PageShift]); the core's
     * follows by itself.
     *
     * Read after apply or revert, like [BodyTextEditOperation.lastOutcome] and
     * [RedactionMarkEdit.lastRenames]: a delete's undo inserts where its apply deleted, so which
     * renumbering happened depends on which way the operation just went.
     */
    val lastPageShifts: List<PageShift> get() = emptyList()

    suspend fun apply(doc: EditTarget)
    suspend fun revert(doc: EditTarget)

    /**
     * This operation has left the history for good and will never be applied or reverted again:
     * the stack dropped its oldest entry, a new edit cleared the redo branch, or the document is
     * closing. Anything the *engine* is holding on its behalf can go — for a page delete, that is
     * the deleted page itself (#174).
     *
     * Not a general dispose: an operation is free to keep its own memory as long as it likes.
     * This exists because a page copy is large and the core holds it until the document closes,
     * so a hundred deletes in one session would otherwise hold a hundred pages nothing can reach.
     */
    fun discard() {}
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
    suspend fun perform(operation: PdfEditOperation, doc: EditTarget) {
        operation.apply(doc)
        rebindRedactionMarks(operation)
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
        // Both of these drop operations that can never be applied or reverted again, so both let
        // go of whatever the engine was holding for them (#174): the oldest edit falling off a
        // full stack, and the redo branch this new edit has just replaced.
        if (done.size > capacity) done.removeFirst().discard()
        undone.forEach { it.discard() }
        undone.clear()
    }

    /** Reverts the last operation; returns it — the caller needs to know if it changed the file. */
    suspend fun undo(doc: EditTarget): PdfEditOperation? {
        val operation = done.removeLastOrNull() ?: return null
        try {
            operation.revert(doc)
        } catch (e: Exception) {
            done.addLast(operation)   // keep the history honest if the revert failed
            throw e
        }
        undone.addLast(operation)
        rebindRedactionMarks(operation)
        return operation
    }

    /** Re-applies the last undone operation; returns it. */
    suspend fun redo(doc: EditTarget): PdfEditOperation? {
        val operation = undone.removeLastOrNull() ?: return null
        try {
            operation.apply(doc)
        } catch (e: Exception) {
            undone.addLast(operation)
            throw e
        }
        done.addLast(operation)
        rebindRedactionMarks(operation)
        return operation
    }

    /**
     * Forgets everything, letting go of whatever the engine held for each operation (#174).
     *
     * The caller must still own an open document when it calls this: a deleted page the core is
     * holding belongs to that document and is freed with it, so discarding one *after* the
     * document closed would be the second free — the exact shape of #549. Both callers are in
     * order today ([ViewerViewModel.closeCurrent] clears before it closes; applying a redaction
     * clears while the document is very much open), and the engine checks it besides
     * (`RemovedPage.discard` does nothing once its document is closed).
     */
    fun clear() {
        done.forEach { it.discard() }
        undone.forEach { it.discard() }
        done.clear()
        undone.clear()
    }

    /**
     * Hands every other operation the mark ids the one just applied or reverted re-marked
     * under (#429).
     *
     * A mark cannot come back under the id it had: the core hands out a fresh one and never
     * reuses the old. So the operation that re-marked is the only one that knows the mark's
     * new id, and every operation still holding the old one — the move recorded before a
     * removal, the placement under it, the clear that swept it up — would otherwise name a
     * mark the core no longer has, and quietly do nothing when its turn came.
     */
    private fun rebindRedactionMarks(source: PdfEditOperation) {
        val renames = (source as? RedactionMarkEdit)?.lastRenames.orEmpty()
        if (renames.isEmpty()) return
        for (operation in done + undone) {
            if (operation !== source && operation is RedactionMarkEdit) operation.rebind(renames)
        }
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

    override suspend fun apply(doc: EditTarget) = if (adding) add(doc) else remove(doc)
    override suspend fun revert(doc: EditTarget) = if (adding) remove(doc) else add(doc)

    private suspend fun add(doc: EditTarget) = doc.onPage(pageIndex) { it.addCheckMark(square, id) }
    private suspend fun remove(doc: EditTarget) = doc.onPage(pageIndex) { it.removeAnnot(id) }

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

    override suspend fun apply(doc: EditTarget) = doc.onPage(pageIndex) { it.clickAt(x, y) }
    override suspend fun revert(doc: EditTarget) = apply(doc)
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

    override suspend fun apply(doc: EditTarget) = if (adding) add(doc) else remove(doc)
    override suspend fun revert(doc: EditTarget) = if (adding) remove(doc) else add(doc)

    private suspend fun add(doc: EditTarget) = doc.onPage(pageIndex) {
        it.addImageStamp(pixels, pixelWidth, pixelHeight, rect, id)
    }
    private suspend fun remove(doc: EditTarget) = doc.onPage(pageIndex) { it.removeAnnot(id) }
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

    override suspend fun apply(doc: EditTarget) = place(doc, to)
    override suspend fun revert(doc: EditTarget) = place(doc, from)

    private suspend fun place(doc: EditTarget, rect: PdfRect) = doc.onPage(pageIndex) {
        it.removeAnnot(id)
        it.addImageStamp(pixels, pixelWidth, pixelHeight, rect, id)
    }
}

// ---- Whiteouts (#3) ---------------------------------------------------------
//
// A whiteout is page content, not an annotation, and carries no id of its own — it is
// addressed by object index, which shifts every time it is placed or removed. Placing
// always appends a fresh object (nothing worth restoring byte-identical about a brand new
// one); removing keeps what it detached, so its own undo puts back the exact bytes rather
// than a redrawn copy — the same asymmetry MoveWhiteoutOperation's two calls below have.

/** Placing a whiteout (drag-to-cover, #3). Revert only ever detaches: nothing here is worth restoring byte-identical, since a redo places a fresh rectangle at the same bounds anyway. */
class WhiteoutAddOperation(
    override val pageIndex: Int,
    private val rect: PdfRect,
) : PdfEditOperation {
    private var objectIndex: Int = -1

    override val name: String get() = "whiteout"

    /** Where it landed after the last apply — read this to keep it selected (#3). */
    val currentObjectIndex: Int get() = objectIndex

    override suspend fun apply(doc: EditTarget) = doc.onPage(pageIndex) { page ->
        objectIndex = page.addWhiteout(rect)
    }

    override suspend fun revert(doc: EditTarget): Unit = doc.onPage(pageIndex) { page ->
        page.detachObject(objectIndex)
        Unit
    }
}

/** Removing a selected whiteout (✕ on its chrome, #3). Undo restores the exact object detached. */
class WhiteoutRemoveOperation(
    override val pageIndex: Int,
    private val objectIndex: Int,
) : PdfEditOperation {
    private var detached: DetachedObject? = null

    override val name: String get() = "remove whiteout"

    override suspend fun apply(doc: EditTarget) = doc.onPage(pageIndex) { page ->
        detached = page.detachObject(objectIndex)
    }

    override suspend fun revert(doc: EditTarget) = doc.onPage(pageIndex) { page ->
        page.restoreObject(checkNotNull(detached) { "nothing to undo" }, objectIndex)
        detached = null
    }
}

/**
 * Moving or resizing a selected whiteout (drag or corner handle, #3).
 *
 * A whiteout has no native "move in place": it is a page object, not an annotation, and
 * there is no primitive that repositions one without touching the object list. So a move is
 * the same two primitives [WhiteoutAddOperation] and [WhiteoutRemoveOperation] already use —
 * detach the rectangle that is there, append a fresh one at the new bounds — which is, byte
 * for byte, the "remove it and redraw it by hand" this chrome replaces. The object index
 * changes every time, which is why [currentObjectIndex] is read back after apply/revert
 * rather than assumed.
 */
class MoveWhiteoutOperation(
    override val pageIndex: Int,
    objectIndex: Int,
    private val from: PdfRect,
    private val to: PdfRect,
) : PdfEditOperation {
    private var objectIndex: Int = objectIndex

    override val name: String get() = "move whiteout"

    val currentObjectIndex: Int get() = objectIndex

    override suspend fun apply(doc: EditTarget) = move(doc, to)
    override suspend fun revert(doc: EditTarget) = move(doc, from)

    private suspend fun move(doc: EditTarget, rect: PdfRect) = doc.onPage(pageIndex) { page ->
        page.detachObject(objectIndex)
        objectIndex = page.addWhiteout(rect)
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
private suspend fun EditTarget.placeTextBoxAt(
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

    override suspend fun apply(doc: EditTarget) = if (adding) add(doc) else remove(doc)
    override suspend fun revert(doc: EditTarget) = if (adding) remove(doc) else add(doc)

    private suspend fun add(doc: EditTarget) =
        if (boundsAnchored) doc.placeTextBoxAt(pageIndex, id, text, fontSize, fontName, x, y)
        else doc.onPage(pageIndex) { it.addTextBox(text, fontSize, x, y, id, fontName) }

    private suspend fun remove(doc: EditTarget) = doc.onPage(pageIndex) { it.removeTextBox(id) }
}

/**
 * Placing a multi-line note as one undo step (#4): the phone's Enter key is its "new line",
 * where a desktop reads that as Shift+Enter. There is no multi-line text object in the
 * format this app writes — a "line" of added text is already the whole of what
 * [TextBoxOperation] places — so a note of several lines is that many boxes, one per [ids]
 * (each already carrying its own place in [lines]), stacked top to bottom at the face's own
 * line height from ([x], [y]). That is also what keeps every line individually editable
 * afterwards (#4's acceptance test, in its own words): none of them ever stopped being an
 * ordinary, separately-selectable text box. Undo takes the whole note at once, because
 * placing all of them was the one gesture that made them.
 */
class AddTextBoxesOperation(
    override val pageIndex: Int,
    private val ids: List<String>,
    private val lines: List<String>,
    private val fontSize: Double,
    private val x: Double,
    private val y: Double,
    private val fontName: String = DEFAULT_FONT,
) : PdfEditOperation {

    override val name: String get() = "text"

    override suspend fun apply(doc: EditTarget) = doc.onPage(pageIndex) { page ->
        lines.indices.forEach { i ->
            page.addTextBox(lines[i], fontSize, x, y - i * fontSize * TEXT_LINE_HEIGHT_FACTOR, ids[i], fontName)
        }
    }

    override suspend fun revert(doc: EditTarget) = doc.onPage(pageIndex) { page ->
        ids.forEach { page.removeTextBox(it) }
    }
}

// ---- Redaction marks (#329) -------------------------------------------------
//
// A mark is the core's own and is never written to the file, so every operation here says
// `changesDocument = false`: a mark leaves the document clean, writes no journal entry and
// re-renders nothing — the overlay draw is the visible change. They are in the history
// because Undo has to be able to take a mark back, which is the whole of #329.
//
// Inverse pairs, one type each, as everywhere above — and all three of them
// [RedactionMarkEdit]s, because a mark is the one target whose id the history does not own.

/** A mark that has come back under a new core id, from the operation that re-marked it (#429). */
data class RedactionMarkRename(val pageIndex: Int, val oldId: Int, val newId: Int)

/**
 * An operation that names redaction marks by the id the core gave them.
 *
 * `markForRedaction` hands out a fresh id for every area marked and the core never reuses one
 * for the life of the document, so a mark that an undo puts back is not the id anything
 * recorded earlier is holding (#429). An operation that re-marks reports the swap in
 * [lastRenames]; the history hands that to every other operation, which follows it in
 * [rebind]. Without it the undo of a move recorded before a removal names a mark the
 * removal's own undo has already replaced, the core answers false, and the Undo the person
 * pressed does nothing at all.
 */
interface RedactionMarkEdit : PdfEditOperation {
    /** What the last apply or revert re-marked. Empty unless this operation handed out ids. */
    val lastRenames: List<RedactionMarkRename>

    /** Follows [renames]: the same marks, under the ids the core has for them now. */
    fun rebind(renames: List<RedactionMarkRename>)
}

/** Points every id this list holds that [rename] renames at the mark's new id. */
private fun MutableList<Int?>.rebind(rename: RedactionMarkRename) {
    indices.forEach { if (this[it] == rename.oldId) this[it] = rename.newId }
}

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
 * id for the life of a document, so every re-mark takes fresh ids; [ids] is what the page
 * carries at this moment, and what the re-mark replaces is reported as a rename (#429).
 */
class RedactMarkOperation(
    override val pageIndex: Int,
    private val rects: List<PdfRect>,
    ids: List<Int>,
    private val adding: Boolean,
) : RedactionMarkEdit {

    /** Per rect, the id the core has for its mark now — null while the mark is off the page. */
    private val live: MutableList<Int?> = rects.indices.map { ids.getOrNull(it) }.toMutableList()

    /**
     * Per rect, the last id its mark carried. Kept across a removal, because that is the id
     * the rest of the history is still holding when this operation marks the area again.
     */
    private val named: MutableList<Int?> = live.toMutableList()

    override val name: String get() = if (adding) "redact" else "remove mark"
    override val changesDocument: Boolean get() = false

    override var lastRenames: List<RedactionMarkRename> = emptyList()
        private set

    override suspend fun apply(doc: EditTarget) {
        if (adding) mark(doc) else remove(doc)
    }

    override suspend fun revert(doc: EditTarget) {
        if (adding) remove(doc) else mark(doc)
    }

    private suspend fun mark(doc: EditTarget) {
        val renames = mutableListOf<RedactionMarkRename>()
        doc.markForRedaction(pageIndex, rects).forEachIndexed { i, id ->
            live[i] = id.takeIf { it >= 0 }
            if (id < 0) return@forEachIndexed   // nothing in that area: no mark, no rename
            named[i]?.let { was -> if (was != id) renames += RedactionMarkRename(pageIndex, was, id) }
            named[i] = id
        }
        lastRenames = renames
    }

    private suspend fun remove(doc: EditTarget) {
        // Already gone counts as success on the core side, so an undo cannot fail.
        doc.removeRedactionMarks(pageIndex, live.filterNotNull())
        live.indices.forEach { live[it] = null }
        lastRenames = emptyList()
    }

    override fun rebind(renames: List<RedactionMarkRename>) {
        renames.forEach { rename ->
            if (rename.pageIndex != pageIndex) return@forEach
            live.rebind(rename)
            named.rebind(rename)
        }
    }
}

/**
 * Moving or resizing a mark. The core moves an id in place, so undo and redo keep the same
 * mark — which is why this is not the remove-and-re-place that [MoveStampOperation] needs.
 * The *id* is not as durable as the mark: a removal undone between this operation and its
 * turn puts the mark back under a new one, and [rebind] is how this follows it (#429).
 */
class MoveRedactionMarkOperation(
    override val pageIndex: Int,
    markId: Int,
    private val from: PdfRect,
    private val to: PdfRect,
) : RedactionMarkEdit {

    private var markId: Int = markId

    override val name: String get() = "move mark"
    override val changesDocument: Boolean get() = false
    override val lastRenames: List<RedactionMarkRename> get() = emptyList()

    override suspend fun apply(doc: EditTarget) = move(doc, to)
    override suspend fun revert(doc: EditTarget) = move(doc, from)

    private suspend fun move(doc: EditTarget, rect: PdfRect) {
        // False means the id is not on the page. That used to be dropped on the floor, which
        // is how #429 stayed invisible: the Undo was pressed, the mark did not move, and
        // nothing said so. It is a broken history, so it fails the way one does — the history
        // puts the operation back and the screen says the edit failed.
        check(doc.moveRedactionMark(pageIndex, markId, rect)) {
            "redaction mark $markId is not on page $pageIndex"
        }
    }

    override fun rebind(renames: List<RedactionMarkRename>) {
        renames.forEach { if (it.pageIndex == pageIndex && it.oldId == markId) markId = it.newId }
    }
}

/**
 * Clearing every mark on the document, as **one** undo step: a person who says "clear all
 * marks" means one action, not one per mark or one per page, and Undo puts every one of
 * them back where it was.
 *
 * [marksByPage] is the whole document's marks as they were, ids and all: the rects are what
 * the undo re-marks, and the ids are what it replaces, so a move recorded before the clear
 * still finds its mark afterwards (#429). [pageIndex] is the page the UI treats as this
 * operation's own — a clear can span pages, and the history wants one page.
 */
class ClearRedactionMarksOperation(
    override val pageIndex: Int,
    marksByPage: Map<Int, List<RedactionMark>>,
) : RedactionMarkEdit {

    private val rectsByPage: Map<Int, List<PdfRect>> =
        marksByPage.mapValues { (_, marks) -> marks.map { it.rect } }

    /** Per page, per rect, the last id that mark carried — as [RedactMarkOperation.named]. */
    private val named: Map<Int, MutableList<Int?>> =
        marksByPage.mapValues { (_, marks) -> marks.map<RedactionMark, Int?> { it.markId }.toMutableList() }

    override val name: String get() = "clear marks"
    override val changesDocument: Boolean get() = false

    override var lastRenames: List<RedactionMarkRename> = emptyList()
        private set

    override suspend fun apply(doc: EditTarget) {
        doc.clearRedactionMarks()
        lastRenames = emptyList()
    }

    override suspend fun revert(doc: EditTarget) {
        val renames = mutableListOf<RedactionMarkRename>()
        for ((page, rects) in rectsByPage) {
            val ids = named.getValue(page)
            doc.markForRedaction(page, rects).forEachIndexed { i, id ->
                if (id < 0) return@forEachIndexed
                ids[i]?.let { was -> if (was != id) renames += RedactionMarkRename(page, was, id) }
                ids[i] = id
            }
        }
        lastRenames = renames
    }

    override fun rebind(renames: List<RedactionMarkRename>) {
        renames.forEach { rename -> named[rename.pageIndex]?.rebind(rename) }
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

    override suspend fun apply(doc: EditTarget) = replace(doc, to)
    override suspend fun revert(doc: EditTarget) = replace(doc, from)

    // One page load for the whole swap, as MoveStampOperation does — pdfium has
    // no in-place text edit, so restyling means rebuilding the object.
    private suspend fun replace(doc: EditTarget, style: TextBoxStyle) = doc.onPage(pageIndex) {
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

    override suspend fun apply(doc: EditTarget) =
        doc.onPage(pageIndex) { it.moveTextBox(id, toX, toY) }

    override suspend fun revert(doc: EditTarget) =
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

    override suspend fun apply(doc: EditTarget) = doc.onPage(pageIndex) { page ->
        // One core call for the whole line, hidden copies included (#136): taking runs one
        // at a time moves the indices of those still to be taken.
        val edit = page.setLineText(line.runs.map { it.objectIndex }, newText)
        originals = edit.original
        lastOutcome = edit.outcome
    }

    override suspend fun revert(doc: EditTarget) = doc.onPage(pageIndex) { page ->
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

    override suspend fun apply(doc: EditTarget) = doc.onPage(pageIndex) { page ->
        held = page.detachTextRuns(line.runs.map { it.objectIndex })
    }

    override suspend fun revert(doc: EditTarget) = doc.onPage(pageIndex) { page ->
        page.restoreDetached(checkNotNull(held) { "nothing to undo" })
        held = null
    }
}
