package com.megapdf.android

import android.content.Intent

/**
 * Which persisted grant to keep for a document the user picked.
 *
 * `ACTION_OPEN_DOCUMENT` and `ACTION_CREATE_DOCUMENT` hand back read *and* write,
 * but a grant the app does not persist lasts only until the device restarts. Keeping
 * read alone meant a document reopened from Recents after a restart opened fine and
 * then refused Save with "No permission to write here anymore" — the listing's
 * "Save writes back to the original file" held only until the next reboot.
 *
 * So ask for both, and fall back to read when the provider did not offer write (a
 * read-only provider, or one whose grant is not persistable for writing): the
 * document still reopens, and Save still says plainly that it can only save a copy.
 */
object UriGrants {
    const val READ_WRITE = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
    const val READ = Intent.FLAG_GRANT_READ_URI_PERMISSION

    /**
     * Tries [take] with read and write, then with read alone. Returns the flags that
     * were kept, or null when the provider offers nothing persistable.
     */
    fun persist(take: (Int) -> Unit): Int? {
        for (flags in intArrayOf(READ_WRITE, READ)) {
            try {
                take(flags)
                return flags
            } catch (_: SecurityException) {
                // Not offered with these flags; try fewer.
            }
        }
        return null
    }
}
