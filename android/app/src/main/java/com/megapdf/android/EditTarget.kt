package com.megapdf.android

import com.megapdf.engine.PdfDocument
import com.megapdf.engine.PdfPage
import com.megapdf.engine.PdfRect

/**
 * What a [PdfEditOperation] edits: the open document, as the history needs it.
 *
 * In the app this is always the open [PdfDocument], wrapped by [PdfDocumentTarget]. The
 * indirection buys one thing, and it is the reason #429 could hide: the redaction marks are
 * the only edit whose target id the *core* hands out, so their bookkeeping is the only part
 * of the history that cannot be checked by reading the operation alone — and driving it on a
 * device is the only way it was ever driven. Everything a mark operation asks of the core is
 * one of the four mark calls below, so a JVM test can stand in for the core and drive the
 * real history through a real sequence of gestures without an emulator.
 *
 * The mark calls take a whole page's worth of work at once because that is what they cost:
 * one page open per operation, as when the operations held a page themselves.
 */
interface EditTarget {
    /** Opens [index], runs [body] on it, and closes it however [body] ends. */
    suspend fun <T> onPage(index: Int, body: suspend (PdfPage) -> T): T

    /** Marks each of [rects] on [pageIndex], answering the new id per rect — -1 for a rect nothing was marked in. */
    suspend fun markForRedaction(pageIndex: Int, rects: List<PdfRect>): List<Int>

    /** Moves or resizes one mark. False when [markId] is not on the page: the caller is out of date. */
    suspend fun moveRedactionMark(pageIndex: Int, markId: Int, rect: PdfRect): Boolean

    /** Removes [markIds] from [pageIndex]. An id already gone is not an error. */
    suspend fun removeRedactionMarks(pageIndex: Int, markIds: List<Int>)

    /** Removes every mark on every page. */
    suspend fun clearRedactionMarks()
}

/** The open document as an [EditTarget]. */
class PdfDocumentTarget(private val doc: PdfDocument) : EditTarget {

    override suspend fun <T> onPage(index: Int, body: suspend (PdfPage) -> T): T {
        val page = doc.openPage(index)
        try {
            return body(page)
        } finally {
            page.close()
        }
    }

    override suspend fun markForRedaction(pageIndex: Int, rects: List<PdfRect>): List<Int> =
        onPage(pageIndex) { page -> rects.map { page.markForRedaction(it) } }

    override suspend fun moveRedactionMark(pageIndex: Int, markId: Int, rect: PdfRect): Boolean =
        onPage(pageIndex) { it.moveRedactionMark(markId, rect) }

    override suspend fun removeRedactionMarks(pageIndex: Int, markIds: List<Int>) =
        onPage(pageIndex) { page -> markIds.forEach { page.removeRedactionMark(it) } }

    override suspend fun clearRedactionMarks() = doc.clearRedactionMarks()
}

/** The open document, ready for the history. */
fun PdfDocument.asEditTarget(): EditTarget = PdfDocumentTarget(this)
