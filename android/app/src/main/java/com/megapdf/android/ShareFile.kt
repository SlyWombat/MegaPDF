package com.megapdf.android

import java.io.File

/**
 * Where a share copy of the open document goes before it is handed to another app (#378):
 * `cacheDir/share/`, the one subtree `@xml/file_paths` grants through the FileProvider —
 * never the whole cache dir, which also holds the copies `openFromUri`/`writeVerified` make of
 * whatever is currently open or mid-save.
 */
private const val SHARE_SUBDIR = "share"

fun shareDirectory(cacheDir: File): File = File(cacheDir, SHARE_SUBDIR)

/**
 * The file a share copy is written to, inside [shareDirectory] regardless of what
 * [displayName] contains — a provider-supplied display name is untrusted input, not a
 * path, so any directory component in it is stripped rather than followed.
 */
fun shareFileFor(cacheDir: File, displayName: String): File =
    File(shareDirectory(cacheDir), sanitizeShareFileName(displayName))

internal fun sanitizeShareFileName(displayName: String): String {
    val base = displayName.substringAfterLast('/').substringAfterLast('\\').ifBlank { "document" }
    return if (base.endsWith(".pdf", ignoreCase = true)) base else "$base.pdf"
}

/**
 * The name of a shrunk copy (#593): [displayName] without its `.pdf`, put through [pattern] (the
 * catalogue's `shrink_file_name`, "%1$s - smaller" in English), then made safe like any share
 * copy's name, which gives it back its `.pdf`.
 */
fun shrinkFileName(displayName: String, pattern: String): String {
    val base = displayName.substringAfterLast('/').substringAfterLast('\\')
        .let { if (it.endsWith(".pdf", ignoreCase = true)) it.dropLast(4) else it }
        .ifBlank { "document" }
    return sanitizeShareFileName(String.format(pattern, base))
}
