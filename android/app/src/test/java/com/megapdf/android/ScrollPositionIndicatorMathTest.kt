package com.megapdf.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The arithmetic behind the scroll-position indicator (Dave, 2026-10-01): turning a
 * `LazyColumn`'s first-visible-item bookkeeping into a position in the *whole document*, not
 * the current page, which is the semantic the task that added this indicator called out as
 * "the one that matters most and is easiest to get wrong".
 *
 * US Letter throughout, at a page width fixed to a 1080 px viewport — the same numbers
 * [ReadingModeMathTest] uses, so the two suites agree on what the demo document looks like
 * rendered.
 */
class ScrollPositionIndicatorMathTest {

    private val letterWidth = 612.0
    private val letterHeight = 792.0
    private val pageWidthPx = 1080f
    private val spacingPx = 24f // 8.dp at a 3x density, a round number for these sums.

    private fun pages(count: Int, widthPoints: Double = letterWidth, heightPoints: Double = letterHeight) =
        List(count) { PageSize(widthPoints, heightPoints) }

    // Every Letter page at this width renders at 1080 * 792 / 612 = 1397.6470... px tall.
    private val letterHeightPx = (pageWidthPx * (letterHeight / letterWidth)).toFloat()

    @Test
    fun prefixSumsAccumulateEachPagesOwnAspect() {
        val mixed = listOf(
            PageSize(letterWidth, letterHeight), // portrait, aspect 1.294
            PageSize(letterHeight, letterWidth), // landscape, aspect 0.773
        )
        val prefix = pageAspectPrefixSums(mixed)
        assertEquals(3, prefix.size)
        assertEquals(0f, prefix[0], 0.001f)
        assertEquals((letterHeight / letterWidth).toFloat(), prefix[1], 0.001f)
        assertEquals(
            (letterHeight / letterWidth + letterWidth / letterHeight).toFloat(), prefix[2], 0.001f,
        )
    }

    @Test
    fun pageTopIsZeroForTheFirstPage() {
        val prefix = pageAspectPrefixSums(pages(5))
        assertEquals(0f, pageTopPx(prefix, 0, pageWidthPx, spacingPx), 0.01f)
    }

    @Test
    fun pageTopSumsEveryPageAndGapBeforeIt() {
        // Ten identical pages: page 5's top is five page-heights and five gaps in (#143's
        // kind of arithmetic, done per page rather than once for the whole scroller).
        val prefix = pageAspectPrefixSums(pages(10))
        val expected = 5 * letterHeightPx + 5 * spacingPx
        assertEquals(expected, pageTopPx(prefix, 5, pageWidthPx, spacingPx), 1f)
    }

    @Test
    fun pageTopClampsAnOutOfRangeIndexRatherThanIndexingPastTheArray() {
        val prefix = pageAspectPrefixSums(pages(3))
        // No throw for an index beyond the document — `LazyListState` should never hand one
        // out, but a defensive clamp costs nothing and a crash here would take the whole
        // viewer down with it. Both land on the same place the array's last entry gives.
        val onePastTheLastPage = pageTopPx(prefix, 3, pageWidthPx, spacingPx)
        val wayPast = pageTopPx(prefix, 999, pageWidthPx, spacingPx)
        assertEquals(onePastTheLastPage, wayPast, 0.01f)
    }

    @Test
    fun totalHeightIsEveryPagePlusEveryGapBetweenThem() {
        val prefix = pageAspectPrefixSums(pages(4))
        val expected = 4 * letterHeightPx + 3 * spacingPx // 3 gaps between 4 pages.
        assertEquals(expected, totalDocumentHeightPx(prefix, pageWidthPx, spacingPx), 1f)
    }

    @Test
    fun totalHeightOfAnEmptyDocumentIsZero() {
        val prefix = pageAspectPrefixSums(pages(0))
        assertEquals(0f, totalDocumentHeightPx(prefix, pageWidthPx, spacingPx), 0.001f)
    }

    @Test
    fun noThumbWhenTheWholeDocumentAlreadyFitsTheViewport() {
        // A one-page document shorter than the viewport: nothing to scroll, so nothing to
        // show — the same reason the Mac/Linux scroller (#143) draws no bars either.
        val total = letterHeightPx
        assertNull(scrollThumb(total, viewportHeightPx = total + 500f, scrolledPastPx = 0f))
        assertNull(scrollThumb(total, viewportHeightPx = total, scrolledPastPx = 0f))
    }

    @Test
    fun noThumbWithAZeroHeightViewport() {
        // Measured before the first layout pass; nothing to divide by yet.
        assertNull(scrollThumb(totalHeightPx = 10_000f, viewportHeightPx = 0f, scrolledPastPx = 0f))
    }

    @Test
    fun theThumbStartsAtTheTopForAnUnscrolledLongDocument() {
        val total = 50 * letterHeightPx
        val viewport = 2000f
        val thumb = scrollThumb(total, viewport, scrolledPastPx = 0f)
        requireNotNull(thumb)
        assertEquals(0f, thumb.start, 0.001f)
        assertEquals(viewport / total, thumb.fraction, 0.001f)
    }

    @Test
    fun theThumbReachesTheBottomAtTheMaximumScrollOffset() {
        val total = 50 * letterHeightPx
        val viewport = 2000f
        val maxScroll = total - viewport
        val thumb = scrollThumb(total, viewport, scrolledPastPx = maxScroll)
        requireNotNull(thumb)
        assertEquals(1f - thumb.fraction, thumb.start, 0.001f)
    }

    @Test
    fun theThumbSitsHalfwayAtHalfTheMaximumScrollOffset() {
        val total = 50 * letterHeightPx
        val viewport = 2000f
        val thumb = scrollThumb(total, viewport, scrolledPastPx = (total - viewport) / 2f)
        requireNotNull(thumb)
        assertEquals((1f - thumb.fraction) / 2f, thumb.start, 0.001f)
    }

    @Test
    fun scrolledPastPxBeyondTheDocumentClampsToTheBottomRatherThanOverflowing() {
        val total = 20 * letterHeightPx
        val viewport = 1800f
        val thumb = scrollThumb(total, viewport, scrolledPastPx = total * 10f)
        requireNotNull(thumb)
        assertEquals(1f - thumb.fraction, thumb.start, 0.001f)
    }

    @Test
    fun theThumbNeverShrinksBelowTheMinimumFractionOnAHugeDocument() {
        // The stress corpus runs to 10,000 pages (docs/qa/pdf-test-corpus.md): a page-count
        // scrollbar whose thumb rounds away to nothing says as little as no scrollbar at all.
        val prefix = pageAspectPrefixSums(pages(10_000))
        val total = totalDocumentHeightPx(prefix, pageWidthPx, spacingPx)
        val viewport = 2000f
        val thumb = scrollThumb(total, viewport, scrolledPastPx = total / 2f)
        requireNotNull(thumb)
        assertTrue(
            "a 10,000-page document's thumb was only ${thumb.fraction}",
            thumb.fraction >= 0.04f,
        )
    }

    @Test
    fun theWholeDocumentPositionIsWhatMovesNotTheCurrentPagesOwnOffset() {
        // The semantic this indicator exists to get right: scrolling from the top of page 50
        // of 100 to the top of page 51 moves the thumb by one page's worth of the *document*,
        // not by "a full page" out of whatever a single page's own scroll range would be — a
        // continuous `LazyColumn` has no such thing, which is exactly why a naive per-item
        // reading could be tempted to reach for `firstVisibleItemIndex` alone and call it
        // "done", and get a count of pages rather than a position in the document.
        val prefix = pageAspectPrefixSums(pages(100))
        val total = totalDocumentHeightPx(prefix, pageWidthPx, spacingPx)
        val viewport = 2000f

        val atPage50 = pageTopPx(prefix, 49, pageWidthPx, spacingPx)
        val atPage51 = pageTopPx(prefix, 50, pageWidthPx, spacingPx)
        val thumb50 = requireNotNull(scrollThumb(total, viewport, atPage50))
        val thumb51 = requireNotNull(scrollThumb(total, viewport, atPage51))

        val onePageOfProgress = (atPage51 - atPage50) / (total - viewport)
        assertEquals(onePageOfProgress * (1f - thumb50.fraction), thumb51.start - thumb50.start, 0.0005f)
        assertTrue("turning a page must move the thumb forward", thumb51.start > thumb50.start)
    }
}
