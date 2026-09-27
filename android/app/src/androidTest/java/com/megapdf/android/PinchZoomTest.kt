package com.megapdf.android

import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.test.doubleClick
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.pinch
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Pinch zoom (#336): two fingers moving apart over the page make it wider than the
 * screen, and a double tap brings it back to fit.
 */
@RunWith(AndroidJUnit4::class)
class PinchZoomTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    private fun pageWidth(): Int = rule.page().fetchSemanticsNode().size.width

    @Test
    fun pinchOutWidensThePageAndDoubleTapFitsItAgain() {
        val file = Fixtures.demo("zoom-346.pdf")
        rule.open(file)
        val fitted = pageWidth()

        rule.page().performTouchInput {
            val c = center
            pinch(
                start0 = c - Offset(60f, 0f), end0 = c - Offset(width * 0.4f, 0f),
                start1 = c + Offset(60f, 0f), end1 = c + Offset(width * 0.4f, 0f),
                durationMillis = 400,
            )
        }
        rule.waitUntil(SETTLE_MS) { pageWidth() > fitted * 3 / 2 }
        val zoomed = pageWidth()
        assertTrue("the page is wider than fit-to-width: $zoomed > $fitted", zoomed > fitted)

        // Double tap at a point still on screen — the page's left edge is where the scroll
        // starts — takes the zoom back to 1×.
        rule.page().performTouchInput { doubleClick(Offset(fitted * 0.25f, height * 0.5f)) }
        rule.waitUntil(SETTLE_MS) { pageWidth() == fitted }
        assertEquals(fitted, pageWidth())
    }
}
