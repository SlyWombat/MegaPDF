package com.megapdf.engine

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Page colours (#513) as the engine sees them: one enum value, one contract-7 flag bit.
 *
 * The values are the core's own (`core/megapdf_core.h`, `MEGAPDF_RENDER_SEPIA` = 2,
 * `MEGAPDF_RENDER_NIGHT` = 4, #509), and the core refuses both bits set at once. An enum
 * that can only ever produce one of them is what makes that refusal unreachable from here;
 * these assertions are what keeps the numbers from drifting away from the header.
 */
class PageTintTest {

    @Test
    fun eachTintCarriesExactlyItsContractSevenBit() {
        assertEquals(0, PageTint.NORMAL.renderFlag)
        assertEquals(2, PageTint.SEPIA.renderFlag)
        assertEquals(4, PageTint.NIGHT.renderFlag)
    }

    @Test
    fun theStoredNamesAreTheOnesTheDesktopsWrite() {
        // settings.json on the desktops stores "", "Sepia" and "Night"; DataStore on Android
        // stores the same three words, so the two are readable side by side.
        assertEquals("", PageTint.NORMAL.storedName)
        assertEquals("Sepia", PageTint.SEPIA.storedName)
        assertEquals("Night", PageTint.NIGHT.storedName)
    }

    @Test
    fun anUnknownStoredValueReadsBackAsNormal() {
        // A preferences store written by a later version must show the page, not nothing.
        assertEquals(PageTint.NORMAL, PageTint.of("Twilight"))
        assertEquals(PageTint.NORMAL, PageTint.of(null))
        for (tint in PageTint.entries) assertEquals(tint, PageTint.of(tint.storedName))
    }
}
