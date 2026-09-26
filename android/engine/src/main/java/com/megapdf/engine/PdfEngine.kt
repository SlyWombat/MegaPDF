package com.megapdf.engine

import android.graphics.Bitmap
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume
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
     * Opens a document from a descriptor, read on demand for as long as it is open (#147,
     * #148): nothing holds the file in memory, so its size is bounded by storage, not the
     * heap. Ownership of [fd] passes to the engine with the call, as from
     * `ParcelFileDescriptor.detachFd()`; it is closed with the document, or at once if the
     * open fails. It must be a regular file: a pipe is refused with [PdfLoadException]
     * ([PdfLoadException.isFileError]).
     *
     * Writing the file in place while the document is open would change what it reads:
     * see [PdfDocument.readsFd] and [PdfDocument.readFromCopy].
     * @throws PdfPasswordException wrong or missing password
     * @throws PdfLoadException corrupt or unreadable document, or security PDFium cannot open
     */
    suspend fun openFd(fd: Int, password: String? = null): PdfDocument =
        withContext(dispatcher) {
            opened(PdfiumNative.nativeOpenFd(fd, password?.nulTerminatedUtf8()))
        }

    /** [openFd] for a file with a path, such as one in the app's cache (#147). */
    suspend fun openFile(path: String, password: String? = null): PdfDocument =
        withContext(dispatcher) {
            opened(PdfiumNative.nativeOpenFile(path.nulTerminatedUtf8(), password?.nulTerminatedUtf8()))
        }

    /** [openLike] for a file, read on demand: how a save is verified without reading it into memory (#147). */
    suspend fun openFileLike(like: PdfDocument, path: String): PdfDocument =
        withContext(dispatcher) {
            opened(PdfiumNative.nativeOpenFileLike(like.nativeHandle(), path.nulTerminatedUtf8()))
        }

    private fun opened(handle: Long): PdfDocument {
        if (handle == 0L) {
            val error = PdfiumNative.nativeLastError()
            if (error == PdfiumNative.ERR_PASSWORD) throw PdfPasswordException()
            throw PdfLoadException(error)
        }
        return PdfDocument(this, handle)
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

    // --- Contract 9 (#142, #353, #386): document structure and the text/Markdown writers over it ---

    /**
     * Contract 9's block structure over `[firstPage, firstPage + pageCount)` (#386): the raw
     * native handle, freed with [freeStructure]. Not used by [writeText]/[writeMarkdown], which
     * load and free their own structure internally (megapdf_write_text.cpp) — this exists so a
     * future Android feature can read contract 9's blocks directly, the same pair
     * `megapdf_core.h` exposes.
     * @throws IllegalStateException an out-of-range range, a cancelled load, or the core could
     *   not allocate (`megapdf_structure_load` returns NULL for all three; nothing to
     *   distinguish them on here — none is expected from a page range this class itself reports)
     */
    suspend fun loadStructure(
        firstPage: Int, pageCount: Int, flags: Int = PdfiumNative.STRUCTURE_DEFAULT,
    ): Long = withContext(engine.dispatcher) {
        check(!closed) { "document is closed" }
        val structure = PdfiumNative.nativeStructureLoad(handle, firstPage, pageCount, flags)
        check(structure != 0L) { "failed to load structure for pages $firstPage..<${firstPage + pageCount}" }
        structure
    }

    /** Frees a handle returned by [loadStructure]. */
    suspend fun freeStructure(structure: Long): Unit = withContext(engine.dispatcher) {
        PdfiumNative.nativeStructureFree(structure)
    }

    /**
     * Writes the whole document's text as plain text or Markdown (#142, #355, #357; #386's
     * binding), over contract 9's blocks — a one-way text export, not an alternate save format:
     * the result cannot be reopened as the document (no form fields, no signatures, no layout).
     * [format] is [PdfiumNative.WRITE_FORMAT_TEXT] or [PdfiumNative.WRITE_FORMAT_MARKDOWN].
     * Returns the count of pages that had a text layer.
     * @throws PdfWriteTextException the core refused (a bad range, a cancelled or failed write)
     */
    suspend fun writeText(
        out: OutputStream, format: Int, options: PdfWriteTextOptions = PdfWriteTextOptions(),
    ): Int = withContext(engine.dispatcher) {
        check(!closed) { "document is closed" }
        val pageCount = PdfiumNative.nativePageCount(handle)
        val status = PdfiumNative.nativeWriteText(handle, 0, pageCount, format, options.packed(), out)
        if (status < 0) throw PdfWriteTextException(status)
        out.flush()
        status
    }

    /**
     * [writeText] with a GUI Save-As's own reasonable defaults (#386), not the CLI's scripting
     * ones ([PdfWriteTextOptions.saveAsDefaults]): the document's filled fields, no running
     * headers/footers/page numbers, and a blank line rather than a form feed between pages.
     */
    suspend fun writeMarkdown(out: OutputStream): Int =
        writeText(out, PdfiumNative.WRITE_FORMAT_MARKDOWN, PdfWriteTextOptions.saveAsDefaults())

    // --- Redaction (#173, contract 8) ---

    /**
     * How many areas are marked for redaction across the document. A mark is the core's
     * own and is never written to the file, so a document saved with marks on it carries
     * none.
     */
    suspend fun redactionMarkCount(): Int = withContext(engine.dispatcher) {
        if (closed) 0 else PdfiumNative.nativeRedactionMarkCount(handle)
    }

    suspend fun clearRedactionMarks(): Unit = withContext(engine.dispatcher) {
        if (!closed) PdfiumNative.nativeClearRedactionMarks(handle)
    }

    /** True when a redaction failed part-way and the document may no longer be saved. */
    suspend fun isRedactionPoisoned(): Boolean = withContext(engine.dispatcher) {
        !closed && PdfiumNative.nativeRedactionPoisoned(handle)
    }

    /**
     * Applies every mark and drops them. It fails closed: when [RedactionReport.applied] is
     * false NOTHING was removed, the document is exactly as it was, the marks are still on
     * it, and [RedactionReport.refusals] says which page and why.
     *
     * Applying also frees every undo handle the core holds, so the caller clears its own
     * history in the same step.
     */
    suspend fun applyRedactions(): RedactionReport = withContext(engine.dispatcher) {
        check(!closed) { "document is closed" }
        RedactionReport.decode(PdfiumNative.nativeApplyRedactions(handle))
    }

    /**
     * Whether this document reads the file [fd] is open on (#147); [fd] stays the caller's.
     * False for a document opened from bytes, or one already moved to a copy.
     */
    suspend fun readsFd(fd: Int): Boolean = withContext(engine.dispatcher) {
        check(!closed) { "document is closed" }
        PdfiumNative.nativeReadsFd(handle, fd)
    }

    /**
     * Moves the document onto a private copy of the file it reads, at [copyPath] (which must
     * not exist), so that file can be written over in place (#147): a document read on demand
     * would otherwise read the new bytes where it expects the old. The copy's name is removed
     * at once and its space freed when the document closes. Nothing to do for a document
     * opened from bytes.
     *
     * The copy runs off the engine thread, so pages keep rendering while a big file copies.
     * @throws java.io.IOException the copy could not be made (no space, say); the document
     *   still reads its file, which must then not be written in place
     */
    suspend fun readFromCopy(copyPath: String) {
        check(!closed) { "document is closed" }
        val status = withContext(Dispatchers.IO) { PdfiumNative.nativeReadFromCopy(handle, copyPath.nulTerminatedUtf8()) }
        if (status != 0) throw java.io.IOException("could not copy the document's file (status $status)")
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

    // ---- The page check started early (#145) -----------------------------------------

    /** Guards [checksInFlight], [closing] and [runningFlags]; checks run on other threads. */
    private val checkLock = Object()
    private var checksInFlight = 0
    private var closing = false
    private val runningFlags = HashSet<NativeCancel>()

    /**
     * Whether regenerating page [pageIndex], with nothing changed, would change how it looks
     * (#139), for a check started early (#145). Unlike [PdfPage.pageRegenerationVerdict] it does
     * not run on the engine's one thread: the core lets go of its lock between the dry run's
     * stages, so renders and edits wait at most one stage while a slow page is judged.
     *
     * Cancelling the calling coroutine raises the check's flag and returns at once; the native
     * run stops at its next stage. [close] raises every flag and waits for running checks to
     * stop before it closes the document, so the document is never closed under a check.
     *
     * @return [PageCheck.Judged] with the verdict, [PageCheck.Cancelled] when the document is
     *   closing, or [PageCheck.Failed] when the page could not be judged.
     */
    suspend fun checkPageRegeneration(pageIndex: Int): PageCheck = suspendCancellableCoroutine { cont ->
        val cancel = NativeCancel()
        val admitted = synchronized(checkLock) {
            if (closing || closed) {
                false
            } else {
                checksInFlight++
                runningFlags += cancel
                true
            }
        }
        if (!admitted) {
            cancel.free()
            cont.resume(PageCheck.Cancelled)
            return@suspendCancellableCoroutine
        }
        cont.invokeOnCancellation { cancel.raise() }
        pageCheckExecutor.execute {
            val result = try {
                runCheck(pageIndex, cancel)
            } catch (t: Throwable) {
                PageCheck.Failed
            } finally {
                synchronized(checkLock) {
                    runningFlags -= cancel
                    checksInFlight--
                    checkLock.notifyAll()
                }
                cancel.free()
            }
            // Ignored when the caller was cancelled meanwhile.
            if (cont.isActive) cont.resume(result)
        }
    }

    /** On a check thread, while this document is held open by [checkPageRegeneration]. */
    private fun runCheck(pageIndex: Int, cancel: NativeCancel): PageCheck {
        if (cancel.isRaised) return PageCheck.Cancelled
        // A page handle of the check's own, opened and closed on this thread before the
        // document may close: megapdf_close() frees pages still open, so it must not outlive it.
        val page = PdfiumNative.nativeOpenPage(handle, pageIndex)
        if (page == 0L) return PageCheck.Failed
        try {
            val packed = PdfiumNative.nativePageRegenerationVerdictCancellable(page, cancel.pointer)
            if (packed.isNotEmpty() && packed[0].toInt() == PdfiumNative.STATUS_CANCELLED) return PageCheck.Cancelled
            return LayoutVerdict.fromPacked(packed)?.let { PageCheck.Judged(it) } ?: PageCheck.Failed
        } finally {
            PdfiumNative.nativeClosePage(page)
        }
    }

    // NonCancellable: close must run even from a cancelled caller, or the native
    // document leaks.
    suspend fun close(): Unit = withContext(NonCancellable) {
        // Stop page checks first (#145): no new one starts, running ones stop at their next
        // stage, and the document closes only once none holds it.
        val flags = synchronized(checkLock) {
            closing = true
            runningFlags.toList()
        }
        flags.forEach { it.raise() }
        if (flags.isNotEmpty() || synchronized(checkLock) { checksInFlight > 0 }) {
            withContext(Dispatchers.IO) {
                synchronized(checkLock) {
                    while (checksInFlight > 0) checkLock.wait()
                }
            }
        }
        withContext(engine.dispatcher) {
            if (!closed) {
                synchronized(checkLock) { closed = true }
                PdfiumNative.nativeCloseDocument(handle)
            }
        }
    }
}

/** How a page check started with [PdfDocument.checkPageRegeneration] ended (#145). */
sealed interface PageCheck {
    /** The page was judged: [LayoutVerdict.editable] false means regenerating it changes its look. */
    data class Judged(val verdict: LayoutVerdict) : PageCheck

    /** The document is closing (or closed) and the check stopped. */
    data object Cancelled : PageCheck

    /** The page could not be judged. */
    data object Failed : PageCheck
}

/**
 * A core cancel flag (#145). Raising is safe from any thread at any time, also after the check
 * returned: [free] and [raise] share one monitor, so a raise never reaches a freed flag.
 */
internal class NativeCancel {
    private var handle: Long = PdfiumNative.nativeCancelNew()
    @Volatile
    var isRaised: Boolean = false
        private set

    /** The flag for the native call; 0 (no flag) when the core was out of memory. */
    val pointer: Long @Synchronized get() = handle

    @Synchronized
    fun raise() {
        isRaised = true
        if (handle != 0L) PdfiumNative.nativeCancelRaise(handle)
    }

    @Synchronized
    fun free() {
        if (handle != 0L) {
            PdfiumNative.nativeCancelFree(handle)
            handle = 0L
        }
    }
}

/**
 * The threads page checks run on (#145): never the engine's one thread, which would hold every
 * render and edit behind a check for its whole run. Daemon threads, so they never hold the
 * process open; idle ones go after a minute.
 */
private val pageCheckExecutor: java.util.concurrent.ExecutorService =
    java.util.concurrent.ThreadPoolExecutor(
        2, 2, 60L, java.util.concurrent.TimeUnit.SECONDS, java.util.concurrent.LinkedBlockingQueue(),
    ) { r -> Thread(r, "megapdf-page-check").apply { isDaemon = true } }.apply {
        // Two, so a stopped check still finishing its stage never holds up the next page's.
        allowCoreThreadTimeOut(true)
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

    // --- Redaction marks (#173) ---

    /**
     * Marks an area for redaction and returns the mark's id, or -1. Nothing on the page
     * changes: a mark is the core's own and is never written to the file, so the app draws
     * it and marking costs no content regeneration.
     */
    suspend fun markForRedaction(rect: PdfRect): Int = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        PdfiumNative.nativeMarkForRedaction(handle, rect.left, rect.bottom, rect.right, rect.top)
    }

    /**
     * Marks the text a drag selected: one mark per line it spans, each grown to the glyphs
     * it touches, so a mark always covers whole glyphs. Returns the ids of the marks it
     * made — an empty list means the selection covered no text, and the caller then marks
     * the rectangle itself. One drag is one list, and therefore one undo step (#329).
     */
    suspend fun markTextForRedaction(rect: PdfRect): List<Int> = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        PdfiumNative
            .nativeMarkTextForRedaction(handle, rect.left, rect.bottom, rect.right, rect.top)
            .toList()
    }

    /** The redaction marks on this page, in the order they were made. */
    suspend fun redactionMarks(): List<RedactionMark> = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        val packed = PdfiumNative.nativeRedactionMarksPacked(handle)
        (0 until packed.size / 5).map { i ->
            RedactionMark(
                markId = packed[i * 5].toInt(),
                rect = PdfRect(packed[i * 5 + 1], packed[i * 5 + 2], packed[i * 5 + 3], packed[i * 5 + 4]),
            )
        }
    }

    suspend fun moveRedactionMark(markId: Int, rect: PdfRect): Boolean = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        PdfiumNative.nativeMoveRedactionMark(handle, markId, rect.left, rect.bottom, rect.right, rect.top)
    }

    /** Removes a mark. Already gone counts as success, so an undo cannot fail. */
    suspend fun removeRedactionMark(markId: Int): Unit = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        PdfiumNative.nativeRemoveRedactionMark(handle, markId)
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

    /**
     * The page verdict already known (#145): what [pageRegenerationVerdict] or a check started
     * with [PdfDocument.checkPageRegeneration] found, without running anything. Null when the
     * page has not been judged since it last changed, or cannot be judged.
     */
    suspend fun cachedPageRegenerationVerdict(): LayoutVerdict? = withContext(engine.dispatcher) {
        check(!closed) { "page is closed" }
        LayoutVerdict.fromPacked(PdfiumNative.nativePageRegenerationVerdictCached(handle))
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

    /** The file could not be opened or read as a file: not a regular file, or gone (#147). */
    val isFileError: Boolean get() = errorCode == PdfiumNative.ERR_FILE

    /** The file is past what this platform can address (#147). */
    val isTooLarge: Boolean get() = errorCode == PdfiumNative.ERR_TOO_LARGE
}

class PdfSaveException : Exception("Failed to serialize document")

/**
 * megapdf_write_options (#386), zero-valued the same way the CLI's own struct is: filled fields,
 * no running furniture, a form feed between pages. Use [saveAsDefaults] for a GUI Save-As
 * context instead, which differs only in [pageBreak] — #357's own Markdown convention, restated
 * here as an explicit choice rather than inherited from the CLI's scripting default.
 */
data class PdfWriteTextOptions(
    val keepLines: Boolean = false,
    val pageBreak: Int = PdfiumNative.PAGE_BREAK_FORM_FEED,
    val keepFurniture: Boolean = false,
    val fields: Int = PdfiumNative.WRITE_FIELDS_FILLED,
    val heuristicOnly: Boolean = false,
) {
    /** [keepLines, pageBreak, keepFurniture, fields, heuristicOnly], nativeWriteText's own order. */
    internal fun packed(): IntArray = intArrayOf(
        if (keepLines) 1 else 0, pageBreak, if (keepFurniture) 1 else 0, fields, if (heuristicOnly) 1 else 0,
    )

    companion object {
        /**
         * A GUI Save-As's own reasonable defaults (#386), not the CLI's scripting ones: the
         * filled fields the document is showing ([PdfiumNative.WRITE_FIELDS_FILLED], already
         * the default), no running headers/footers/page numbers ([keepFurniture] false, already
         * the default), and — the one difference — a blank line rather than a form feed between
         * pages of Markdown ([PdfiumNative.PAGE_BREAK_NONE]).
         */
        fun saveAsDefaults(): PdfWriteTextOptions = PdfWriteTextOptions(pageBreak = PdfiumNative.PAGE_BREAK_NONE)
    }
}

/** megapdf_write_text() refused: [status] is one of megapdf_core.h's negative MEGAPDF_ERR_* codes. */
class PdfWriteTextException(val status: Int) : Exception("Failed to write document text (status $status)")

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
