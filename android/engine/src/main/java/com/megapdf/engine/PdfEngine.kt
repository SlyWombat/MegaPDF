package com.megapdf.engine

import android.graphics.Bitmap
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.withContext
import java.io.OutputStream
import java.util.concurrent.Executors

/**
 * Engine facade over PDFium, reimplementing the fill-check-sign subset of the
 * desktop `IPdfEngine` (behavioral reference: `PdfiumEngine.cs`; contracts: SDD §6.2).
 *
 * PDFium is not thread-safe, so every native call runs on one dedicated thread —
 * the mobile analog of the desktop's global `PdfiumLibrary.Lock`. The public API
 * is `suspend` throughout and callers may use any dispatcher.
 */
class PdfEngine {
    internal val dispatcher: CoroutineDispatcher get() = pdfiumDispatcher

    init {
        ensureInitialized()
    }

    /**
     * Opens a document from its full bytes (the caller reads them from SAF or assets;
     * the source file is never held open — same rationale as the desktop engine).
     * The password crosses JNI as UTF-8 bytes, not a jstring (#131, ADR-004 decision 9).
     * @throws PdfPasswordException wrong or missing password
     * @throws PdfLoadException corrupt or unreadable document, or security PDFium cannot open
     */
    suspend fun open(bytes: ByteArray, password: String? = null): PdfDocument =
        withContext(dispatcher) {
            val handle = PdfiumNative.nativeOpen(bytes, password?.nulTerminatedUtf8())
            if (handle == 0L) {
                val error = PdfiumNative.nativeLastError()
                if (error == PdfiumNative.ERR_PASSWORD) throw PdfPasswordException()
                throw PdfLoadException(error)
            }
            PdfDocument(this@PdfEngine, handle)
        }

    /**
     * Opens [bytes] with the credentials [like] was opened with (#132): a saved copy of a
     * protected document is still protected, so reading it back needs the same password.
     * @throws PdfPasswordException the bytes need a different password
     * @throws PdfLoadException corrupt or unreadable document
     */
    suspend fun openLike(like: PdfDocument, bytes: ByteArray): PdfDocument =
        withContext(dispatcher) {
            val handle = PdfiumNative.nativeOpenLike(like.nativeHandle(), bytes)
            if (handle == 0L) {
                val error = PdfiumNative.nativeLastError()
                if (error == PdfiumNative.ERR_PASSWORD) throw PdfPasswordException()
                throw PdfLoadException(error)
            }
            PdfDocument(this@PdfEngine, handle)
        }

    companion object {
        /**
         * The pixel size to render a page at when it would ideally be [idealWidth] ×
         * [idealHeight]: the shared core's aspect-preserving clamp (16,384 px a side,
         * 32 MP, #93/#111), never smaller than 1 × 1. The view scales the bitmap up
         * over the remaining distance.
         */
        fun renderSize(idealWidth: Double, idealHeight: Double): Pair<Int, Int> {
            val wh = PdfiumNative.nativeRenderSize(idealWidth, idealHeight)
            return wh[0] to wh[1]
        }

        private var initialized = false

        /**
         * The one thread every native call runs on, for the whole process (#54).
         *
         * Deliberately shared rather than one per engine. PDFium is initialised
         * once per process — `nativeInit` below is static and guarded — so a
         * per-engine thread bought no isolation, and nothing ever shut those
         * executors down: every PdfEngine left a live non-daemon thread behind.
         *
         * A daemon thread, so it can never hold the process open by itself.
         */
        internal val pdfiumDispatcher: CoroutineDispatcher =
            Executors.newSingleThreadExecutor { r ->
                Thread(r, "pdfium").apply { isDaemon = true }
            }.asCoroutineDispatcher()

        /**
         * Scope for teardown that must finish even though the caller is gone.
         * Process-lifetime by design: the work it runs is short, native, and
         * releasing memory that would otherwise leak.
         */
        private val teardownScope = CoroutineScope(pdfiumDispatcher + SupervisorJob())

        /**
         * Closes a document without waiting for it (#53).
         *
         * For callers whose own scope has already been cancelled — a ViewModel in
         * `onCleared`, say — where riding on that scope would skip the close and
         * leak the native document, but blocking to wait for it would stall the
         * caller's thread behind whatever the single engine thread is doing.
         */
        fun closeDetached(document: PdfDocument) {
            teardownScope.launch { document.close() }
        }

        @Synchronized
        fun ensureInitialized() {
            if (!initialized) {
                PdfiumNative.nativeInit()
                initialized = true
            }
        }
    }
}

class PdfDocument internal constructor(
    private val engine: PdfEngine,
    private val handle: Long,
) {
    private var closed = false

    /** The native handle, for opening a saved copy like this document (#132). */
    internal fun nativeHandle(): Long {
        check(!closed) { "document is closed" }
        return handle
    }

    suspend fun pageCount(): Int = withContext(engine.dispatcher) {
        check(!closed) { "document is closed" }
        PdfiumNative.nativePageCount(handle)
    }

    suspend fun openPage(index: Int): PdfPage = withContext(engine.dispatcher) {
        check(!closed) { "document is closed" }
        val page = PdfiumNative.nativeOpenPage(handle, index)
        check(page != 0L) { "failed to load page $index" }
        PdfPage(
            engine,
            page,
            widthPoints = PdfiumNative.nativePageWidth(page),
            heightPoints = PdfiumNative.nativePageHeight(page),
        )
    }

    /**
     * Serializes the document (`FPDF_SaveAsCopy`, full rewrite — the desktop default).
     * Atomicity is the caller's job: write to a temp destination first (#18).
     */
    suspend fun save(out: OutputStream): Unit = withContext(engine.dispatcher) {
        check(!closed) { "document is closed" }
        if (!PdfiumNative.nativeSave(handle, out)) throw PdfSaveException()
        out.flush()
    }

    /** Whether the document is encrypted and what this open may do (#131). */
    suspend fun security(): PdfSecurity = withContext(engine.dispatcher) {
        check(!closed) { "document is closed" }
        val v = PdfiumNative.nativeSecurityInfo(handle)
        PdfSecurity(isEncrypted = v[0] != 0, revision = v[1], permissions = v[2], hasFullAccess = v[3] != 0)
    }

    /**
     * Writes a copy encrypted with AES-256 under new passwords, in place of any security
     * the document had (#131). The owner password opens it with every permission; null
     * means the same as the user password. The copy no longer opens like this document:
     * verify it with the new password.
     * @throws PdfRestrictedException without full access
     */
    suspend fun saveWithSecurity(
        out: OutputStream, userPassword: String, ownerPassword: String?, permissions: Int,
    ): Unit = withContext(engine.dispatcher) {
        check(!closed) { "document is closed" }
        checkSecurityStatus(
            PdfiumNative.nativeSaveWithSecurity(
                handle, out, userPassword.nulTerminatedUtf8(), ownerPassword?.nulTerminatedUtf8(), permissions,
            ),
        )
        out.flush()
    }

    /**
     * Writes a copy with no security (#131).
     * @throws PdfRestrictedException without full access
     */
    suspend fun saveWithoutSecurity(out: OutputStream): Unit = withContext(engine.dispatcher) {
        check(!closed) { "document is closed" }
        checkSecurityStatus(PdfiumNative.nativeSaveWithoutSecurity(handle, out))
        out.flush()
    }

    // NonCancellable: close must run even from a cancelled caller, or the native
    // document leaks.
    suspend fun close(): Unit = withContext(engine.dispatcher + NonCancellable) {
        if (!closed) {
            closed = true
            PdfiumNative.nativeCloseDocument(handle)
        }
    }
}

class PdfPage internal constructor(
    private val engine: PdfEngine,
    private val handle: Long,
    val widthPoints: Double,
    val heightPoints: Double,
) {
    private var closed = false

    /**
     * Renders the full page into [bitmap] (must be ARGB_8888), scaled to the bitmap's
     * pixel size: white ground, page content, then live form-field values. Size the
     * bitmap with [PdfEngine.renderSize] first — a raster past the clamp is refused.
     */
    suspend fun render(bitmap: Bitmap): Unit = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        require(bitmap.config == Bitmap.Config.ARGB_8888) { "bitmap must be ARGB_8888" }
        check(PdfiumNative.nativeRenderPage(handle, bitmap)) { "render failed" }
    }

    /** Checkbox and radio widgets on this page. */
    suspend fun formFields(): List<FormField> = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        val packed = PdfiumNative.nativeFormFieldsPacked(handle)
        (packed.indices step 6).map { i ->
            FormField(
                isRadio = packed[i] == 3.0,  // FPDF_FORMFIELD_RADIOBUTTON
                isChecked = packed[i + 1] != 0.0,
                rect = PdfRect(packed[i + 2], packed[i + 3], packed[i + 4], packed[i + 5]),
            )
        }
    }

    /**
     * Simulated click at page coordinates (points, bottom-left origin) — toggles
     * the form field under the point via PDFium's form machinery, keeping radio
     * groups and appearance states consistent.
     */
    suspend fun clickAt(x: Double, y: Double): Unit = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        PdfiumNative.nativeClickAt(handle, x, y)
    }

    /** Drawn (non-form) checkbox candidates per the SDD §6.2 heuristic. */
    suspend fun detectCheckboxSquares(): List<PdfRect> = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        val packed = PdfiumNative.nativeDetectSquaresPacked(handle)
        (packed.indices step 4).map { i ->
            PdfRect(packed[i], packed[i + 1], packed[i + 2], packed[i + 3])
        }
    }

    /** Places an X check-mark stamp over [square], tagged `MegaPDF_Id` = [id]. */
    suspend fun addCheckMark(square: PdfRect, id: String): Unit =
        withContext(engine.dispatcher) {
            check(!closed) { "page is closed" }
            check(
                PdfiumNative.nativeAddCheckMark(
                    handle, square.left, square.bottom, square.right, square.top, id
                )
            ) { "failed to add check mark" }
        }

    /** All MegaPDF-placed stamps on this page (desktop or Android alike). */
    suspend fun stamps(): List<Stamp> = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        val ids = PdfiumNative.nativeAnnotIds(handle)
        val rects = PdfiumNative.nativeAnnotRectsPacked(handle)
        ids.withIndex()
            .filter { it.value.isNotEmpty() }
            .map { (i, id) ->
                Stamp(i, id, PdfRect(rects[i * 4], rects[i * 4 + 1], rects[i * 4 + 2], rects[i * 4 + 3]))
            }
    }

    /** Removes the annotation at [annotIndex] (from [stamps]). */
    suspend fun removeAnnot(annotIndex: Int): Unit = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        check(PdfiumNative.nativeRemoveAnnot(handle, annotIndex)) { "failed to remove annot" }
    }

    /**
     * Places an image stamp over [rect], tagged `MegaPDF_Id` = [id] (`sig:` for
     * signatures). [pixels] is ARGB, [pixelWidth] x [pixelHeight].
     */
    suspend fun addImageStamp(
        pixels: IntArray, pixelWidth: Int, pixelHeight: Int, rect: PdfRect, id: String,
    ): Unit = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        require(pixels.size == pixelWidth * pixelHeight) { "pixel buffer size mismatch" }
        check(
            PdfiumNative.nativeAddImageStamp(
                handle, pixels, pixelWidth, pixelHeight,
                rect.left, rect.bottom, rect.right, rect.top, id,
            )
        ) { "failed to add image stamp" }
    }

    /**
     * Reads a stamp's image back at native resolution ([width, height, argb...])
     * — the desktop move/resize pattern: read back, remove, re-add under the
     * same id, so repeated moves never lose resolution. Null when the annot has
     * no image object.
     */
    suspend fun stampImagePacked(annotIndex: Int): IntArray? =
        withContext(engine.dispatcher) {
            check(!closed) { "page is closed" }
            PdfiumNative.nativeGetStampImagePacked(handle, annotIndex)
        }

    // ---- Added text (#34) ------------------------------------------------

    /**
     * Places [text] with its baseline starting at ([x], [y]) in crop space, in
     * [fontName], tagged with the given [id]. The representation matches the
     * desktop's, so the box is movable text in MegaPDF for Windows and
     * searchable text everywhere else.
     *
     * [fontName] must be one of [STANDARD_FONTS]; the face is recorded on the
     * mark so every platform reads back exactly what was chosen (#43).
     */
    suspend fun addTextBox(
        text: String, fontSize: Double, x: Double, y: Double, id: String,
        fontName: String = DEFAULT_FONT,
    ): Unit = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        require(text.isNotBlank()) { "text must not be blank" }
        require(fontName in STANDARD_FONTS) { "unsupported font: $fontName" }
        check(PdfiumNative.nativeAddTextBox(handle, text.trim(), fontName, fontSize, x, y, id)) {
            "failed to add text box"
        }
    }

    /** Every MegaPDF text box on this page, in page-object order. */
    suspend fun textBoxes(): List<TextBox> = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        val ids = PdfiumNative.nativeTextBoxIds(handle)
        val texts = PdfiumNative.nativeTextBoxTexts(handle)
        val fonts = PdfiumNative.nativeTextBoxFonts(handle)
        val packed = PdfiumNative.nativeTextBoxRectsPacked(handle)
        ids.mapIndexed { i, id ->
            TextBox(
                id = id,
                text = texts.getOrElse(i) { "" },
                rect = PdfRect(packed[i * 5], packed[i * 5 + 1],
                               packed[i * 5 + 2], packed[i * 5 + 3]),
                fontSize = packed[i * 5 + 4],
                fontName = fonts.getOrElse(i) { DEFAULT_FONT },
            )
        }
    }

    /** Moves the box with [id] so its lower-left corner lands on ([x], [y]). */
    suspend fun moveTextBox(id: String, x: Double, y: Double): Unit =
        withContext(engine.dispatcher) {
            check(!closed) { "page is closed" }
            check(PdfiumNative.nativeMoveTextBox(handle, id, x, y)) { "failed to move text box" }
        }

    /** Removes the box with [id]. Already gone is success — undo may race a render. */
    suspend fun removeTextBox(id: String): Unit = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        check(PdfiumNative.nativeRemoveTextBox(handle, id)) { "failed to remove text box" }
    }

    /**
     * Removes the annotation carrying `MegaPDF_Id` == [id], wherever it now sits.
     * Indices shift as annots come and go, so reversible edits address by id (#34).
     * A no-op when it is already gone.
     */
    suspend fun removeAnnot(id: String) {
        val found = stamps().firstOrNull { it.id == id } ?: return
        removeAnnot(found.annotIndex)
    }

    // ---- The document's own text (#114) ------------------------------------

    /**
     * The page's visual lines of body text, top to bottom, as the shared core merges
     * them (#106). MegaPDF text boxes are left out — they have their own editing path.
     */
    suspend fun textLines(): List<TextLine> = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        val packed = PdfiumNative.nativeTextRunsPacked(handle)
        val strings = PdfiumNative.nativeTextRunStrings(handle)
        if (packed.isEmpty()) return@withContext emptyList()
        var i = 0
        val runCount = packed[i++].toInt()
        val runs = ArrayList<TextRun>(runCount)
        for (r in 0 until runCount) {
            runs += TextRun(
                objectIndex = packed[i].toInt(),
                text = strings.getOrElse(r * 2) { "" }.trim(),
                rect = PdfRect(packed[i + 1], packed[i + 2], packed[i + 3], packed[i + 4]),
                fontSize = packed[i + 5],
                fontName = strings.getOrElse(r * 2 + 1) { "" },
                isTextBox = packed[i + 6] != 0.0,
                endsWithSeparator = packed[i + 7] != 0.0,
            )
            i += 8
        }
        val lineCount = packed[i++].toInt()
        val lines = ArrayList<TextLine>(lineCount)
        repeat(lineCount) {
            val rect = PdfRect(packed[i], packed[i + 1], packed[i + 2], packed[i + 3])
            val n = packed[i + 4].toInt()
            val members = (0 until n).map { runs[packed[i + 5 + it].toInt()] }.filter { !it.isTextBox }
            i += 5 + n
            if (members.isNotEmpty()) lines += TextLine(members, rect)
        }
        lines
    }

    /**
     * Replaces the text of the run at [objectIndex] — in the run's own font when it can
     * carry the text, otherwise the closest standard face (#116). The edited run is a new
     * object at the same index; the untouched original comes back in the result so an
     * undo can put it back byte-identical with [restoreOriginal] (#117).
     */
    suspend fun setText(objectIndex: Int, text: String): TextEdit = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        require(text.isNotEmpty()) { "text must not be empty" }
        val result = PdfiumNative.nativeSetText(handle, objectIndex, text)
        if (result.size == 3 && result[0] == -5L) throw TextLayoutException(lastLayoutVerdict())
        check(result.size == 3 && result[0] == 0L && result[2] != 0L) { "failed to change text" }
        TextEdit(
            if (result[1] == 1L) TextEditOutcome.SUBSTITUTED else TextEditOutcome.IN_PLACE,
            DetachedObject(result[2]),
        )
    }

    /**
     * Whether the run at [objectIndex] can be changed without PDFium disturbing the rest
     * of the page when it rewrites it (#118). Asked before the editor opens.
     */
    suspend fun textEditable(objectIndex: Int): Boolean = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        PdfiumNative.nativeTextEditable(handle, objectIndex) == 1
    }

    /**
     * [textEditable] with its reason (#128): the same cached dry run. Null when the object is
     * not text, which [textEditable] answers with false.
     */
    suspend fun layoutVerdict(objectIndex: Int): LayoutVerdict? = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        LayoutVerdict.fromPacked(PdfiumNative.nativeTextEditableReason(handle, objectIndex))
    }

    /**
     * Whether regenerating this page's content, with nothing changed, would change how it looks
     * (#139). Whiteouts, text boxes, signatures and removals regenerate the page too and are never
     * refused; ask before the first such change on a page and warn when [LayoutVerdict.editable] is
     * false. The same dry run and budgets as [layoutVerdict], cached per page until the page changes.
     * Null when the page cannot be judged.
     */
    suspend fun pageRegenerationVerdict(): LayoutVerdict? = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        LayoutVerdict.fromPacked(PdfiumNative.nativePageRegenerationVerdict(handle))
    }

    /** Why the core's last call on this thread was refused; call it straight after, with no suspension between. */
    private fun lastLayoutVerdict(): LayoutVerdict? = LayoutVerdict.fromPacked(PdfiumNative.nativeLastLayoutVerdict())

    /** Undoes [setText]: takes the edited run at [objectIndex] off and puts [original] back. */
    suspend fun restoreOriginal(original: DetachedObject, objectIndex: Int): Unit =
        withContext(engine.dispatcher) {
            check(!closed) { "page is closed" }
            check(original.handle != 0L) { "the original was already restored" }
            check(PdfiumNative.nativeRestoreOriginal(handle, original.handle, objectIndex)) {
                "failed to restore the original text"
            }
            original.handle = 0L
        }

    /** Removes the page object at [objectIndex] and keeps it alive for undo. */
    suspend fun detachObject(objectIndex: Int): DetachedObject = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        val detached = PdfiumNative.nativeDetachObject(handle, objectIndex)
        check(detached != 0L) { "failed to remove the text" }
        DetachedObject(detached)
    }

    /** Puts a detached object back at [objectIndex], byte-identical. */
    suspend fun restoreObject(detached: DetachedObject, objectIndex: Int): Unit =
        withContext(engine.dispatcher) {
            check(!closed) { "page is closed" }
            check(detached.handle != 0L) { "the object was already restored" }
            check(PdfiumNative.nativeRestoreObject(handle, detached.handle, objectIndex)) {
                "failed to restore the text"
            }
            detached.handle = 0L
        }

    /**
     * Retypes a visual line (#136): [text] goes into the run at the first of [objectIndices], the
     * line's other runs leave the page, and so do the hidden copies a producer drew under any of
     * them for fake bold, an outline or a shadow, all in one call. Taking them one at a time would
     * move the indices of those still to be taken. [restoreDetached] undoes it byte-identical.
     */
    suspend fun setLineText(objectIndices: List<Int>, text: String): TextEdit = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        require(objectIndices.isNotEmpty()) { "a line has at least one run" }
        require(text.isNotEmpty()) { "text must not be empty" }
        val result = PdfiumNative.nativeSetLineText(handle, objectIndices.toIntArray(), text)
        if (result.size == 3 && result[0] == -5L) throw TextLayoutException(lastLayoutVerdict())
        check(result.size == 3 && result[0] == 0L && result[2] != 0L) { "failed to change text" }
        TextEdit(
            if (result[1] == 1L) TextEditOutcome.SUBSTITUTED else TextEditOutcome.IN_PLACE,
            DetachedObject(result[2]),
        )
    }

    /** Removes a visual line's runs with the hidden copies drawn under them (#136), kept for [restoreDetached]. */
    suspend fun detachTextRuns(objectIndices: List<Int>): DetachedObject = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        require(objectIndices.isNotEmpty()) { "a line has at least one run" }
        val detached = PdfiumNative.nativeDetachTextRuns(handle, objectIndices.toIntArray())
        if (detached == 0L) {
            // A layout refusal says so on this thread (#128); anything else is a failed removal.
            val refusal = lastLayoutVerdict()
            if (refusal != null && !refusal.editable) throw TextLayoutException(refusal)
        }
        check(detached != 0L) { "failed to remove the text" }
        DetachedObject(detached)
    }

    /** Undoes [setLineText] or [detachTextRuns]: every object back at its own index, the edited run off first. */
    suspend fun restoreDetached(detached: DetachedObject): Unit =
        withContext(engine.dispatcher) {
            check(!closed) { "page is closed" }
            check(detached.handle != 0L) { "the text was already restored" }
            check(PdfiumNative.nativeRestoreDetached(handle, detached.handle)) {
                "failed to restore the text"
            }
            detached.handle = 0L
        }

    /**
     * Case-insensitive literal substring search on this page (#26), matches in
     * reading order. A match that wraps across lines carries one rect per line.
     */
    suspend fun search(query: String): List<SearchMatch> = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        if (query.isEmpty()) return@withContext emptyList()
        val packed = PdfiumNative.nativeSearchPagePacked(handle, query)
        val matches = ArrayList<SearchMatch>()
        var i = 0
        while (i < packed.size) {
            val rectCount = packed[i].toInt()
            i++
            matches += SearchMatch(
                (0 until rectCount).map { r ->
                    val j = i + r * 4
                    PdfRect(packed[j], packed[j + 1], packed[j + 2], packed[j + 3])
                }
            )
            i += rectCount * 4
        }
        matches
    }

    // NonCancellable: a cancelled render job's finally-block close must still run.
    suspend fun close(): Unit = withContext(engine.dispatcher + NonCancellable) {
        if (!closed) {
            closed = true
            PdfiumNative.nativeClosePage(handle)
        }
    }
}

/** Rectangle in PDF points, bottom-left origin (top > bottom). */
data class PdfRect(val left: Double, val bottom: Double, val right: Double, val top: Double) {
    fun contains(x: Double, y: Double): Boolean = x in left..right && y in bottom..top

    /** The same rect with [margin] points added on every side — touch targets. */
    fun grownBy(margin: Double): PdfRect =
        PdfRect(left - margin, bottom - margin, right + margin, top + margin)
    val centerX: Double get() = (left + right) / 2
    val centerY: Double get() = (bottom + top) / 2
}

/** A checkbox or radio-button widget on a page. */
data class FormField(val isRadio: Boolean, val isChecked: Boolean, val rect: PdfRect)

/** A MegaPDF-placed stamp annotation (`mark:` check mark or `sig:` signature). */
data class Stamp(val annotIndex: Int, val id: String, val rect: PdfRect)

/** One search hit on a page; several rects when the hit wraps across lines. */
data class SearchMatch(val rects: List<PdfRect>)

/** One run of body text: a single text object on the page, crop space (#114). */
data class TextRun(
    val objectIndex: Int,
    /** What the run says, with PDFium's generated separator trimmed off. */
    val text: String,
    val rect: PdfRect,
    val fontSize: Double,
    val fontName: String,
    val isTextBox: Boolean,
    /** Whether the page reads a separator after this run (a word boundary). */
    val endsWithSeparator: Boolean,
)

/** A visual line: same-baseline runs, left to right. */
data class TextLine(val runs: List<TextRun>, val rect: PdfRect) {
    /** The line as one string: runs joined with a space only where the page separates them. */
    val text: String
        get() = buildString {
            runs.forEachIndexed { i, run ->
                append(run.text)
                if (run.endsWithSeparator && i < runs.size - 1) append(' ')
            }
        }
}

/** How a text edit landed. */
enum class TextEditOutcome {
    /** The run's own font drew the new text. */
    IN_PLACE,
    /** That font could not carry it, so a similar standard face was used. */
    SUBSTITUTED,
}

/** A page object kept alive by the core for undo; restoring consumes it. */
class DetachedObject internal constructor(internal var handle: Long)

/** The result of [PdfPage.setText]: how it landed, and the original for undo. */
data class TextEdit(val outcome: TextEditOutcome, val original: DetachedObject)

/** A MegaPDF text box (#34): added text, addressed by its stable [id]. */
data class TextBox(
    val id: String,
    val text: String,
    val rect: PdfRect,
    val fontSize: Double,
    /** The face it was written in; [DEFAULT_FONT] for boxes that predate #43. */
    val fontName: String = DEFAULT_FONT,
)

/**
 * The base-14 face a text box may be written in (#43).
 *
 * Three, not fourteen: SDD §3.1 keeps formatting controls out of the app, and a
 * choice between serif, sans and monospace is what "make this match the form I
 * am filling in" actually needs. These are the exact names
 * `FPDFText_LoadStandardFont` takes, so nothing has to be mapped.
 */
val STANDARD_FONTS = listOf("Helvetica", "Times-Roman", "Courier")

/** What a box with no recorded face is, and what a new one defaults to. */
const val DEFAULT_FONT = "Helvetica"

class PdfPasswordException : Exception("Password required or incorrect password")

class PdfLoadException(val errorCode: Int) :
    Exception("Failed to load document (FPDF error $errorCode)") {
    /**
     * The document uses a security handler PDFium cannot open — a certificate handler,
     * say. Not corrupt and not a wrong password (#131, ADR-004 decision 8).
     */
    val isUnsupportedSecurity: Boolean get() = errorCode == PdfiumNative.ERR_SECURITY
}

class PdfSaveException : Exception("Failed to serialize document")

/**
 * PDFium would change the rest of the page if it rewrote this text (#118). [verdict] says why
 * (#128); null only when the core gave no reason.
 */
class TextLayoutException(val verdict: LayoutVerdict? = null) :
    Exception("rewriting this text would change the page's layout")

/** Which check of the layout guard refused (#128). Mirrors MEGAPDF_LAYOUT_* in megapdf_core.h. */
enum class LayoutCause {
    /** Editable: the rewrite changed nothing past the guard's budgets. */
    OK,
    /** More than 0.05% of the page's pixels would look different. */
    RENDER,
    /** Some text object's bounds would move by more than 0.5 pt. */
    TEXT_MOVED,
    /** The page would have a different number of text objects, or different text. */
    TEXT_CHANGED,
    /** PDFium could not rewrite, save or reopen the copy of the page. */
    REWRITE_FAILED;

    /** Text elsewhere on the page would move or change, rather than only look different. */
    val textWouldMove: Boolean get() = this == TEXT_MOVED || this == TEXT_CHANGED

    companion object {
        fun fromCore(value: Int): LayoutCause = values().getOrElse(value) { REWRITE_FAILED }
    }
}

/**
 * The layout guard's verdict on one text object (#118, #128), with the numbers its dry run saw:
 * pixels changed in its render of the page, [where] they are (the WHERE_* bits), and the largest
 * move of any text object's bounds in points.
 */
data class LayoutVerdict(
    val editable: Boolean,
    val cause: LayoutCause,
    val where: Int,
    val changedPixels: Int,
    val totalPixels: Int,
    val maxShiftPoints: Double,
) {
    companion object {
        const val WHERE_OBJECT = 1
        const val WHERE_TEXT = 2
        const val WHERE_OTHER = 4

        /** From the native [status, editable, cause, where, changedPixels, totalPixels, maxShiftPt]; null for a bad status. */
        internal fun fromPacked(packed: DoubleArray): LayoutVerdict? {
            if (packed.size != 7 || packed[0] < 0) return null
            return LayoutVerdict(
                editable = packed[1] != 0.0,
                cause = LayoutCause.fromCore(packed[2].toInt()),
                where = packed[3].toInt(),
                changedPixels = packed[4].toInt(),
                totalPixels = packed[5].toInt(),
                maxShiftPoints = packed[6],
            )
        }
    }
}
