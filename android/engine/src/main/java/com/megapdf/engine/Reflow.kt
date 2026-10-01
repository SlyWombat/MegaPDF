package com.megapdf.engine

import kotlinx.coroutines.withContext

/**
 * Contract 9's blocks, read into Kotlin — the input side of the #514 reflow spike's prototype.
 *
 * **This is spike surface, not a shipped API.** It exists so the prototype behind the app's debug
 * flag can lay contract 9's blocks out in a real Compose text view and be read by a person, which
 * is the one thing the corpus measures cannot answer (`docs/reading-mode-plan.md` §5 criterion 2:
 * whether the result *reads well*, as opposed to diffing well against `pdftotext`). Nothing in the
 * shipped app calls it.
 *
 * The core does the inferring; this does none. A block arrives in reading order with canonical text
 * (SDD §6.2 contract 6 — "exactly the concatenation of its spans"), and the only decisions here are
 * which blocks the reader is shown as text and which are shown as a picture of the page. Those are
 * `docs/reading-mode-plan.md` §2 tier 3's own rules, written once, in [ReflowItem.of].
 */

/** megapdf_block_kind. */
object BlockKind {
    const val HEADING = 1
    const val PARAGRAPH = 2
    const val LIST_ITEM = 3
    const val TABLE_ROW = 4
    const val FIGURE = 5
    const val PAGE_IMAGE = 6
    const val FURNITURE = 7
    const val FIELD = 8
}

/** megapdf_span.flags. */
object SpanFlags {
    const val BOLD = 1
    const val ITALIC = 2
    const val MONOSPACE = 4
    const val CELL_START = 8
    const val LINK = 16
    const val CELL_HEADER = 32
}

/** megapdf_structure_page_source. */
object BlockSource {
    const val HEURISTIC = 0
    const val TAGGED = 1
}

/**
 * One span of a block: a same-style run. Since #514 the font family is part of "same style", so
 * [font] describes every character of the span, not only its first.
 */
data class ReflowSpan(
    val text: String,
    /** The family as the document names it, subset tag and all; empty when no font was named. */
    val font: String,
    val flags: Int,
    val fontSize: Double,
    val sizeRatio: Double,
    val bounds: PdfRect,
) {
    val bold: Boolean get() = flags and SpanFlags.BOLD != 0
    val italic: Boolean get() = flags and SpanFlags.ITALIC != 0
    val monospace: Boolean get() = flags and SpanFlags.MONOSPACE != 0
    val cellStart: Boolean get() = flags and SpanFlags.CELL_START != 0
    val cellHeader: Boolean get() = flags and SpanFlags.CELL_HEADER != 0
    val link: Boolean get() = flags and SpanFlags.LINK != 0
}

/** One megapdf_block, with its strings and spans already read. */
data class ReflowBlock(
    val kind: Int,
    val level: Int,
    val page: Int,
    val objectIndex: Int,
    val continues: Boolean,
    val source: Int,
    val confidence: Int,
    val bounds: PdfRect,
    val text: String,
    /** The list marker as drawn, or a FIELD's fully qualified name; empty otherwise. */
    val marker: String,
    /** A FIGURE's /Alt text on a tagged page; empty otherwise. */
    val alt: String,
    val spans: List<ReflowSpan>,
)

/**
 * What the reader is shown, in order. The mapping is `docs/reading-mode-plan.md` §2 tier 3's
 * table, and the two "as a picture" cases are its forms rule and its scan rule.
 */
sealed interface ReflowItem {
    /** A heading at [level], scaled by the block's own size ratio. */
    data class Heading(val block: ReflowBlock, val level: Int) : ReflowItem

    /** A paragraph; [joinsPrevious] is contract 9's `continues`, so no break is drawn before it. */
    data class Paragraph(val block: ReflowBlock, val joinsPrevious: Boolean) : ReflowItem

    /** A list item: [marker] as the document drew it, indented by [block].level. */
    data class ListItem(val block: ReflowBlock, val marker: String) : ReflowItem

    /** A table row; [cells] are split on the spans' MEGAPDF_SPAN_CELL_START (tagged pages only). */
    data class TableRow(val block: ReflowBlock, val cells: List<String>, val isHeaderRow: Boolean) : ReflowItem

    /** An image, rendered from its own object; [alt] when the page was tagged. */
    data class Figure(val block: ReflowBlock, val alt: String) : ReflowItem

    /**
     * A page shown as a picture, with [reason] saying why, because the honesty of this is the whole
     * point of plan §5's criterion 4: the reader must be told the page could not be reflowed rather
     * than left to think the feature lost it. [fieldValues] are read out even for a form page, so a
     * screen reader still gets them (plan §2: "its filled values are still read out").
     */
    data class PageAsImage(
        val page: Int,
        val bounds: PdfRect,
        val reason: DeclineReason,
        val fieldValues: List<Pair<String, String>>,
    ) : ReflowItem
}

/** Why a page is shown as a picture instead of reflowed. */
enum class DeclineReason {
    /** The page carries form fields. Reflowing a form loses the thing that makes it a form. */
    FORM,

    /** The page has no text layer: a scan. OCR is #175 and out of scope. */
    NO_TEXT_LAYER,

    /** The page's confidence is under the gate the document was offered at. */
    LOW_CONFIDENCE,
}

/** A whole document's reflow, plus the numbers the spike reports about it. */
data class Reflow(
    val items: List<ReflowItem>,
    val bodySize: Double,
    /** Per page: the confidence the core gave it. */
    val confidence: Map<Int, Int>,
    /** Per page: MEGAPDF_STRUCTURE_SOURCE_*. */
    val source: Map<Int, Int>,
    /** Pages shown as a picture, and why — criterion 4's own count, per document. */
    val declined: Map<Int, DeclineReason>,
    val pageCount: Int,
) {
    /** The share of pages this document would have shown as a picture. */
    val declineShare: Double get() = if (pageCount == 0) 0.0 else declined.size.toDouble() / pageCount
}

/**
 * Reads every block of [structure] (a handle from [PdfDocument.loadStructure]) into Kotlin.
 *
 * One JNI call for the scalars, one for the bounds, three per block for its strings and spans —
 * deliberately not one per field, because a 500-page document is thousands of blocks and the
 * crossing is what costs. Call it off the main thread; it is a suspend function over the engine's
 * own dispatcher for the same reason every other engine call is.
 */
suspend fun readBlocks(structure: Long): List<ReflowBlock> = withContext(PdfEngine.pdfiumDispatcher) {
    val packed = PdfiumNative.nativeBlocksPacked(structure) ?: return@withContext emptyList()
    val bounds = PdfiumNative.nativeBlockBounds(structure) ?: return@withContext emptyList()
    val n = packed.size / 8
    (0 until n).map { i ->
        val o = i * 8
        val b = i * 4
        val spanCount = packed[o + 7]
        val spanInts = if (spanCount > 0) PdfiumNative.nativeSpansPacked(structure, i) else null
        val spanDoubles = if (spanCount > 0) PdfiumNative.nativeSpanMetrics(structure, i) else null
        val spanTexts = if (spanCount > 0) PdfiumNative.nativeSpanStrings(structure, i, false) else null
        val spanFonts = if (spanCount > 0) PdfiumNative.nativeSpanStrings(structure, i, true) else null
        val spans = if (spanInts == null || spanDoubles == null || spanTexts == null || spanFonts == null) {
            emptyList()
        } else {
            (0 until minOf(spanCount, spanTexts.size, spanFonts.size)).map { k ->
                ReflowSpan(
                    text = spanTexts[k],
                    font = spanFonts[k],
                    flags = spanInts[k * 2],
                    fontSize = spanDoubles[k * 6],
                    sizeRatio = spanDoubles[k * 6 + 1],
                    bounds = PdfRect(
                        spanDoubles[k * 6 + 2], spanDoubles[k * 6 + 3],
                        spanDoubles[k * 6 + 4], spanDoubles[k * 6 + 5],
                    ),
                )
            }
        }
        ReflowBlock(
            kind = packed[o], level = packed[o + 1], page = packed[o + 2], objectIndex = packed[o + 3],
            continues = packed[o + 4] != 0, source = packed[o + 5], confidence = packed[o + 6],
            bounds = PdfRect(bounds[b], bounds[b + 1], bounds[b + 2], bounds[b + 3]),
            text = PdfiumNative.nativeBlockString(structure, i, 0),
            marker = PdfiumNative.nativeBlockString(structure, i, 1),
            alt = PdfiumNative.nativeBlockString(structure, i, 2),
            spans = spans,
        )
    }
}

/**
 * Turns one document's blocks into what the reader sees — plan §2 tier 3's mapping table and its
 * two "show the page instead" rules, in one place so the prototype's UI makes no such decision of
 * its own.
 *
 * [confidenceGate] is the threshold below which a page is refused. The spike's criterion 1 was
 * meant to derive this number from the corpus; pass 0 to apply no gate at all, which is what the
 * prototype does by default so a reader sees the order the engine actually produced rather than
 * only the pages a gate already approved.
 */
fun reflowOf(
    blocks: List<ReflowBlock>,
    pages: Iterable<Int>,
    bodySize: Double,
    confidence: Map<Int, Int>,
    source: Map<Int, Int>,
    confidenceGate: Int = 0,
): Reflow {
    val byPage = blocks.groupBy { it.page }
    val declined = linkedMapOf<Int, DeclineReason>()
    var pageCount = 0
    for (page in pages) {
        pageCount++
        val onPage = byPage[page].orEmpty()
        val reason = when {
            // Forms first: a form page is a form page whether or not it also has text, and
            // reflowing it loses the thing that makes it a form (plan §2, and plan §7's note that
            // this will be read as "reflow doesn't work" on the documents people actually open).
            onPage.any { it.kind == BlockKind.FIELD } -> DeclineReason.FORM
            onPage.any { it.kind == BlockKind.PAGE_IMAGE } -> DeclineReason.NO_TEXT_LAYER
            confidenceGate > 0 && (confidence[page] ?: 100) < confidenceGate -> DeclineReason.LOW_CONFIDENCE
            else -> null
        }
        if (reason != null) declined[page] = reason
    }

    val items = mutableListOf<ReflowItem>()
    var lastDeclinedPage = -1
    for (b in blocks) {
        val reason = declined[b.page]
        if (reason != null) {
            if (b.page != lastDeclinedPage) {
                lastDeclinedPage = b.page
                val fields = byPage[b.page].orEmpty()
                    .filter { it.kind == BlockKind.FIELD }
                    .map { it.marker to it.text }
                items += ReflowItem.PageAsImage(
                    page = b.page,
                    bounds = byPage[b.page].orEmpty().firstOrNull { it.kind == BlockKind.PAGE_IMAGE }?.bounds
                        ?: PdfRect(0.0, 0.0, 0.0, 0.0),
                    reason = reason,
                    fieldValues = fields,
                )
            }
            continue
        }
        when (b.kind) {
            BlockKind.HEADING -> items += ReflowItem.Heading(b, b.level.coerceIn(1, 6))
            BlockKind.PARAGRAPH -> items += ReflowItem.Paragraph(b, b.continues)
            BlockKind.LIST_ITEM -> items += ReflowItem.ListItem(b, b.marker)
            BlockKind.TABLE_ROW -> items += ReflowItem.TableRow(b, cellsOf(b), b.spans.any { it.cellHeader })
            BlockKind.FIGURE -> items += ReflowItem.Figure(b, b.alt)
            // A PAGE_IMAGE that reached here is a page the rules above did not decline, which
            // cannot happen — the rule reads the same field. Left as a picture rather than
            // dropped, because dropping a page silently is the one thing plan §2 forbids.
            BlockKind.PAGE_IMAGE -> items += ReflowItem.PageAsImage(
                b.page, b.bounds, DeclineReason.NO_TEXT_LAYER, emptyList(),
            )
            // FURNITURE is not requested (default flags) and FIELD pages are declined above.
            else -> {}
        }
    }
    return Reflow(items, bodySize, confidence, source, declined, pageCount)
}

/**
 * The whole thing in one call, for the prototype: load contract 9 over [firstPage]..[pageCount),
 * read it, map it, free the handle. The structure handle never escapes, so the prototype cannot
 * leak one — which matters because a 500-page structure is hundreds of megabytes (the #514 spike's
 * criterion 3 measured it) and one leaked handle on a phone is the whole app's memory.
 */
suspend fun PdfDocument.readReflow(
    firstPage: Int,
    pageCount: Int,
    flags: Int = PdfiumNative.STRUCTURE_DEFAULT,
    confidenceGate: Int = 0,
): Reflow {
    val structure = loadStructure(firstPage, pageCount, flags)
    try {
        val blocks = readBlocks(structure)
        val confidence = mutableMapOf<Int, Int>()
        val source = mutableMapOf<Int, Int>()
        for (p in firstPage until firstPage + pageCount) {
            confidence[p] = PdfiumNative.nativeStructurePageConfidence(structure, p)
            source[p] = PdfiumNative.nativeStructurePageSource(structure, p)
        }
        return reflowOf(
            blocks = blocks,
            pages = firstPage until firstPage + pageCount,
            bodySize = PdfiumNative.nativeStructureBodySize(structure),
            confidence = confidence,
            source = source,
            confidenceGate = confidenceGate,
        )
    } finally {
        freeStructure(structure)
    }
}

/**
 * A TABLE_ROW's cells: the block's text split where a span says a new cell starts
 * (MEGAPDF_SPAN_CELL_START, tagged pages only). The block's own text is the cells joined by single
 * spaces, so this is the only way back to the cell boundaries.
 */
private fun cellsOf(b: ReflowBlock): List<String> {
    if (b.spans.isEmpty()) return listOf(b.text)
    val cells = mutableListOf<StringBuilder>()
    for (s in b.spans) {
        if (s.cellStart || cells.isEmpty()) cells += StringBuilder()
        cells.last().append(s.text)
    }
    return cells.map { it.toString().trim() }
}
