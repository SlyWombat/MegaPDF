package com.megapdf.android

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** The FileProvider path-scoping (#378): every share copy must land under cacheDir/share/. */
class ShareFileTest {
    private val cacheDir = File("/data/app-cache")

    @Test
    fun `a normal display name keeps its own name under the share directory`() {
        val file = shareFileFor(cacheDir, "lease.pdf")
        assertEquals(File(cacheDir, "share/lease.pdf"), file)
    }

    @Test
    fun `a name without the pdf extension gets one appended`() {
        val file = shareFileFor(cacheDir, "lease")
        assertEquals(File(cacheDir, "share/lease.pdf"), file)
    }

    @Test
    fun `a provider name with path separators is not followed as a path`() {
        val file = shareFileFor(cacheDir, "../../etc/passwd")
        assertEquals(shareDirectory(cacheDir), file.parentFile)
        assertTrue(file.name.none { it == '/' || it == '\\' })
    }

    @Test
    fun `a blank display name still produces a file under the share directory`() {
        val file = shareFileFor(cacheDir, "")
        assertEquals(shareDirectory(cacheDir), file.parentFile)
    }

    @Test
    fun `the share directory is a single fixed subtree of the cache dir`() {
        assertEquals(File(cacheDir, "share"), shareDirectory(cacheDir))
    }
}
