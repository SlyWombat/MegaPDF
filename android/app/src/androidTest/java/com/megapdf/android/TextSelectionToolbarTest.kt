package com.megapdf.android

import android.view.ViewGroup
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.LocalTextToolbar
import androidx.compose.ui.platform.TextToolbar
import androidx.compose.ui.platform.TextToolbarStatus
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.longClick
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Where the floating Copy toolbar is told to appear as the selected page scrolls (#803). On the
 * Pixel 4 XL the pill rode up with the page and ended over the title bar. The system's toolbar window
 * is not visible to the Compose test rule (see [TextSelectCopyTest]), so the real
 * [TextSelectionOverlay] is hosted in a second view of the activity with a toolbar that records what
 * it is asked, over the real selection and the real engine geometry of `selection.pdf`; the page
 * is then moved under a stand-in content area the way scrolling moves it.
 */
@RunWith(AndroidJUnit4::class)
class TextSelectionToolbarTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    /** What the overlay asked the toolbar to do last. */
    private class RecordingToolbar : TextToolbar {
        @Volatile var shown: Rect? = null
        @Volatile var hidden = true
        override val status: TextToolbarStatus
            get() = if (hidden) TextToolbarStatus.Hidden else TextToolbarStatus.Shown
        override fun hide() { hidden = true }
        override fun showMenu(
            rect: Rect,
            onCopyRequested: (() -> Unit)?,
            onPasteRequested: (() -> Unit)?,
            onCutRequested: (() -> Unit)?,
            onSelectAllRequested: (() -> Unit)?,
        ) { shown = rect; hidden = false }
    }

    @Test
    fun theCopyPillStaysBelowTheTitleBarAsTheSelectionScrollsUpAndGoesWhenItIsGone() {
        val file = Fixtures.testAsset("selection.pdf", "toolbar-anchor-803.pdf")
        val facts = PageFacts.of(file)
        rule.open(file)
        rule.page().performTouchInput { longClick(rule.page().pointAt(facts, 140.0, 684.0)) }
        rule.waitUntil(SETTLE_MS) { rule.viewModel.textSelection != null }
        val selection = rule.viewModel.textSelection!!
        val selectable = rule.viewModel.selectableFor(0)!!
        val pageSize = PageSize(facts.widthPoints, facts.heightPoints)

        val toolbar = RecordingToolbar()
        var pageTop by mutableIntStateOf(800)
        val titleBarBottom = 300f
        val reserve = 64 * Fixtures.appContext.resources.displayMetrics.density
        rule.runOnUiThread {
            val view = ComposeView(rule.activity).apply {
                setContent {
                    CompositionLocalProvider(LocalTextToolbar provides toolbar) {
                        Box(Modifier.fillMaxSize()) {
                            Box(Modifier.offset { IntOffset(0, pageTop) }.size(600.dp, 800.dp)) {
                                TextSelectionOverlay(
                                    selection = selection,
                                    selectable = selectable,
                                    pageSize = pageSize,
                                    canCopy = true,
                                    onChange = {},
                                    onCopy = {},
                                    viewport = Rect(0f, titleBarBottom, 100_000f, 100_000f),
                                )
                            }
                        }
                    }
                }
            }
            rule.activity.addContentView(view, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        }

        // Well inside the content area: the pill is told where the selection is.
        rule.waitUntil(SETTLE_MS) { toolbar.shown != null }
        val resting = toolbar.shown!!
        assertTrue("selection starts below the title bar: ${resting.top}", resting.top > titleBarBottom + reserve)

        // Scrolled up until the selection's top is under the title bar, its line still showing.
        val selectionTopInPage = resting.top - 800
        val underTitleBar = (titleBarBottom - selectionTopInPage - 4).toInt()
        pageTop = underTitleBar
        rule.waitUntil(SETTLE_MS) { toolbar.shown?.top != resting.top }
        rule.waitForIdle()
        assertTrue("shown while the selection is partly in view", !toolbar.hidden)
        val clamped = toolbar.shown!!
        assertTrue(
            "the pill needs ${reserve}px above ${clamped.top} and the content area starts at $titleBarBottom",
            clamped.top - reserve >= titleBarBottom - 1f,
        )

        // Scrolled right out of sight: no pill at all.
        pageTop = -5_000
        rule.waitUntil(SETTLE_MS) { toolbar.hidden }
        assertTrue(toolbar.hidden)

        // And back again.
        pageTop = 800
        rule.waitUntil(SETTLE_MS) { !toolbar.hidden }
        assertEquals(resting.top, toolbar.shown!!.top, 1f)
    }
}
