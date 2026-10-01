package com.megapdf.android

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.unit.dp
import com.megapdf.android.ui.Brand
import com.megapdf.engine.PageTint
import kotlinx.coroutines.delay

/**
 * A small side indicator of scroll position (Dave, 2026-10-01): Android had nothing at all —
 * no scrollbar on the `LazyColumn` (Compose draws none by default) and no page counter outside
 * reading mode, where it lives only inside the floating bar. Windows, Mac/Linux and iOS each
 * already carry their own native answer to "where am I" into reading mode unchanged (a kept
 * `ScrollViewer`/`ScrollView` scrollbar); this is Android's.
 *
 * It is not a scrollbar in the platform-chrome sense — Compose has no stock one to reach for,
 * and a hand-drawn track-and-arrows widget would be the "one shared custom control" the
 * standing platform-idiom rule asks not to build. What it is instead is the thing Android
 * *does* draw natively for a long scrolling surface: a thin, translucent trailing-edge thumb
 * that appears while the list moves and fades a moment after it stops, the same shape as the
 * system's own list/RecyclerView fast-scroll hint.
 *
 * It measures the whole document, not the current page. The two are easy to conflate — see
 * [pageTopPx]'s doc — and a document of thousands of pages (the stress corpus runs to
 * 10,000) made that the one semantic worth getting right before the first line of UI.
 */

/** The gap `Arrangement.spacedBy` puts between pages in the viewer's `LazyColumn`. */
internal val PAGE_GAP = 8.dp

/** How long the indicator stays up after a scroll stops, before its fade starts. */
internal const val SCROLL_INDICATOR_IDLE_MS = 800L

/** The thumb's own width, and the corner radius it is drawn with (a capsule, not a bar). */
private val INDICATOR_WIDTH = 4.dp

/** Never thinner than this fraction of the track, however long the document is. */
private const val MIN_THUMB_FRACTION = 0.04f

/**
 * Prefix sums of each page's aspect ratio (`heightPoints / widthPoints`), index `i` holding
 * the sum over pages `[0, i)`. Pages render at a width fixed to the viewport (times zoom), so
 * a page's on-screen height is that width times its aspect — and because every page shares the
 * same width, the *sum* of on-screen heights is just this prefix sum times that one width,
 * which is what makes an exact, O(1)-per-frame document position cheap even at 10,000 pages:
 * the aspect sums do not depend on zoom or viewport size and are only recomputed when the
 * document's own pages change.
 */
internal fun pageAspectPrefixSums(pageSizes: List<PageSize>): FloatArray {
    val prefix = FloatArray(pageSizes.size + 1)
    for (i in pageSizes.indices) {
        val width = pageSizes[i].widthPoints
        val aspect = if (width > 0.0) (pageSizes[i].heightPoints / width).toFloat() else 0f
        prefix[i + 1] = prefix[i] + aspect
    }
    return prefix
}

/**
 * Where page [index]'s top edge sits in the document's scroll range, in pixels, at the current
 * [pageWidthPx] (container width × zoom) and [spacingPx] (the gap between pages).
 *
 * This is the whole-document coordinate, not a per-page one — the distinction the indicator
 * exists to get right. A `LazyColumn`'s own `firstVisibleItemIndex` only ever names *a* page;
 * turning that into *where in the document* means knowing how tall everything before it is,
 * which this and [totalDocumentHeightPx] compute analytically rather than by measuring pages
 * that may not even be laid out yet.
 */
internal fun pageTopPx(prefixAspects: FloatArray, index: Int, pageWidthPx: Float, spacingPx: Float): Float {
    val clamped = index.coerceIn(0, prefixAspects.size - 1)
    return pageWidthPx * prefixAspects[clamped] + clamped * spacingPx
}

/** The whole document's rendered height: every page plus every gap between them. */
internal fun totalDocumentHeightPx(prefixAspects: FloatArray, pageWidthPx: Float, spacingPx: Float): Float {
    val pageCount = prefixAspects.size - 1
    if (pageCount <= 0) return 0f
    return pageWidthPx * prefixAspects[pageCount] + (pageCount - 1) * spacingPx
}

/** The indicator's thumb: where it starts and how tall it is, both a fraction of the track. */
internal data class ScrollThumb(val start: Float, val fraction: Float)

/**
 * The thumb for a document [totalHeightPx] tall, a viewport [viewportHeightPx] tall, scrolled
 * [scrolledPastPx] from the top — or `null` when the whole document already fits the viewport,
 * which is the same reason a desktop scrollbar does not draw one either (#143's rule, the one
 * this indicator is Android's answer to).
 */
internal fun scrollThumb(totalHeightPx: Float, viewportHeightPx: Float, scrolledPastPx: Float): ScrollThumb? {
    if (viewportHeightPx <= 0f || totalHeightPx <= viewportHeightPx) return null
    val fraction = (viewportHeightPx / totalHeightPx).coerceIn(MIN_THUMB_FRACTION, 1f)
    val maxStart = 1f - fraction
    val progress = (scrolledPastPx / (totalHeightPx - viewportHeightPx)).coerceIn(0f, 1f)
    return ScrollThumb(start = progress * maxStart, fraction = fraction)
}

/**
 * The indicator itself: a sibling of the page `LazyColumn`, not inside it, so it draws over
 * the page rather than scrolling with it. Present in both the plain viewer and reading mode —
 * nothing here reads `readingMode` — because the whole point of adding it is the one place
 * the rest of the chrome goes away.
 *
 * Decorative, not a control: [clearAndSetSemantics] keeps it off the accessibility tree
 * entirely, the same "not merely invisible" standard reading mode's own chrome holds itself
 * to. It is not TalkBack's only way to know where it is, either — every page already carries
 * its own "Page N" content description, and reading mode's bar carries a page counter besides
 * — so there is nothing here for a screen-reader user to lose by this staying unreachable.
 */
@Composable
fun ScrollPositionIndicator(
    listState: LazyListState,
    pageSizes: List<PageSize>,
    pageWidthPx: Float,
    tint: PageTint,
    reducedMotion: Boolean,
    modifier: Modifier = Modifier,
) {
    if (pageSizes.isEmpty() || pageWidthPx <= 0f) return

    val density = LocalDensity.current
    val spacingPx = with(density) { PAGE_GAP.toPx() }
    val prefixAspects = remember(pageSizes) { pageAspectPrefixSums(pageSizes) }

    val thumb by remember(prefixAspects, pageWidthPx, spacingPx) {
        derivedStateOf {
            val info = listState.layoutInfo
            val viewportHeightPx = info.viewportSize.height.toFloat()
            val totalHeightPx = totalDocumentHeightPx(prefixAspects, pageWidthPx, spacingPx)
            val scrolledPastPx = pageTopPx(
                prefixAspects, listState.firstVisibleItemIndex, pageWidthPx, spacingPx,
            ) + listState.firstVisibleItemScrollOffset
            scrollThumb(totalHeightPx, viewportHeightPx, scrolledPastPx)
        }
    }
    val current = thumb ?: return

    // Up while the list is moving, and for a moment after — never an always-on piece of
    // chrome, in or out of reading mode. `LaunchedEffect(scrolling)` is the plain Compose way
    // to do this: a resumed scroll cancels whatever idle delay was already counting down,
    // the same mechanism the reading bar's own idle timer relies on elsewhere in this screen.
    val scrolling = listState.isScrollInProgress
    var visible by remember { mutableStateOf(false) }
    LaunchedEffect(scrolling) {
        if (scrolling) {
            visible = true
        } else {
            delay(SCROLL_INDICATOR_IDLE_MS)
            visible = false
        }
    }

    val fadeSpec = readingBarFadeSpec(reducedMotion)
    AnimatedVisibility(
        visible = visible,
        enter = fadeIn(fadeSpec),
        exit = fadeOut(fadeSpec),
        modifier = modifier
            .fillMaxHeight()
            .width(INDICATOR_WIDTH + 8.dp)
            .padding(end = 3.dp, vertical = 8.dp)
            .clearAndSetSemantics {},
    ) {
        Column(
            modifier = Modifier.fillMaxSize(),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            if (current.start > 0f) {
                Spacer(Modifier.weight(current.start))
            }
            Box(
                Modifier
                    .weight(current.fraction.coerceAtLeast(0.0001f))
                    .width(INDICATOR_WIDTH)
                    .clip(RoundedCornerShape(INDICATOR_WIDTH / 2))
                    .background(Brand.scrollIndicator(tint)),
            )
            val bottom = 1f - current.start - current.fraction
            if (bottom > 0f) {
                Spacer(Modifier.weight(bottom))
            }
        }
    }
}
