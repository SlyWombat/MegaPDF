package com.megapdf.engine

import android.graphics.Bitmap
import java.io.ByteArrayOutputStream

/** Called from native code on the shrink's own thread (#593). */
internal interface ShrinkCallbacks {
    /** ARGB_8888 pixels, alpha already 0xFF; quality 0..100. Null when the encode failed. */
    fun encodeJpeg(argb: IntArray, width: Int, height: Int, quality: Int): ByteArray?
    fun progress(done: Int, total: Int)
}

/**
 * The platform's JPEG encoder for the core's shrink (#593). `Bitmap.compress`, because the NDK's
 * `AndroidBitmap_compress` needs API 30 and minSdk is 26.
 */
internal object AndroidJpegEncoder {
    fun encode(argb: IntArray, width: Int, height: Int, quality: Int): ByteArray? {
        val bitmap = Bitmap.createBitmap(argb, width, height, Bitmap.Config.ARGB_8888)
        return try {
            ByteArrayOutputStream().use { out -> if (bitmap.compress(Bitmap.CompressFormat.JPEG, quality, out)) out.toByteArray() else null }
        } finally {
            bitmap.recycle()
        }
    }
}
