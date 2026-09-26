package com.megapdf.android

import android.content.Intent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * ViewIntent's routing decision (#376), kept free of real `Intent`/`Uri` instances since
 * this module's test classpath has no Robolectric to back them.
 */
class ViewIntentTest {
    @Test
    fun `ACTION_VIEW with data yields that data`() {
        assertEquals("content://example/doc.pdf", ViewIntent.uriToOpen(Intent.ACTION_VIEW, "content://example/doc.pdf"))
    }

    @Test
    fun `ACTION_VIEW with no data yields nothing`() {
        assertNull(ViewIntent.uriToOpen<String>(Intent.ACTION_VIEW, null))
    }

    @Test
    fun `a launcher intent yields nothing, even with data set`() {
        assertNull(ViewIntent.uriToOpen(Intent.ACTION_MAIN, "content://example/doc.pdf"))
    }

    @Test
    fun `a null action yields nothing`() {
        assertNull(ViewIntent.uriToOpen(null, "content://example/doc.pdf"))
    }
}
