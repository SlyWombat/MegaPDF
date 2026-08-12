package com.megapdf.engine

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Text search engine behavior (#26) against the SDD §6.2 parity fixtures.
 * Fixture page 1 draws "MegaPDF engine fixture - page 1" (24pt, baseline at
 * 72,720) and "The square below is a drawn checkbox candidate." (12pt);
 * page 2 draws only "Page 2".
 */
@RunWith(AndroidJUnit4::class)
class TextSearchTest {

    private val engine = PdfEngine()

    private fun asset(name: String): ByteArray =
        InstrumentationRegistry.getInstrumentation().context.assets
            .open(name).use { it.readBytes() }

    @Test
    fun findsLiteralSubstringWithPlausibleRect() {
        runBlocking {
            val doc = engine.open(asset("fixture.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    val matches = page.search("fixture")
                    assertEquals(1, matches.size)
                    val rect = matches[0].rects.single()
                    // "fixture" sits right of the leading "MegaPDF engine " and
                    // spans the 24pt line whose baseline is at y=720.
                    assertTrue("left should clear the line start", rect.left > 72.0)
                    assertTrue("rect must not be degenerate",
                        rect.right > rect.left && rect.top > rect.bottom)
                    assertTrue("rect should straddle the baseline",
                        rect.bottom < 726.0 && rect.top > 714.0)
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun searchIsCaseInsensitive() {
        runBlocking {
            val doc = engine.open(asset("fixture.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    // Query and content differ in case in both directions.
                    assertEquals(1, page.search("megapdf").size)
                    assertEquals(1, page.search("CHECKBOX").size)
                } finally {
                    page.close()
                }

                // "page" also hits the capitalized "Page 2" on page 2.
                val page2 = doc.openPage(1)
                try {
                    assertEquals(1, page2.search("page").size)
                } finally {
                    page2.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun reportsEveryOccurrenceInOrder() {
        runBlocking {
            val doc = engine.open(asset("fixture.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    val matches = page.search("e")
                    assertTrue("'e' occurs many times on page 1", matches.size > 1)
                    // Reading order on the fixture: top line first, so the first
                    // match's rect sits above the last match's.
                    assertTrue(matches.first().rects.single().top >
                        matches.last().rects.single().top)
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun noMatchesAndEmptyQueryReturnEmpty() {
        runBlocking {
            val doc = engine.open(asset("fixture.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    assertTrue(page.search("zebra").isEmpty())
                    assertTrue(page.search("").isEmpty())
                } finally {
                    page.close()
                }

                // Page 2 has none of page 1's text.
                val page2 = doc.openPage(1)
                try {
                    assertTrue(page2.search("checkbox").isEmpty())
                } finally {
                    page2.close()
                }
            } finally {
                doc.close()
            }
        }
    }
}
