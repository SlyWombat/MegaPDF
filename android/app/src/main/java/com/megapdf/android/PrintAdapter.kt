package com.megapdf.android

import android.os.Bundle
import android.os.CancellationSignal
import android.os.ParcelFileDescriptor
import android.print.PageRange
import android.print.PrintAttributes
import android.print.PrintDocumentAdapter
import android.print.PrintDocumentInfo
import java.io.OutputStream
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Which pages a print job asked for, as 0-based indexes in the order asked (#675); null means
 * every page, which is what the system sends until the person types a range.
 *
 * Pure and over [IntRange] rather than [PageRange] so the mapping is tested on the JVM, where the
 * framework class is only a stub. [PageRange] is 0-based and inclusive at both ends, and
 * `PageRange.ALL_PAGES` is `0..Int.MAX_VALUE`, so a range is clamped to the document before it is
 * expanded — never expanded first.
 */
internal fun printedPageIndexes(ranges: List<IntRange>, pageCount: Int): List<Int>? {
    if (pageCount <= 0) return emptyList()
    if (ranges.isEmpty()) return null
    val last = pageCount - 1
    if (ranges.any { it.first <= 0 && it.last >= last }) return null
    return ranges.flatMap { range ->
        val from = range.first.coerceAtLeast(0)
        val to = range.last.coerceAtMost(last)
        if (from > to) emptyList() else (from..to).toList()
    }
}

/**
 * The system print service's view of the open document (#675). The OS owns the preview, the page
 * range, copies, duplex and the printer; this adapter's one job is to write a PDF into the
 * descriptor it is handed, holding to the [PageRange]s it asked for.
 *
 * What it writes is the document **as it is on screen**, serialised on the spot, and not the file
 * the document was opened from: printing the file on disk would print the page as it was before
 * the person's edits, which is a silent wrong answer on paper. That is also why there is no
 * unsaved-changes question first, unlike Share.
 *
 * [writeWhole] and [writePages] are the view model's, so this class knows nothing about security
 * or the engine. [writePages] is a real extraction, which needs the document's copy permission —
 * a print-permitted document that forbids copying prints in full but not in part, and says so
 * through [onFailed] rather than printing the wrong pages.
 *
 * [scope] must be the main-thread scope (a view model's): the framework's callbacks are made from
 * it, after the write, which runs on [Dispatchers.IO].
 */
class MegaPdfPrintAdapter(
    private val jobName: String,
    private val pageCount: Int,
    private val scope: CoroutineScope,
    private val writeWhole: suspend (OutputStream) -> Unit,
    private val writePages: suspend (List<Int>, OutputStream) -> Unit,
    private val onFailed: (Throwable) -> Unit,
) : PrintDocumentAdapter() {

    override fun onLayout(
        oldAttributes: PrintAttributes?,
        newAttributes: PrintAttributes,
        cancellationSignal: CancellationSignal,
        callback: LayoutResultCallback,
        extras: Bundle?,
    ) {
        if (cancellationSignal.isCanceled) {
            callback.onLayoutCancelled()
            return
        }
        // The pages are the document's own and do not depend on the paper, so a change of
        // attributes changes nothing about the content; the OS still wants to hear that it did.
        val info = PrintDocumentInfo.Builder(jobName)
            .setContentType(PrintDocumentInfo.CONTENT_TYPE_DOCUMENT)
            .setPageCount(pageCount)
            .build()
        callback.onLayoutFinished(info, newAttributes != oldAttributes)
    }

    override fun onWrite(
        pages: Array<PageRange>,
        destination: ParcelFileDescriptor,
        cancellationSignal: CancellationSignal,
        callback: WriteResultCallback,
    ) {
        val wanted = printedPageIndexes(pages.map { it.start..it.end }, pageCount)
        val job = scope.launch {
            try {
                withContext(Dispatchers.IO) {
                    ParcelFileDescriptor.AutoCloseOutputStream(destination).use { out ->
                        if (wanted == null) writeWhole(out) else writePages(wanted, out)
                        out.flush()
                    }
                }
                callback.onWriteFinished(pages)
            } catch (e: CancellationException) {
                callback.onWriteCancelled()
                throw e
            } catch (e: Exception) {
                onFailed(e)
                callback.onWriteFailed(null)
            }
        }
        cancellationSignal.setOnCancelListener { job.cancel() }
    }
}
