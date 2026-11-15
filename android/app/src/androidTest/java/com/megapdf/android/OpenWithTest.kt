package com.megapdf.android

import android.content.Intent
import android.net.Uri
import android.os.StrictMode
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.lifecycle.ViewModelProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.runner.lifecycle.ActivityLifecycleMonitorRegistry
import androidx.test.runner.lifecycle.Stage
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/**
 * Open with (#376): a PDF handed over by another app as an `ACTION_VIEW` — a content:// uri
 * from a FileProvider, the way a mail client sends an attachment, both cold and again while
 * a document with unsaved changes is on screen; and a file:// uri, the way a Files app or a
 * download manager may.
 *
 * The activity is launched and finished by hand rather than through an ActivityScenario:
 * the scenario follows its activity by the activity's *current* intent, and
 * [MainActivity.onNewIntent] replaces that intent with the second document's, after which
 * the scenario loses sight of it and its close waits forever.
 */
@RunWith(AndroidJUnit4::class)
class OpenWithTest {

    @get:Rule
    val rule = createEmptyComposeRule()

    private val instrumentation get() = InstrumentationRegistry.getInstrumentation()

    private fun viewIntent(uri: Uri): Intent =
        Intent(Intent.ACTION_VIEW)
            .setDataAndType(uri, "application/pdf")
            .setClass(Fixtures.appContext, MainActivity::class.java)
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)

    private fun viewIntent(file: File): Intent = viewIntent(Fixtures.uri(file))

    /** Launches [MainActivity] on [intent] as the system would, and runs [body] before finishing it. */
    private fun launched(intent: Intent, body: (MainActivity) -> Unit) {
        // A launch from outside an activity, as a system hand-over is: it needs its own task.
        val activity = instrumentation.startActivitySync(intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) as MainActivity
        try {
            body(activity)
        } finally {
            instrumentation.runOnMainSync { activity.finish() }
            rule.waitUntil(SETTLE_MS) { activity.isDestroyed }
        }
    }

    private val MainActivity.viewModel: ViewerViewModel
        get() = ViewModelProvider(this)[ViewerViewModel::class.java]

    @Test
    fun fileUriOpensTheDocumentToo() {
        val file = Fixtures.demo("downloaded-346.pdf")
        // A file:// intent leaving a process trips StrictMode's exposure check, which is the
        // sender's concern and not this app's: relaxed for the launch alone.
        val policy = StrictMode.getVmPolicy()
        StrictMode.setVmPolicy(StrictMode.VmPolicy.LAX)
        try {
            launched(viewIntent(Fixtures.fileUri(file))) {
                rule.waitForText(file.name)
                rule.waitForPage()
            }
        } finally {
            StrictMode.setVmPolicy(policy)
        }
    }

    @Test
    fun viewIntentOpensTheDocumentColdAndAsksBeforeReplacingADirtyOne() {
        val first = Fixtures.demo("from-mail-346.pdf")
        val second = Fixtures.demo("from-files-346.pdf")
        val facts = PageFacts.of(first)

        launched(viewIntent(first)) { activity ->
            // Cold: straight into the viewer on the handed-over document, not Home.
            rule.waitForText(first.name)
            rule.waitForPage()
            assertFalse(activity.viewModel.isDirty)

            // Dirty it, then hand over a second document to the running activity — the
            // onNewIntent path (#376), delivered the way the system delivers it.
            rule.page().tapCentreOf(facts, facts.unmarkedSquare())
            rule.waitForText("• " + first.name)
            instrumentation.runOnMainSync { instrumentation.callActivityOnNewIntent(activity, viewIntent(second)) }

            // The same Save / Discard / Cancel question closing by hand asks.
            rule.waitForText(str(R.string.unsaved_changes))
            rule.waitForText(str(R.string.save))
            rule.waitForText(str(R.string.discard))
            rule.clickText(str(R.string.cancel))
            rule.waitForGone(hasText(str(R.string.unsaved_changes)))
            rule.waitForText("• " + first.name)
            assertTrue("Cancel keeps the first document and its edit", activity.viewModel.isDirty)

            instrumentation.runOnMainSync { instrumentation.callActivityOnNewIntent(activity, viewIntent(second)) }
            rule.clickText(str(R.string.discard))
            // Discard: the second document replaces the first, edit and history gone.
            rule.waitForText(second.name)
            rule.waitForPage()
            rule.waitUntil(SETTLE_MS) { !rule.undoButton().isEnabled() }
            assertFalse(activity.viewModel.isDirty)
            assertEquals(facts.checkMarks.size, PageFacts.of(first).checkMarks.size)
        }
    }

    /**
     * #826: a configuration change recreates the activity and hands it the intent it was
     * launched with. Launch on A, switch to B through onNewIntent, make an edit, recreate:
     * the replay would put A in front of the edited B and raise the unsaved-changes prompt.
     */
    @Test
    fun recreatingTheActivityDoesNotReplayTheLaunchingIntent() {
        val first = Fixtures.demo("launched-826.pdf")
        val second = Fixtures.demo("opened-later-826.pdf")
        val facts = PageFacts.of(second)

        launched(viewIntent(first)) { activity ->
            rule.waitForText(first.name)
            rule.waitForPage()
            instrumentation.runOnMainSync { instrumentation.callActivityOnNewIntent(activity, viewIntent(second)) }
            rule.waitForText(second.name)
            rule.waitForPage()
            rule.page().tapCentreOf(facts, facts.unmarkedSquare())
            rule.waitForText("• " + second.name)

            instrumentation.runOnMainSync { activity.recreate() }
            rule.waitUntil(SETTLE_MS) { activity.isDestroyed }
            rule.waitForText("• " + second.name)
            rule.waitForIdle()

            rule.onAllNodes(hasText(str(R.string.unsaved_changes))).assertCountEquals(0)
            assertTrue("the edited second document is still the one on screen", activity.viewModel.isDirty)

            // `launched` finishes the instance it started, which recreation has replaced.
            instrumentation.runOnMainSync {
                ActivityLifecycleMonitorRegistry.getInstance().getActivitiesInStage(Stage.RESUMED)
                    .forEach { it.finish() }
            }
        }
    }
}
