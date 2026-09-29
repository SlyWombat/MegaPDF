package com.megapdf.android

import android.graphics.Bitmap
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyGridLayoutInfo
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.BottomAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.text.KeyboardOptions
import com.megapdf.android.ui.Brand
import com.megapdf.engine.PageTint
import kotlinx.coroutines.flow.distinctUntilChanged

/**
 * The page tools (#174), in the shape a phone puts them in: **a screen of pages you select and
 * act on**, not a panel beside the document.
 *
 * Why a screen and not a side panel or a drawer. Rotating, deleting, reordering, combining and
 * extracting all need somewhere to see pages *as pages*, and the desktops have room to put that
 * beside the document. A phone does not: a 112 dp thumbnail grid and a page at a readable size
 * cannot share a 5-inch screen, and a drawer over the document would leave the document doing
 * nothing but sit behind it. So this is its own destination, reached from the ⋮ overflow where the
 * document's other whole-file commands already live (Share, Export as Markdown, Reading mode), and
 * Back returns to the page you were reading. Nothing underneath is composed while it is up — the
 * choice `SettingsScreen` already makes, and for its reason: leaving the viewer in the tree would
 * leave its bars and its page in the accessibility tree too.
 *
 * Why selection rather than per-page buttons. Every operation here applies to one page or to
 * several, and Android's answer to "do this to some of these" is the contextual selection bar:
 * tap a page to select it, and the top bar becomes what you can do to the selection. That is what
 * Files, Photos and Gmail do, it costs the grid no chrome at all while nothing is selected, and it
 * makes "rotate these six pages" one gesture and one undo step instead of six of each.
 *
 * Reordering has two ways in on purpose. **Long-press a page and drag it** is the direct one, and
 * it is what a finger expects. It is also unusable with a screen reader and impossible without a
 * touchscreen, so every page also carries *Move earlier* and *Move later* as custom accessibility
 * actions, and a selected page has **Move to…** in the overflow — a position typed into a box,
 * which is the phone's equivalent of the desktops' cut and paste (#2, #174's own scope note).
 *
 * Undo is in the bottom bar, where the viewer keeps it, and it covers every operation on this
 * screen: contract 10 gives each of them an inverse, and `megapdf_page_restore` exists precisely
 * so that a deleted page can come back as itself.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PagesScreen(
    pageSizes: List<PageSize>,
    thumbnails: Map<Int, Bitmap>,
    selection: Set<Int>,
    /** The document's security allows pages to be rotated, deleted, moved, inserted, combined. */
    canAssemble: Boolean,
    /** …and allows a copy, which is what saving a selection as a new file is. */
    canExtract: Boolean,
    canUndo: Boolean,
    canRedo: Boolean,
    busy: BusyState?,
    /** A change is still going in, or a save runs: the tools wait rather than queue (#145). */
    toolsDisabled: Boolean,
    pageTint: PageTint,
    onThumbnailWindowChange: (firstVisible: Int, lastVisible: Int, targetWidthPx: Int) -> Unit,
    onToggleSelection: (pageIndex: Int) -> Unit,
    onSelectAll: () -> Unit,
    onClearSelection: () -> Unit,
    onRotate: (quarterTurns: Int) -> Unit,
    onDelete: () -> Unit,
    onMove: (from: Int, to: Int) -> Unit,
    onInsertBlank: () -> Unit,
    onAddFromFile: () -> Unit,
    onSaveSelectionAs: () -> Unit,
    onUndo: () -> Unit,
    onRedo: () -> Unit,
    onClose: () -> Unit,
) {
    val pageCount = pageSizes.size
    var menuOpen by remember { mutableStateOf(false) }
    var moveToOpen by remember { mutableStateOf(false) }
    val enabled = canAssemble && !toolsDisabled
    // Everything selected cannot be deleted: a PDF must keep a page, and the engine refuses it
    // (MEGAPDF_ERR_ARGUMENT). Saying so by disabling the button is better than saying so in a
    // dialog after the tap — the refusal dialog is for what cannot be predicted from here.
    val canDelete = enabled && selection.isNotEmpty() && selection.size < pageCount

    androidx.activity.compose.BackHandler {
        if (selection.isEmpty()) onClose() else onClearSelection()
    }

    // --- Drag to reorder ---------------------------------------------------------------
    //
    // The pointer input sits on the box *around* the grid rather than on each cell, so one
    // gesture can start on one page and end on another; the grid's own scrolling is untouched
    // because a long press is what claims the pointer. Live reordering under the finger was
    // deliberately not attempted: the grid would have to lay out again on every frame of the
    // drag, and what the person needs to see is which page they are about to drop onto, which
    // is what the highlighted target does.
    val gridState = rememberLazyGridState()
    var dragFrom by remember { mutableIntStateOf(-1) }
    var dragTo by remember { mutableIntStateOf(-1) }

    Scaffold(
        topBar = {
            Column {
                TopAppBar(
                    title = {
                        Text(
                            if (selection.isEmpty()) {
                                if (pageCount == 1) stringResource(R.string.pages_count_one)
                                else stringResource(R.string.pages_count, pageCount)
                            } else {
                                stringResource(R.string.pages_selected, selection.size)
                            }
                        )
                    },
                    navigationIcon = {
                        if (selection.isEmpty()) {
                            IconButton(onClick = onClose) {
                                Icon(
                                    Icons.AutoMirrored.Filled.ArrowBack,
                                    contentDescription = stringResource(R.string.pages_done),
                                )
                            }
                        } else {
                            IconButton(onClick = onClearSelection) {
                                Icon(
                                    Icons.Filled.Close,
                                    contentDescription = stringResource(R.string.pages_clear_selection),
                                )
                            }
                        }
                    },
                    actions = {
                        if (selection.isNotEmpty()) {
                            ToolbarAction(
                                icon = ToolbarIcons.RotateLeft,
                                label = stringResource(R.string.rotate_left),
                                enabled = enabled,
                                onClick = { onRotate(-1) },
                            )
                            ToolbarAction(
                                icon = ToolbarIcons.RotateRight,
                                label = stringResource(R.string.rotate_right),
                                enabled = enabled,
                                onClick = { onRotate(1) },
                            )
                            ToolbarAction(
                                icon = ToolbarIcons.DeletePages,
                                label = stringResource(R.string.pages_delete),
                                enabled = canDelete,
                                onClick = onDelete,
                            )
                        }
                        IconButton(onClick = { menuOpen = true }) {
                            Icon(
                                Icons.Filled.MoreVert,
                                contentDescription = stringResource(R.string.more_options),
                            )
                        }
                        DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                            DropdownMenuItem(
                                text = { Text(stringResource(R.string.pages_select_all)) },
                                enabled = selection.size < pageCount,
                                onClick = { menuOpen = false; onSelectAll() },
                            )
                            // One page, one place to put it: the accessible and keyboard way to
                            // reorder, and the only way at all without a touchscreen.
                            DropdownMenuItem(
                                text = { Text(stringResource(R.string.pages_move_to)) },
                                enabled = enabled && selection.size == 1 && pageCount > 1,
                                onClick = { menuOpen = false; moveToOpen = true },
                            )
                            DropdownMenuItem(
                                text = { Text(stringResource(R.string.pages_save_selection)) },
                                enabled = selection.isNotEmpty() && canExtract && !toolsDisabled,
                                onClick = { menuOpen = false; onSaveSelectionAs() },
                            )
                            HorizontalDivider()
                            DropdownMenuItem(
                                text = { Text(stringResource(R.string.pages_add_from_file)) },
                                enabled = enabled,
                                onClick = { menuOpen = false; onAddFromFile() },
                            )
                            DropdownMenuItem(
                                text = { Text(stringResource(R.string.pages_insert_blank)) },
                                enabled = enabled,
                                onClick = { menuOpen = false; onInsertBlank() },
                            )
                        }
                    },
                )
                // #145: the same strip the viewer shows, in the same place, so a delete or an
                // import that takes a moment says so where it is being asked for.
                if (busy != null) BusyStrip(busy.document)
            }
        },
        bottomBar = {
            BottomAppBar(
                actions = {
                    ToolbarAction(
                        icon = ToolbarIcons.Undo,
                        label = stringResource(R.string.undo),
                        enabled = canUndo && !toolsDisabled,
                        onClick = onUndo,
                    )
                    ToolbarAction(
                        icon = ToolbarIcons.Redo,
                        label = stringResource(R.string.redo),
                        enabled = canRedo && !toolsDisabled,
                        onClick = onRedo,
                    )
                    Spacer(Modifier.width(8.dp))
                    Text(
                        stringResource(R.string.pages_hint),
                        style = MaterialTheme.typography.labelMedium,
                    )
                },
            )
        },
    ) { padding ->
        Box(
            Modifier
                .fillMaxSize()
                .padding(padding)
                .background(Brand.backdrop(pageTint))
                .pointerInputForReorder(
                    enabled = enabled && pageCount > 1,
                    pageCount = pageCount,
                    layoutInfo = { gridState.layoutInfo },
                    from = { dragFrom },
                    to = { dragTo },
                    onFrom = { dragFrom = it },
                    onTo = { dragTo = it },
                    onMove = onMove,
                )
        ) {
            val density = LocalDensity.current
            LazyVerticalGrid(
                state = gridState,
                columns = GridCells.Adaptive(minSize = CELL_MIN_WIDTH.dp),
                contentPadding = PaddingValues(12.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                items(pageCount, key = { it }) { index ->
                    PageCell(
                        index = index,
                        size = pageSizes[index],
                        bitmap = thumbnails[index],
                        tint = pageTint,
                        selected = index in selection,
                        dragged = index == dragFrom,
                        dropTarget = dragFrom >= 0 && index == dragTo && dragTo != dragFrom,
                        canMoveEarlier = enabled && index > 0,
                        canMoveLater = enabled && index < pageCount - 1,
                        onToggle = { onToggleSelection(index) },
                        onMoveEarlier = { onMove(index, index - 1) },
                        onMoveLater = { onMove(index, index + 1) },
                    )
                }
            }

            // Only the thumbnails on screen are rendered, and only while they are (#147): a
            // thousand-page document must open this screen as fast as a two-page one. The width
            // is measured rather than assumed, because the grid decides how many columns fit.
            LaunchedEffect(pageCount, density) {
                snapshotFlow {
                    val info = gridState.layoutInfo.visibleItemsInfo
                    Triple(
                        info.firstOrNull()?.index ?: 0,
                        info.lastOrNull()?.index ?: 0,
                        info.firstOrNull()?.size?.width ?: with(density) { CELL_MIN_WIDTH.dp.roundToPx() },
                    )
                }
                    .distinctUntilChanged()
                    .collect { (first, last, widthPx) -> onThumbnailWindowChange(first, last, widthPx) }
            }
        }
    }

    if (moveToOpen) {
        val from = selection.first()
        MovePageDialog(
            pageCount = pageCount,
            from = from,
            onMove = { to -> moveToOpen = false; onMove(from, to) },
            onDismiss = { moveToOpen = false },
        )
    }
}

/** How wide a page thumbnail may be before the grid fits another column in. */
private const val CELL_MIN_WIDTH = 104

/**
 * One page in the grid: its picture, its number, and whether it is selected.
 *
 * The cell keeps the page's own shape ([PageSize]), so a page that has just been turned is
 * visibly a landscape page among portrait ones before its thumbnail has even been redrawn.
 */
@Composable
private fun PageCell(
    index: Int,
    size: PageSize,
    bitmap: Bitmap?,
    tint: PageTint,
    selected: Boolean,
    dragged: Boolean,
    dropTarget: Boolean,
    canMoveEarlier: Boolean,
    canMoveLater: Boolean,
    onToggle: () -> Unit,
    onMoveEarlier: () -> Unit,
    onMoveLater: () -> Unit,
) {
    val label = stringResource(R.string.page_n, index + 1)
    val moveEarlier = stringResource(R.string.pages_move_earlier)
    val moveLater = stringResource(R.string.pages_move_later)
    val outline = when {
        dropTarget -> MaterialTheme.colorScheme.tertiary
        selected -> MaterialTheme.colorScheme.primary
        else -> MaterialTheme.colorScheme.outlineVariant
    }
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        modifier = Modifier
            .clickable(onClick = onToggle)
            // A screen reader gets the page, whether it is selected, and the two ways to move it
            // that a drag cannot give it. `selected` is the trait a QA accessibility dump can
            // read, as the armed Redact row's is (#328).
            .semantics {
                contentDescription = label
                this.selected = selected
                customActions = buildList {
                    if (canMoveEarlier) add(CustomAccessibilityAction(moveEarlier) { onMoveEarlier(); true })
                    if (canMoveLater) add(CustomAccessibilityAction(moveLater) { onMoveLater(); true })
                }
            },
    ) {
        Surface(
            shape = RoundedCornerShape(8.dp),
            color = Brand.pageGround(tint),
            shadowElevation = if (dragged) 8.dp else 1.dp,
            modifier = Modifier
                .fillMaxWidth()
                .aspectRatio((size.widthPoints / size.heightPoints).toFloat())
                .border(
                    width = if (selected || dropTarget) 2.dp else 1.dp,
                    color = outline,
                    shape = RoundedCornerShape(8.dp),
                ),
        ) {
            Box(Modifier.fillMaxSize()) {
                if (bitmap != null) {
                    Image(
                        bitmap = bitmap.asImageBitmap(),
                        // The cell itself carries the page's name; the picture inside it is not a
                        // second thing to land on.
                        contentDescription = null,
                        modifier = Modifier.fillMaxSize(),
                        contentScale = ContentScale.Fit,
                    )
                }
                if (selected) {
                    Surface(
                        shape = RoundedCornerShape(50),
                        color = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.padding(4.dp).align(Alignment.TopEnd),
                    ) {
                        Icon(
                            Icons.Filled.Check,
                            contentDescription = null,
                            tint = MaterialTheme.colorScheme.onPrimary,
                            modifier = Modifier.size(18.dp).padding(2.dp),
                        )
                    }
                }
            }
        }
        Text(
            (index + 1).toString(),
            style = MaterialTheme.typography.labelMedium,
            modifier = Modifier.padding(top = 4.dp),
        )
    }
}

/** *Move to…*: one page, one position, typed. The phone's cut and paste. */
@Composable
private fun MovePageDialog(
    pageCount: Int,
    from: Int,
    onMove: (to: Int) -> Unit,
    onDismiss: () -> Unit,
) {
    var typed by remember { mutableStateOf("") }
    val target = typed.toIntOrNull()
    val valid = target != null && target in 1..pageCount && target - 1 != from
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.pages_move_to)) },
        text = {
            DialogBody {
                Text(stringResource(R.string.pages_move_to_body, from + 1))
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
            TextButton(onClick = { target?.let { onMove(it - 1) } }, enabled = valid) {
                Text(stringResource(R.string.pages_move))
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) }
        },
    )
}

/**
 * Long-press-and-drag reordering, as a modifier so the screen above reads as a layout.
 *
 * The state is passed in as accessors rather than captured: `pointerInput`'s block is built once
 * per key set and a captured value would be the one from the composition that built it — the trap
 * #513's zoom floor had to be turned into a `MutableFloatState` for.
 */
private fun Modifier.pointerInputForReorder(
    enabled: Boolean,
    pageCount: Int,
    layoutInfo: () -> LazyGridLayoutInfo,
    from: () -> Int,
    to: () -> Int,
    onFrom: (Int) -> Unit,
    onTo: (Int) -> Unit,
    onMove: (from: Int, to: Int) -> Unit,
): Modifier = this.then(
    Modifier.pointerInput(enabled, pageCount) {
        if (!enabled) return@pointerInput
        detectDragGesturesAfterLongPress(
            onDragStart = { at ->
                val start = pageUnderPoint(layoutInfo().cellBounds(), at.x, at.y)
                onFrom(start)
                onTo(start)
            },
            onDrag = { change, _ ->
                if (from() < 0) return@detectDragGesturesAfterLongPress
                change.consume()
                val over = pageUnderPoint(layoutInfo().cellBounds(), change.position.x, change.position.y)
                if (over >= 0) onTo(over)
            },
            onDragCancel = { onFrom(-1); onTo(-1) },
            onDragEnd = {
                val source = from()
                val target = to()
                onFrom(-1)
                onTo(-1)
                if (source >= 0 && target >= 0 && source != target) onMove(source, target)
            },
        )
    }
)

/** One cell's box in the grid's own coordinates. */
data class PageCellBounds(
    val index: Int,
    val left: Float,
    val top: Float,
    val right: Float,
    val bottom: Float,
)

private fun LazyGridLayoutInfo.cellBounds(): List<PageCellBounds> = visibleItemsInfo.map {
    PageCellBounds(
        index = it.index,
        left = it.offset.x.toFloat(),
        top = it.offset.y.toFloat(),
        right = (it.offset.x + it.size.width).toFloat(),
        bottom = (it.offset.y + it.size.height).toFloat(),
    )
}

/**
 * Which page a drag at ([x], [y]) is over, or -1 for the gaps between cells and the space past
 * the last one. Pure, and tested on the JVM: the arithmetic is the whole of whether a drop lands
 * on the page the finger was over.
 */
internal fun pageUnderPoint(cells: List<PageCellBounds>, x: Float, y: Float): Int =
    cells.firstOrNull { x >= it.left && x < it.right && y >= it.top && y < it.bottom }?.index ?: -1
