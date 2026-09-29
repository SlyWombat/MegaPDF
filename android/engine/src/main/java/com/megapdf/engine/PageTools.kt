package com.megapdf.engine

import kotlinx.coroutines.withContext

/**
 * Contract 10: page tools (#174) — the Kotlin face of `megapdf_page_rotate`, `_delete`,
 * `_restore`, `_move`, `_insert_blank`, `megapdf_pages_import` and `megapdf_pages_extract`.
 *
 * Extensions rather than members of [PdfDocument] for the reason iOS keeps
 * `PdfEngine+Pages.swift` beside `PdfEngine.swift`: one contract, one file, and the class that
 * predates it is not made longer by it. They run on the engine's one thread like every other
 * call here ([PdfEngine.pdfiumDispatcher]), because PDFium is not thread-safe.
 *
 * **Every refusal is named.** These are the first calls on this boundary that can be refused
 * for several genuinely different reasons, and the difference is the whole of what the app owes
 * the person: "this document does not allow pages to be moved about" is not "those pages carry
 * a form field that cannot be copied" is not "the file could not be written". So instead of a
 * boolean or one exception type, a refusal arrives as [PdfPagesException] carrying a
 * [PageToolRefusal], and the app turns that into a sentence in the user's language. Nothing
 * here is retried, softened or hidden: when the core refuses, the document is untouched, and
 * saying so plainly is the feature.
 */

/** Why a page tool refused. Each one has its own sentence in each app's catalogue. */
enum class PageToolRefusal {
    /**
     * This document's security does not allow it: MEGAPDF_ERR_RESTRICTED from the assemble /
     * modify check (ISO 32000-2 Table 22, "assemble the document — insert, rotate or delete
     * pages"), or from the copy check an extract needs. Its owner password would allow it.
     */
    RESTRICTED,

    /** The *other* file needs a password, so no pages can be taken from it. */
    SOURCE_NEEDS_PASSWORD,

    /** The other file's own security does not allow copying out of it. */
    SOURCE_RESTRICTED,

    /**
     * MEGAPDF_ERR_FIELDS: the pages carry form fields in a /Parent hierarchy this copy cannot
     * carry — about 0.8% of a real corpus. Deliberate, and whole: nothing was changed, because
     * the alternative is a document whose fields quietly lost their names and values.
     */
    FIELD_HIERARCHY,

    /** A PDF must keep at least one page, so the last one may not be deleted. */
    LAST_PAGE,

    /** The file could not be opened, read, written or renamed into place. */
    FILE,

    /** A redaction failed part-way, so the document may only be closed (#173). */
    REDACTION_POISONED,

    /** PDFium refused, or the core could not allocate. Nothing was changed. */
    ENGINE,
}

/**
 * A page tool refused, with [refusal] saying which refusal it was and [status] the core's own
 * MEGAPDF_ERR_* code behind it. The document is as it was: contract 10's calls change either
 * everything they promised or nothing.
 */
class PdfPagesException(val status: Int, val refusal: PageToolRefusal) :
    Exception("page tool refused: $refusal (status $status)")

/**
 * A deleted page the core is holding so an undo can put it back — the page itself, not a
 * description of it, so what comes back is exactly what went away (its content, annotations,
 * appearance streams and its fields with their values).
 *
 * The core owns it: [PdfDocument.restorePage] consumes it, [discard] frees it, and closing the
 * document frees any still held. A handle restores only into the document it came from.
 */
class RemovedPage internal constructor(private val owner: PdfDocument, internal var handle: Long) {

    /** False once this page has been restored or discarded. */
    val isHeld: Boolean get() = handle != 0L

    /**
     * Frees the page without putting it back: the undo that would have used it is gone.
     *
     * Detached, on the engine's own teardown scope, because the callers are the undo history
     * dropping its oldest entry and clearing its redo branch — neither of which is a coroutine,
     * and neither of which should wait. Unlike simply leaving it held, this releases the copy
     * before the document closes; a long session of deletes would otherwise keep every deleted
     * page alive on a phone for as long as the document was open.
     *
     * A page whose document has already closed went with it — `megapdf_close()` frees every
     * removed page still held — so this does nothing then. That guard is [PdfPage.close]'s (#549)
     * and it is here for the same reason: freeing it twice is a use after free, and teardown order
     * is not something the caller of a `discard()` is thinking about.
     */
    fun discard() {
        val held = handle
        if (held == 0L) return
        handle = 0L
        PdfEngine.discardRemovedPage(owner, held)
    }
}

/** Where this page stands now (contract 10): -1 once it has been deleted. */
suspend fun PdfPage.currentIndex(): Int = withContext(PdfEngine.pdfiumDispatcher) {
    PdfiumNative.nativePageIndex(nativePageHandle())
}

/** The page's /Rotate in quarter turns clockwise, 0–3. */
suspend fun PdfDocument.pageRotation(pageIndex: Int): Int = withContext(PdfEngine.pdfiumDispatcher) {
    PdfiumNative.nativePageRotation(nativeHandle(), pageIndex).orThrow(Op.ROTATE)
}

/**
 * Turns the page by [quarterTurns] quarter turns clockwise (negative anticlockwise): its
 * /Rotate changes and nothing else does — no content is rewritten, so this is not the kind of
 * edit PDFium's writer can decline. Every open handle on the page sees the new size, and the
 * rectangles every other contract reports are in the rotated space the render draws (#439), so
 * a tap and a highlight follow the page round with no work on this side.
 */
suspend fun PdfDocument.rotatePage(pageIndex: Int, quarterTurns: Int): Unit =
    withContext(PdfEngine.pdfiumDispatcher) {
        PdfiumNative.nativePageRotate(nativeHandle(), pageIndex, quarterTurns).orThrow(Op.ROTATE)
    }

/**
 * Deletes the page and hands back the copy the core kept for an undo. The page's form fields
 * leave the document's AcroForm with it, so a saved file carries neither a field whose only
 * widget was on a deleted page nor the page such a field would have kept reachable.
 */
suspend fun PdfDocument.deletePage(pageIndex: Int): RemovedPage {
    val document = this
    return withContext(PdfEngine.pdfiumDispatcher) {
        val answer = PdfiumNative.nativePageDelete(nativeHandle(), pageIndex, keep = true)
            ?: throw PdfPagesException(PdfiumNative.STATUS_MEMORY, PageToolRefusal.ENGINE)
        answer[0].toInt().orThrow(Op.DELETE)
        RemovedPage(document, answer[1])
    }
}

/** Deletes the page for good, keeping nothing for an undo — for an undo that is itself undoing an insert. */
suspend fun PdfDocument.deletePageWithoutUndo(pageIndex: Int): Unit =
    withContext(PdfEngine.pdfiumDispatcher) {
        val answer = PdfiumNative.nativePageDelete(nativeHandle(), pageIndex, keep = false)
            ?: throw PdfPagesException(PdfiumNative.STATUS_MEMORY, PageToolRefusal.ENGINE)
        answer[0].toInt().orThrow(Op.DELETE)
    }

/**
 * Puts a deleted page back at [at] (0 … page count, the count appends) and consumes [removed].
 * Throws [IllegalStateException] for a handle already used, which is the shape of the bug #429
 * and #441 were: an operation that names something the core no longer has must fail loudly, not
 * quietly do nothing and leave the history one step out.
 */
suspend fun PdfDocument.restorePage(removed: RemovedPage, at: Int): Unit =
    withContext(PdfEngine.pdfiumDispatcher) {
        val held = removed.handle
        check(held != 0L) { "the removed page was already restored or discarded" }
        PdfiumNative.nativePageRestore(nativeHandle(), held, at).orThrow(Op.RESTORE)
        removed.handle = 0L   // the core consumed it
    }

/** Moves the page at [from] so it stands at [to] afterwards; the pages between shift by one. */
suspend fun PdfDocument.movePage(from: Int, to: Int): Unit = withContext(PdfEngine.pdfiumDispatcher) {
    PdfiumNative.nativePageMove(nativeHandle(), from, to).orThrow(Op.MOVE)
}

/** Inserts an empty page of [widthPoints] × [heightPoints] at [at] (0 … page count appends). */
suspend fun PdfDocument.insertBlankPage(at: Int, widthPoints: Double, heightPoints: Double): Unit =
    withContext(PdfEngine.pdfiumDispatcher) {
        PdfiumNative.nativePageInsertBlank(nativeHandle(), at, widthPoints, heightPoints).orThrow(Op.INSERT)
    }

/**
 * Combine: inserts the pages of the PDF at [path] before [insertAt], in the order [pages] lists
 * them (null means all of them), and answers how many arrived. Only the pages asked for are
 * copied, with the fonts, images and forms they draw; a field whose name is already taken here
 * is renamed with a numeric suffix so the two never merge into one field.
 *
 * The other document stays open inside this one until this one closes, so [path] must stay
 * *readable* for that long — its directory entry need not survive, which is how the app can
 * unlink its cached copy straight away and still let the pages be read on demand (#147).
 *
 * @throws PdfPagesException [PageToolRefusal.FIELD_HIERARCHY] when a page's fields sit in a
 *   /Parent hierarchy whose top-level name is already taken here: the copy would have to rename
 *   a name that lives on the parent rather than on the widget, so the pages are refused whole
 *   and nothing is changed.
 */
suspend fun PdfDocument.importPages(
    path: String, unlock: String? = null, pages: List<Int>? = null, insertAt: Int,
): Int = withContext(PdfEngine.pdfiumDispatcher) {
    val answer = PdfiumNative.nativePagesImport(
        nativeHandle(), path.nulTerminatedUtf8(), unlock?.nulTerminatedUtf8(),
        pages?.toIntArray()?.takeIf { it.isNotEmpty() }, insertAt,
    ) ?: throw PdfPagesException(PdfiumNative.STATUS_MEMORY, PageToolRefusal.ENGINE)
    answer[0].orThrow(Op.IMPORT)
    answer[1]
}

/**
 * Split: writes [pages] (null means every page, repeats allowed, in the order given) as a new
 * PDF at [outPath], with the save discipline every platform's save uses — the whole file to a
 * sibling temporary name, reopened and its page count checked, and only then given the
 * destination's name, so a crash or a full disk leaves either no file or a whole one. This
 * document is unchanged and nothing is recorded: an extract is a copy, not an edit.
 */
suspend fun PdfDocument.extractPages(pages: List<Int>?, outPath: String): Unit =
    withContext(PdfEngine.pdfiumDispatcher) {
        PdfiumNative.nativePagesExtract(
            nativeHandle(), pages?.toIntArray()?.takeIf { it.isNotEmpty() },
            outPath.nulTerminatedUtf8(), 0L,
        ).orThrow(Op.EXTRACT)
    }

/** Which call is being made, for the one refusal that means different things depending. */
private enum class Op { ROTATE, DELETE, RESTORE, MOVE, INSERT, IMPORT, EXTRACT }

/**
 * The core's status, or the named refusal behind it.
 *
 * Two codes carry more than one meaning, and both are resolved here rather than in the app:
 *
 *  * MEGAPDF_ERR_ARGUMENT from a delete is the one-page rule — every index the app passes comes
 *    from a page count it has just read, so "no page at that index" is not reachable from the UI
 *    while "a document must keep at least one page" is exactly what a person can ask for.
 *  * MEGAPDF_ERR_RESTRICTED from an import is the *other* file's security, not this document's:
 *    the app checks its own document's assemble permission before it offers the command at all
 *    (`DocumentCapabilities.canAssemblePages`), so what is left is a file that needs a password
 *    (FPDF_ERR_PASSWORD) or one that forbids copying out of it.
 */
private fun Int.orThrow(op: Op): Int {
    if (this >= 0) return this
    val refusal = when (this) {
        PdfiumNative.STATUS_FIELDS -> PageToolRefusal.FIELD_HIERARCHY
        PdfiumNative.STATUS_REDACT -> PageToolRefusal.REDACTION_POISONED
        PdfiumNative.STATUS_FILE -> PageToolRefusal.FILE
        PdfiumNative.STATUS_RESTRICTED -> when {
            op != Op.IMPORT -> PageToolRefusal.RESTRICTED
            PdfiumNative.nativeLastError() == PdfiumNative.ERR_PASSWORD -> PageToolRefusal.SOURCE_NEEDS_PASSWORD
            else -> PageToolRefusal.SOURCE_RESTRICTED
        }
        PdfiumNative.STATUS_ARGUMENT -> if (op == Op.DELETE) PageToolRefusal.LAST_PAGE else PageToolRefusal.ENGINE
        else -> PageToolRefusal.ENGINE
    }
    throw PdfPagesException(this, refusal)
}
