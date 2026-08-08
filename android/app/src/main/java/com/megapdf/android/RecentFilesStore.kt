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
)

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

    /** Adds or refreshes [entry]; most recent first, deduped by URI, capped. */
    fun add(entry: RecentEntry): List<RecentEntry> {
        val updated = (listOf(entry) + load().filterNot { it.uri == entry.uri })
            .take(maxEntries)
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
