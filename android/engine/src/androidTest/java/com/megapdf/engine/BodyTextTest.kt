package com.megapdf.engine

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Editing text that is already in the document (#114), through the shared core:
 * visual lines, the two tiers (#116) and the byte-identical undo (#117). The
 * Android half of what iOS's BodyTextTests and the core tests assert.
 */
@RunWith(AndroidJUnit4::class)
class BodyTextTest {

    private val engine = PdfEngine()

    private companion object {
        const val CLIPPED_BY_TEXT =
            "BT /F1 24 Tf 72 700 Td (Spaced report) Tj ET q BT 7 Tr /F1 72 Tf 72 480 Td (CLIP) Tj ET 0 0 1 rg 60 460 400 100 re f Q"
    }

    private fun asset(name: String): ByteArray =
        InstrumentationRegistry.getInstrumentation().context.assets
            .open(name).use { it.readBytes() }

    /** One page, one run in the non-embedded Symbol font, whose encoding has no Latin letters. */
    private fun symbolFontPdf(): ByteArray {
        val pdf = StringBuilder("%PDF-1.4\n")
        val offsets = ArrayList<Int>()
        fun add(body: String) {
            offsets += pdf.length
            pdf.append("${offsets.size} 0 obj\n$body\nendobj\n")
        }
        val content = "BT /F1 24 Tf 72 700 Td (abgd) Tj ET"
        add("<< /Type /Catalog /Pages 2 0 R >>")
        add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>")
        add("<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>")
        add("<< /Length ${content.length} >>\nstream\n$content\nendstream")
        val xref = pdf.length
        pdf.append("xref\n0 ${offsets.size + 1}\n0000000000 65535 f \n")
        for (offset in offsets) pdf.append(String.format("%010d 00000 n \n", offset))
        pdf.append("trailer\n<< /Size ${offsets.size + 1} /Root 1 0 R >>\nstartxref\n$xref\n%%EOF\n")
        return pdf.toString().toByteArray(Charsets.US_ASCII)
    }

    /**
     * One Helvetica page. [CLIPPED_BY_TEXT] puts a box clipped by invisible text (7 Tr) under
     * the heading, which PDFium's writer cannot write back even patched (#118, #119).
     * Character spacing alone was that example until patch 0001; it is now editable.
     */
    private fun helveticaPdf(content: String): ByteArray {
        val pdf = StringBuilder("%PDF-1.4\n")
        val offsets = ArrayList<Int>()
        fun add(body: String) {
            offsets += pdf.length
            pdf.append("${offsets.size} 0 obj\n$body\nendobj\n")
        }
        add("<< /Type /Catalog /Pages 2 0 R >>")
        add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>")
        add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
        add("<< /Length ${content.length} >>\nstream\n$content\nendstream")
        val xref = pdf.length
        pdf.append("xref\n0 ${offsets.size + 1}\n0000000000 65535 f \n")
        for (offset in offsets) pdf.append(String.format("%010d 00000 n \n", offset))
        pdf.append("trailer\n<< /Size ${offsets.size + 1} /Root 1 0 R >>\nstartxref\n$xref\n%%EOF\n")
        return pdf.toString().toByteArray(Charsets.US_ASCII)
    }

    @Test
    fun textPdfiumCannotRewriteFaithfullyIsRefusedAndLeftAlone() = onFirstPage(helveticaPdf(CLIPPED_BY_TEXT)) { page ->
        val before = page.textLines()
        val run = before.first().runs.first()
        assertEquals("the tap-time check says no", false, page.textEditable(run.objectIndex))
        assertEquals("and says why: the render (#128)", LayoutCause.RENDER, page.layoutVerdict(run.objectIndex)?.cause)
        try {
            page.setText(run.objectIndex, "Annual report")
            throw AssertionError("an edit that would disturb the page must be refused")
        } catch (expected: TextLayoutException) {
            assertEquals("the refusal carries the cause", LayoutCause.RENDER, expected.verdict?.cause)
        }
        assertEquals("the page is exactly as it was", before, page.textLines())
    }

    @Test
    fun textUnderCharacterSpacingIsEditableNowThatTheWriterKeepsIt() =
        onFirstPage(helveticaPdf("BT /F1 24 Tf 4 Tc 72 700 Td (Spaced report) Tj ET")) { page ->
            val run = page.textLines().single().runs.single()
            assertEquals("the tap-time check says yes", true, page.textEditable(run.objectIndex))
            page.setText(run.objectIndex, "Annual report")
            assertEquals("Annual report", page.textLines().single().text.trimEnd())
        }

    private fun <T> onFirstPage(bytes: ByteArray, body: suspend (PdfPage) -> T): T = runBlocking {
        val doc = engine.open(bytes)
        try {
            val page = doc.openPage(0)
            try {
                body(page)
            } finally {
                page.close()
            }
        } finally {
            doc.close()
        }
    }

    @Test
    fun linesComeBackTopToBottom() = onFirstPage(asset("fixture.pdf")) { page ->
        val lines = page.textLines()
        assertEquals(2, lines.size)
        assertEquals("MegaPDF engine fixture - page 1", lines[0].text)
        assertTrue("lines are ordered top to bottom", lines[0].rect.top > lines[1].rect.top)
    }

    @Test
    fun anEditTheRunsFontCanCarryStaysInThatFontAndUndoesExactly() = onFirstPage(asset("fixture.pdf")) { page ->
        val run = page.textLines().first().runs.first()
        val edit = page.setText(run.objectIndex, "Symbols beyond original: XYZQ!?")
        assertEquals(TextEditOutcome.IN_PLACE, edit.outcome)
        val edited = page.textLines().first().runs.first()
        assertEquals("Symbols beyond original: XYZQ!?", edited.text)
        assertEquals("tier 1 keeps the run's own font", run.fontName, edited.fontName)

        page.restoreOriginal(edit.original, run.objectIndex)
        assertEquals("undo puts the original run back exactly", run, page.textLines().first().runs.first())
    }

    @Test
    fun aFontThatCannotCarryTheTextIsSubstitutedAndUndoBringsTheOriginalBack() = onFirstPage(symbolFontPdf()) { page ->
        val run = page.textLines().first().runs.first()
        val edit = page.setText(run.objectIndex, "Hello")
        assertEquals("Symbol cannot draw Latin text, so a standard face stands in",
            TextEditOutcome.SUBSTITUTED, edit.outcome)
        assertEquals("Hello", page.textLines().first().runs.first().text)

        page.restoreOriginal(edit.original, run.objectIndex)
        assertEquals("undo must bring back the Symbol run itself", run, page.textLines().first().runs.first())
    }

    @Test
    fun anEditSurvivesSaveAndReopen() {
        val saved = runBlocking {
            val doc = engine.open(asset("fixture.pdf"))
            try {
                val page = doc.openPage(0)
                try {
                    val run = page.textLines().first().runs.first()
                    page.setText(run.objectIndex, "Retyped heading")
                } finally {
                    page.close()
                }
                java.io.ByteArrayOutputStream().also { doc.save(it) }.toByteArray()
            } finally {
                doc.close()
            }
        }
        onFirstPage(saved) { page ->
            assertEquals("Retyped heading", page.textLines().first().text)
        }
    }
}
