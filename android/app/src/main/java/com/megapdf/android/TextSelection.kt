package com.megapdf.android

import com.megapdf.engine.PdfRect

/**
 * The text a person has selected on one page (#710): two carets into that page's selection
 * snapshot (`megapdf_selectable_*`, contract 11), in either order. The anchor stays where the
 * long-press put it and the focus is the end a handle is dragging; neither has to be the
 * smaller. One page only: a selection does not cross a page boundary on Android (a handle
 * dragged past the end of its page stops at the last character).
 */
data class TextSelection(val pageIndex: Int, val anchor: Int, val focus: Int) {
    val start: Int get() = minOf(anchor, focus)
    val end: Int get() = maxOf(anchor, focus)
    val isEmpty: Boolean get() = start == end

    /** The same selection with the handle that was at [moved] now at [caret]. */
    fun withHandleAt(movedIsStart: Boolean, caret: Int): TextSelection =
        if (movedIsStart) copy(anchor = end, focus = caret) else copy(anchor = start, focus = caret)
}

/** How far from a line, in points, a long-press still counts as being on its text. */
internal const val SELECT_TOLERANCE_POINTS = 8.0

/**
 * A point on the page as a fraction of its box (top-left origin, what a gesture reports) in the
 * core's crop space: points, bottom-left origin.
 */
internal fun pagePoint(size: PageSize, xFraction: Float, yFraction: Float): Pair<Double, Double> =
    Pair(xFraction * size.widthPoints, (1 - yFraction) * size.heightPoints)

/**
 * Where the selection's two handles hang, in page points: the bottom-left of the first line's
 * rectangle and the bottom-right of the last one's. Null for an empty selection, which has no
 * rectangles and so no handles.
 */
internal fun handleAnchors(rects: List<PdfRect>): Pair<Pair<Double, Double>, Pair<Double, Double>>? {
    val first = rects.firstOrNull() ?: return null
    val last = rects.last()
    return Pair(Pair(first.left, first.bottom), Pair(last.right, last.bottom))
}

/**
 * Where a dragged handle may put its caret: the start handle stops one character short of the
 * end and the end handle one past the start, so the two never cross and a handle keeps meaning
 * the same end for the whole drag. [caret] is what the core's hit test answered, -1 for none.
 */
internal fun clampedHandleCaret(caret: Int, movedIsStart: Boolean, selection: TextSelection, length: Int): Int {
    if (caret < 0) return if (movedIsStart) selection.start else selection.end
    return if (movedIsStart) caret.coerceIn(0, (selection.end - 1).coerceAtLeast(0))
    else caret.coerceIn((selection.start + 1).coerceAtMost(length), length)
}

/**
 * Where a handle goes when TalkBack moves it one word (#710): [movedIsStart] says which end,
 * [later] which way. [wordAtChar] answers the word, as a caret range, that a character belongs
 * to, or null for a character that is in no word (a space, a gap).
 *
 * The end handle goes to the end of the nearest word that ends past it (later) or before it
 * (earlier); the start handle to the start of the nearest word that begins after it or before it.
 * A handle part-way through a word therefore finishes or abandons that word first, and the two
 * ends keep the one-character gap [clampedHandleCaret] keeps. Null when there is no word to
 * move to, in the direction asked, without the ends meeting or leaving the page.
 */
internal fun steppedHandleCaret(
    selection: TextSelection,
    movedIsStart: Boolean,
    later: Boolean,
    length: Int,
    wordAtChar: (Int) -> IntRange?,
): Int? {
    val start = selection.start
    val end = selection.end
    if (movedIsStart) {
        if (later) {
            var i = start
            while (i < end) {
                val w = wordAtChar(i)
                if (w != null && w.first > start) return w.first.takeIf { it < end }
                i = if (w != null) maxOf(i + 1, w.last) else i + 1
            }
        } else {
            var i = start - 1
            while (i >= 0) {
                val w = wordAtChar(i)
                if (w != null && w.first < start) return w.first
                i = if (w != null) minOf(i - 1, w.first - 1) else i - 1
            }
        }
    } else {
        if (later) {
            var i = end
            while (i < length) {
                val w = wordAtChar(i)
                if (w != null && w.last > end) return w.last.coerceAtMost(length)
                i = if (w != null) maxOf(i + 1, w.last) else i + 1
            }
        } else {
            var i = end - 1
            while (i >= start) {
                val w = wordAtChar(i)
                if (w != null && w.last < end) return w.last.takeIf { it > start }
                i = if (w != null) minOf(i - 1, w.first - 1) else i - 1
            }
        }
    }
    return null
}

/**
 * Everything the page list needs to select text (#710), so [ViewerScreen] takes one defaulted
 * parameter instead of seven: previews and tests that do not select pass nothing.
 */
class TextSelectionHost(
    val selection: TextSelection? = null,
    val selectableFor: (pageIndex: Int) -> com.megapdf.engine.SelectableText? = { null },
    val canCopy: Boolean = true,
    val onLongPress: (pageIndex: Int, xFraction: Float, yFraction: Float) -> Unit = { _, _, _ -> },
    val onChange: (TextSelection) -> Unit = {},
    val onCopy: () -> Unit = {},
    val onClear: () -> Unit = {},
)
