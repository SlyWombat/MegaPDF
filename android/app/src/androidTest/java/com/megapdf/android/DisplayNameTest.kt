package com.megapdf.android

import android.content.ContentProvider
import android.content.ContentValues
import android.content.Intent
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.lifecycle.ViewModelProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.After
import org.junit.Assert.assertFalse
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/**
 * A provider that has the `_display_name` column but a null — or blank — value in it (#670).
 * `Cursor.getString` is a platform type, so the old read threw inside `queryDisplayName`
 * instead of reaching its `lastPathSegment` fallback: the open ended in "Couldn't open", and
 * Save a copy in "Save failed" after the copy had been written and verified.
 *
 * Drives the real open and Save a copy paths through a real ContentProvider, not a mock of
 * the lookup: the provider is in this test APK's manifest, exported, serving a real PDF.
 */
@RunWith(AndroidJUnit4::class)
class DisplayNameTest {

    @get:Rule
    val rule = createEmptyComposeRule()

    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()

    @After
    fun resetProvider() {
        NamelessProvider.name = null
        NamelessProvider.file = null
    }

    /** A document of the provider's, served under [segment], its display name answered as [name]. */
    private fun served(segment: String, name: String?, bytes: ByteArray): Uri {
        val file = File(instrumentation.context.cacheDir, segment)
        file.writeBytes(bytes)
        NamelessProvider.name = name
        NamelessProvider.file = file
        return Uri.parse("content://${NamelessProvider.AUTHORITY}/$segment")
    }

    private fun opensWithTheFallbackName(name: String?, segment: String) {
        val uri = served(segment, name, TestPdfs.multiPage(1))
        val intent = Intent(Intent.ACTION_VIEW)
            .setDataAndType(uri, "application/pdf")
            .setClass(Fixtures.appContext, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_GRANT_READ_URI_PERMISSION)
        val activity = instrumentation.startActivitySync(intent) as MainActivity
        try {
            // The title is the name the viewer settled on: the uri's last segment.
            rule.waitForText(segment)
            rule.waitForPage()
            assertFalse(rule.nodeExists(hasText(str(R.string.open_failed))))
        } finally {
            instrumentation.runOnMainSync { activity.finish() }
            rule.waitUntil(SETTLE_MS) { activity.isDestroyed }
        }
    }

    @Test
    fun aNullDisplayNameFallsBackToTheUrisName() = opensWithTheFallbackName(null, "null-name-670.pdf")

    @Test
    fun aBlankDisplayNameFallsBackToTheUrisName() = opensWithTheFallbackName("  ", "blank-name-670.pdf")

    @Test
    fun saveACopyToANullNamedDocumentStillSucceeds() {
        val source = Fixtures.written("copy-source-670.pdf", TestPdfs.multiPage(1))
        val target = served("copy-target-670.pdf", null, ByteArray(0))
        val intent = Intent(Intent.ACTION_VIEW)
            .setDataAndType(Fixtures.uri(source), "application/pdf")
            .setClass(Fixtures.appContext, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_GRANT_READ_URI_PERMISSION)
        val activity = instrumentation.startActivitySync(intent) as MainActivity
        try {
            rule.waitForText(source.name)
            rule.waitForPage()
            instrumentation.runOnMainSync {
                ViewModelProvider(activity)[ViewerViewModel::class.java].saveAs(target)
            }
            // The copy becomes the document, titled by the fallback; a failed save keeps the
            // source's title.
            rule.waitForText("copy-target-670.pdf")
            assertFalse(rule.nodeExists(hasText(str(R.string.save_failed))))
        } finally {
            instrumentation.runOnMainSync { activity.finish() }
            rule.waitUntil(SETTLE_MS) { activity.isDestroyed }
        }
    }
}

/** Serves [file] for any path, naming it [name] — which the tests make null or blank. */
class NamelessProvider : ContentProvider() {
    override fun onCreate() = true

    override fun query(
        uri: Uri, projection: Array<out String>?, selection: String?,
        selectionArgs: Array<out String>?, sortOrder: String?,
    ): Cursor =
        MatrixCursor(arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE)).apply {
            addRow(arrayOf(name, file?.length() ?: 0L))
        }

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor =
        ParcelFileDescriptor.open(file, ParcelFileDescriptor.parseMode(mode))

    override fun getType(uri: Uri) = "application/pdf"
    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?) = 0
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?) = 0

    companion object {
        const val AUTHORITY = "ca.electricrv.megapdf.test.nameless"

        @Volatile var name: String? = null
        @Volatile var file: File? = null
    }
}
