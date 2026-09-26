package com.megapdf.android

/**
 * Which format a "Save a copy" destination was picked as (#386). [PDF] is the existing,
 * lossless copy of the document; [MARKDOWN] is the new one-way text export
 * ([ViewerViewModel.exportMarkdown]) the SAF picker's mime-type list now also offers
 * (`text/markdown`, alongside `application/pdf` — `MainActivity`'s `CreateDocumentOrMarkdown`).
 *
 * A `.md` is not an alternate save of the document: it cannot be reopened as one (no form
 * fields, no signatures, no layout — contract 9's blocks are text only). [MARKDOWN] is handled
 * by a separate write path precisely so it never inherits [PDF]'s "this is now the current
 * document" bookkeeping (`currentUri`, Recents, the persisted grant) — see
 * [ViewerViewModel.exportMarkdown]'s own note.
 */
enum class SaveFormat { PDF, MARKDOWN }

/**
 * Decides [SaveFormat] for a destination the "Save a copy" SAF picker returned (#386), from the
 * name the provider gave the new document. DocumentsUI (and providers that follow its
 * convention) appends the extension matching whichever type the person picked from the
 * intent's `EXTRA_MIME_TYPES` list, so a `.md` name means Markdown was picked; anything else —
 * `.pdf` included, and a provider that reports no name at all — keeps the existing PDF path.
 */
fun saveFormatFor(displayName: String): SaveFormat =
    if (displayName.endsWith(".md", ignoreCase = true)) SaveFormat.MARKDOWN else SaveFormat.PDF
