package com.megapdf.engine

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The form-field flags and /MaxLen across the JNI boundary (#712), on `formflow.pdf`
 * (`tools/gen_test_fixtures.py`, committed copy): page 1 has "first" and "last" single-line text
 * fields, a multi-line "notes" and a checkbox "agree", in that order.
 */
@RunWith(AndroidJUnit4::class)
class FormFieldFlagsTest {

    private val engine = PdfEngine()

    private fun fields(): List<FormField> = runBlocking {
        val bytes = InstrumentationRegistry.getInstrumentation().context.assets
            .open("formflow.pdf").use { it.readBytes() }
        val doc = engine.open(bytes)
        try {
            val page = doc.openPage(0)
            try { page.formFields() } finally { page.close() }
        } finally {
            doc.close()
        }
    }

    @Test
    fun theMultilineFlagComesFromTheCoreAndNotFromTheBoxHeight() {
        val f = fields()
        assertEquals(4, f.size)
        assertFalse(f[0].isMultiline)
        assertFalse(f[1].isMultiline)
        assertTrue("notes carries the multi-line flag", f[2].isMultiline)
        assertTrue(f[2].isText)
    }

    @Test
    fun aCheckboxCarriesNoTextFlagsAndNothingIsReadOnlyOrLimited() {
        val f = fields()
        assertFalse(f[3].isText)
        assertFalse(f[3].isMultiline)
        assertTrue(f.none { it.isReadOnly })
        assertTrue("no /MaxLen anywhere in this fixture", f.all { it.maxLength == 0 })
        assertEquals(0, f[0].flags and (FormField.FLAG_COMB or FormField.FLAG_MASKED))
    }
}
