package com.megapdf.android

/**
 * The page tools as undoable edits (#174): rotate, delete, move, insert a blank page, and combine
 * the pages of another file in.
 *
 * Every one of them has an inverse in contract 10 — that is why `megapdf_page_restore` exists —
 * so they go in the same [EditHistory] as every other edit and Undo takes them back like
 * anything else. Two things about that are worth spelling out, because both have already cost a
 * defect on this codebase:
 *
 *  1. **Nothing here is addressed by an identifier the engine hands out.** #429 and #441 were both
 *     a history holding a redaction-mark id the core had replaced: the undo named a mark that no
 *     longer existed, the core answered false, the return value was dropped, and the Undo the
 *     person pressed did nothing at all. A page is addressed by its index, and an index *does*
 *     move — but contract 10 says exactly when: an entry recorded after a delete already carries
 *     the post-delete numbering, and a history replayed in order therefore needs no rewriting.
 *     Undo walks it backwards, so each operation puts the numbering back as it goes and the one
 *     before it finds the document numbered as it was when it was recorded. `PageToolsTest`
 *     drives the #429 shape on a device: rotate a page, delete the page in front of it, undo
 *     both, and the rotation must come off the page it went on.
 *  2. **The one handle that is the engine's is checked, not assumed.** A delete keeps the page
 *     itself alive for the undo, and a restore spends that handle: a second restore of the same
 *     one is a bug, and [EditTarget.restorePage] throws rather than quietly doing nothing. A redo
 *     therefore deletes again and keeps the *new* handle, which is what [DeletePagesOperation]
 *     does below, and [discard] lets go when the operation leaves the history for good.
 */

/**
 * Turning pages, 90° at a time. One step per gesture however many pages are selected: a person
 * who selects six pages and taps Rotate right means one action, as for *Clear all marks* (#329).
 *
 * Its own inverse, turned the other way. Rotation sets the page's /Rotate and rewrites no content,
 * so it is not an edit PDFium's writer can decline — and everything the app draws over the page
 * follows it round without a rotation term of its own (#439, contract 10's coordinates section).
 */
class RotatePagesOperation(
    pages: List<Int>,
    private val quarterTurns: Int,
) : PdfEditOperation {

    private val pages: List<Int> = pages.distinct().sorted()

    override val pageIndex: Int get() = pages.firstOrNull() ?: 0
    override val pagesChanged: List<Int> get() = pages
    override val name: String get() = if (quarterTurns > 0) "rotate right" else "rotate left"

    override suspend fun apply(doc: EditTarget) = turn(doc, quarterTurns)
    override suspend fun revert(doc: EditTarget) = turn(doc, -quarterTurns)

    private suspend fun turn(doc: EditTarget, by: Int) {
        for (page in pages) doc.rotatePage(page, by)
    }
}

/**
 * Deleting pages — one undo step for the whole selection, and the page itself is what comes back.
 *
 * Applied from the last page to the first, so the indices still to be deleted do not move under
 * it; reverted from the first to the last, each page going back at the index it was taken from,
 * which lands every one of them in its original place.
 */
class DeletePagesOperation(pages: List<Int>) : PdfEditOperation {

    /** Ascending, as the person sees them, and the order a revert puts them back in. */
    private val pages: List<Int> = pages.distinct().sorted()

    /** The deleted pages the engine is holding, aligned with [pages]; empty while they are back. */
    private var held: List<RestorablePage> = emptyList()

    override val pageIndex: Int get() = pages.firstOrNull() ?: 0
    override val pagesChanged: List<Int> get() = emptyList()   // they are gone; the window re-renders
    override val name: String get() = if (pages.size == 1) "delete page" else "delete pages"

    override var lastPageShifts: List<PageShift> = emptyList()
        private set

    override suspend fun apply(doc: EditTarget) {
        val kept = ArrayList<RestorablePage>(pages.size)
        val shifts = ArrayList<PageShift>(pages.size)
        for (page in pages.reversed()) {
            kept += doc.deletePage(page)
            shifts += PageShift.Deleted(page)
        }
        held = kept.reversed()            // back into page order, to match [pages]
        lastPageShifts = shifts
    }

    override suspend fun revert(doc: EditTarget) {
        val pagesBack = checkNotNull(held.takeIf { it.size == pages.size }) {
            "the deleted pages are no longer held, so this delete cannot be taken back"
        }
        val shifts = ArrayList<PageShift>(pages.size)
        pages.forEachIndexed { i, at ->
            doc.restorePage(pagesBack[i], at)
            shifts += PageShift.Inserted(at)
        }
        held = emptyList()                // the restores spent every handle
        lastPageShifts = shifts
    }

    override fun discard() {
        held.forEach { it.discard() }
        held = emptyList()
    }
}

/** Moving one page: its own inverse, the other way round. */
class MovePageOperation(
    private val from: Int,
    private val to: Int,
) : PdfEditOperation {

    override val pageIndex: Int get() = to
    override val pagesChanged: List<Int> get() = emptyList()
    override val name: String get() = "move page"

    override var lastPageShifts: List<PageShift> = emptyList()
        private set

    override suspend fun apply(doc: EditTarget) = move(doc, from, to)
    override suspend fun revert(doc: EditTarget) = move(doc, to, from)

    private suspend fun move(doc: EditTarget, from: Int, to: Int) {
        doc.movePage(from, to)
        lastPageShifts = listOf(PageShift.Moved(from, to))
    }
}

/**
 * A blank page. Its inverse is a plain delete with nothing kept for an undo: what a redo would
 * put back is a blank page of the same size, which is exactly what inserting one makes again.
 */
class InsertBlankPageOperation(
    private val at: Int,
    private val widthPoints: Double,
    private val heightPoints: Double,
) : PdfEditOperation {

    override val pageIndex: Int get() = at
    override val pagesChanged: List<Int> get() = emptyList()
    override val name: String get() = "insert page"

    override var lastPageShifts: List<PageShift> = emptyList()
        private set

    override suspend fun apply(doc: EditTarget) {
        doc.insertBlankPage(at, widthPoints, heightPoints)
        lastPageShifts = listOf(PageShift.Inserted(at))
    }

    override suspend fun revert(doc: EditTarget) {
        doc.deletePageWithoutUndo(at)
        lastPageShifts = listOf(PageShift.Deleted(at))
    }
}

/**
 * Combine: the pages of another file, inserted before [insertAt].
 *
 * The first apply reads them out of the file. Every apply after that — a redo — puts back the very
 * pages its own undo took off, rather than reading the file again, for two reasons: the pages that
 * arrived are not always the pages in the file (a field whose name was already taken here was
 * renamed on the way in, and a redo that re-imported would rename it a second time), and the file
 * may be gone by then. [path] is a copy of the picked document the view model keeps for as long as
 * the document is open; the engine holds it open too, since imported pages are read from it on
 * demand.
 */
class ImportPagesOperation(
    private val path: String,
    private val insertAt: Int,
) : PdfEditOperation {

    /** How many pages arrived; known only after the first apply. */
    var imported: Int = 0
        private set

    /** The imported pages while they are off the document, in page order; empty while they are on it. */
    private var held: List<RestorablePage> = emptyList()

    override val pageIndex: Int get() = insertAt
    override val pagesChanged: List<Int> get() = emptyList()
    override val name: String get() = "add pages"

    override var lastPageShifts: List<PageShift> = emptyList()
        private set

    override suspend fun apply(doc: EditTarget) {
        if (held.isEmpty()) {
            imported = doc.importPages(path, insertAt)
        } else {
            held.forEachIndexed { i, page -> doc.restorePage(page, insertAt + i) }
            held = emptyList()
        }
        lastPageShifts = if (imported > 0) listOf(PageShift.Inserted(insertAt, imported)) else emptyList()
    }

    override suspend fun revert(doc: EditTarget) {
        val kept = ArrayList<RestorablePage>(imported)
        val shifts = ArrayList<PageShift>(imported)
        // From the last of them back, so the ones still to go do not move.
        for (i in imported - 1 downTo 0) {
            kept += doc.deletePage(insertAt + i)
            shifts += PageShift.Deleted(insertAt + i)
        }
        held = kept.reversed()
        lastPageShifts = shifts
    }

    override fun discard() {
        held.forEach { it.discard() }
        held = emptyList()
    }
}
