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

    // Checkbox surface (#15). Packed arrays keep the JNI boundary simple:
    // form fields are [type, checked, l, b, r, t] per field; squares and annot
    // rects are [l, b, r, t] each, all in PDF points, bottom-left origin.
    external fun nativeFormFieldsPacked(handle: Long): DoubleArray
    external fun nativeClickAt(handle: Long, x: Double, y: Double)
    external fun nativeDetectSquaresPacked(handle: Long): DoubleArray
    external fun nativeAddCheckMark(
        handle: Long, l: Double, b: Double, r: Double, t: Double, id: String,
    ): Boolean
    external fun nativeAnnotIds(handle: Long): Array<String>
    external fun nativeAnnotRectsPacked(handle: Long): DoubleArray
    external fun nativeRemoveAnnot(handle: Long, index: Int): Boolean

    // Signature stamps (#17). Pixels are ARGB ints; readback returns
    // [width, height, argb...] at native image resolution, or null.
    external fun nativeAddImageStamp(
        handle: Long, pixels: IntArray, pixelWidth: Int, pixelHeight: Int,
        l: Double, b: Double, r: Double, t: Double, id: String,
    ): Boolean
    external fun nativeGetStampImagePacked(handle: Long, annotIndex: Int): IntArray?

    // FPDF_GetLastError codes (fpdfview.h).
    const val ERR_PASSWORD = 4
}
