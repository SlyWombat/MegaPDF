package com.megapdf.android

import android.graphics.Bitmap
import com.google.mlkit.vision.common.InputImage
import com.google.mlkit.vision.text.TextRecognition
import com.google.mlkit.vision.text.latin.TextRecognizerOptions
import com.megapdf.engine.PdfRect
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/**
 * How a rendered page is read (#676). An interface so the view model's tests and the on-device
 * tests can substitute their own, and so the one place that names ML Kit is [MlKitPageRecogniser].
 */
interface PageRecogniser {
    /** Reads [bitmap], a render of a page that is [pageWidthPoints] × [pageHeightPoints] points. */
    suspend fun read(bitmap: Bitmap, pageWidthPoints: Double, pageHeightPoints: Double): OcrPage
}

/**
 * ML Kit text recognition, Latin script, **bundled model** (Dave, 2026-10-17): it needs no
 * network and no Play Services, which is what the manifest's no-network rule and a de-Googled
 * device both ask for, at about 13-16 MB per device (#676, phase 0). Nothing read leaves the
 * process and nothing is written anywhere.
 *
 * Measured on a Pixel 4 XL (2019) at 150 dpi: about 190 ms a page, 193 ms cold.
 */
class MlKitPageRecogniser : PageRecogniser {

    private val client by lazy { TextRecognition.getClient(TextRecognizerOptions.DEFAULT_OPTIONS) }

    override suspend fun read(bitmap: Bitmap, pageWidthPoints: Double, pageHeightPoints: Double): OcrPage {
        val text = suspendCancellableCoroutine { continuation ->
            client.process(InputImage.fromBitmap(bitmap, 0))
                .addOnSuccessListener { continuation.resume(it) }
                .addOnFailureListener { continuation.resumeWithException(it) }
        }
        val w = bitmap.width.toDouble()
        val h = bitmap.height.toDouble()
        val lines = ArrayList<OcrLine>()
        for (block in text.textBlocks) for (line in block.lines) {
            val whole = line.text
            var cursor = 0
            val words = ArrayList<OcrWord>()
            for (element in line.elements) {
                val box = element.boundingBox ?: continue
                val start = whole.indexOf(element.text, cursor)
                if (start < 0) continue
                cursor = start + element.text.length
                // The bitmap's origin is top-left, the page's is bottom-left, and the bitmap is the
                // page scaled to fit, so a pixel box scales straight to points.
                words += OcrWord(
                    start, cursor,
                    PdfRect(
                        left = box.left / w * pageWidthPoints,
                        bottom = pageHeightPoints - box.bottom / h * pageHeightPoints,
                        right = box.right / w * pageWidthPoints,
                        top = pageHeightPoints - box.top / h * pageHeightPoints,
                    ),
                )
            }
            if (words.isNotEmpty()) lines += OcrLine(whole, words)
        }
        return OcrPage(lines)
    }
}
