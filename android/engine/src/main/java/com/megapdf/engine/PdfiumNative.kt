package com.megapdf.engine

import android.graphics.Bitmap
import java.io.OutputStream

/**
 * Raw JNI surface (see src/main/cpp/engine.cpp). Handles are native pointers.
 * Never call directly from app code — [PdfEngine] owns the single engine thread
 * these must run on (PDFium is not thread-safe).
 */
internal object PdfiumNative {
    init {
        System.loadLibrary("megapdf_engine")
    }

    external fun nativeInit()
    external fun nativeOpen(bytes: ByteArray, password: String?): Long
    external fun nativeLastError(): Int
    external fun nativeCloseDocument(handle: Long)
    external fun nativePageCount(handle: Long): Int
    external fun nativeOpenPage(handle: Long, index: Int): Long
    external fun nativeClosePage(handle: Long)
    external fun nativePageWidth(handle: Long): Double
    external fun nativePageHeight(handle: Long): Double
    external fun nativeRenderPage(handle: Long, bitmap: Bitmap): Boolean
    external fun nativeSave(handle: Long, out: OutputStream): Boolean

    // FPDF_GetLastError codes (fpdfview.h).
    const val ERR_PASSWORD = 4
}
