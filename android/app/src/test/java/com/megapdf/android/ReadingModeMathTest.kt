package com.megapdf.android

import com.megapdf.engine.PageTint
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The parts of reading mode that are arithmetic and policy rather than pixels (#507, #513):
 * the fit-page zoom, the floor a pinch may reach, when the floating bar is allowed to hide
 * itself, and what makes a held page bitmap stale.
 *
 * These run on the JVM. Everything else about reading mode — the chrome not being composed,
 * the tap, the Back ordering — needs a real device and lives in `androidTest`.
 */
class ReadingModeMathTest {

    // A phone-shaped viewport, in pixels: 1080 × 2000 is a Pixel-class screen with the
    // system bars off, which is what reading mode leaves.
    private val viewportWidth = 1080f
    private val viewportHeight = 2000f

    // US Letter and its landscape twin, in PDF points.
    private val letterWidth = 612.0
    private val letterHeight = 792.0

    @Test
    fun fitPageOnAPortraitPageZoomsOutBelowFitWidth() {
        val zoom = fitPageZoom(viewportWidth, viewportHeight, letterWidth, letterHeight)
        // 2000 × 612 / (1080 × 792) — a tall page in a tall viewport still has to shrink,
        // because the viewport is proportionally narrower than the page is.
        assertEquals(1.43, zoom.toDouble(), 0.01)
        assertTrue("Letter in a phone viewport still fits its height at > fit width here", zoom > 1f)
    }

    @Test
    fun fitPageOnASquareViewportShrinksATallPage() {
        // A short, wide viewport is where fit page really is a zoom *out*: the page's height
        // is what does not fit, and fit width (1) is too big.
        val zoom = fitPageZoom(1080f, 700f, letterWidth, letterHeight)
        assertEquals(0.5, zoom.toDouble(), 0.01)
        assertTrue(zoom < 1f)
    }

    @Test
    fun fitPageOnALandscapePageZoomsIn() {
        val zoom = fitPageZoom(viewportWidth, viewportHeight, letterHeight, letterWidth)
        assertTrue("a landscape page laid out at the screen's width is short", zoom > 1f)
    }

    @Test
    fun fitPageIsSafeWhenNothingHasBeenMeasuredYet() {
        assertEquals(1f, fitPageZoom(0f, 0f, letterWidth, letterHeight), 0f)
        assertEquals(1f, fitPageZoom(viewportWidth, viewportHeight, 0.0, 0.0), 0f)
    }

    @Test
    fun theZoomFloorNeverStopsAboveFitPage() {
        // Fit page below fit width: the floor follows it down, or the preset would put the
        // page somewhere the next pinch snapped straight back out of.
        assertEquals(0.5f, readingZoomFloor(0.5f), 0f)
        // Fit page above fit width (a landscape page): fit width is still the floor, because
        // it is the one that shows more, and it is where the horizontal scroll turns off.
        assertEquals(1f, readingZoomFloor(1.8f), 0f)
    }

    @Test
    fun theBarNeverHidesItselfWhileTouchExplorationIsOn() {
        assertTrue("with no screen reader the bar is allowed to go", readingBarAutoHides(false))
        assertFalse(
            "the bar is reading mode's only chrome; pinned is the answer, not a longer wait",
            readingBarAutoHides(true),
        )
    }

    @Test
    fun aHeldPageIsStaleWhenTheTintMoves() {
        val normal = RenderedPage(1080, PageTint.NORMAL)
        assertEquals(normal, RenderedPage(1080, PageTint.NORMAL))
        // The whole point of #513's cache rule: same width, different colours, different
        // pixels. Keyed on width alone this was equal, and a page already on screen kept its
        // old colours until something else happened to dirty it.
        assertNotEquals(normal, RenderedPage(1080, PageTint.NIGHT))
        assertNotEquals(normal, RenderedPage(1080, PageTint.SEPIA))
        assertNotEquals(normal, RenderedPage(720, PageTint.NORMAL))
    }

    @Test
    fun pageTintRoundTripsThroughItsStoredName() {
        for (tint in PageTint.entries) {
            assertEquals(tint, PageTint.of(tint.storedName))
        }
        // Normal is the empty string, so a preferences store never written reads as Normal…
        assertEquals(PageTint.NORMAL, PageTint.of(""))
        assertEquals(PageTint.NORMAL, PageTint.of(null))
        // …and so does anything a later version of the app may have written.
        assertEquals(PageTint.NORMAL, PageTint.of("Twilight"))
    }
}
