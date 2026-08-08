package com.megapdf.engine

import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.asCoroutineDispatcher
import java.util.concurrent.Executors

/**
 * Engine facade over PDFium via JNI. Scaffold stage: only the toolchain stub exists;
 * #13 adds the real surface (open from bytes, render, form-fill, stamps, save),
 * reimplementing the behavior of the desktop `IPdfEngine`/`PdfiumEngine`
 * (see SDD §6 for the cross-platform behavioral contracts).
 *
 * PDFium is not thread-safe. Every native call must run on [dispatcher] — a single
 * thread, the mobile analog of the desktop's global `PdfiumLibrary.Lock`.
 */
class PdfEngine {
    val dispatcher: CoroutineDispatcher =
        Executors.newSingleThreadExecutor { r -> Thread(r, "pdfium") }.asCoroutineDispatcher()

    external fun nativeScaffoldVersion(): String

    companion object {
        init {
            System.loadLibrary("megapdf_engine")
        }
    }
}
