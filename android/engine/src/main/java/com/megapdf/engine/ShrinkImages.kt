package com.megapdf.engine

import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.withContext
import java.util.concurrent.Executors

/** How [shrinkImagesForEmail] ended (#593). */
sealed interface ShrinkOutcome {
    /** Every image was looked at; [replaced] of the [considered] were re-encoded smaller. */
    data class Done(val considered: Int, val replaced: Int) : ShrinkOutcome

    /** The flag was raised: the document is part-shrunk and is to be discarded. */
    data object Stopped : ShrinkOutcome

    /** The core's status for a run that could not finish. */
    data class Failed(val status: Int) : ShrinkOutcome
}

/**
 * Shrinks THIS document's pictures in place; call it only on a private copy (#593). The rules are
 * the core's `megapdf_shrink_images` (150 dpi of the placed size, quality 0.75, the size skips and
 * the 10% guard). [onProgress] is told "pictures done of total" from the shrink's own thread.
 *
 * Deliberately not on [PdfEngine.pdfiumDispatcher]: the core lets go of its lock while each
 * picture is encoded, so renders of the open document go on on the engine thread meanwhile.
 * [cancel] is freed when this returns.
 */
suspend fun PdfDocument.shrinkImagesForEmail(cancel: PdfCancelFlag, onProgress: (done: Int, total: Int) -> Unit): ShrinkOutcome =
    withContext(shrinkDispatcher) {
        try {
            val r = PdfiumNative.nativeShrinkImages(nativeHandle(), cancel.native.pointer, object : ShrinkCallbacks {
                override fun encodeJpeg(argb: IntArray, width: Int, height: Int, quality: Int) =
                    AndroidJpegEncoder.encode(argb, width, height, quality)
                override fun progress(done: Int, total: Int) = onProgress(done, total)
            })
            when (r[0]) {
                PdfiumNative.STATUS_OK -> ShrinkOutcome.Done(r[1], r[2])
                PdfiumNative.STATUS_CANCELLED -> ShrinkOutcome.Stopped
                else -> ShrinkOutcome.Failed(r[0])
            }
        } finally {
            cancel.native.free()
        }
    }

private val shrinkDispatcher =
    Executors.newSingleThreadExecutor { r -> Thread(r, "megapdf-shrink").apply { isDaemon = true } }.asCoroutineDispatcher()
