package com.megapdf.android

/**
 * Find on a scanned page (#676): reads a page that has no text of its own, once per open
 * document, and answers searches from what was read. The view model owns one and delegates; it
 * is a class of its own so everything it decides is tested on the JVM with the recogniser
 * scripted, as iOS does through a seam on the model.
 *
 * [hasOwnText] says whether the page carries text of its own, **not counting boxes the person
 * added in MegaPDF**: a text box laid over a scan is not a text layer, the scan under it still
 * needs reading, and Find searches the box as ordinary text. [read] renders and recognises a
 * page, or answers null when it could not.
 */
class ScanSearcher(
    private val hasOwnText: suspend (Int) -> Boolean,
    private val read: suspend (Int) -> OcrPage?,
) {
    var cache = OcrCache()
        private set

    /**
     * Bumped by everything that can make a read in flight wrong — a page edit, a renumbering, a
     * close — so a recognition that finishes afterwards is used once and not kept.
     */
    private var generation = 0

    /**
     * Hits found in what the recogniser reads of [page] when it has no text of its own. Empty
     * for a page with text, for one that could not be read, and for a read that an edit
     * overtook. [own] is what the document's own text already found on the page: a read hit that
     * lands on one of those is the same words seen twice and is dropped.
     */
    suspend fun hits(page: Int, query: String, own: List<SearchHit>): List<SearchHit> {
        if (cache.hasText(page)) return emptyList()
        val scanned: OcrPage
        val cached = cache[page]
        if (cached != null) {
            scanned = cached
        } else {
            val started = generation
            if (hasOwnText(page)) {
                if (started == generation) cache.noteText(page)
                return emptyList()
            }
            val fresh = read(page) ?: return emptyList()
            if (started != generation) return emptyList()
            cache[page] = fresh
            scanned = fresh
        }
        return scanned.matches(query, own.flatMap { it.rects })
            .map { SearchHit(page, it.rects, fromScan = true) }
    }

    /** A page operation renumbered the document: what was read follows its page. */
    fun renumbered(shifts: List<PageShift>) {
        generation++
        cache = cache.shifted(shifts)
    }

    /** A page's content changed (an edit, a rotation): what was read from it is stale. */
    fun pageChanged(page: Int) {
        generation++
        cache.remove(page)
    }

    /** Everything is stale: a redaction was applied, the pages were re-read, or the document closed. */
    fun clear() {
        generation++
        cache.clear()
    }

    companion object {
        /** The page's own hits and the ones read from it, top of the page first. */
        fun inReadingOrder(own: List<SearchHit>, read: List<SearchHit>): List<SearchHit> =
            if (read.isEmpty()) own
            else (own + read).sortedWith(
                compareByDescending<SearchHit> { it.rects.firstOrNull()?.top ?: 0.0 }
                    .thenBy { it.rects.firstOrNull()?.left ?: 0.0 },
            )
    }
}
