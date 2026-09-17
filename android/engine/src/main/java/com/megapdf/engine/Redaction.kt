package com.megapdf.engine

/**
 * An area marked for redaction (#173, SDD §6.2 contract 5).
 *
 * A mark is the core's own: it is never a page object and is never written to the file, so
 * a document saved with marks on it cannot carry them. That is the failure this feature
 * exists to stop — a file that *looks* redacted, carrying both the content and a set of
 * rectangles pointing at where the interesting content is — made impossible by the design
 * rather than left to a rule every screen has to remember.
 *
 * The app therefore draws marks itself, over the page raster. In exchange, marking costs no
 * content regeneration and invalidates no layout verdict, so it is instant on any page.
 */
data class RedactionMark(val markId: Int, val rect: PdfRect)

/** Why [PdfDocument.applyRedactions] refused. */
enum class RedactionRefusalReason(val code: Int) {
    /** A partly covered run in a Type 3 font, whose glyphs are content streams. */
    TYPE3_FONT(1),

    /** The glyphs outside the area cannot be drawn back in the run's own font. */
    FONT_CANNOT_REDRAW(2),

    /** The guard saw something outside the area move or change (#118, #128). */
    LAYOUT_GUARD(3),

    /**
     * A form XObject reaches into the area. PDFium does not write back an edit made inside
     * a form at all (tools/pdfium/README.md), so the redaction refuses rather than leave
     * the content behind.
     */
    FORM_XOBJECT(4),

    /** An image's stored pixels could not be read or written back. */
    IMAGE(5),

    /** An annotation or form field could not be removed with its value. */
    ANNOTATION(6),

    /** The engine refused a step. */
    ENGINE(7),

    /** A reason this build of the app does not know about. */
    UNKNOWN(0);

    companion object {
        fun of(code: Int): RedactionRefusalReason = entries.firstOrNull { it.code == code } ?: UNKNOWN
    }
}

/** One page the redaction would not touch, and why. */
data class RedactionRefusal(val pageIndex: Int, val reason: RedactionRefusalReason)

/** What a completed redaction removed, for the summary shown after saving. */
data class RedactionCounts(
    val areas: Int = 0,
    val pages: Int = 0,
    val characters: Int = 0,
    val textRuns: Int = 0,
    val partialRuns: Int = 0,
    val hiddenCopies: Int = 0,
    val images: Int = 0,
    val inlineImages: Int = 0,
    val softMasks: Int = 0,
    val paths: Int = 0,
    val shadings: Int = 0,
    val formXObjects: Int = 0,
    val annotations: Int = 0,
    val formFields: Int = 0,
    val links: Int = 0,
    val outlineEntries: Int = 0,
    val structureEntries: Int = 0,
    val pageLabels: Int = 0,
    val metadataFields: Int = 0,
)

/**
 * The result of applying every mark. [applied] is true when the redaction went through;
 * otherwise nothing was touched and [refusals] says why.
 */
data class RedactionReport(
    val applied: Boolean,
    val status: Int,
    val counts: RedactionCounts,
    val refusals: List<RedactionRefusal>,
) {
    /** True when the document lacks the modify permission (ADR-004 decision 2). */
    val needsPermission: Boolean get() = status == -6

    companion object {
        internal fun decode(packed: IntArray): RedactionReport {
            if (packed.size < 21) {
                return RedactionReport(applied = false, status = -1, counts = RedactionCounts(), refusals = emptyList())
            }
            val counts = RedactionCounts(
                areas = packed[1], pages = packed[2], characters = packed[3], textRuns = packed[4],
                partialRuns = packed[5], hiddenCopies = packed[6], images = packed[7], inlineImages = packed[8],
                softMasks = packed[9], paths = packed[10], shadings = packed[11], formXObjects = packed[12],
                annotations = packed[13], formFields = packed[14], links = packed[15], outlineEntries = packed[16],
                structureEntries = packed[17], pageLabels = packed[18], metadataFields = packed[19],
            )
            val refusalCount = packed[20]
            val refusals = (0 until refusalCount).mapNotNull { i ->
                val at = 21 + i * 2
                if (at + 1 >= packed.size) null
                else RedactionRefusal(packed[at], RedactionRefusalReason.of(packed[at + 1]))
            }
            return RedactionReport(applied = packed[0] == 0, status = packed[0], counts = counts, refusals = refusals)
        }
    }
}

/**
 * What the app says after a redaction: "3 areas redacted: 41 characters, 1 image". The
 * logic lives here and the words come from the caller, so every platform counts the same
 * things in the same order.
 */
object RedactionSummary {
    enum class Kind { CHARACTERS, IMAGES, FORM_FIELDS, ANNOTATIONS }

    fun removed(
        counts: RedactionCounts,
        plural: (Kind, Int) -> String,
        singular: (Kind) -> String,
        nothing: String,
    ): String {
        val parts = mutableListOf<String>()
        fun add(kind: Kind, n: Int) {
            if (n > 0) parts += if (n == 1) singular(kind) else plural(kind, n)
        }
        // Characters first: it is what people mark, and what they want to hear went.
        add(Kind.CHARACTERS, counts.characters)
        add(Kind.IMAGES, counts.images)
        add(Kind.FORM_FIELDS, counts.formFields)
        // Annotations less the fields already counted: a widget is both, and saying so
        // twice would read as more than was removed.
        add(Kind.ANNOTATIONS, maxOf(0, counts.annotations - counts.formFields))
        return if (parts.isEmpty()) nothing else parts.joinToString(", ")
    }
}
