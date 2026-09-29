package com.megapdf.android

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.pinch
import androidx.test.ext.junit.runners.AndroidJUnit4
import kotlin.math.abs
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Pinch anchoring (#527): the point between the fingers has to stay under them as the page
 * scales, in both axes — not slide toward the top-left corner the way a bare
 * `pageWidthDp = maxWidth * zoom` layout change does on its own, since a `LazyColumn` grows
 * from its own origin rather than from wherever the gesture happened.
 *
 * This is the regression test #527 asks for: pinch around a known point, off-centre in both
 * directions so a fix that only corrected one axis would still be caught, and assert that
 * point is still under the same spot on screen once the zoom settles. Run against the
 * pre-fix handler (reading only `calculateZoom()`), this fails — the anchor point drifts by
 * far more than [DRIFT_TOLERANCE_PX].
 *
 * The measured pinch starts from an already-zoomed page (a first, unmeasured pinch to
 * roughly 2x), not from fit-to-width itself: at fit-to-width the demo page is shorter than
 * the viewport and `Arrangement.spacedBy(8.dp, Alignment.CenterVertically)` centres it (#48)
 * — there is nothing to scroll yet either way, so no scroll-based correction (this fix or any
 * other) has anything to act on until the page is tall enough to need scrolling at all. That
 * is a separate, pre-existing piece of behaviour outside #527's scope; starting past it keeps
 * this test on the actual bug, which is what happens once the page can be scrolled.
 */
@RunWith(AndroidJUnit4::class)
class PinchAnchorTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    /** Where the page-fraction point ([xFrac], [yFrac]) currently sits on screen, in root px. */
    private fun anchorOnScreen(xFrac: Float, yFrac: Float): Offset {
        val node = rule.page().fetchSemanticsNode()
        return node.positionInRoot + Offset(node.size.width * xFrac, node.size.height * yFrac)
    }

    /** Polls the page's width itself, rather than [ComposeTestRule.waitUntil], purely so a
     *  timeout's message says what the width actually reached instead of just "didn't". */
    private fun waitForWidthAbove(threshold: Int, label: String, timeoutMs: Long = SETTLE_MS): Int {
        val deadline = System.currentTimeMillis() + timeoutMs
        var last = rule.page().fetchSemanticsNode().size.width
        while (System.currentTimeMillis() < deadline) {
            last = rule.page().fetchSemanticsNode().size.width
            if (last > threshold) return last
            Thread.sleep(100)
        }
        throw AssertionError("$label: width never exceeded $threshold; last seen was $last")
    }

    @Test
    fun pinchKeepsTheCentroidPointFixed() {
        val file = Fixtures.demo("zoom-anchor-527.pdf")
        rule.open(file)
        val fittedWidth = rule.page().fetchSemanticsNode().size.width

        // A first, unmeasured pinch (exactly 2x, by construction: the end offsets are simply
        // the start ones doubled) to get well past fit-to-width, so the page is already
        // taller than the viewport and ordinary scrolling is in play before the gesture this
        // test actually measures begins.
        rule.page().performTouchInput {
            val c = center
            pinch(
                start0 = c - Offset(60f, 40f), end0 = c - Offset(120f, 80f),
                start1 = c + Offset(60f, 40f), end1 = c + Offset(120f, 80f),
                durationMillis = 300,
            )
        }
        val zoomedWidth = waitForWidthAbove(fittedWidth * 3 / 2, "pre-zoom pinch")

        val xFrac = 0.68f
        val yFrac = 0.30f
        val before = anchorOnScreen(xFrac, yFrac)

        // The pinch is centred at that same point, in the page's own pre-pinch local
        // coordinates: the two fingers move symmetrically apart around it, the shape a real
        // two-finger zoom makes. The ratio this particular gesture asks for (≈1.8x) keeps the
        // total zoom (2x already, before it starts) comfortably under MAX_ZOOM's 4x — the
        // clamp case belongs to PinchAnchorMathTest, not this test.
        val anchorLocal = Offset(
            rule.page().fetchSemanticsNode().size.width * xFrac,
            rule.page().fetchSemanticsNode().size.height * yFrac,
        )
        rule.page().performTouchInput {
            pinch(
                start0 = anchorLocal - Offset(60f, 40f), end0 = anchorLocal - Offset(110f, 70f),
                start1 = anchorLocal + Offset(60f, 40f), end1 = anchorLocal + Offset(110f, 70f),
                durationMillis = 400,
            )
        }

        waitForWidthAbove(zoomedWidth * 5 / 4, "measured pinch")

        val after = anchorOnScreen(xFrac, yFrac)
        val driftX = abs(after.x - before.x)
        val driftY = abs(after.y - before.y)
        assertTrue(
            "the pinched point should stay put: moved by ($driftX, $driftY) px, from $before to $after",
            driftX < DRIFT_TOLERANCE_PX && driftY < DRIFT_TOLERANCE_PX,
        )
    }

    companion object {
        /** A few px of slack for touch-slop and rounding — nowhere near the many tens to
         *  hundreds of px the uncorrected, top-left-anchored zoom drifts an off-centre
         *  point by. */
        private const val DRIFT_TOLERANCE_PX = 24f
    }
}
