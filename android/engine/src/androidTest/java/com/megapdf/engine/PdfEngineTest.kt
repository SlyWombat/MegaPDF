package com.megapdf.engine

import android.graphics.Bitmap
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import org.junit.runner.RunWith
import android.os.ParcelFileDescriptor
import java.io.ByteArrayOutputStream
import java.io.File

@RunWith(AndroidJUnit4::class)
class PdfEngineTest {

    private val engine = PdfEngine()

    private fun fixtureBytes(): ByteArray =
        InstrumentationRegistry.getInstrumentation().context.assets
            .open("fixture.pdf").use { it.readBytes() }

    // JUnit requires void test methods, so each test wraps its body in a
    // statement-position runBlocking rather than an expression body.

    @Test
    fun opensAndReportsGeometry() {
        runBlocking {
            val doc = engine.open(fixtureBytes())
            try {
                assertEquals(2, doc.pageCount())
                val page = doc.openPage(0)
                try {
                    assertEquals(612.0, page.widthPoints, 0.01)  // US Letter
                    assertEquals(792.0, page.heightPoints, 0.01)
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun renderProducesInk() {
        runBlocking {
            val doc = engine.open(fixtureBytes())
            try {
                val page = doc.openPage(0)
                try {
                    val bitmap = Bitmap.createBitmap(612, 792, Bitmap.Config.ARGB_8888)
                    page.render(bitmap)
                    val pixels = IntArray(bitmap.width * bitmap.height)
                    bitmap.getPixels(pixels, 0, bitmap.width, 0, 0, bitmap.width, bitmap.height)
                    val inked = pixels.count { it != -1 }  // -1 == opaque white ARGB
                    assertTrue("expected ink on the page, found $inked non-white pixels", inked > 100)
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun saveRoundTrips() {
        runBlocking {
            val doc = engine.open(fixtureBytes())
            val saved = ByteArrayOutputStream()
            try {
                doc.save(saved)
            } finally {
                doc.close()
            }
            assertTrue("saved document is empty", saved.size() > 0)

            val reopened = engine.open(saved.toByteArray())
            try {
                assertEquals(2, reopened.pageCount())
            } finally {
                reopened.close()
            }
        }
    }

    @Test
    fun protectedDocumentSavesStillProtectedAndReadsBack() {
        // #132: the save check reopened the copy without the password, and the copy is
        // still protected, so every protected save failed.
        runBlocking {
            val bytes = InstrumentationRegistry.getInstrumentation().context.assets
                .open("encrypted.pdf").use { it.readBytes() }
            val unlock = "u123"   // tools/gen_test_fixtures.py
            val doc = engine.open(bytes, unlock)
            try {
                val saved = ByteArrayOutputStream()
                doc.save(saved)
                try {
                    engine.open(saved.toByteArray()).close()
                    fail("the saved copy should still be protected")
                } catch (expected: PdfPasswordException) {
                    // expected
                }
                val reopened = engine.openLike(doc, saved.toByteArray())
                try {
                    assertEquals(1, reopened.pageCount())
                } finally {
                    reopened.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun newSecurityNeedsItsPasswordAndGrantsOnlyWhatWasAllowed() {
        // #131: a copy saved with new AES-256 security, then one with none.
        runBlocking {
            val doc = engine.open(fixtureBytes())
            val locked = ByteArrayOutputStream()
            try {
                assertEquals(PdfSecurity.UNPROTECTED, doc.security())
                doc.saveWithSecurity(locked, "new-user", "new-owner", PdfPermissions.PRINT)
            } finally {
                doc.close()
            }

            try {
                engine.open(locked.toByteArray()).close()
                fail("the copy should need a password")
            } catch (expected: PdfPasswordException) {
                // expected
            }

            val asUser = engine.open(locked.toByteArray(), "new-user")
            try {
                assertEquals(
                    PdfSecurity(isEncrypted = true, revision = 6, permissions = PdfPermissions.PRINT, hasFullAccess = false),
                    asUser.security(),
                )
            } finally {
                asUser.close()
            }

            val asOwner = engine.open(locked.toByteArray(), "new-owner")
            val unprotected = ByteArrayOutputStream()
            try {
                assertTrue(asOwner.security().hasFullAccess)
                asOwner.saveWithoutSecurity(unprotected)
            } finally {
                asOwner.close()
            }

            val reopened = engine.open(unprotected.toByteArray())
            try {
                assertEquals(PdfSecurity.UNPROTECTED, reopened.security())
            } finally {
                reopened.close()
            }
        }
    }

    @Test
    fun opensWithAPasswordOutsideTheBasicMultilingualPlane() {
        // #131, ADR-004 decision 9: open passes UTF-8 bytes like the saves do. As a
        // jstring, modified UTF-8 would encode the emoji differently and the copy saved
        // under it would not open.
        runBlocking {
            val secret = "clé-🔒"
            val locked = ByteArrayOutputStream()
            val doc = engine.open(fixtureBytes())
            try {
                doc.saveWithSecurity(locked, secret, null, PdfPermissions.ALL)
            } finally {
                doc.close()
            }
            val reopened = engine.open(locked.toByteArray(), secret)
            try {
                assertTrue(reopened.security().hasFullAccess)
            } finally {
                reopened.close()
            }
        }
    }

    @Test
    fun restrictedDocumentRefusesToChangeItsSecurity() {
        // #131: owner-only.pdf opens without a password but allows nothing.
        runBlocking {
            val bytes = InstrumentationRegistry.getInstrumentation().context.assets
                .open("owner-only.pdf").use { it.readBytes() }
            val doc = engine.open(bytes)
            try {
                val security = doc.security()
                assertTrue(security.isEncrypted)
                assertEquals(false, security.hasFullAccess)
                assertEquals(false, security.allows(PdfPermissions.PRINT))
                try {
                    doc.saveWithoutSecurity(ByteArrayOutputStream())
                    fail("only the owner may remove the security")
                } catch (expected: PdfRestrictedException) {
                    // expected
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun invalidBytesThrowLoadException() {
        runBlocking {
            try {
                engine.open("not a pdf".toByteArray())
                fail("expected PdfLoadException")
            } catch (expected: PdfLoadException) {
                // expected
            }
        }
    }

    private fun scratchFile(name: String, bytes: ByteArray): File {
        val dir = File(InstrumentationRegistry.getInstrumentation().targetContext.cacheDir, "engine-test-files")
        dir.mkdirs()
        return File(dir, name).apply { writeBytes(bytes) }
    }

    // #147/#148: a document opened from its descriptor is read on demand; the descriptor is
    // the engine's from the call on, and a failed open closes it too.
    @Test
    fun opensFromADescriptorAndAFile() {
        runBlocking {
            val file = scratchFile("fd.pdf", fixtureBytes())
            val fd = ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).detachFd()
            val doc = engine.openFd(fd)
            try {
                assertEquals(2, doc.pageCount())
                val page = doc.openPage(1)
                page.close()
                ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use {
                    assertTrue("the document reads the file its descriptor is on", doc.readsFd(it.fd))
                }
            } finally {
                doc.close()
            }
            val byPath = engine.openFile(file.path)
            try {
                assertEquals(2, byPath.pageCount())
            } finally {
                byPath.close()
            }

            val junk = scratchFile("junk.pdf", "not a pdf".toByteArray())
            try {
                engine.openFd(ParcelFileDescriptor.open(junk, ParcelFileDescriptor.MODE_READ_ONLY).detachFd())
                fail("junk opened")
            } catch (_: PdfLoadException) {
            }

            // A pipe is not a file: refused as a file error, which the app answers by copying.
            val pipe = ParcelFileDescriptor.createPipe()
            pipe[1].close()
            try {
                engine.openFd(pipe[0].detachFd())
                fail("a pipe opened")
            } catch (e: PdfLoadException) {
                assertTrue("a pipe is a file error, got ${e.errorCode}", e.isFileError)
            }
        }
    }

    // #147: before the app writes the document's own file in place, the document moves onto a
    // private copy, and then keeps reading what it was opened on.
    @Test
    fun readFromCopySurvivesTheFileBeingWrittenInPlace() {
        runBlocking {
            val file = scratchFile("in-place.pdf", fixtureBytes())
            val doc = engine.openFd(ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).detachFd())
            try {
                val copy = File(file.parentFile, "copy-of-in-place.pdf")
                doc.readFromCopy(copy.path)
                assertTrue("the copy's name is gone", !copy.exists())
                ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).use {
                    assertTrue("the document no longer reads the file", !doc.readsFd(it.fd))
                }
                file.writeBytes("overwritten".toByteArray())

                val page = doc.openPage(1)
                page.close()
                val saved = ByteArrayOutputStream()
                doc.save(saved)
                val reopened = engine.open(saved.toByteArray())
                try {
                    assertEquals(2, reopened.pageCount())
                } finally {
                    reopened.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    @Test
    fun concurrentRendersAreSerialized() {
        runBlocking {
            val doc = engine.open(fixtureBytes())
            try {
                val page = doc.openPage(0)
                try {
                    // Two coroutines hammering render; the single-threaded dispatcher
                    // must serialize the native calls (PDFium is not thread-safe).
                    List(2) {
                        async {
                            repeat(10) {
                                val bitmap = Bitmap.createBitmap(306, 396, Bitmap.Config.ARGB_8888)
                                page.render(bitmap)
                            }
                        }
                    }.awaitAll()
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }
}
