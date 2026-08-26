package com.megapdf.engine

import android.graphics.Bitmap
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.NonCancellable
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
    internal val dispatcher: CoroutineDispatcher =
        Executors.newSingleThreadExecutor { r -> Thread(r, "pdfium") }.asCoroutineDispatcher()

    init {
        ensureInitialized()
    }

    /**
     * Opens a document from its full bytes (the caller reads them from SAF or assets;
     * the source file is never held open — same rationale as the desktop engine).
     * @throws PdfPasswordException wrong or missing password
     * @throws PdfLoadException corrupt or unreadable document
     */
    suspend fun open(bytes: ByteArray, password: String? = null): PdfDocument =
        withContext(dispatcher) {
            val handle = PdfiumNative.nativeOpen(bytes, password)
            if (handle == 0L) {
                val error = PdfiumNative.nativeLastError()
                if (error == PdfiumNative.ERR_PASSWORD) throw PdfPasswordException()
                throw PdfLoadException(error)
            }
            PdfDocument(this@PdfEngine, handle)
        }

    private companion object {
        private var initialized = false

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
     * pixel size: white ground, page content, then live form-field values (FPDF_FFLDraw).
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
    Exception("Failed to load document (FPDF error $errorCode)")

class PdfSaveException : Exception("Failed to serialize document")
