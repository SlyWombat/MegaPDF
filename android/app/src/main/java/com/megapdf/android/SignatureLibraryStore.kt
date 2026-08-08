package com.megapdf.android

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import kotlinx.serialization.Serializable
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.File
import java.io.FileOutputStream
import java.util.UUID

/**
 * App-private signature library, mirroring the desktop `SignatureLibrary.cs`:
 * transparent PNGs plus an `index.json` under filesDir/signatures. Atomic
 * temp+rename writes (real filesystem, unlike SAF destinations).
 */
@Serializable
data class SignatureEntry(
    val id: String,
    val displayName: String,
    val fileName: String,
    val pixelWidth: Int,
    val pixelHeight: Int,
    val createdEpochMs: Long,
)

class SignatureLibraryStore(private val dir: File) {

    private val json = Json { ignoreUnknownKeys = true; prettyPrint = true }
    private val indexFile = File(dir, "index.json")

    fun load(): List<SignatureEntry> {
        if (!indexFile.exists()) return emptyList()
        return try {
            json.decodeFromString<List<SignatureEntry>>(indexFile.readText())
        } catch (_: Exception) {
            emptyList()
        }
    }

    fun add(displayName: String, bitmap: Bitmap): SignatureEntry {
        dir.mkdirs()
        val id = UUID.randomUUID().toString()
        val fileName = "$id.png"
        val temp = File(dir, "$fileName.tmp")
        FileOutputStream(temp).use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
        temp.renameTo(File(dir, fileName))
        val entry = SignatureEntry(
            id, displayName, fileName, bitmap.width, bitmap.height,
            System.currentTimeMillis(),
        )
        writeIndex(load() + entry)
        return entry
    }

    fun delete(id: String) {
        val entries = load()
        entries.firstOrNull { it.id == id }?.let { File(dir, it.fileName).delete() }
        writeIndex(entries.filterNot { it.id == id })
    }

    fun loadBitmap(entry: SignatureEntry): Bitmap? =
        BitmapFactory.decodeFile(File(dir, entry.fileName).absolutePath)

    private fun writeIndex(entries: List<SignatureEntry>) {
        dir.mkdirs()
        val temp = File(dir, "index.json.tmp")
        temp.writeText(json.encodeToString(entries))
        if (!temp.renameTo(indexFile)) {
            indexFile.delete()
            temp.renameTo(indexFile)
        }
    }
}
