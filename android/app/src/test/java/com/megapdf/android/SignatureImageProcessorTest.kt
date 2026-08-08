package com.megapdf.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SignatureImageProcessorTest {

    private fun argb(a: Int, r: Int, g: Int, b: Int): Int =
        (a shl 24) or (r shl 16) or (g shl 8) or b

    @Test
    fun `near-white becomes transparent, ink stays`() {
        val white = argb(255, 250, 250, 250)     // luminance ~250 > 235
        val ink = argb(255, 30, 30, 120)         // dark blue, stays
        val out = SignatureImageProcessor.removeWhiteBackground(intArrayOf(white, ink))
        assertEquals(0, out[0] ushr 24)
        assertEquals(ink, out[1])
    }

    @Test
    fun `luminance cutoff is 235 with BGR weights`() {
        // Pure green 255: luminance = 0.587*255 ≈ 149.7 → kept.
        val green = argb(255, 0, 255, 0)
        // 240 gray: luminance 240 → removed.
        val gray = argb(255, 240, 240, 240)
        val out = SignatureImageProcessor.removeWhiteBackground(intArrayOf(green, gray))
        assertEquals(green, out[0])
        assertEquals(0, out[1] ushr 24)
    }

    @Test
    fun `trim crops to ink bounding box plus 4px margin`() {
        val w = 30
        val h = 20
        val pixels = IntArray(w * h)  // all transparent
        pixels[10 * w + 12] = argb(255, 0, 0, 0)  // single ink dot at (12,10)
        val trimmed = SignatureImageProcessor.trimToInk(pixels, w, h)
        assertEquals(9, trimmed.width)   // 12±4 → x 8..16
        assertEquals(9, trimmed.height)  // 10±4 → y 6..14
        assertEquals(argb(255, 0, 0, 0), trimmed.pixels[4 * 9 + 4])  // dot now centered
    }

    @Test
    fun `trim margin clamps at edges`() {
        val w = 10
        val h = 10
        val pixels = IntArray(w * h)
        pixels[0] = argb(255, 0, 0, 0)  // ink at top-left corner
        val trimmed = SignatureImageProcessor.trimToInk(pixels, w, h)
        assertEquals(5, trimmed.width)   // 0..4
        assertEquals(5, trimmed.height)
    }

    @Test
    fun `nothing visible keeps image unchanged`() {
        val pixels = IntArray(25) { argb(10, 0, 0, 0) }  // alpha 10 ≤ 16
        val trimmed = SignatureImageProcessor.trimToInk(pixels, 5, 5)
        assertEquals(5, trimmed.width)
        assertEquals(5, trimmed.height)
    }

    @Test
    fun `transparency detection`() {
        assertTrue(SignatureImageProcessor.hasTransparency(intArrayOf(argb(0, 0, 0, 0))))
        assertFalse(SignatureImageProcessor.hasTransparency(
            intArrayOf(argb(255, 10, 10, 10), argb(252, 200, 200, 200))))
    }
}
