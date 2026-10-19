package com.megapdf.android

import com.megapdf.engine.PdfDocument
import com.megapdf.engine.PdfEngine
import com.megapdf.engine.extractPages
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.withContext
import java.io.File
import java.io.OutputStream

/**
 * Writes the print service's copy of [doc] to [out] (#682), with every annotation that would
 * print baked into the page content.
 *
 * The system print preview draws pages with `PdfRenderer`, which draws no annotations, so a
 * stamp (a signature, a tick) that is still an annotation in the copy is missing from the preview
 * and may be from the paper. Baking it in means whatever renders the job draws it as page
 * content. The flatten follows each annotation's Print flag (`flattenForPrint`): a hidden or
 * no-print annotation is not drawn into the copy.
 *
 * **The open document is never flattened.** The document is serialised to a scratch file, that
 * file is opened as a separate document and flattened, and that is what is written. [pages] null
 * is the whole document; otherwise the pages are extracted from [doc] itself, so the copy
 * permission check, and the person's choice to continue past an advisory restriction, apply
 * exactly as they did before, and only then is the extract flattened.
 *
 * [stripSecurity] writes an unprotected copy, which only full access can do; without it a
 * protected document's copy keeps its protection and is reopened with the same password.
 *
 * Both scratch files are deleted whatever happens, including cancellation by the person.
 */
internal suspend fun writePrintCopy(
    engine: PdfEngine,
    doc: PdfDocument,
    stripSecurity: Boolean,
    pages: List<Int>?,
    cacheDir: File,
    out: OutputStream,
) {
    val stamp = System.nanoTime()
    val source = File(cacheDir, "print-src-$stamp.pdf")
    val extract = File(cacheDir, "print-pages-$stamp.pdf")
    var flat: PdfDocument? = null
    try {
        if (pages == null) {
            withContext(Dispatchers.IO) {
                source.outputStream().buffered().use { sink ->
                    if (stripSecurity) doc.saveWithoutSecurity(sink) else doc.save(sink)
                }
            }
            val copy = if (stripSecurity) engine.openFile(source.path) else engine.openFileLike(doc, source.path)
            flat = copy
            copy.flattenForPrint()
            copy.save(out)
        } else {
            // The extract is a new, unprotected document whatever [doc] was.
            doc.extractPages(pages, extract.path)
            val copy = engine.openFile(extract.path)
            flat = copy
            copy.flattenForPrint()
            copy.save(out)
        }
    } finally {
        withContext(NonCancellable + Dispatchers.IO) {
            flat?.close()
            source.delete()
            extract.delete()
        }
    }
}
