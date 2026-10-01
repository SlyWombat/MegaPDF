package com.megapdf.engine

import android.graphics.Bitmap
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The #514 JNI bindings for the rest of contract 9, plus `megapdf_render_clip`.
 *
 * What contract 9 *says* is covered by `core_tests.cpp` against these same fixtures
 * (`tools/gen_structure_fixtures.py`, and the `.blocks` goldens beside them); this proves the
 * blocks, spans, strings and font names cross the JNI boundary intact, and that the clip render
 * lands where the rectangle said. Packed-array bindings are exactly where an off-by-one in a stride
 * hides, so the strides are asserted, not assumed.
 */
@RunWith(AndroidJUnit4::class)
class ReflowBindingTest {

    private val engine = PdfEngine()

    private fun assetBytes(name: String): ByteArray =
        InstrumentationRegistry.getInstrumentation().context.assets.open(name).use { it.readBytes() }

    @Test
    fun blocksSpansAndFontNamesCrossTheBoundary() {
        runBlocking {
            val doc = engine.open(assetBytes("structure/headings.pdf"))
            try {
                val pages = doc.pageCount()
                val structure = doc.loadStructure(0, pages)
                try {
                    val count = PdfiumNative.nativeBlockCount(structure)
                    assertTrue("the fixture has blocks", count > 0)

                    // The two packed arrays must be exactly 8 and 4 wide per block, or every field
                    // after the first block is read from the wrong place.
                    val packed = PdfiumNative.nativeBlocksPacked(structure)
                    val bounds = PdfiumNative.nativeBlockBounds(structure)
                    assertNotNull(packed); assertNotNull(bounds)
                    assertEquals(count * 8, packed!!.size)
                    assertEquals(count * 4, bounds!!.size)

                    val blocks = readBlocks(structure)
                    assertEquals(count, blocks.size)

                    // A block's text is exactly the concatenation of its spans (SDD §6.2
                    // contract 6). If a span string were dropped or doubled at the boundary this
                    // is what would catch it, on every block at once.
                    for (b in blocks) {
                        if (b.spans.isEmpty()) continue
                        assertEquals(
                            "block on page ${b.page} kind ${b.kind}: spans concatenate to its text",
                            b.text, b.spans.joinToString("") { it.text },
                        )
                    }

                    // megapdf_block_span_font (#514): the fixtures embed two faces under known
                    // /BaseFont names, so this proves the NAME arrives and that it follows the span
                    // rather than being one page-wide constant.
                    val named = blocks.flatMap { it.spans }.filter { it.font.isNotEmpty() }
                    assertTrue("some span names a font", named.isNotEmpty())
                    assertTrue(
                        "the names are the fixtures' own /BaseFont names",
                        named.all { it.font.startsWith("MegaPDFStructureFixture-") },
                    )
                    assertTrue("a bold face is named", named.any { it.font.endsWith("-Bold") })
                    assertTrue("a regular face is named", named.any { it.font.endsWith("-Regular") })
                    // And the name agrees with the weight flag the same span carries.
                    for (s in named) {
                        assertEquals(
                            "the -Bold face is exactly the spans flagged bold (${s.font})",
                            s.font.endsWith("-Bold"), s.bold,
                        )
                    }

                    // There is at least one heading, and headings are what the reflow's levels come
                    // from — a binding that lost `level` would read every heading as level 0.
                    val headings = blocks.filter { it.kind == BlockKind.HEADING }
                    assertTrue("the headings fixture has headings", headings.isNotEmpty())
                    assertTrue("a heading's level is 1..6", headings.all { it.level in 1..6 })

                    assertTrue("a body size was computed", PdfiumNative.nativeStructureBodySize(structure) > 0)
                    for (p in 0 until pages) {
                        val conf = PdfiumNative.nativeStructurePageConfidence(structure, p)
                        assertTrue("page $p confidence is 0..100 (got $conf)", conf in 0..100)
                        val src = PdfiumNative.nativeStructurePageSource(structure, p)
                        assertTrue("page $p source is a known value", src == BlockSource.HEURISTIC || src == BlockSource.TAGGED)
                    }
                } finally {
                    doc.freeStructure(structure)
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun reflowMapsBlocksAndCountsWhatItDeclines() {
        runBlocking {
            val doc = engine.open(assetBytes("structure/lists.pdf"))
            try {
                val r = doc.readReflow(0, doc.pageCount())
                assertTrue("the reflow has items", r.items.isNotEmpty())
                // A list fixture must produce list items with the markers the document drew, not
                // bullets this code invented.
                val listItems = r.items.filterIsInstance<ReflowItem.ListItem>()
                assertTrue("the lists fixture reflows to list items", listItems.isNotEmpty())
                assertTrue("a marker came from the document", listItems.any { it.marker.isNotEmpty() })
                // Nothing in this fixture is a form or a scan, so nothing may be declined. A
                // decline rule that fired on everything would pass a test that only asserted the
                // rule exists; this asserts it does NOT fire where it should not.
                assertTrue("a text fixture declines no page", r.declined.isEmpty())
                assertEquals(0.0, r.declineShare, 0.0)
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun renderClipShowsTheRegionTheRectangleNames() {
        runBlocking {
            val doc = engine.open(assetBytes("structure/columns.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    // The whole page, and then the page's top-left quarter through the clip. The
                    // clip must differ from the same-sized whole-page raster (it is a 2x zoom into
                    // one corner), and must not come back blank -- the two ways this binding fails
                    // are "ignored the rectangle" and "drew nothing".
                    val whole = Bitmap.createBitmap(200, 260, Bitmap.Config.ARGB_8888)
                    page.render(whole)
                    val w = page.widthPoints
                    val h = page.heightPoints
                    val clip = Bitmap.createBitmap(200, 260, Bitmap.Config.ARGB_8888)
                    page.renderClip(clip, PdfRect(0.0, h / 2, w / 2, h))
                    assertFalse("the clip is not the whole page", whole.sameAs(clip))
                    var ink = 0
                    for (y in 0 until clip.height step 4) {
                        for (x in 0 until clip.width step 4) {
                            if (clip.getPixel(x, y) != android.graphics.Color.WHITE) ink++
                        }
                    }
                    assertTrue("the clip drew something", ink > 0)
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }
}
