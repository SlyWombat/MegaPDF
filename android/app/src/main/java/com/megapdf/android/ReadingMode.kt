package com.megapdf.android

import android.content.Context
import android.provider.Settings
import android.view.accessibility.AccessibilityManager
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.FiniteAnimationSpec
import androidx.compose.animation.core.snap
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.VerticalDivider
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.Search
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.text.KeyboardOptions
import com.megapdf.android.ui.Brand
import com.megapdf.engine.PageTint

/**
 * Reading mode (#507, #513) — the Android half of `docs/reading-mode-plan.md` §2 and §4.
 *
 * (The plan's SDD scope amendment is its own issue, #502, and has not landed; §3.10 F9 in
 * the SDD today is Page tools, so nothing here cites a section number that does not exist.)
 *
 * Tier 1 is a *way of looking at* the open document: the `Scaffold`'s bars are not composed,
 * a tap on the page toggles this bar, Back leaves the mode before it leaves the document,
 * and the system bars go immersive. Tier 2 adds the fit-page preset this bar carries and the
 * page colours a *Reading* group in Settings chooses.
 *
 * Nothing in here touches the document, the undo history or the file.
 */

/** How long the bar stays up with nothing happening (`docs/reading-mode-plan.md` §2: "~2 s"). */
const val READING_BAR_IDLE_MS = 2_000L

/**
 * Whether the bar counts down to hiding itself at all.
 *
 * With touch exploration on, it never does: the bar is the only chrome reading mode has, and
 * a control that removes itself from the accessibility tree two seconds after it appears is
 * a control a TalkBack user cannot reach. Pinned open is the whole answer — not a longer
 * timeout, which only makes the disappearance harder to reproduce.
 */
internal fun readingBarAutoHides(touchExplorationEnabled: Boolean): Boolean = !touchExplorationEnabled

/**
 * The zoom at which a page of [pageWidthPoints] × [pageHeightPoints] fits inside a viewport
 * of [viewportWidthPx] × [viewportHeightPx] whole (#513's *fit page* preset).
 *
 * Android's zoom is a multiple of fit-to-width, not of the page's own size (see MIN_ZOOM in
 * `ViewerScreen.kt`): the page is laid out at `viewportWidth × zoom` and its height follows
 * from its aspect, so fitting the height means
 *
 *     viewportWidth × zoom × (heightPoints / widthPoints) = viewportHeight
 *
 * which is what this solves. For a portrait page on a phone the answer is *below* 1 — fit
 * page is a zoom *out* from fit width there, which is why [readingZoomFloor] exists. For a
 * landscape page on a phone it is above 1, and fit width is the smaller of the two.
 */
internal fun fitPageZoom(
    viewportWidthPx: Float,
    viewportHeightPx: Float,
    pageWidthPoints: Double,
    pageHeightPoints: Double,
): Float {
    if (viewportWidthPx <= 0f || viewportHeightPx <= 0f) return 1f
    if (pageWidthPoints <= 0.0 || pageHeightPoints <= 0.0) return 1f
    val zoom = (viewportHeightPx * pageWidthPoints) / (viewportWidthPx * pageHeightPoints)
    return zoom.toFloat().coerceIn(MIN_FIT_PAGE_ZOOM, MAX_READING_ZOOM)
}

/**
 * The floor a pinch may zoom out to, given the current page's [fitPage] zoom.
 *
 * Before fit page existed the floor was a flat 1 (fit width), which is also the point at
 * which the page stops needing a horizontal scroll. Fit page can sit below that, and a floor
 * that ignored it would let the preset put the page somewhere the very next pinch snapped
 * back out of. So the floor is whichever of the two shows *more* of the page — never less
 * than fit page, never more zoomed-out than that either.
 */
internal fun readingZoomFloor(fitPage: Float): Float = minOf(1f, fitPage)

/** Below this a page is too small to read on any phone; the preset stops there. */
private const val MIN_FIT_PAGE_ZOOM = 0.1f

/** Mirrors `ViewerScreen.kt`'s MAX_ZOOM; kept here so this file is unit-testable on the JVM. */
private const val MAX_READING_ZOOM = 4f

/**
 * True while TalkBack (or any touch-exploring service) is on, kept live: it can be turned on
 * from the notification shade while the viewer is open, and a bar that had already armed its
 * fade timer would then vanish under the user's finger.
 */
@Composable
internal fun rememberTouchExplorationEnabled(): Boolean {
    val context = LocalContext.current
    val manager = remember(context) {
        context.getSystemService(Context.ACCESSIBILITY_SERVICE) as? AccessibilityManager
    }
    var enabled by remember(manager) { mutableStateOf(manager?.isTouchExplorationEnabled == true) }
    DisposableEffect(manager) {
        val listener = AccessibilityManager.TouchExplorationStateChangeListener { enabled = it }
        manager?.addTouchExplorationStateChangeListener(listener)
        onDispose { manager?.removeTouchExplorationStateChangeListener(listener) }
    }
    return enabled
}

/**
 * "Remove animations" in Accessibility settings, which Android exposes as an animation
 * duration scale of zero. The bar then shows and hides outright rather than fading — the
 * plan's reduced-motion rule, and the same stance the Avalonia pill takes.
 */
@Composable
internal fun rememberReducedMotion(): Boolean {
    val context = LocalContext.current
    return remember(context) {
        Settings.Global.getFloat(
            context.contentResolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f,
        ) == 0f
    }
}

/** The fade the bar uses, or an instant swap when the device asks for no animation. */
@Composable
internal fun readingBarFadeSpec(reducedMotion: Boolean): FiniteAnimationSpec<Float> =
    if (reducedMotion) snap() else tween(220)

/**
 * The floating bar: reading mode's only chrome, and the one place its commands live.
 *
 * Appears on entry and on a tap on the page, hides itself again after [READING_BAR_IDLE_MS]
 * unless [readingBarAutoHides] says otherwise. Composed only while it is showing — a hidden
 * bar is not in the tree at all, so TalkBack's swipe navigation cannot land on it (the same
 * rule the `Scaffold`'s bars follow in this mode).
 *
 * The row scrolls sideways if it has to. Eight 48 dp targets and a page counter are wider
 * than a narrow phone, and the choice is between clipping the last control and letting the
 * row scroll; `ChipRow` in `ViewerScreen.kt` made the same call for the same reason. Nothing
 * is dropped and no target shrinks below the 48 dp #347 settled on.
 */
@Composable
fun ReadingBar(
    pageLabel: String,
    canPrevious: Boolean,
    canNext: Boolean,
    tint: PageTint,
    onPrevious: () -> Unit,
    onNext: () -> Unit,
    onGoToPage: () -> Unit,
    onFitWidth: () -> Unit,
    onFitPage: () -> Unit,
    onZoomOut: () -> Unit,
    onZoomIn: () -> Unit,
    onFind: () -> Unit,
    onExit: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val controls = stringResource(R.string.reading_controls)
    Surface(
        // radius.control on Android is 8 (docs/design-tokens.md §3), and the bar is a tonal
        // surface rather than a floating white card: it sits over the page, and Material 3's
        // tonal containers are what "over, but of the app" looks like here.
        shape = RoundedCornerShape(8.dp),
        color = Brand.readingBarSurface(tint),
        contentColor = Brand.readingBarContent(tint),
        shadowElevation = 6.dp,
        modifier = modifier
            .padding(16.dp)
            .semantics { contentDescription = controls },
    ) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(2.dp),
            modifier = Modifier
                .horizontalScroll(rememberScrollState())
                .padding(horizontal = 4.dp)
                // The bar is where the mode is said (#507). BusyStrip's polite live region
                // (`ViewerScreen.kt`) is the pattern: a node TalkBack reads when it appears
                // and when its contents change, without taking focus off the page.
                .semantics { liveRegion = LiveRegionMode.Polite },
        ) {
            ReadingAction(
                icon = Icons.Filled.KeyboardArrowUp,
                label = stringResource(R.string.previous_page),
                enabled = canPrevious,
                onClick = onPrevious,
            )
            // The counter is a button, not a label: tapping it is how you jump (plan §2).
            TextButton(onClick = onGoToPage) {
                Text(pageLabel, fontWeight = FontWeight.Medium, maxLines = 1)
            }
            ReadingAction(
                icon = Icons.Filled.KeyboardArrowDown,
                label = stringResource(R.string.next_page),
                enabled = canNext,
                onClick = onNext,
            )
            ReadingDivider()
            ReadingAction(ToolbarIcons.FitWidth, stringResource(R.string.fit_width), onClick = onFitWidth)
            ReadingAction(ToolbarIcons.FitPage, stringResource(R.string.fit_page), onClick = onFitPage)
            ReadingAction(ToolbarIcons.ZoomOut, stringResource(R.string.zoom_out), onClick = onZoomOut)
            ReadingAction(ToolbarIcons.ZoomIn, stringResource(R.string.zoom_in), onClick = onZoomIn)
            ReadingDivider()
            // The magnifier the plan asks the phones' bar to carry (§2, "Find"): the find
            // bar is the one piece of chrome allowed over the reading view, and with the
            // bottom bar gone this is the only way left to reach it.
            ReadingAction(
                icon = Icons.Filled.Search,
                label = stringResource(R.string.search),
                onClick = onFind,
            )
            ReadingAction(
                icon = Icons.Filled.Close,
                label = stringResource(R.string.exit_reading_mode),
                onClick = onExit,
            )
        }
    }
}

/** One of the bar's icon buttons. Icon-only, so the label is its accessible name (#144, #237). */
@Composable
private fun ReadingAction(
    icon: ImageVector,
    label: String,
    onClick: () -> Unit,
    enabled: Boolean = true,
) {
    IconButton(onClick = onClick, enabled = enabled) {
        Icon(icon, contentDescription = label)
    }
}

/** A rule between the bar's three groups. 1 dp is a stroke, not layout spacing (design-tokens §3). */
@Composable
private fun ReadingDivider() {
    VerticalDivider(
        modifier = Modifier.height(24.dp).padding(horizontal = 4.dp),
        color = LocalContentColor.current.copy(alpha = 0.3f),
    )
}

/**
 * *Go to page* (#507): what the bar's page counter opens. A number and nothing else — the
 * desktops use a flyout with a text box, and a phone's equivalent is a small dialog.
 */
@Composable
fun GoToPageDialog(pageCount: Int, onGo: (pageIndex: Int) -> Unit, onDismiss: () -> Unit) {
    var typed by remember { mutableStateOf("") }
    val target = typed.toIntOrNull()
    val valid = target != null && target in 1..pageCount
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.go_to_page)) },
        text = {
            DialogBody {
                OutlinedTextField(
                    value = typed,
                    onValueChange = { typed = it.filter(Char::isDigit).take(7) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    label = { Text(stringResource(R.string.page_of, pageCount)) },
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        },
        confirmButton = {
            TextButton(onClick = { target?.let { onGo(it - 1) } }, enabled = valid) {
                Text(stringResource(R.string.go_to_page))
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) }
        },
    )
}

/**
 * The bar, with its fade. Split from [ReadingBar] so the bar itself stays a plain row of
 * controls and this holds the one rule that matters: composed while showing, gone otherwise.
 */
@Composable
fun ReadingBarHost(
    visible: Boolean,
    reducedMotion: Boolean,
    modifier: Modifier = Modifier,
    content: @Composable () -> Unit,
) {
    val spec = readingBarFadeSpec(reducedMotion)
    AnimatedVisibility(
        visible = visible,
        enter = fadeIn(spec),
        exit = fadeOut(spec),
        modifier = modifier,
    ) {
        content()
    }
}
