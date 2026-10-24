package com.megapdf.android

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.text.selection.LocalTextSelectionColors
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.changedToUpIgnoreConsumed
import androidx.compose.ui.input.pointer.positionChange
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.positionInRoot
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalTextToolbar
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import com.megapdf.engine.SelectableText
import kotlin.math.roundToInt

internal const val SELECTION_START_HANDLE_TAG = "selection-start-handle"
internal const val SELECTION_END_HANDLE_TAG = "selection-end-handle"

/** The room the floating toolbar needs above a selection: its 48dp and the margin the system leaves around it. */
private val TOOLBAR_RESERVE = 64.dp

/** How far from a character's own middle the word probe may land and still count as on it. */
private const val STEP_PROBE_TOLERANCE_POINTS = 1.0

/**
 * The selected text of one page (#710), drawn in the theme's own selection colours, with Android's
 * teardrop-shaped handles at its two ends and the system's floating text toolbar above it.
 *
 * Why this is built from Compose parts and not a `SelectionContainer`: that only selects `Text`
 * composables, and a PDF page is a bitmap whose text lives in the core. What Compose does offer,
 * and what makes this the platform's own selection and not an imitation, is the same pieces its
 * text fields use: [LocalTextSelectionColors] for the highlight and handle colour, and
 * [LocalTextToolbar], which on Android is the floating `ActionMode` toolbar a `TextView` shows.
 * The handles are drawn here because Compose offers no public handle for custom content.
 *
 * Positions are page points, bottom-left origin, mapped to this box's pixels the way
 * `SearchHighlightOverlay` maps them.
 */
@Composable
internal fun TextSelectionOverlay(
    selection: TextSelection,
    selectable: SelectableText,
    pageSize: PageSize,
    canCopy: Boolean,
    onChange: (TextSelection) -> Unit,
    onCopy: () -> Unit,
    viewport: Rect? = null,
) {
    val colors = LocalTextSelectionColors.current
    val rects = remember(selection, selectable) { selectable.rects(selection.start, selection.end) }
    // Where this box is in the window: a state, so the toolbar follows the page as it scrolls.
    var origin by remember { mutableStateOf<Offset?>(null) }
    var dragging by remember { mutableStateOf(false) }
    val toolbar = LocalTextToolbar.current
    val density = LocalDensity.current
    var pagePixels by remember { mutableStateOf(Size.Zero) }
    val currentSelection by rememberUpdatedState(selection)
    val currentRects by rememberUpdatedState(rects)

    // Moves one end of the selection a word, as the handles' TalkBack actions do. Each probe is
    // the word under the middle of one character's rectangle, so "a word" is the core's own,
    // the one a long-press selects. Answers false when there is no word to go to.
    val stepHandle: (Boolean, Boolean) -> Boolean = { isStart, later ->
        val now = currentSelection
        val caret = steppedHandleCaret(now, isStart, later, selectable.length) { i ->
            selectable.rects(i, i + 1).firstOrNull()?.let { r ->
                selectable.wordAt((r.left + r.right) / 2, (r.bottom + r.top) / 2, STEP_PROBE_TOLERANCE_POINTS)
            }
        }
        val moved = caret?.let { now.withHandleAt(isStart, it) }
        if (moved == null || moved == now) false else { onChange(moved); true }
    }

    BoxWithConstraints(Modifier.fillMaxSize().onGloballyPositioned {
        origin = it.positionInRoot()
        pagePixels = Size(it.size.width.toFloat(), it.size.height.toFloat())
    }) {
        val widthPx = constraints.maxWidth.toFloat()
        val heightPx = constraints.maxHeight.toFloat()
        val sx = widthPx / pageSize.widthPoints.toFloat()
        val sy = heightPx / pageSize.heightPoints.toFloat()

        Canvas(Modifier.fillMaxSize()) {
            for (r in rects) {
                drawRect(
                    color = colors.backgroundColor,
                    topLeft = Offset(r.left.toFloat() * sx, (pageSize.heightPoints - r.top).toFloat() * sy),
                    size = Size((r.right - r.left).toFloat() * sx, (r.top - r.bottom).toFloat() * sy),
                )
            }
        }

        val anchors = handleAnchors(rects)
        if (anchors != null) {
            val handleSize = 40.dp
            val handleSizePx = with(density) { handleSize.toPx() }
            for (isStart in listOf(true, false)) {
                val handleName = stringResource(if (isStart) R.string.selection_start_handle else R.string.selection_end_handle)
                val nextWord = stringResource(R.string.selection_next_word)
                val previousWord = stringResource(R.string.selection_previous_word)
                val (px, py) = if (isStart) anchors.first else anchors.second
                val tip = Offset(px.toFloat() * sx, (pageSize.heightPoints - py).toFloat() * sy)
                // The start handle's body hangs to the left of its tip and the end handle's to the
                // right, as a TextView's do, so the pointed corner touches the caret.
                val left = if (isStart) tip.x - handleSizePx else tip.x
                Canvas(
                    Modifier
                        .offset { IntOffset(left.roundToInt(), tip.y.roundToInt()) }
                        .size(handleSize)
                        .testTag(if (isStart) SELECTION_START_HANDLE_TAG else SELECTION_END_HANDLE_TAG)
                        // What TalkBack says, and the way to move the handle without dragging it: a
                        // screen-reader user cannot drag, so each end steps a word at a time (#710).
                        .semantics {
                            contentDescription = handleName
                            customActions = listOf(
                                CustomAccessibilityAction(nextWord) { stepHandle(isStart, true) },
                                CustomAccessibilityAction(previousWord) { stepHandle(isStart, false) },
                            )
                        }
                        .pointerInput(isStart) {
                            // Not detectDragGestures: that leaves the down event unconsumed, so the
                            // page's own detector, further up, goes on timing a long-press and
                            // re-selects the word under a finger that is only resting on a handle.
                            // Consuming the down takes this touch away from it, and a handle should
                            // answer at once rather than after the touch slop.
                            awaitEachGesture {
                                val down = awaitFirstDown(requireUnconsumed = true)
                                down.consume()
                                dragging = true
                                val rect = currentRects.let { if (isStart) it.first() else it.last() }
                                var finger = Offset(
                                    (if (isStart) rect.left else rect.right).toFloat() * sx,
                                    (pageSize.heightPoints - rect.bottom).toFloat() * sy,
                                )
                                val lineHeightPx = ((rect.top - rect.bottom) * sy).toFloat()
                                while (true) {
                                    val event = awaitPointerEvent()
                                    val change = event.changes.firstOrNull { it.id == down.id } ?: break
                                    if (change.changedToUpIgnoreConsumed()) break
                                    val moved = change.positionChange()
                                    if (moved == Offset.Zero) continue
                                    change.consume()
                                    finger += moved
                                    // The handle hangs under its line, so the caret is looked for a
                                    // half line above the tip, on the line being pointed at.
                                    val xPt = (finger.x / sx).toDouble()
                                    val yPt = pageSize.heightPoints - ((finger.y - lineHeightPx / 2f) / sy)
                                    val now = currentSelection
                                    val caret = clampedHandleCaret(
                                        selectable.caretAt(xPt, yPt), isStart, now, selectable.length,
                                    )
                                    val next = now.withHandleAt(isStart, caret)
                                    if (next != now) onChange(next)
                                }
                                dragging = false
                            }
                        },
                ) {
                    val radius = size.width * 0.28f
                    val centre = Offset(if (isStart) size.width - radius else radius, radius)
                    drawCircle(colors.handleColor, radius, centre)
                    // The pointed corner, against the caret.
                    drawRect(
                        colors.handleColor,
                        topLeft = Offset(if (isStart) size.width - radius else 0f, 0f),
                        size = Size(radius, radius),
                    )
                }
            }
        }
    }

    // The floating toolbar, over the selection: Copy when the document allows it, and nothing
    // otherwise (the reason is said once, when the selection is made). Hidden while a handle is
    // being dragged, as a TextView's is, and re-placed when the selection or the page moves.
    val reservePx = with(density) { TOOLBAR_RESERVE.toPx() }
    val rootRect = origin?.let { at ->
        if (rects.isEmpty()) null else {
            val sxp = pagePixels.width / pageSize.widthPoints.toFloat()
            val syp = pagePixels.height / pageSize.heightPoints.toFloat()
            toolbarAnchor(
                Rect(
                    at.x + rects.minOf { it.left }.toFloat() * sxp,
                    at.y + rects.minOf { (pageSize.heightPoints - it.top).toFloat() } * syp,
                    at.x + rects.maxOf { it.right }.toFloat() * sxp,
                    at.y + rects.maxOf { (pageSize.heightPoints - it.bottom).toFloat() } * syp,
                ),
                viewport,
                reservePx,
            )
        }
    }
    LaunchedEffect(rootRect, dragging, canCopy) {
        if (dragging || rootRect == null) toolbar.hide()
        else toolbar.showMenu(rect = rootRect, onCopyRequested = if (canCopy) onCopy else null)
    }
    DisposableEffect(Unit) { onDispose { toolbar.hide() } }
}

/**
 * Where the floating toolbar is told the selection is (#803).
 *
 * The toolbar is a system window that knows the app's window and nothing of its layout, so it will
 * happily float above a selection that has scrolled up under the title bar, and that is where the
 * Copy pill ended on the Pixel 4 XL. This keeps it in the content area: it still follows the
 * selection, but the box it is given is cut to the [viewport], and its top is held [reservePx] below
 * the viewport's top, which is the room the pill needs above the box. A selection with nothing
 * left in view (scrolled out above, below or sideways) has no pill, as in a `TextView` inside a
 * scrolling parent, and it comes back as the selection scrolls back.
 *
 * Both rectangles are in root pixels. A null [viewport] means no limit. The result is null for
 * "hide the toolbar".
 */
internal fun toolbarAnchor(selection: Rect, viewport: Rect?, reservePx: Float): Rect? {
    if (viewport == null) return selection
    if (selection.bottom < viewport.top || selection.top > viewport.bottom) return null
    val left = maxOf(selection.left, viewport.left)
    val right = minOf(selection.right, viewport.right)
    if (left > right) return null
    val top = maxOf(selection.top, viewport.top + reservePx)
    val bottom = maxOf(minOf(selection.bottom, viewport.bottom), top)
    return Rect(left, top, right, bottom)
}
