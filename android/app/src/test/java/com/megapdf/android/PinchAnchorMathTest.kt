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

    /**
     * The real bug found on the emulator: a fast pinch queues several of these steps before
     * Compose ever re-lays the page out at the new width, and `ScrollState`/`LazyListState`
     * both cap `dispatchRawDelta` to whatever the *last completed layout* measured — so a
     * correct-in-isolation delta, computed step by step against the real (still-too-small)
     * scroll position, gets cut down by that stale ceiling and the shortfall is simply gone.
     * Over a fast multi-step pinch this does not average out: a first CI run of
     * [PinchAnchorTest] against exactly this per-step design (no `shadow`/`pending`, deltas
     * computed straight off the real, possibly-clamped offset) measured the pinched point
     * drifting 323px off a page that had grown about 3.6x — most, but not all, of what an
     * uncorrected zoom would have drifted it. `shadowH`/`pendingH` (and their vertical
     * equivalents) in [ViewerScreen] are the fix: the shadow value is never clamped, so it is
     * always exactly right, and whatever a step's request could not land waits in `pending`
     * for the next one instead of being forgotten.
     *
     * This models that failure directly: a scrollable that refuses every `dispatchRawDelta`
     * until layout "catches up" partway through a fast multi-step zoom.
     */
    @Test
    fun `dropping an unconsumed delta loses it for good, but carrying it forward recovers it in full`() {
        val centroid = 734.4f
        val steps = List(20) { 1.07f } // compounds to roughly the 3.6x seen on the emulator
        val catchUpAfterStep = 15

        // The bug: each step computes its delta from the real (possibly still-clamped)
        // scroll position, and whatever `dispatchRawDelta` refuses is simply dropped.
        run {
            var real = 0f
            var roomAvailable = false
            steps.forEachIndexed { index, ratio ->
                val delta = anchoredScrollDelta(real, centroid, ratio)
                val consumed = if (roomAvailable) delta else 0f
                real += consumed
                if (index + 1 == catchUpAfterStep) roomAvailable = true
            }
            val ideal = idealFinalOffset(centroid, steps)
            assertTrue(
                "the naive, unpatched approach should fall well short once some early " +
                    "deltas were clamped away: real=$real, ideal=$ideal",
                ideal - real > 200f,
            )
        }

        // The fix: the shadow value accumulates the exact delta regardless of what the real
        // scrollable could accept, and any shortfall is retried as `pending` on the next step.
        run {
            var shadow = 0f
            var real = 0f
            var pending = 0f
            var roomAvailable = false
            steps.forEachIndexed { index, ratio ->
                val delta = anchoredScrollDelta(shadow, centroid, ratio)
                shadow += delta
                pending += delta
                val consumed = if (roomAvailable) pending else 0f
                real += consumed
                pending -= consumed
                if (index + 1 == catchUpAfterStep) roomAvailable = true
            }
            assertEquals("the patched approach recovers the full correction", shadow, real, 0.01f)
            assertEquals(idealFinalOffset(centroid, steps), real, 0.01f)
        }
    }

    /** The exact scroll offset a perfectly-applied sequence of [steps] ratios converges to,
     *  starting from offset 0 — computed the same way [anchoredScrollDelta] is, applied with
     *  nothing ever clamped, as a reference for what "fully corrected" means. */
    private fun idealFinalOffset(centroid: Float, steps: List<Float>): Float {
        var offset = 0f
        for (ratio in steps) offset += anchoredScrollDelta(offset, centroid, ratio)
        return offset
    }

    companion object {
        private const val MAX_ZOOM_FOR_TEST = 4f
    }
}
