package com.megapdf.android

/**
 * Which pages to draw, and how wide.
 *
 * The page list is what normally decides this: it calls back whenever its visible
 * range or its target width changes, and the view model renders that window. That
 * is enough while one document stays open — but not when a document is replaced
 * *in place*, which is what setting, changing or removing a password does, and what
 * the owner-password unlock does (#131, ADR-004 decision 6): the file is written,
 * then reopened with the new credentials so the open document matches the disk.
 *
 * Reopening clears every rendered bitmap, and the page list does not call back,
 * because from its side nothing has changed: the same range of the same-sized pages
 * at the same width. The result was a viewer full of blank white pages that no
 * amount of scrolling or zooming brought back — only closing the document and
 * opening it again (#146).
 *
 * So the window the list last asked for is carried across the reopen, through
 * [clampedTo], which fits it to the document that arrives.
 */
data class RenderWindow(val firstVisible: Int, val lastVisible: Int, val targetWidthPx: Int) {

    /**
     * This window as it applies to a document of [pageCount] pages: null when there
     * is nothing to render, and otherwise a range inside the document, in order.
     * The replacement can be shorter than what it replaced — removing a password
     * rewrites the file — so the old indices are not necessarily in it.
     */
    fun clampedTo(pageCount: Int): RenderWindow? {
        if (pageCount <= 0 || targetWidthPx <= 0) return null
        val top = pageCount - 1
        val first = firstVisible.coerceIn(0, top)
        val last = lastVisible.coerceIn(first, top)
        return if (first == firstVisible && last == lastVisible) this
        else RenderWindow(first, last, targetWidthPx)
    }
}
