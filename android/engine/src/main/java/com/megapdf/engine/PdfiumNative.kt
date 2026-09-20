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
    /** [passwordUtf8] is NUL-terminated UTF-8, as for the security saves below (#131). */
    external fun nativeOpen(bytes: ByteArray, passwordUtf8: ByteArray?): Long
    external fun nativeOpenLike(like: Long, bytes: ByteArray): Long
    /** Takes ownership of [fd] (#147): the core closes it with the document, or at once if the open fails. */
    external fun nativeOpenFd(fd: Int, passwordUtf8: ByteArray?): Long
    /** [pathUtf8] is NUL-terminated UTF-8, like the password (#147). */
    external fun nativeOpenFile(pathUtf8: ByteArray, passwordUtf8: ByteArray?): Long
    external fun nativeOpenFileLike(like: Long, pathUtf8: ByteArray): Long
    external fun nativeReadsFd(handle: Long, fd: Int): Boolean
    external fun nativeReadFromCopy(handle: Long, pathUtf8: ByteArray): Int
    external fun nativeLastError(): Int
    external fun nativeCloseDocument(handle: Long)
    external fun nativePageCount(handle: Long): Int
    external fun nativeOpenPage(handle: Long, index: Int): Long
    external fun nativeClosePage(handle: Long)
    external fun nativePageWidth(handle: Long): Double
    external fun nativePageHeight(handle: Long): Double
    external fun nativeRenderPage(handle: Long, bitmap: Bitmap): Boolean
    external fun nativeRenderSize(idealWidth: Double, idealHeight: Double): IntArray
    external fun nativeSave(handle: Long, out: OutputStream): Boolean

    // Document security (#131). Passwords go over as NUL-terminated UTF-8 bytes; the
    // saves return the core's status (-6 when the document's security forbids it).
    external fun nativeSecurityInfo(handle: Long): IntArray
    external fun nativeSaveWithSecurity(
        handle: Long, out: OutputStream, userUtf8: ByteArray, ownerUtf8: ByteArray?, permissions: Int,
    ): Int
    external fun nativeSaveWithoutSecurity(handle: Long, out: OutputStream): Int

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

    // Added text (#34). A text box is a page text object carrying the
    // "MegaPDFTextBox" mark plus "id" and "font" params — the desktop's
    // representation, addressed by id because page-object indices shift. Ids,
    // texts, faces and [l, b, r, t, fontSize] arrive as four aligned arrays.
    external fun nativeAddTextBox(
        handle: Long, text: String, fontName: String, fontSize: Double,
        x: Double, y: Double, id: String,
    ): Boolean
    external fun nativeTextBoxIds(handle: Long): Array<String>
    external fun nativeTextBoxTexts(handle: Long): Array<String>
    external fun nativeTextBoxFonts(handle: Long): Array<String>
    external fun nativeTextBoxRectsPacked(handle: Long): DoubleArray
    external fun nativeMoveTextBox(handle: Long, id: String, x: Double, y: Double): Boolean
    external fun nativeRemoveTextBox(handle: Long, id: String): Boolean

    // Text search (#26). Case-insensitive literal substring; packed
    // [rectCount, l, b, r, t...] per match, PDF points, bottom-left origin.
    external fun nativeSearchPagePacked(handle: Long, query: String): DoubleArray

    // ---- The document's own text (#114) ----------------------------------
    external fun nativeTextRunsPacked(handle: Long): DoubleArray
    external fun nativeTextRunStrings(handle: Long): Array<String>
    external fun nativeSetText(handle: Long, objectIndex: Int, text: String): LongArray
    external fun nativeDetachObject(handle: Long, objectIndex: Int): Long
    external fun nativeRestoreObject(handle: Long, detached: Long, objectIndex: Int): Boolean
    external fun nativeRestoreOriginal(handle: Long, original: Long, objectIndex: Int): Boolean
    // A line at once, with the hidden copies drawn under its runs (#136).
    external fun nativeSetLineText(handle: Long, objectIndices: IntArray, text: String): LongArray
    external fun nativeDetachTextRuns(handle: Long, objectIndices: IntArray): Long
    external fun nativeRestoreDetached(handle: Long, detached: Long): Boolean
    external fun nativeDiscardDetached(detached: Long)
    external fun nativeTextEditable(handle: Long, objectIndex: Int): Int
    // The layout guard's reason (#128): [status, editable, cause, where, changedPixels, totalPixels, maxShiftPt].
    external fun nativeTextEditableReason(handle: Long, objectIndex: Int): DoubleArray
    external fun nativeLastLayoutVerdict(): DoubleArray
    external fun nativePageRegenerationVerdict(handle: Long): DoubleArray

    // The page check started early and run off the engine thread (#145). These are the only
    // calls made from another thread: the core serialises them, and PdfDocument keeps the
    // document open until the check has returned. Status [0] may be ERR_CANCELLED or ERR_NOT_JUDGED.
    external fun nativeCancelNew(): Long
    external fun nativeCancelRaise(cancel: Long)
    external fun nativeCancelFree(cancel: Long)
    external fun nativePageRegenerationVerdictCancellable(handle: Long, cancel: Long): DoubleArray
    external fun nativePageRegenerationVerdictCached(handle: Long): DoubleArray

    // Contract 8: redaction (#173). Marks are the core's own and never reach the file, so
    // nothing here changes the page. Rectangles are PDF points, bottom-left origin.
    external fun nativeMarkForRedaction(
        handle: Long, left: Double, bottom: Double, right: Double, top: Double,
    ): Int
    /**
     * NOT count-then-fill: this MAKES the marks, one per line, and returns their ids —
     * empty when the selection covered no text (#329: undo has to be able to name them).
     */
    external fun nativeMarkTextForRedaction(
        handle: Long, left: Double, bottom: Double, right: Double, top: Double,
    ): IntArray
    /** [id, l, b, r, t] per mark. */
    external fun nativeRedactionMarksPacked(handle: Long): DoubleArray
    external fun nativeMoveRedactionMark(
        handle: Long, markId: Int, left: Double, bottom: Double, right: Double, top: Double,
    ): Boolean
    external fun nativeRemoveRedactionMark(handle: Long, markId: Int)
    external fun nativeRedactionMarkCount(handle: Long): Int
    external fun nativeClearRedactionMarks(handle: Long)
    /** [status, 19 counts, refusalCount, (pageIndex, reason) per refusal]. */
    external fun nativeApplyRedactions(handle: Long): IntArray
    external fun nativeRedactionPoisoned(handle: Long): Boolean

    /** MEGAPDF_ERR_REDACT: the redaction removed nothing, or could not finish (#173). */
    const val STATUS_REDACT = -10

    // megapdf_status codes (megapdf_core.h) the page check returns (#145).
    const val STATUS_CANCELLED = -7
    const val STATUS_NOT_JUDGED = -8

    // FPDF_GetLastError codes (fpdfview.h).
    const val ERR_PASSWORD = 4
    /** FPDF_ERR_SECURITY: a security handler PDFium does not support (#131). */
    const val ERR_SECURITY = 6
    /** FPDF_ERR_FILE: the file could not be opened or read. */
    const val ERR_FILE = 2
    /** MEGAPDF_OPEN_ERR_TOO_LARGE: past what the platform can address (#147); not reachable on 64-bit Android. */
    const val ERR_TOO_LARGE = 100
}
