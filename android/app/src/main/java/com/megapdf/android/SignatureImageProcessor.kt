package com.megapdf.android

/**
 * Signature image cleanup — the SDD §6.2 contract pixel math, ported from the
 * desktop `SignatureImageProcessor` (lines 80–118). Pure functions over ARGB
 * int arrays so the JVM unit tests can assert exact parity.
 */
object SignatureImageProcessor {

    private const val WHITE_LUMINANCE_CUTOFF = 235
    private const val INK_ALPHA_CUTOFF = 16
    private const val TRIM_MARGIN = 4

    data class Trimmed(val pixels: IntArray, val width: Int, val height: Int)

    /** True if the image already carries meaningful transparency (skip cleanup). */
    fun hasTransparency(pixels: IntArray): Boolean =
        pixels.any { (it ushr 24) < 250 }

    /**
     * Photographed/scanned signatures: near-white becomes transparent.
     * Luminance = 0.114·B + 0.587·G + 0.299·R; if > 235, alpha := 0.
     */
    fun removeWhiteBackground(pixels: IntArray): IntArray {
        val out = IntArray(pixels.size)
        for (i in pixels.indices) {
            val p = pixels[i]
            val r = (p shr 16) and 0xFF
            val g = (p shr 8) and 0xFF
            val b = p and 0xFF
            val luminance = 0.114 * b + 0.587 * g + 0.299 * r
            out[i] = if (luminance > WHITE_LUMINANCE_CUTOFF) p and 0x00FFFFFF else p
        }
        return out
    }

    /**
     * Crops to the bounding box of visible ink (alpha > 16) plus a 4px margin.
     * Returns the input unchanged when nothing is visible.
     */
    fun trimToInk(pixels: IntArray, width: Int, height: Int): Trimmed {
        var minX = width
        var minY = height
        var maxX = -1
        var maxY = -1
        for (y in 0 until height) {
            for (x in 0 until width) {
                if ((pixels[y * width + x] ushr 24) > INK_ALPHA_CUTOFF) {
                    if (x < minX) minX = x
                    if (x > maxX) maxX = x
                    if (y < minY) minY = y
                    if (y > maxY) maxY = y
                }
            }
        }
        if (maxX < 0) return Trimmed(pixels, width, height)

        minX = (minX - TRIM_MARGIN).coerceAtLeast(0)
        minY = (minY - TRIM_MARGIN).coerceAtLeast(0)
        maxX = (maxX + TRIM_MARGIN).coerceAtMost(width - 1)
        maxY = (maxY + TRIM_MARGIN).coerceAtMost(height - 1)

        val w = maxX - minX + 1
        val h = maxY - minY + 1
        val out = IntArray(w * h)
        for (y in 0 until h) {
            System.arraycopy(pixels, (minY + y) * width + minX, out, y * w, w)
        }
        return Trimmed(out, w, h)
    }
}
