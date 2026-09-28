package com.megapdf.engine

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The JNI binding for `megapdf_document_flags()` (#456/#457): the core's own detection is
 * covered by `core_tests.cpp`'s `test_dynamic_xfa` (PR #459) against the same two fixtures
 * (`tools/gen_xfa_fixtures.py`); this just proves the bits cross the JNI boundary intact.
 */
@RunWith(AndroidJUnit4::class)
class DocumentFlagsTest {

    private val engine = PdfEngine()

    private fun assetBytes(name: String): ByteArray =
        InstrumentationRegistry.getInstrumentation().context.assets.open(name).use { it.readBytes() }

    @Test
    fun dynamicXfaFixtureSetsTheFlag() {
        runBlocking {
            val doc = engine.open(assetBytes("dynamic-xfa.pdf"))
            try {
                assertTrue(doc.documentFlags().isDynamicXfa)
                // #457: still opens, still reports a plausible page count, still renders —
                // only filling is unavailable, which is the app's job, not the engine's.
                assertTrue(doc.pageCount() > 0)
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun hybridXfaFixtureDoesNotSetTheFlag() {
        runBlocking {
            val doc = engine.open(assetBytes("hybrid-xfa.pdf"))
            try {
                assertFalse(doc.documentFlags().isDynamicXfa)
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun anOrdinaryDocumentDoesNotSetTheFlag() {
        runBlocking {
            val doc = engine.open(assetBytes("forms.pdf"))
            try {
                assertFalse(doc.documentFlags().isDynamicXfa)
            } finally {
                doc.close()
            }
        }
    }
}
