package com.megapdf.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class NoticeParagraphsTest {
    @Test
    fun `splits on blank lines`() {
        val text = "First block\nline two\n\nSecond block\n\n\nThird block"
        assertEquals(
            listOf("First block\nline two", "Second block", "Third block"),
            splitNoticeParagraphs(text),
        )
    }

    @Test
    fun `handles crlf line endings`() {
        val text = "First\r\n\r\nSecond\r\nstill second"
        assertEquals(
            listOf("First", "Second\r\nstill second"),
            splitNoticeParagraphs(text),
        )
    }

    @Test
    fun `blank lines with stray spaces still separate blocks`() {
        val text = "First\n   \nSecond"
        assertEquals(listOf("First", "Second"), splitNoticeParagraphs(text))
    }

    @Test
    fun `preserves leading indentation and drops trailing whitespace`() {
        val text = "   indented license line   \n\nnext"
        assertEquals(listOf("   indented license line", "next"), splitNoticeParagraphs(text))
    }

    @Test
    fun `no empty blocks from leading trailing or repeated separators`() {
        val text = "\n\nA\n\n\n\nB\n\n"
        val blocks = splitNoticeParagraphs(text)
        assertEquals(listOf("A", "B"), blocks)
        assertTrue(blocks.none { it.isBlank() })
    }
}
