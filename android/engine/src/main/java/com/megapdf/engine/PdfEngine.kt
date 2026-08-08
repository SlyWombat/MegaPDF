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

    // NonCancellable: a cancelled render job's finally-block close must still run.
    suspend fun close(): Unit = withContext(engine.dispatcher + NonCancellable) {
        if (!closed) {
            closed = true
            PdfiumNative.nativeClosePage(handle)
        }
    }
}

class PdfPasswordException : Exception("Password required or incorrect password")

class PdfLoadException(val errorCode: Int) :
    Exception("Failed to load document (FPDF error $errorCode)")

class PdfSaveException : Exception("Failed to serialize document")
