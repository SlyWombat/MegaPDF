package com.megapdf.android

import kotlin.math.abs
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The scroll math behind #527: pinching must keep the content point under the gesture's
 * centroid fixed on screen, even though zoom here is a layout size change that otherwise
 * grows from the scrollable's own origin.
 *
 * These model a scrollable in one axis: a point sits at [contentPosAtZoom1] px from the
 * scrollable's origin when zoom is 1, so at zoom `z` it sits at `contentPosAtZoom1 * z`;
 * what shows at viewport-relative position `screen` is whatever content position the
 * current scroll offset lines up with it — `screen == contentPos - scrollOffset`.
 *
 * [PinchAnchorTest] is the real regression test (#346's Compose instrumentation): it drives
 * an actual two-finger pinch and reads the point back off the rendered page. It needs a
 * device or emulator this module's plain JVM unit tests don't have, so the first test below
 * stands in as the "red" proof: it models exactly what the pre-fix handler did — read
 * `calculateZoom()`, apply it, touch nothing else — and shows that leaves the pinched point
 * far from where it was. [anchoredScrollDelta] is what closes that gap.
 */
class PinchAnchorMathTest {

    @Test
    fun `leaving the scroll offset untouched drifts the pinched point toward the origin (the bug)`() {
        // A point 400px into the content, dead centre of the viewport before the pinch.
        val contentPosAtZoom1 = 400f
        val zoomBefore = 1f
        val scrollBefore = 0f
        val centroidOnScreen = contentPosAtZoom1 * zoomBefore - scrollBefore
        val appliedRatio = 2f

        // The pre-#527 handler: `zoom = (zoom * change).coerceIn(...)` and nothing else —
        // no read of the scroll state, so it stays exactly where it was.
        val scrollAfterBug = scrollBefore
        val screenAfterBug = contentPosAtZoom1 * zoomBefore * appliedRatio - scrollAfterBug

        assertTrue(
            "an uncorrected zoom moves a centred point hundreds of px toward the origin: " +
                "was at $centroidOnScreen, landed at $screenAfterBug",
            abs(screenAfterBug - centroidOnScreen) > 300f,
        )
    }

    @Test
    fun `anchoredScrollDelta keeps the pinched point exactly where it was, at any zoom, offset or ratio`() {
        for (contentPosAtZoom1 in listOf(0f, 120f, 400f, 933.5f)) {
            for (zoomBefore in listOf(1f, 1.5f, 2.4f, MAX_ZOOM_FOR_TEST)) {
                for (scrollBefore in listOf(0f, 50f, 213f)) {
                    for (appliedRatio in listOf(1f, 1.3f, 2f, 0.7f)) {
                        val centroidOnScreen = contentPosAtZoom1 * zoomBefore - scrollBefore
                        val delta = anchoredScrollDelta(scrollBefore, centroidOnScreen, appliedRatio)
                        val scrollAfter = scrollBefore + delta
                        val screenAfter = contentPosAtZoom1 * zoomBefore * appliedRatio - scrollAfter
                        assertEquals(
                            "content=$contentPosAtZoom1 zoom=$zoomBefore scroll=$scrollBefore ratio=$appliedRatio",
                            centroidOnScreen, screenAfter, 0.01f,
                        )
                    }
                }
            }
        }
    }

    /** #527's clamp case: at MIN_ZOOM/MAX_ZOOM the ratio actually applied is 1 even though a
     *  pinch is still going on, and that must make no correction — a nonzero one there would
     *  drift the page while the zoom itself holds still. */
    @Test
    fun `a ratio of exactly 1 — the zoom already clamped — makes no correction`() {
        assertEquals(0f, anchoredScrollDelta(123f, 45f, 1f), 0f)
        assertEquals(0f, anchoredScrollDelta(0f, 0f, 1f), 0f)
    }

    companion object {
        private const val MAX_ZOOM_FOR_TEST = 4f
    }
}
