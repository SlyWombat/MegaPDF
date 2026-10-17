package com.megapdf.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** Which pages a print job asked for (#675), which is the part of printing that can go silently wrong. */
class PrintAdapterTest {

    @Test fun `no range at all is every page`() {
        assertNull(printedPageIndexes(emptyList(), pageCount = 5))
    }

    @Test fun `the system's ALL_PAGES is every page and is never expanded`() {
        // PageRange.ALL_PAGES is 0..Int.MAX_VALUE: expanding it first would be a two-billion-entry list.
        assertNull(printedPageIndexes(listOf(0..Int.MAX_VALUE), pageCount = 300))
    }

    @Test fun `a range that spans the whole document is every page`() {
        assertNull(printedPageIndexes(listOf(0..4), pageCount = 5))
    }

    @Test fun `pages 2 to 3 of 5 are two pages and not five`() {
        // The check the issue names: a range that is ignored prints the lot.
        assertEquals(listOf(1, 2), printedPageIndexes(listOf(1..2), pageCount = 5))
    }

    @Test fun `separate ranges keep the order the system gave`() {
        assertEquals(listOf(0, 3, 4), printedPageIndexes(listOf(0..0, 3..4), pageCount = 5))
    }

    @Test fun `a range running past the end is clamped to the document`() {
        assertEquals(listOf(3, 4), printedPageIndexes(listOf(3..99), pageCount = 5))
    }

    @Test fun `a range wholly outside the document is no pages`() {
        assertEquals(emptyList<Int>(), printedPageIndexes(listOf(7..9), pageCount = 5))
    }

    @Test fun `an empty document prints no pages`() {
        assertEquals(emptyList<Int>(), printedPageIndexes(listOf(0..Int.MAX_VALUE), pageCount = 0))
    }
}
