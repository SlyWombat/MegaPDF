package com.megapdf.android

/**
 * How a page operation renumbered the document (#174), for everything the *app* keeps by page
 * index.
 *
 * The core keeps its own per-page state right on its own: an open page handle follows its page,
 * and so do that page's redaction marks, layout verdicts and detached objects (contract 10). What
 * it cannot see is this side's index-keyed state — the rendered bitmaps, the thumbnail grid, the
 * pages the selection holds, the search hits, the page sizes the list lays out from — and the
 * contract says plainly that the app renumbers those itself, "exactly as it would after a page op
 * made elsewhere".
 *
 * This is that renumbering, as a value rather than as code spread through the view model: one
 * small type per thing a page operation can do to the order, each one mirroring the remap the
 * core's own `RenumberPages` applies at the same moment, so the two cannot drift. Being a pure
 * value, it is also the part of #174 that is tested on the JVM rather than on an emulator.
 *
 * An operation reports what its last apply or revert did as a *list* of these, in the order it
 * happened, because deleting a selection of five pages is one undo step and five renumberings.
 */
sealed interface PageShift {

    /** Where the page that stood at [index] stands afterwards; -1 when it is gone. */
    fun map(index: Int): Int

    /** How the page count changed. */
    val countDelta: Int

    /** A page was deleted. */
    data class Deleted(val at: Int) : PageShift {
        override fun map(index: Int): Int = when {
            index == at -> -1
            index > at -> index - 1
            else -> index
        }

        override val countDelta: Int get() = -1
    }

    /** [count] pages arrived at [at] — a blank page, or the pages of another file. */
    data class Inserted(val at: Int, val count: Int = 1) : PageShift {
        override fun map(index: Int): Int = if (index >= at) index + count else index
        override val countDelta: Int get() = count
    }

    /**
     * The page at [from] now stands at [to]; the pages between them shifted by one. The same
     * arithmetic `megapdf_page_move` renumbers the core's own state with.
     */
    data class Moved(val from: Int, val to: Int) : PageShift {
        override fun map(index: Int): Int = when {
            index == from -> to
            from < to -> if (index > from && index <= to) index - 1 else index
            else -> if (index >= to && index < from) index + 1 else index
        }

        override val countDelta: Int get() = 0
    }
}

/** Every page still where it was — what a rotation does, and the identity of a fold over shifts. */
fun List<PageShift>.mapIndex(index: Int): Int =
    fold(index) { at, shift -> if (at < 0) at else shift.map(at) }

/** The page count after these shifts. */
fun List<PageShift>.mapCount(count: Int): Int = fold(count) { n, shift -> n + shift.countDelta }

/**
 * A map keyed by page index, renumbered: entries for deleted pages are dropped. Used for the
 * rendered page bitmaps and the thumbnails, so a delete or a move does not throw away the
 * pictures of every page that did not move.
 */
fun <T> Map<Int, T>.shiftedBy(shifts: List<PageShift>): Map<Int, T> {
    if (shifts.isEmpty()) return this
    val out = HashMap<Int, T>(size)
    for ((index, value) in this) {
        val to = shifts.mapIndex(index)
        if (to >= 0) out[to] = value
    }
    return out
}

/** A set of page indices, renumbered: deleted pages leave it. The grid's selection. */
fun Set<Int>.shiftedBy(shifts: List<PageShift>): Set<Int> {
    if (shifts.isEmpty()) return this
    return mapNotNull { shifts.mapIndex(it).takeIf { to -> to >= 0 } }.toSet()
}

/**
 * A per-page list — the page sizes the viewer lays out from — with one shift applied. [inserted]
 * is the value for each page that arrived, which only the engine can answer, so the caller reads
 * those from the document and hands them in; it is ignored for the other shifts.
 */
fun <T> List<T>.shiftedBy(shift: PageShift, inserted: List<T> = emptyList()): List<T> = when (shift) {
    is PageShift.Deleted -> if (shift.at in indices) toMutableList().apply { removeAt(shift.at) } else this
    is PageShift.Inserted -> toMutableList().apply { addAll(shift.at.coerceIn(0, size), inserted) }
    is PageShift.Moved -> toMutableList().apply { add(shift.to, removeAt(shift.from)) }
}
