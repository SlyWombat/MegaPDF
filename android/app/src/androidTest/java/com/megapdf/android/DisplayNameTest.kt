package com.megapdf.android

import android.content.Intent
import android.net.Uri
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.lifecycle.ViewModelProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertFalse
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * A provider that has the `_display_name` column but a null — or blank — value in it (#670).
 * `Cursor.getString` is a platform type, so the old read threw inside `queryDisplayName`
 * instead of reaching its `lastPathSegment` fallback: the open ended in "Couldn't open", and
 * Save a copy in "Save failed" after the copy had been written and verified.
 *
 * Drives the real open and Save a copy paths through a real ContentProvider, not a mock of
 * the lookup: [NamelessProvider] is in this test APK's manifest, exported, serving a real PDF
 * from its own process.
 */
@RunWith(AndroidJUnit4::class)
class DisplayNameTest {

    @get:Rule
    val rule = createEmptyComposeRule()

    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()

    /** The provider's uri for a document named [name], whose display name it answers as [kind] ("null" or "blank"). */
    private fun served(kind: String, name: String): Uri =
        Uri.parse("content://ca.electricrv.megapdf.test.nameless/$kind/$name")

    private fun opensWithTheFallbackName(kind: String, segment: String) {
        val uri = served(kind, segment)
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
    fun aNullDisplayNameFallsBackToTheUrisName() = opensWithTheFallbackName("null", "null-name-670.pdf")

    @Test
    fun aBlankDisplayNameFallsBackToTheUrisName() = opensWithTheFallbackName("blank", "blank-name-670.pdf")

    @Test
    fun saveACopyToANullNamedDocumentStillSucceeds() {
        val source = Fixtures.written("copy-source-670.pdf", TestPdfs.multiPage(1))
        val target = served("null", "copy-target-670.pdf")
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
