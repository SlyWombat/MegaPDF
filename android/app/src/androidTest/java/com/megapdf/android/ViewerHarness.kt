package com.megapdf.android

import android.content.Context
import android.net.Uri
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.SemanticsMatcher
import androidx.compose.ui.test.SemanticsNodeInteraction
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.click
import androidx.compose.ui.test.hasContentDescription
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.AndroidComposeTestRule
import androidx.compose.ui.test.junit4.ComposeTestRule
import androidx.compose.ui.test.onNodeWithContentDescription
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTouchInput
import androidx.core.content.FileProvider
import androidx.lifecycle.ViewModelProvider
import androidx.test.ext.junit.rules.ActivityScenarioRule
import androidx.test.platform.app.InstrumentationRegistry
import com.megapdf.engine.PdfEngine
import com.megapdf.engine.PdfRect
import com.megapdf.engine.Stamp
import com.megapdf.engine.TextLine
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertTrue
import java.io.File

/**
 * What the app module's instrumented tests share (#346): the fixture the viewer opens, how
 * it is opened, and how a point on the PDF page is found on the screen.
 *
 * The tests drive the real [MainActivity] with its real [ViewerViewModel] and engine — the
 * wiring #346 says nothing else exercises: the menu row that arms a tool, the gesture that
 * places a mark, the toolbar while something is selected, the hand-back to the undo history.
 * They find things the way a screen reader does, by the labels the accessibility work gave
 * them (#328, #347), so a label that goes missing fails a test rather than a TalkBack pass.
 */
object Fixtures {
    /** The app's demo agreement: one page, three drawn checkboxes, a paragraph of body text. */
    const val DEMO_ASSET = "demo.pdf"

    /**
     * #456/#457's synthetic fixtures (`tools/gen_xfa_fixtures.py`), test-only: this test APK's
     * own assets, not the app's — a real IRCC form is Crown copyright and stays local-only
     * (Dave, 2026-09-27), so nothing here ships in the app.
     */
    const val DYNAMIC_XFA_ASSET = "dynamic-xfa.pdf"
    const val HYBRID_XFA_ASSET = "hybrid-xfa.pdf"

    /**
     * #476/#481's synthetic signed fixtures (`tools/gen_signature_fixtures.py`), test-only —
     * never one of the 33 real GPO documents the finding was measured against, which stay off
     * this repo (staged read-only at `~/pdf-public` on kdocker3).
     */
    const val SIGNED_APPROVAL_ASSET = "signed-approval.pdf"
    const val SIGNED_CERTIFIED_ASSET = "signed-certified.pdf"

    val appContext: Context
        get() = InstrumentationRegistry.getInstrumentation().targetContext

    /** This test APK's own context, for assets that are not part of the app under test. */
    private val testContext: Context
        get() = InstrumentationRegistry.getInstrumentation().context

    /**
     * Where the fixtures live: `cacheDir/share/fixtures-346/`, inside the one subtree the
     * app's own FileProvider grants (`@xml/file_paths`), so [uri] can hand them over as
     * `content://` the way another app's provider would (#376). The Share copy (#378) is
     * written to `share/<name>` and never into this folder, so the two cannot meet.
     *
     * Not a provider of the test package's: the test APK is installed under its own uid
     * (10131 beside the app's 10130 on the API 30 emulator), so a non-exported provider
     * declared in its manifest is unreachable from the app's process, and its data
     * directory is not writable from there either.
     */
    private fun dir(): File = File(shareDirectory(appContext.cacheDir), "fixtures-346").apply { mkdirs() }

    /**
     * A fresh copy of the demo agreement under [name], so a test that saves into it
     * cannot change what the next one opens. The name is what the viewer's title shows.
     */
    fun demo(name: String): File {
        val file = File(dir(), name)
        appContext.assets.open(DEMO_ASSET).use { input ->
            file.outputStream().use { input.copyTo(it) }
        }
        return file
    }

    /**
     * A document the test built itself, under [name] (#174): the page tools need documents of
     * several pages, of known page sizes, and with form fields in a /Parent hierarchy, none of
     * which the demo agreement is — see [TestPdfs] for why each is built rather than shipped.
     */
    fun written(name: String, bytes: ByteArray): File =
        File(dir(), name).apply { writeBytes(bytes) }

    /** A fresh copy of [asset] from this test APK's own assets — see [DYNAMIC_XFA_ASSET]. */
    fun testAsset(asset: String, name: String = asset): File {
        val file = File(dir(), name)
        testContext.assets.open(asset).use { input ->
            file.outputStream().use { input.copyTo(it) }
        }
        return file
    }

    /** An empty slot under [name] for a picker to "create" (Save a copy, Export as Markdown). */
    fun empty(name: String): File = File(dir(), name).apply { delete() }

    /** The content:// uri another app would hand over for [file] (#376). */
    fun uri(file: File): Uri =
        FileProvider.getUriForFile(appContext, "${appContext.packageName}.fileprovider", file)

    /** The file:// uri a Files app or a download manager may hand over instead (#376). */
    fun fileUri(file: File): Uri = Uri.fromFile(file)
}

/** The app's strings, so a test matches what the screen says rather than a copy of it. */
fun str(id: Int, vararg args: Any): String = Fixtures.appContext.getString(id, *args)

/** The page's geometry, read by a second engine instance so the test never reaches into the view model's. */
class PageFacts(val widthPoints: Double, val heightPoints: Double, val lines: List<TextLine>,
                val checkboxSquares: List<PdfRect>, val stamps: List<Stamp>) {
    companion object {
        fun of(file: File): PageFacts = runBlocking {
            val engine = PdfEngine()
            val doc = engine.open(file.readBytes())
            try {
                val page = doc.openPage(0)
                try {
                    PageFacts(
                        page.widthPoints, page.heightPoints, page.textLines(),
                        page.detectCheckboxSquares(), page.stamps(),
                    )
                } finally {
                    page.close()
                }
            } finally {
                doc.close()
            }
        }
    }

    /** The check marks MegaPDF has put on the page — the demo ships with one already ticked. */
    val checkMarks: List<Stamp> get() = stamps.filter { it.id.startsWith("mark:") }

    /** A drawn square with no check mark on it yet, so a tap on it adds one rather than taking one off. */
    fun unmarkedSquare(): PdfRect =
        checkboxSquares.firstOrNull { square -> checkMarks.none { square.contains(it.rect.centerX, it.rect.centerY) } }
            ?: error("every square is already ticked")

    /** The line whose text contains [phrase], or the longest line — the same rule the redact pose uses. */
    fun line(phrase: String): TextLine =
        lines.firstOrNull { it.text.contains(phrase) } ?: lines.maxByOrNull { it.text.length }
            ?: error("no text on page 1")
}

/** How long a document open, a render or an engine edit is given before the test gives up. */
const val SETTLE_MS = 20_000L

/** The [ViewerViewModel] the activity's screen observes — the same instance, by the activity's store. */
val AndroidComposeTestRule<ActivityScenarioRule<MainActivity>, MainActivity>.viewModel: ViewerViewModel
    get() = ViewModelProvider(activity)[ViewerViewModel::class.java]

fun ComposeTestRule.nodeExists(matcher: SemanticsMatcher): Boolean =
    onAllNodes(matcher, useUnmergedTree = false).fetchSemanticsNodes().isNotEmpty()

fun ComposeTestRule.waitFor(matcher: SemanticsMatcher, timeoutMs: Long = SETTLE_MS) =
    waitUntil(timeoutMs) { nodeExists(matcher) }

fun ComposeTestRule.waitForGone(matcher: SemanticsMatcher, timeoutMs: Long = SETTLE_MS) =
    waitUntil(timeoutMs) { !nodeExists(matcher) }

fun ComposeTestRule.waitForText(text: String) = waitFor(hasText(text))

/** Waits for the node, then clicks it. */
fun ComposeTestRule.clickText(text: String) {
    waitForText(text)
    onNodeWithText(text).performClick()
}

fun ComposeTestRule.clickLabelled(contentDescription: String) {
    waitFor(hasContentDescription(contentDescription))
    onNodeWithContentDescription(contentDescription).performClick()
}

/** Opens the ⋮ menu and taps [row]. */
fun ComposeTestRule.menu(row: String) {
    clickLabelled(str(R.string.more_options))
    clickText(row)
}

/** The rendered page image (#346): its node is the page's box on screen, so gestures land in page space. */
fun ComposeTestRule.page(index: Int = 0): SemanticsNodeInteraction =
    onNodeWithContentDescription(str(R.string.page_n, index + 1))

/** True once page [index] has rendered — the Image only exists once its bitmap does. */
fun ComposeTestRule.waitForPage(index: Int = 0) = waitFor(hasContentDescription(str(R.string.page_n, index + 1)))

/**
 * Opens [file] through the view model, as the Open button's picker result would, and waits
 * for page 1 to render.
 */
fun AndroidComposeTestRule<ActivityScenarioRule<MainActivity>, MainActivity>.open(file: File) {
    val uri = Fixtures.uri(file)
    activityRule.scenario.onActivity { ViewModelProvider(it)[ViewerViewModel::class.java].openUri(uri) }
    waitForText(file.name)
    waitForPage()
}

/** [pt] on the page, in the page node's own pixels: PDF points are bottom-left, the screen is top-left. */
fun SemanticsNodeInteraction.pointAt(facts: PageFacts, xPt: Double, yPt: Double): Offset {
    val size = fetchSemanticsNode().size
    return Offset(
        (xPt / facts.widthPoints * size.width).toFloat(),
        ((facts.heightPoints - yPt) / facts.heightPoints * size.height).toFloat(),
    )
}

fun SemanticsNodeInteraction.centreOf(facts: PageFacts, rect: PdfRect): Offset =
    pointAt(facts, rect.centerX, rect.centerY)

/** A single tap on the page at PDF point ([xPt], [yPt]) — what a finger on that spot does. */
fun SemanticsNodeInteraction.tapAt(facts: PageFacts, xPt: Double, yPt: Double) {
    val at = pointAt(facts, xPt, yPt)
    performTouchInput { click(at) }
}

fun SemanticsNodeInteraction.tapCentreOf(facts: PageFacts, rect: PdfRect) =
    tapAt(facts, rect.centerX, rect.centerY)

/** The Undo tool's button, enabled or not — the toolbar's word on whether the history has something. */
fun ComposeTestRule.undoButton() = onNodeWithContentDescription(str(R.string.undo))

fun ComposeTestRule.assertUndoEnabled() {
    waitUntil(SETTLE_MS) { runCatching { undoButton().assertIsEnabled() }.isSuccess }
}

fun SemanticsNodeInteraction.isEnabled(): Boolean =
    !fetchSemanticsNode().config.contains(SemanticsProperties.Disabled)

/**
 * The warning before the first text-box change on a page PDFium's rewrite would alter
 * (#139): answered Continue when it comes up, so a test about the text is not a test about
 * the page's geometry.
 */
fun ComposeTestRule.continuePastPageRewriteWarning(done: () -> Boolean) {
    val proceed = str(R.string.action_continue)
    waitUntil(SETTLE_MS) { done() || nodeExists(hasText(proceed)) }
    if (nodeExists(hasText(proceed))) onNodeWithText(proceed).performClick()
    waitUntil(SETTLE_MS) { done() }
}

fun assertContains(haystack: String, needle: String) =
    assertTrue("expected to find '$needle' in:\n$haystack", haystack.contains(needle))
