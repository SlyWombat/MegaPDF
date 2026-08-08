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
}
