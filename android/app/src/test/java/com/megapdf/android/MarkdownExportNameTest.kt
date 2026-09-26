package com.megapdf.android

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The name Export as Markdown (#409) suggests to the `text/markdown` picker. There is no
 * test of a name deciding a format any more — nothing does: `SaveFormatTest` went with
 * `saveFormatFor`, the #386 rule that let a provider-renamed `lease.md.pdf` become a PDF
 * copy and the current document when Markdown had been asked for.
 */
class MarkdownExportNameTest {

    @Test
    fun `the document's name with md in place of pdf`() {
        assertEquals("lease.md", markdownExportName("lease.pdf"))
        assertEquals("Rental Agreement.md", markdownExportName("Rental Agreement.pdf"))
    }

    @Test
    fun `the pdf extension is dropped whatever its case`() {
        assertEquals("LEASE.md", markdownExportName("LEASE.PDF"))
        assertEquals("lease.md", markdownExportName("lease.Pdf"))
    }

    @Test
    fun `a name with no extension gets md`() {
        assertEquals("lease.md", markdownExportName("lease"))
    }

    @Test
    fun `an extension that is not pdf is kept, never mistaken for the format`() {
        assertEquals("scan.txt.md", markdownExportName("scan.txt"))
    }

    @Test
    fun `the file the #386 bug left behind exports without a doubled extension`() {
        assertEquals("demo-export.md", markdownExportName("demo-export.md.pdf"))
        assertEquals("notes.md", markdownExportName("notes.md"))
    }

    @Test
    fun `only the last pdf is stripped`() {
        assertEquals("lease.pdf.md", markdownExportName("lease.pdf.pdf"))
    }

    @Test
    fun `a blank name falls back the way Share does`() {
        assertEquals("document.md", markdownExportName(""))
        assertEquals("document.md", markdownExportName("   "))
        assertEquals("document.md", markdownExportName(".pdf"))
    }
}
