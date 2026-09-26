package com.megapdf.android

import org.junit.Assert.assertEquals
import org.junit.Test

/** Which binding "Save a copy" (#386) should write to, from the SAF-picked destination's name. */
class SaveFormatTest {

    @Test
    fun `a dot-md name is Markdown`() {
        assertEquals(SaveFormat.MARKDOWN, saveFormatFor("lease.md"))
    }

    @Test
    fun `the extension is not case-sensitive`() {
        assertEquals(SaveFormat.MARKDOWN, saveFormatFor("LEASE.MD"))
    }

    @Test
    fun `a dot-pdf name keeps the existing PDF path`() {
        assertEquals(SaveFormat.PDF, saveFormatFor("lease.pdf"))
    }

    @Test
    fun `no recognised extension defaults to PDF, never Markdown`() {
        assertEquals(SaveFormat.PDF, saveFormatFor("lease"))
        assertEquals(SaveFormat.PDF, saveFormatFor(""))
    }

    @Test
    fun `a name that merely contains md is not Markdown`() {
        assertEquals(SaveFormat.PDF, saveFormatFor("amended.pdf"))
    }
}
