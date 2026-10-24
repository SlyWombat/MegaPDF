package com.megapdf.android

import kotlinx.serialization.Serializable
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.File

/**
 * Recent documents, the mobile analog of the desktop `RecentFiles.cs`.
 * Document identity is the SAF persisted-permission URI string — never a file path.
 * Stored as JSON in app-private storage with an atomic temp+rename write
 * (a real filesystem here, unlike SAF destinations).
 */
@Serializable
data class RecentEntry(
    val uri: String,
    val displayName: String,
    val lastOpenedEpochMs: Long,
    /**
     * Where the file lives, outermost folder first (#165) — recorded when the
     * document is opened, because asking a provider is a query and the recents
     * list must not make one per row while it draws.
     *
     * Empty for an entry stored before 2.0, and for a provider that will not say.
     * The default is what migrates the old store: `ignoreUnknownKeys` already
     * tolerated extra fields, and a missing one takes this.
     */
    val location: List<String> = emptyList(),
    /**
     * The provider the document came from, so a root *we* name can be re-named
     * when the app's language changes (#165). [location] is otherwise frozen in
     * whatever language it was captured in, which is right for a folder on disk
     * and wrong for a word of ours.
     */
    val authority: String? = null,
    /**
     * What the document is, apart from the URI it arrived under (#803): its size and last-modified
     * time as the provider reported them, "size:modified", or null when the provider would not say
     * both. The picker reaches one file through several providers (by folder, by kind, by
     * Downloads) and each hands back a different URI, so the URI alone lists one file twice.
     * Null for an entry stored before this existed, which the default migrates.
     */
    val identity: String? = null,
)

/** A recent entry and whether it can still be opened (#165). */
data class RecentRow(val entry: RecentEntry, val available: Boolean = true)

class RecentFilesStore(private val file: File, private val maxEntries: Int = 10) {

    private val json = Json { ignoreUnknownKeys = true; prettyPrint = true }

    fun load(): List<RecentEntry> {
        if (!file.exists()) return emptyList()
        return try {
            json.decodeFromString<List<RecentEntry>>(file.readText())
        } catch (_: Exception) {
            emptyList()  // corrupt store is not worth crashing over; start fresh
        }
    }

    /**
     * Adds or refreshes [entry]; most recent first, one row per document, capped.
     *
     * A document is the same document when its URI is the same, or when its name and
     * [RecentEntry.identity] are (#803): the picker reaches one file through several providers
     * and each hands back its own URI. An entry stored before identities existed has none, so
     * where the name matches it is asked once through [identityOf] (a provider query, the caller's
     * to make); an answer that is null or different keeps it.
     */
    fun add(entry: RecentEntry, identityOf: (RecentEntry) -> String? = { null }): List<RecentEntry> {
        val identity = entry.identity
        fun sameDocument(other: RecentEntry): Boolean = when {
            other.uri == entry.uri -> true
            identity == null || other.displayName != entry.displayName -> false
            else -> (other.identity ?: identityOf(other)) == identity
        }
        val updated = (listOf(entry) + load().filterNot(::sameDocument)).take(maxEntries)
        write(updated)
        return updated
    }

    /** Drops [uri] (e.g. after the provider revoked our persisted permission). */
    fun remove(uri: String): List<RecentEntry> {
        val updated = load().filterNot { it.uri == uri }
        write(updated)
        return updated
    }

    private fun write(entries: List<RecentEntry>) {
        file.parentFile?.mkdirs()
        val temp = File(file.parentFile, file.name + ".tmp")
        temp.writeText(json.encodeToString(entries))
        if (!temp.renameTo(file)) {
            // renameTo can't replace on some filesystems; fall back to delete-then-rename.
            file.delete()
            temp.renameTo(file)
        }
    }
}
