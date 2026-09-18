package com.megapdf.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Where a recent document is shown to live (#165). Every case here is one a real
 * provider produces, or one where the rule is "show less rather than leak".
 */
class RecentLocationTest {

    private val external = RecentLocation.EXTERNAL_STORAGE

    // --- external storage: the ids that carry a path ------------------------

    @Test
    fun `a file in a folder shows the root and the folders`() {
        assertEquals(
            listOf("Internal storage", "Documents", "Clients"),
            RecentLocation.segments(external, "primary:Documents/Clients/agreement.pdf", "Internal storage"))
    }

    @Test
    fun `a file at the root of the volume shows only the root`() {
        assertEquals(
            listOf("Internal storage"),
            RecentLocation.segments(external, "primary:agreement.pdf", "Internal storage"))
    }

    @Test
    fun `the SD card is whatever the provider calls it`() {
        assertEquals(
            listOf("SD card", "Scans"),
            RecentLocation.segments(external, "1A2B-3C4D:Scans/receipt.pdf", "SD card"))
    }

    @Test
    fun `empty path segments are not folders`() {
        assertEquals(
            listOf("Internal storage", "Documents"),
            RecentLocation.segments(external, "primary:Documents//agreement.pdf", "Internal storage"))
    }

    // --- every other provider: the root, and nothing invented ---------------

    @Test
    fun `a provider with opaque ids shows only its root`() {
        assertEquals(
            listOf("Drive"),
            RecentLocation.segments("com.google.android.apps.docs.storage", "acc=1;doc=42", "Drive"))
        assertEquals(
            listOf("Downloads"),
            RecentLocation.segments("com.android.providers.downloads.documents", "msf:37", "Downloads"))
    }

    @Test
    fun `the media provider's opaque ids yield no folders`() {
        // The same file reaches us two ways: by folder through external storage, and by
        // kind through the media provider, whose ids carry no path (#165). One of those
        // showed as "Local storage" until DocumentLocations named the volume instead.
        assertEquals(
            listOf("Internal shared storage"),
            RecentLocation.segments("com.android.providers.media.documents",
                "document:38", "Internal shared storage"))
    }

    @Test
    fun `a downloads raw id never becomes a path`() {
        // The Downloads provider hands out `raw:/storage/emulated/0/Download/x.pdf`
        // for some documents. It is not external storage, so it is not read — and
        // #165 says a storage path is never shown anyway.
        val segments = RecentLocation.segments(
            "com.android.providers.downloads.documents",
            "raw:/storage/emulated/0/Download/agreement.pdf", "Downloads")
        assertEquals(listOf("Downloads"), segments)
        assertTrue(segments.none { it.contains("/") })
    }

    @Test
    fun `an absolute path in an external storage id is dropped, not printed`() {
        val segments = RecentLocation.segments(
            external, "primary:/storage/emulated/0/Documents/agreement.pdf", "Internal storage")
        assertEquals(listOf("Internal storage"), segments)
    }

    @Test
    fun `an id with no colon yields no folders`() {
        assertEquals(
            listOf("Internal storage"),
            RecentLocation.segments(external, "37", "Internal storage"))
    }

    // --- the parts that may be missing --------------------------------------

    @Test
    fun `no root title leaves only the folders`() {
        assertEquals(
            listOf("Documents"),
            RecentLocation.segments(external, "primary:Documents/agreement.pdf", null))
    }

    @Test
    fun `nothing known at all is an empty location`() {
        assertEquals(emptyList<String>(), RecentLocation.segments(null, null, null))
        assertEquals(emptyList<String>(), RecentLocation.segments(external, null, "   "))
    }

    // --- shortening ----------------------------------------------------------

    private val deep = listOf("Internal storage", "Documents", "Work", "Clients", "Smith")

    @Test
    fun `a short location is not shortened`() {
        assertEquals("Downloads", RecentLocation.format(listOf("Downloads")))
        assertEquals("Drive › Clients", RecentLocation.format(listOf("Drive", "Clients")))
    }

    @Test
    fun `a deep location keeps the root and the innermost folders`() {
        assertEquals("Internal storage › … › Clients › Smith", RecentLocation.format(deep, maxSegments = 4))
        assertEquals("Internal storage › … › Smith", RecentLocation.format(deep, maxSegments = 3))
    }

    @Test
    fun `two segments keep the ends, one keeps the folder that disambiguates`() {
        assertEquals("Internal storage › Smith", RecentLocation.format(deep, maxSegments = 2))
        assertEquals("Smith", RecentLocation.format(deep, maxSegments = 1))
    }

    @Test
    fun `an empty location formats to nothing`() {
        assertEquals("", RecentLocation.format(emptyList()))
        assertEquals("", RecentLocation.format(deep, maxSegments = 0))
    }

    // --- the roots we name ourselves ----------------------------------------

    @Test
    fun `the downloads root is re-named in the language the app is in now`() {
        assertEquals(
            listOf("Téléchargements"),
            RecentLocation.localised(listOf("Downloads"), RecentLocation.DOWNLOADS, "Téléchargements"))
    }

    @Test
    fun `a folder on disk keeps its own name`() {
        // "Clients" is a folder, not a word of ours: it is the same in every language.
        assertEquals(
            listOf("Internal shared storage", "Documents", "Clients"),
            RecentLocation.localised(
                listOf("Internal shared storage", "Documents", "Clients"),
                RecentLocation.EXTERNAL_STORAGE, "Téléchargements"))
    }

    @Test
    fun `an entry stored before the authority was recorded is left alone`() {
        assertEquals(
            listOf("Downloads"),
            RecentLocation.localised(listOf("Downloads"), null, "Téléchargements"))
        assertEquals(emptyList<String>(),
            RecentLocation.localised(emptyList(), RecentLocation.DOWNLOADS, "Téléchargements"))
    }

    // --- the root re-asked for in the language the app is in now -------------

    @Test
    fun `the root is replaced and the folders are left alone`() {
        assertEquals(
            listOf("Stockage interne", "Documents", "Clients"),
            RecentLocation.withRoot(
                listOf("Internal shared storage", "Documents", "Clients"), "Stockage interne"))
    }

    @Test
    fun `an unresolvable root leaves what was recorded`() {
        // A stale root still says where the file is; a blank one says nothing (#165).
        val recorded = listOf("Local storage", "Documents")
        assertEquals(recorded, RecentLocation.withRoot(recorded, null))
        assertEquals(recorded, RecentLocation.withRoot(recorded, "   "))
        assertEquals(emptyList<String>(), RecentLocation.withRoot(emptyList(), "Downloads"))
    }

    @Test
    fun `the formatter never emits a path separator of its own`() {
        assertTrue(RecentLocation.format(deep, maxSegments = 3).none { it == '/' || it == '\\' })
    }
}
