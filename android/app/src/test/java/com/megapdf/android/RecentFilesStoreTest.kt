package com.megapdf.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.rules.TemporaryFolder
import java.io.File

class RecentFilesStoreTest {

    @get:Rule
    val temp = TemporaryFolder()

    private fun store(max: Int = 10) =
        RecentFilesStore(File(temp.root, "recent.json"), maxEntries = max)

    private fun entry(uri: String, at: Long = 0L) =
        RecentEntry(uri, displayName = uri.substringAfterLast('/'), lastOpenedEpochMs = at)

    @Test
    fun `empty store loads empty list`() {
        assertEquals(emptyList<RecentEntry>(), store().load())
    }

    @Test
    fun `add persists and survives reload`() {
        val s = store()
        s.add(entry("content://docs/a.pdf", at = 1))
        assertEquals(listOf("content://docs/a.pdf"), store().load().map { it.uri })
    }

    @Test
    fun `most recent first and deduped by uri`() {
        val s = store()
        s.add(entry("content://docs/a.pdf", at = 1))
        s.add(entry("content://docs/b.pdf", at = 2))
        s.add(entry("content://docs/a.pdf", at = 3))
        val uris = s.load().map { it.uri }
        assertEquals(listOf("content://docs/a.pdf", "content://docs/b.pdf"), uris)
        assertEquals(3, s.load().first().lastOpenedEpochMs)
    }

    @Test
    fun `capped at maxEntries dropping oldest`() {
        val s = store(max = 3)
        for (i in 1..5) s.add(entry("content://docs/$i.pdf", at = i.toLong()))
        assertEquals(
            listOf("content://docs/5.pdf", "content://docs/4.pdf", "content://docs/3.pdf"),
            s.load().map { it.uri },
        )
    }

    @Test
    fun `remove drops the entry`() {
        val s = store()
        s.add(entry("content://docs/a.pdf"))
        s.add(entry("content://docs/b.pdf"))
        s.remove("content://docs/a.pdf")
        assertEquals(listOf("content://docs/b.pdf"), s.load().map { it.uri })
    }

    @Test
    fun `corrupt file loads as empty rather than crashing`() {
        val file = File(temp.root, "recent.json")
        file.writeText("{ not json ]")
        assertTrue(RecentFilesStore(file).load().isEmpty())
    }

    // #803: the picker reaches one file through several providers, each with its own URI.

    private fun picked(uri: String, name: String = "selection.pdf", identity: String? = "5123:1700000000000", at: Long = 0L) =
        RecentEntry(uri, displayName = name, lastOpenedEpochMs = at, identity = identity)

    @Test
    fun `the same document opened under two providers is listed once`() {
        val s = store()
        s.add(picked("content://com.android.providers.downloads.documents/document/msf%3A42", at = 1))
        s.add(picked("content://com.android.providers.media.documents/document/document%3A1234", at = 2))
        val rows = s.load()
        assertEquals(1, rows.size)
        assertEquals("content://com.android.providers.media.documents/document/document%3A1234", rows[0].uri)
    }

    @Test
    fun `the same name with a different size is a different document`() {
        val s = store()
        s.add(picked("content://a/document/1", identity = "5123:1700000000000"))
        s.add(picked("content://b/document/2", identity = "9999:1700000000000"))
        assertEquals(2, s.load().size)
    }

    @Test
    fun `a different name with the same size and time is a different document`() {
        val s = store()
        s.add(picked("content://a/document/1", name = "a.pdf"))
        s.add(picked("content://b/document/2", name = "b.pdf"))
        assertEquals(2, s.load().size)
    }

    @Test
    fun `a provider that gave no identity is never merged by name alone`() {
        val s = store()
        s.add(picked("content://a/document/1", identity = null))
        s.add(picked("content://b/document/2", identity = null))
        assertEquals(2, s.load().size)
    }

    @Test
    fun `an entry stored before identities existed is merged once the provider vouches for it`() {
        val s = store()
        s.add(picked("content://old/document/1", identity = null, at = 1))
        s.add(picked("content://new/document/2", at = 2), identityOf = { "5123:1700000000000" })
        assertEquals(listOf("content://new/document/2"), s.load().map { it.uri })
    }

    @Test
    fun `an old entry whose identity differs or cannot be read is kept`() {
        val s = store()
        s.add(picked("content://old/document/1", identity = null, at = 1))
        s.add(picked("content://new/document/2", at = 2), identityOf = { "1:1" })
        s.add(picked("content://new/document/3", identity = "7:7", at = 3), identityOf = { null })
        assertEquals(3, s.load().size)
    }

    @Test
    fun `an old entry is only asked about when its name matches`() {
        val s = store()
        s.add(picked("content://old/document/1", name = "other.pdf", identity = null, at = 1))
        s.add(picked("content://new/document/2", at = 2), identityOf = { error("asked about an unrelated name") })
        assertEquals(2, s.load().size)
    }
}
