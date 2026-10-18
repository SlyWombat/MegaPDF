package com.megapdf.android

import com.megapdf.engine.PdfRect
import com.megapdf.engine.SearchMatch

// Recognised text for the pages of a scan (#676), held in memory for as long as the document is
// open and answering Find with it. Nothing here is written to the document or to disk: this is a
// transient cache, not a text layer (that is #175). The iOS build's OcrText.swift is the same
// design; the recogniser itself (ML Kit, bundled model) is MlKitPageRecogniser.kt.
//
// Plain values and a pure matcher, so how a hit is found and where its rectangle falls are
// tested on the JVM without a recogniser, a document or a device.

/** One recognised word: where it stands in its line's text ([start] inclusive, [end] exclusive, UTF-16 units) and where it sits on the page, in points. */
data class OcrWord(val start: Int, val end: Int, val rect: PdfRect)

/** One recognised line of text, with the words that make it up. */
data class OcrLine(val text: String, val words: List<OcrWord>)

/** Everything the recogniser read on one page, in the order it read it. */
data class OcrPage(val lines: List<OcrLine>) {

    /**
     * Case-insensitive literal matches of [query], in reading order — the same contract as
     * `PdfPage.search`, so the caller merges the two without caring which produced a hit.
     *
     * A match is within one recognised line (the engine's own search can wrap across lines;
     * this does not, because recognised lines are not guaranteed to be one paragraph's
     * consecutive lines). Its rectangle is the union of the **words** it touches: a highlight
     * that is a word too wide is honest about what the recogniser told us, where one that
     * interpolates inside a word would claim a precision nobody measured.
     *
     * [besides] are rectangles the document's own text already matched: a read hit that mostly
     * lies on one is the same words seen twice (a text box rendered into the picture being read)
     * and is left out.
     */
    fun matches(query: String, besides: List<PdfRect> = emptyList()): List<SearchMatch> {
        if (query.isEmpty()) return emptyList()
        val found = ArrayList<SearchMatch>()
        for (line in lines) {
            var from = 0
            while (from <= line.text.length) {
                val at = line.text.indexOf(query, from, ignoreCase = true)
                if (at < 0) break
                val end = at + query.length
                val touched = line.words.filter { it.start < end && it.end > at }
                if (touched.isNotEmpty()) {
                    val rect = touched.drop(1).fold(touched.first().rect) { a, w -> a.union(w.rect) }
                    if (besides.none { rect.mostlyLiesOn(it) }) found += SearchMatch(listOf(rect))
                }
                // Past the match, not past one character: "aa" in "aaaa" is two hits, as in the engine.
                from = end
            }
        }
        return found
    }
}

internal fun PdfRect.union(other: PdfRect) = PdfRect(
    minOf(left, other.left), minOf(bottom, other.bottom),
    maxOf(right, other.right), maxOf(top, other.top),
)

/**
 * Whether the two overlap by at least half of the smaller one's area. Symmetric on purpose: a
 * recognised word box is wider than the glyphs the engine matched inside it, and either may be
 * the larger.
 */
internal fun PdfRect.mostlyLiesOn(other: PdfRect): Boolean {
    val w = minOf(right, other.right) - maxOf(left, other.left)
    val h = minOf(top, other.top) - maxOf(bottom, other.bottom)
    val area = minOf((right - left) * (top - bottom), (other.right - other.left) * (other.top - other.bottom))
    return w > 0 && h > 0 && area > 0 && (w * h) / area >= 0.5
}

/**
 * The recognised pages of one open document, by page index.
 *
 * **A missing entry means "not read yet", never "read and empty"**: a page that was read and
 * had no words is stored as an empty [OcrPage], so a scan is not redone for every search.
 */
class OcrCache(
    pages: Map<Int, OcrPage> = emptyMap(),
    textPages: Set<Int> = emptySet(),
) {
    private val pages = HashMap(pages)
    /** Pages found to carry text of their own, so a text document is not asked again on every keystroke. */
    private val textPages = HashSet(textPages)

    val isEmpty: Boolean get() = pages.isEmpty() && textPages.isEmpty()

    operator fun get(page: Int): OcrPage? = pages[page]
    operator fun set(page: Int, read: OcrPage) { pages[page] = read }
    fun hasText(page: Int): Boolean = page in textPages
    fun noteText(page: Int) { textPages += page }

    /**
     * Carried across a page operation, exactly like the rendered bitmaps (#174): recognised text
     * that stayed on the wrong page after a move would be a silent wrong answer.
     */
    fun shifted(shifts: List<PageShift>) = OcrCache(pages.shiftedBy(shifts), textPages.shiftedBy(shifts))

    fun remove(page: Int) {
        pages.remove(page)
        textPages.remove(page)
    }

    fun clear() {
        pages.clear()
        textPages.clear()
    }
}

/**
 * Whether a rendered page is a uniform sheet: a sampled grid in which almost nothing differs from
 * the corner pixel. A few specks of scanner noise stay under the line; a ruled form or a drawing
 * goes over it and is read, as is any real scan.
 */
object OcrBlank {
    fun isBlank(pixels: IntArray, width: Int, height: Int): Boolean {
        if (width <= 0 || height <= 0 || pixels.size < width * height) return false
        val corner = pixels[0]
        var sampled = 0
        var different = 0
        var y = 0
        while (y < height) {
            var x = 0
            while (x < width) {
                sampled++
                val p = pixels[y * width + x]
                val d = Math.abs(((p shr 16) and 0xFF) - ((corner shr 16) and 0xFF)) +
                    Math.abs(((p shr 8) and 0xFF) - ((corner shr 8) and 0xFF)) +
                    Math.abs((p and 0xFF) - (corner and 0xFF))
                if (d > 60) different++
                x += 4
            }
            y += 4
        }
        return sampled > 0 && different.toDouble() / sampled < 0.0002
    }
}
