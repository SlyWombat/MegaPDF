package com.megapdf.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** The persisted grant a picked document keeps (Save after a restart). */
class UriGrantsTest {

    @Test
    fun `read and write are kept when the provider offers both`() {
        val asked = mutableListOf<Int>()
        assertEquals(UriGrants.READ_WRITE, UriGrants.persist { asked += it })
        assertEquals(listOf(UriGrants.READ_WRITE), asked)
    }

    @Test
    fun `read alone is kept when write is not persistable`() {
        val asked = mutableListOf<Int>()
        val kept = UriGrants.persist {
            asked += it
            if (it == UriGrants.READ_WRITE) throw SecurityException("no persistable write")
        }
        assertEquals(UriGrants.READ, kept)
        assertEquals(listOf(UriGrants.READ_WRITE, UriGrants.READ), asked)
    }

    @Test
    fun `nothing is kept when the provider offers no persistable grant`() {
        assertNull(UriGrants.persist { throw SecurityException("not persistable") })
    }
}
