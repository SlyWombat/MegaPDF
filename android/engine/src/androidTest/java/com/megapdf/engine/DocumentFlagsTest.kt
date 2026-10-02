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

    // --- Digital signature (#476/#481): the core's own detection is covered by
    // core_tests.cpp's test_signature_detection against the same two fixtures
    // (tools/gen_signature_fixtures.py); this just proves the bits cross the JNI boundary.

    @Test
    fun signedApprovalFixtureSetsSignedButNotCertification() {
        runBlocking {
            val doc = engine.open(assetBytes("signed-approval.pdf"))
            try {
                val flags = doc.documentFlags()
                assertTrue(flags.isSigned)
                assertFalse(flags.isCertificationSigned)
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun signedCertifiedFixtureSetsBothBits() {
        runBlocking {
            val doc = engine.open(assetBytes("signed-certified.pdf"))
            try {
                val flags = doc.documentFlags()
                assertTrue(flags.isSigned)
                assertTrue(flags.isCertificationSigned)
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun anUnsignedDocumentSetsNeitherSignatureBit() {
        runBlocking {
            val doc = engine.open(assetBytes("forms.pdf"))
            try {
                val flags = doc.documentFlags()
                assertFalse(flags.isSigned)
                assertFalse(flags.isCertificationSigned)
            } finally {
                doc.close()
            }
        }
    }

    // --- Removing a dead signature (#576): the JNI binding for megapdf_signatures_remove(),
    // the same core call Windows and the Avalonia desktops already make. The core's own
    // behaviour (field tree + value + widget all removed, FPDFDoc_RemoveFormField) is covered
    // by core_tests.cpp against the real GPO corpus; this proves it crosses the JNI boundary
    // and that the saved file genuinely stops reporting as signed when reopened.

    @Test
    fun removingSignaturesOnASignedDocumentReportsSuccessAndClearsTheFlag() {
        runBlocking {
            val doc = engine.open(assetBytes("signed-approval.pdf"))
            try {
                assertTrue(doc.documentFlags().isSigned)
                assertTrue(doc.removeDigitalSignatures())
                assertFalse(doc.documentFlags().isSigned)
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun theSavedFileGenuinelyCarriesNoSignatureOnceRemoved() {
        runBlocking {
            val doc = engine.open(assetBytes("signed-approval.pdf"))
            val bytes = try {
                doc.removeDigitalSignatures()
                val out = java.io.ByteArrayOutputStream()
                doc.save(out)
                out.toByteArray()
            } finally {
                doc.close()
            }

            val reopened = engine.open(bytes)
            try {
                assertFalse("a save nobody asked to keep the signature must not still report one",
                    reopened.documentFlags().isSigned)
            } finally {
                reopened.close()
            }
        }
    }

    @Test
    fun aSaveThatDoesNotAskToRemoveTheSignatureKeepsIt() {
        // #576's own worry, held here the same way CheckSignedSaveRemovalAsync holds it on
        // Windows: a removal creeping into the ordinary save path is the one thing this
        // feature decided against.
        runBlocking {
            val doc = engine.open(assetBytes("signed-approval.pdf"))
            val bytes = try {
                val out = java.io.ByteArrayOutputStream()
                doc.save(out)
                out.toByteArray()
            } finally {
                doc.close()
            }

            val reopened = engine.open(bytes)
            try {
                assertTrue("a save nobody asked to remove the signature from must still report one (now invalid)",
                    reopened.documentFlags().isSigned)
            } finally {
                reopened.close()
            }
        }
    }

    @Test
    fun removingSignaturesOnAnUnsignedDocumentReportsNothingRemoved() {
        runBlocking {
            val doc = engine.open(assetBytes("forms.pdf"))
            try {
                assertFalse(doc.removeDigitalSignatures())
            } finally {
                doc.close()
            }
        }
    }
}
