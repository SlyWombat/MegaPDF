package com.megapdf.android

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import kotlinx.serialization.Serializable
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.util.UUID

/**
 * App-private signature library, mirroring the desktop `SignatureLibrary.cs`:
 * transparent PNGs plus an `index.json` under filesDir/signatures. Atomic
 * temp+rename writes (real filesystem, unlike SAF destinations).
 *
 * The desktop store is the reference for all three platforms (#333), and this
 * one follows it on the three points where it did not: the same soft limit, an
 * index whose entries are dropped when their image has gone, and a delete that
 * takes the index entry first.
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
        val entries = try {
            json.decodeFromString<List<SignatureEntry>>(indexFile.readText())
        } catch (_: Exception) {
            return emptyList()
        }
        // An index entry whose image has gone is a broken thumbnail; the desktop
        // store drops it on load and so does this one (#333).
        return entries.filter { File(dir, it.fileName).exists() }
    }

    /** Whether the library is full: [add] will refuse another one (#333). */
    fun isFull(): Boolean = load().size >= SOFT_LIMIT

    /** Encodes [bitmap] and stores it. */
    fun add(displayName: String, bitmap: Bitmap): SignatureEntry =
        add(displayName, bitmap.toPngBytes(), bitmap.width, bitmap.height)

    /**
     * Stores a PNG that is already encoded, with the size to place it by.
     *
     * The real entry point: the image is bytes plus its dimensions, and nothing
     * here needs a `Bitmap`, so this is what the unit tests drive (#333).
     *
     * @throws IllegalStateException when the library is full — the desktop store
     *   refuses the 21st signature the same way.
     */
    fun add(displayName: String, pngBytes: ByteArray, pixelWidth: Int, pixelHeight: Int): SignatureEntry {
        val entries = load()
        if (entries.size >= SOFT_LIMIT) {
            throw IllegalStateException("The signature library is limited to $SOFT_LIMIT signatures.")
        }

        dir.mkdirs()
        val id = UUID.randomUUID().toString()
        val fileName = "$id.png"
        val temp = File(dir, "$fileName.tmp")
        FileOutputStream(temp).use { it.write(pngBytes) }
        if (!temp.renameTo(File(dir, fileName))) {
            // The index must not name an image that is not there: nothing is added,
            // and the stale temp file goes (#333).
            temp.delete()
            throw IOException("Couldn't write $fileName")
        }

        val entry = SignatureEntry(
            id, displayName, fileName, pixelWidth, pixelHeight,
            System.currentTimeMillis(),
        )
        writeIndex(entries + entry)
        return entry
    }

    /**
     * Takes the entry out of the index and then deletes the image. The desktop
     * store's order, and the one all three platforms use (#333): a crash between
     * the two leaves an orphan PNG, which nothing shows, where the other order
     * leaves an index entry naming a file that is gone.
     */
    fun delete(id: String) {
        val entries = load()
        writeIndex(entries.filterNot { it.id == id })
        entries.firstOrNull { it.id == id }?.let { File(dir, it.fileName).delete() }
    }

    /** Changes the display name only; the file and id stay put (#99). */
    fun rename(id: String, displayName: String) {
        writeIndex(load().map { if (it.id == id) it.copy(displayName = displayName) else it })
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

    companion object {
        /** The desktop store's `SoftLimit`: the 21st signature is refused. */
        const val SOFT_LIMIT = 20
    }
}

private fun Bitmap.toPngBytes(): ByteArray {
    val out = ByteArrayOutputStream()
    compress(Bitmap.CompressFormat.PNG, 100, out)
    return out.toByteArray()
}
