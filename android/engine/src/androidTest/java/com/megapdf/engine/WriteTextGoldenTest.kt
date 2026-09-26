package com.megapdf.engine

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.ByteArrayOutputStream

/**
 * #386's binding of `megapdf_write_text()` (#142, #355, #357), checked against the same golden
 * fixtures `core/tests/core_tests.cpp`'s `test_write_text_goldens()`/`test_write_markdown_goldens()`
 * compare byte-for-byte: `tests/MegaPDF.Core.Tests/Fixtures/structure/{name}.pdf` and
 * `core/tests/expected/structure/{name}.{txt,md}`, copied verbatim into this module's assets
 * under `structure/` (re-copy them here if the core ever regenerates those goldens).
 *
 * Single-page fixtures only (headings, lists, columns): `megapdf_write_options{}` — the exact
 * options the core's own goldens were written with — and [PdfWriteTextOptions.saveAsDefaults]
 * (this binding's own GUI default) differ only in [PdfWriteTextOptions.pageBreak], which no
 * single-page document's output can show (a page-break separator is only ever emitted between
 * two pages), so both are asserted against the one golden.
 */
@RunWith(AndroidJUnit4::class)
class WriteTextGoldenTest {

    private val engine = PdfEngine()

    private fun asset(name: String): ByteArray =
        InstrumentationRegistry.getInstrumentation().context.assets
            .open("structure/$name").use { it.readBytes() }

    private fun goldenText(name: String): String = asset(name).toString(Charsets.UTF_8)

    private fun writtenText(bytes: ByteArray, format: Int, options: PdfWriteTextOptions): String = runBlocking {
        val doc = engine.open(bytes)
        try {
            val out = ByteArrayOutputStream()
            val pagesWithText = doc.writeText(out, format, options)
            assertTrue("every page here has a text layer", pagesWithText >= 1)
            out.toByteArray().toString(Charsets.UTF_8)
        } finally {
            doc.close()
        }
    }

    private fun assertGolden(name: String) {
        val pdf = asset("$name.pdf")

        assertEquals(
            "plain text matches its golden exactly (default options, as core_tests.cpp writes it)",
            goldenText("$name.txt"),
            writtenText(pdf, PdfiumNative.WRITE_FORMAT_TEXT, PdfWriteTextOptions()),
        )
        assertEquals(
            "Markdown matches its golden exactly (default options)",
            goldenText("$name.md"),
            writtenText(pdf, PdfiumNative.WRITE_FORMAT_MARKDOWN, PdfWriteTextOptions()),
        )
        assertEquals(
            "the GUI Save-As default (blank-line page breaks) writes the same Markdown for a" +
                " single-page document, where the two options cannot differ",
            goldenText("$name.md"),
            writtenText(pdf, PdfiumNative.WRITE_FORMAT_MARKDOWN, PdfWriteTextOptions.saveAsDefaults()),
        )
    }

    @Test
    fun headingsMatchesTheCoreGolden() = assertGolden("headings")

    @Test
    fun listsMatchesTheCoreGolden() = assertGolden("lists")

    @Test
    fun columnsMatchesTheCoreGolden() = assertGolden("columns")

    @Test
    fun writeMarkdownConvenienceMatchesTheGolden() = runBlocking {
        val doc = engine.open(asset("headings.pdf"))
        try {
            val out = ByteArrayOutputStream()
            doc.writeMarkdown(out)
            assertEquals(goldenText("headings.md"), out.toByteArray().toString(Charsets.UTF_8))
        } finally {
            doc.close()
        }
    }

    @Test
    fun structureLoadAndFreeRoundTripWithoutCrashing() = runBlocking {
        // #386's other half of the binding: not used by writeText/writeMarkdown (which load and
        // free their own structure internally), but exercised here so the pair is known to work.
        val doc = engine.open(asset("headings.pdf"))
        try {
            val structure = doc.loadStructure(0, 1)
            assertTrue("a real handle came back", structure != 0L)
            doc.freeStructure(structure)
        } finally {
            doc.close()
        }
    }
}
