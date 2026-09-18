package com.megapdf.android

/**
 * Where a recent document lives, in the words the system file picker uses (#165).
 *
 * The Windows half of #165 has `RecentLocation.cs` for the same job; this is the
 * Android one, and it is deliberately the same shape — pure string work with no
 * Android types in it, so the awkward cases are tested on the JVM rather than on
 * an emulator.
 *
 * **What Android will and will not tell us.** `ACTION_OPEN_DOCUMENT` hands back a
 * *document* URI, not a tree, so there is no parent to walk: `DocumentsContract`
 * has no "give me this document's folder" call. What there is:
 *
 * - the **root** the document came from. Not its *title*, which only the picker
 *   may read (see `DocumentLocations`), but a name for it that needs no
 *   permission: the volume's description, or the owning app's label;
 * - the **document id**, whose shape is the provider's business. Exactly one
 *   provider documents it, and it is the common one: external storage uses
 *   `<rootId>:<relative/path>`, so its folders can be read straight off.
 *
 * So a row shows the root always, and the folders underneath it when the provider
 * spells them out. "Downloads", "Drive", "Internal storage › Documents › Clients".
 * That is what the picker itself shows, which is the point.
 *
 * Nothing here ever emits a `content://` URI, a `/storage/emulated/0/…` path or a
 * document id: those are the three things #165 says never to put in front of a
 * person, and [segments] is the only way out of this file.
 */
object RecentLocation {

    /** The separator #165 uses on every platform. */
    const val SEPARATOR = " › "

    /** The one provider whose document ids are documented to carry a path. */
    const val EXTERNAL_STORAGE = "com.android.externalstorage.documents"

    /** The provider whose root MegaPDF names itself — see `DocumentLocations`. */
    const val DOWNLOADS = "com.android.providers.downloads.documents"

    /**
     * The folders to show for a document, outermost first.
     *
     * [rootTitle] is what the provider calls the root — already localised by it,
     * because a provider publishes its title in the device's language. [documentId]
     * is the provider's own id for the document, and is only read when the provider
     * is one whose ids are documented to be paths.
     */
    fun segments(authority: String?, documentId: String?, rootTitle: String?): List<String> {
        val root = rootTitle?.trim().orEmpty()
        val folders = if (authority == EXTERNAL_STORAGE) foldersFromPathId(documentId) else emptyList()
        return (listOf(root) + folders).filter { it.isNotEmpty() }
    }

    /**
     * The folders inside `<rootId>:<relative/path/name.pdf>`, without the file name.
     *
     * A document at the root of the volume has no folders, and an id in any other
     * shape gets none — better a row that says only "Internal storage" than one
     * that leaks half a path.
     */
    private fun foldersFromPathId(documentId: String?): List<String> {
        val id = documentId ?: return emptyList()
        val colon = id.indexOf(':')
        if (colon < 0) return emptyList()
        val relative = id.substring(colon + 1)
        // An absolute path here would mean the provider changed its id format under
        // us; drop it rather than print it.
        if (relative.startsWith("/")) return emptyList()
        return relative.split('/')
            .dropLast(1)          // the file itself
            .filter { it.isNotBlank() }
    }

    /**
     * [segments] with any root *we* named swapped for its name in the language the
     * app is in now (#165).
     *
     * A location is recorded once, when the document is opened, and folders on
     * disk keep whatever they are called — "Clients" is "Clients" in French. But
     * "Downloads" is a word of ours, and a stored one goes stale the moment
     * someone changes the app's language, so it is re-read rather than replayed.
     */
    fun localised(segments: List<String>, authority: String?, downloadsName: String): List<String> =
        if (authority == DOWNLOADS && segments.isNotEmpty())
            listOf(downloadsName) + segments.drop(1)
        else segments

    /**
     * [segments] with its root replaced by [root], which is the same name asked for
     * again in the language the app is in now (#165).
     *
     * Nothing changes when the root cannot be resolved: what was recorded at open
     * time is still the truth about where the file is, just possibly in the wrong
     * language, and that beats a row with no location at all.
     */
    fun withRoot(segments: List<String>, root: String?): List<String> {
        val name = root?.trim().orEmpty()
        if (name.isEmpty() || segments.isEmpty()) return segments
        return listOf(name) + segments.drop(1)
    }

    /**
     * [segments] as one line, shortened to at most [maxSegments] by dropping from
     * the middle.
     *
     * The root and the innermost folders survive, because those are the two ends
     * that tell two same-named files apart: the root says which provider, and the
     * last folder is usually the answer to "which of these two?". #165 says the
     * same thing for Windows — "never away from the folder that tells two entries
     * apart".
     */
    fun format(segments: List<String>, maxSegments: Int = 3, ellipsis: String = "…"): String {
        if (segments.isEmpty()) return ""
        if (maxSegments < 1) return ""
        if (segments.size <= maxSegments) return segments.joinToString(SEPARATOR)
        if (maxSegments == 1) return segments.last()
        if (maxSegments == 2) return segments.first() + SEPARATOR + segments.last()
        val tail = segments.takeLast(maxSegments - 2)
        return (listOf(segments.first(), ellipsis) + tail).joinToString(SEPARATOR)
    }
}
