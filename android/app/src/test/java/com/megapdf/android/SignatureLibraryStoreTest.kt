package com.megapdf.android

import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.util.UUID

/**
 * The signature library on disk, against the desktop store it mirrors (#333): the
 * soft limit, the index dropping entries whose image has gone, and the order a
 * delete happens in.
 *
 * Written against the bytes entry point, which needs no `Bitmap`: there is no
 * Robolectric on this module's test classpath, so anything reaching into
 * `android.graphics` cannot be unit tested here.
 */
class SignatureLibraryStoreTest {

    private val dir = File(
        System.getProperty("java.io.tmpdir"),
        "megapdf-signatures-${UUID.randomUUID()}",
    ).apply { mkdirs() }

    private val store = SignatureLibraryStore(dir)

    @After
    fun cleanUp() {
        dir.setWritable(true)
        dir.deleteRecursively()
    }

    private fun add(name: String, seed: Int, width: Int = 8, height: Int = 4) =
        store.add(name, ByteArray(8) { seed.toByte() }, width, height)

    @Test
    fun `add stores the image and its index entry`() {
        val entry = add("Signature 1", 7, width = 8, height = 4)

        assertEquals(8, entry.pixelWidth)
        assertEquals(4, entry.pixelHeight)
        assertEquals(listOf(entry), store.load())
        assertArrayEquals(ByteArray(8) { 7.toByte() }, File(dir, entry.fileName).readBytes())
    }

    @Test
    fun `load drops an entry whose image has gone`() {
        val kept = add("Signature 1", 1)
        val gone = add("Signature 2", 2)

        File(dir, gone.fileName).delete()

        // The broken row is dropped, not returned with a missing image behind it.
        assertEquals(listOf(kept), store.load())
        // And the next write drops it from the index itself, so it is not listed again.
        val third = add("Signature 3", 3)
        assertEquals(listOf(kept, third), store.load())
    }

    @Test
    fun `add never indexes an image that is not there`() {
        repeat(3) { add("Signature $it", it) }

        assertTrue(store.load().all { File(dir, it.fileName).exists() })
    }

    @Test
    fun `the twenty-first signature is refused`() {
        repeat(SignatureLibraryStore.SOFT_LIMIT) { add("Signature $it", it) }

        val error = assertThrows(IllegalStateException::class.java) { add("One too many", 99) }

        assertTrue(error.message!!.contains("${SignatureLibraryStore.SOFT_LIMIT}"))
        // Refused before anything was written: no image, and the index still holds 20.
        assertEquals(SignatureLibraryStore.SOFT_LIMIT, store.load().size)
        assertEquals(
            SignatureLibraryStore.SOFT_LIMIT,
            dir.listFiles()!!.count { it.name.endsWith(".png") },
        )
    }

    @Test
    fun `delete removes the entry and its image`() {
        val entry = add("Signature 1", 1)

        store.delete(entry.id)

        assertTrue(store.load().isEmpty())
        assertFalse(File(dir, entry.fileName).exists())
    }

    /**
     * The order the three platforms standardised on: the index entry goes first, so a
     * failure between the two leaves an image nothing refers to rather than a row
     * naming an image that is not there (#333). An index that cannot be written is how
     * that becomes visible — the image must still be on disk afterwards.
     *
     * A directory where the index's temp file goes is what makes the write fail: no
     * permission bits are involved, so this behaves the same on every file system and
     * the same for a user the modes would not deny.
     */
    @Test
    fun `delete takes the index entry before the image`() {
        val entry = add("Signature 1", 1)
        assertTrue("the index is where it is expected", File(dir, "index.json.tmp").mkdirs())

        runCatching { store.delete(entry.id) }

        assertTrue("the image outlives a failed index write", File(dir, entry.fileName).exists())
    }
}
