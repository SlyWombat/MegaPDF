package com.megapdf.android

/**
 * The name Export as Markdown suggests to the system's create-document picker (#409): the
 * document's own name with `.md` in place of `.pdf` — "lease.pdf" becomes "lease.md".
 *
 * The picker is typed `text/markdown`, and the name it opens with is the only thing that
 * decides what the provider calls the file: DocumentsUI keeps a name whose extension matches
 * the intent's type, and appends the type's extension to one that does not — which is how a
 * PDF-typed picker turned a typed `lease.md` into `lease.md.pdf` (#409). Suggesting `.md`
 * to a `text/markdown` picker means the file comes out `.md` whatever the provider's rule.
 *
 * The name never decides the format the other way round: whatever the provider hands back
 * is written as Markdown, because the row was the choice. The `.pdf` is stripped case-
 * insensitively and once; any other extension is kept and `.md` added after it, so the
 * suggested name never claims a file is something it is not. A name that already ends in
 * `.md` (a `lease.md.pdf` left behind by the #386 bug, exported again) is not doubled. The
 * fallback for a blank name matches the Share flow's "document.pdf".
 */
fun markdownExportName(displayName: String): String {
    val name = displayName.trim()
    val stem = if (name.endsWith(".pdf", ignoreCase = true)) name.dropLast(".pdf".length) else name
    return when {
        stem.isBlank() -> "document.md"
        stem.endsWith(".md", ignoreCase = true) -> stem
        else -> "$stem.md"
    }
}
