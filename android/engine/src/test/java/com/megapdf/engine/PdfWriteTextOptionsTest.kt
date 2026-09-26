package com.megapdf.engine

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * #386's `megapdf_write_options` packing — pure Kotlin, so it runs as a JVM unit test even
 * though the native call it feeds ([PdfDocument.writeText]) only runs instrumented (#13).
 */
class PdfWriteTextOptionsTest {

    @Test
    fun `defaults match the core's own zero-initialised struct`() {
        val opt = PdfWriteTextOptions()
        assertArrayEquals(
            intArrayOf(0, PdfiumNative.PAGE_BREAK_FORM_FEED, 0, PdfiumNative.WRITE_FIELDS_FILLED, 0),
            opt.packed(),
        )
    }

    @Test
    fun `Save-As defaults differ from the CLI's only in page break`() {
        val guiDefaults = PdfWriteTextOptions.saveAsDefaults()
        assertEquals(PdfiumNative.PAGE_BREAK_NONE, guiDefaults.pageBreak)
        assertEquals(false, guiDefaults.keepLines)
        assertEquals(false, guiDefaults.keepFurniture)
        assertEquals(PdfiumNative.WRITE_FIELDS_FILLED, guiDefaults.fields)
        assertEquals(false, guiDefaults.heuristicOnly)
    }

    @Test
    fun `every field packs into its own slot in megapdf_write_options order`() {
        val opt = PdfWriteTextOptions(
            keepLines = true,
            pageBreak = PdfiumNative.PAGE_BREAK_MARKER,
            keepFurniture = true,
            fields = PdfiumNative.WRITE_FIELDS_ALL,
            heuristicOnly = true,
        )
        assertArrayEquals(intArrayOf(1, PdfiumNative.PAGE_BREAK_MARKER, 1, PdfiumNative.WRITE_FIELDS_ALL, 1), opt.packed())
    }
}
