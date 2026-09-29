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

    @Test
    fun pinchKeepsTheCentroidPointFixed() {
        val file = Fixtures.demo("zoom-anchor-527.pdf")
        rule.open(file)

        val xFrac = 0.68f
        val yFrac = 0.30f
        val before = anchorOnScreen(xFrac, yFrac)
        val fittedWidth = rule.page().fetchSemanticsNode().size.width

        // The pinch is centred at that same point, in the page's own pre-zoom local
        // coordinates: the two fingers move symmetrically apart around it, the shape a
        // real two-finger zoom makes.
        val anchorLocal = Offset(
            rule.page().fetchSemanticsNode().size.width * xFrac,
            rule.page().fetchSemanticsNode().size.height * yFrac,
        )
        rule.page().performTouchInput {
            pinch(
                start0 = anchorLocal - Offset(60f, 40f), end0 = anchorLocal - Offset(220f, 140f),
                start1 = anchorLocal + Offset(60f, 40f), end1 = anchorLocal + Offset(220f, 140f),
                durationMillis = 400,
            )
        }

        rule.waitUntil(SETTLE_MS) {
            rule.page().fetchSemanticsNode().size.width > fittedWidth * 3 / 2
        }

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
