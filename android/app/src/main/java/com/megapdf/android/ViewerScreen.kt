package com.megapdf.android

import android.graphics.Bitmap
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import kotlin.math.roundToInt
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.material3.TopAppBar
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Search
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.flow.distinctUntilChanged
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material.icons.materialIcon
import androidx.compose.material.icons.materialPath
import androidx.compose.ui.graphics.vector.ImageVector

/**
 * The undo arrow. `material-icons-core` does not carry it and one glyph is not
 * worth pulling in `material-icons-extended` (SDD §4.5 keeps the footprint
 * small), so it is drawn here from the standard 24dp Material path.
 */
private val UndoIcon: ImageVector = materialIcon(name = "Filled.Undo") {
    materialPath {
        moveTo(12.5f, 8.0f)
        curveToRelative(-2.65f, 0.0f, -5.05f, 0.99f, -6.9f, 2.6f)
        lineTo(2.0f, 7.0f)
        verticalLineToRelative(9.0f)
        horizontalLineToRelative(9.0f)
        lineToRelative(-3.62f, -3.62f)
        curveToRelative(1.39f, -1.16f, 3.16f, -1.88f, 5.12f, -1.88f)
        curveToRelative(3.54f, 0.0f, 6.55f, 2.31f, 7.6f, 5.5f)
        lineToRelative(2.37f, -0.78f)
        curveTo(21.08f, 11.03f, 17.15f, 8.0f, 12.5f, 8.0f)
        close()
    }
}

private const val MIN_ZOOM = 1f
private const val MAX_ZOOM = 4f

// Search highlight fills (#26): every match gets the translucent accent; the
// current match is set apart in translucent orange.
private val MATCH_HIGHLIGHT = Color(0x4D1E88E5)
private val CURRENT_MATCH_HIGHLIGHT = Color(0x66FF8F00)

@OptIn(ExperimentalMaterial3Api::class, kotlinx.coroutines.FlowPreview::class)
@Composable
fun ViewerScreen(
    displayName: String,
    pageSizes: List<PageSize>,
    pageBitmaps: Map<Int, Bitmap>,
    isDirty: Boolean,
    isSaving: Boolean,
    signatures: List<SignatureEntry>,
    selectedStamp: SelectedStamp?,
    selectedTextBox: SelectedTextBox?,
    canUndo: Boolean,
    canRedo: Boolean,
    pendingTextTap: PendingTextTap?,
    searchQuery: String,
    searchHits: List<SearchHit>,
    currentHitIndex: Int,
    isSearching: Boolean,
    onRenderWindowChange: (firstVisible: Int, lastVisible: Int, targetWidthPx: Int) -> Unit,
    onPageTap: (pageIndex: Int, xFraction: Float, yFraction: Float) -> Unit,
    onSearchQueryChange: (String) -> Unit,
    onSearchPrevious: () -> Unit,
    onSearchNext: () -> Unit,
    onCloseSearch: () -> Unit,
    onStartPlacement: (SignatureEntry) -> Unit,
    onAddSignature: () -> Unit,
    onSaveDrawnSignature: (Bitmap) -> Unit,
    onDeleteSignature: (String) -> Unit,
    screenshotSheet: String? = null,
    onUndo: () -> Unit,
    onRedo: () -> Unit,
    onStartTextPlacement: () -> Unit,
    onCommitText: (text: String, fontSize: Double, fontName: String) -> Unit,
    onCancelTextPlacement: () -> Unit,
    onCommitStampRect: (com.megapdf.engine.PdfRect) -> Unit,
    onRemoveStamp: () -> Unit,
    onCommitTextBoxRect: (com.megapdf.engine.PdfRect) -> Unit,
    onEditTextBox: () -> Unit,
    onRemoveTextBox: () -> Unit,
    onSave: () -> Unit,
    onSaveAs: () -> Unit,
    onClose: () -> Unit,
) {
    var zoom by remember { mutableFloatStateOf(1f) }
    val listState = rememberLazyListState()
    // Hoisted so search navigation can reach a hit that is off to the side when zoomed.
    val hScroll = rememberScrollState()
    var menuOpen by remember { mutableStateOf(false) }
    var confirmDiscard by remember { mutableStateOf(false) }
    var signDialogOpen by remember { mutableStateOf(false) }
    var searchOpen by remember { mutableStateOf(false) }
    val closeSearch = { searchOpen = false; onCloseSearch() }
    val requestClose = { if (isDirty) confirmDiscard = true else onClose() }
    androidx.activity.compose.BackHandler { if (searchOpen) closeSearch() else requestClose() }

    var drawDialogOpen by remember { mutableStateOf(false) }
    LaunchedEffect(screenshotSheet) {
        if (screenshotSheet == "sign") signDialogOpen = true
        if (screenshotSheet == "draw") drawDialogOpen = true
        // The query itself is already seeded by the view model, so the bar
        // opens filled in, with its match count and highlights in place.
        if (screenshotSheet == "search") searchOpen = true
    }
    if (signDialogOpen) {
        SignatureDialog(
            signatures = signatures,
            onPick = { signDialogOpen = false; onStartPlacement(it) },
            onAdd = onAddSignature,
            onDraw = { signDialogOpen = false; drawDialogOpen = true },
            onDelete = onDeleteSignature,
            onDismiss = { signDialogOpen = false },
        )
    }
    if (pendingTextTap != null) {
        // A correction opens on the box's current text, size and face (#36/#43);
        // a new box opens empty, at whatever the last one used.
        val editing = pendingTextTap.editingId != null
        var typed by remember(pendingTextTap) { mutableStateOf(pendingTextTap.initialText) }
        var size by remember(pendingTextTap) { mutableStateOf(pendingTextTap.fontSize) }
        var face by remember(pendingTextTap) { mutableStateOf(pendingTextTap.fontName) }
        AlertDialog(
            onDismissRequest = onCancelTextPlacement,
            title = { Text(if (editing) "Edit text" else "Add text") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Text(
                        if (editing) "This replaces the text you tapped."
                        else "This will be added where you tapped."
                    )
                    OutlinedTextField(
                        value = typed,
                        onValueChange = { typed = it },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(),
                    )
                    ChipRow(
                        label = "Size",
                        options = TEXT_SIZES,
                        selected = size,
                        labelOf = { it.toInt().toString() },
                        onSelect = { size = it },
                    )
                    ChipRow(
                        label = "Font",
                        options = com.megapdf.engine.STANDARD_FONTS,
                        selected = face,
                        labelOf = ::fontLabel,
                        onSelect = { face = it },
                    )
                }
            },
            confirmButton = {
                TextButton(
                    onClick = { onCommitText(typed, size, face) },
                    enabled = typed.isNotBlank(),
                ) {
                    Text(if (editing) "Save" else "Add")
                }
            },
            dismissButton = {
                TextButton(onClick = onCancelTextPlacement) { Text("Cancel") }
            },
        )
    }

    if (drawDialogOpen) {
        DrawSignatureDialog(
            onSave = onSaveDrawnSignature,
            onDismiss = { drawDialogOpen = false; signDialogOpen = true },
            screenshotMode = screenshotSheet == "draw",
        )
    }

    if (confirmDiscard) {
        AlertDialog(
            onDismissRequest = { confirmDiscard = false },
            title = { Text("Unsaved changes") },
            text = { Text("This document has unsaved changes.") },
            confirmButton = {
                TextButton(onClick = { confirmDiscard = false; onSave() }) { Text("Save") }
            },
            dismissButton = {
                TextButton(onClick = { confirmDiscard = false; onClose() }) { Text("Discard") }
            },
        )
    }

    Scaffold(
        topBar = {
            if (searchOpen) {
                SearchTopBar(
                    query = searchQuery,
                    hitCount = searchHits.size,
                    currentHitIndex = currentHitIndex,
                    isSearching = isSearching,
                    onQueryChange = onSearchQueryChange,
                    onPrevious = onSearchPrevious,
                    onNext = onSearchNext,
                    onClose = closeSearch,
                    screenshotMode = screenshotSheet == "search",
                )
            } else {
                TopAppBar(
                    title = { Text((if (isDirty) "• " else "") + displayName, maxLines = 1) },
                    navigationIcon = {
                        IconButton(onClick = requestClose) {
                            Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Close document")
                        }
                    },
                    actions = {
                        IconButton(onClick = onUndo, enabled = canUndo) {
                            Icon(UndoIcon, contentDescription = "Undo")
                        }
                        IconButton(onClick = { searchOpen = true }) {
                            Icon(Icons.Filled.Search, contentDescription = "Search")
                        }
                        TextButton(onClick = { signDialogOpen = true }) { Text("Sign") }
                        TextButton(onClick = onStartTextPlacement) { Text("Text") }
                        TextButton(onClick = onSave, enabled = isDirty && !isSaving) {
                            Text(if (isSaving) "Saving…" else "Save")
                        }
                        IconButton(onClick = { menuOpen = true }) {
                            Icon(Icons.Filled.MoreVert, contentDescription = "More options")
                        }
                        DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                            DropdownMenuItem(
                                text = { Text("Save a copy") },
                                enabled = !isSaving,
                                onClick = { menuOpen = false; onSaveAs() },
                            )
                            DropdownMenuItem(
                                text = { Text("Redo") },
                                enabled = canRedo,
                                onClick = { menuOpen = false; onRedo() },
                            )
                        }
                    },
                )
            }
        },
    ) { padding ->
        BoxWithConstraints(
            Modifier
                .fillMaxSize()
                .padding(padding)
                .background(Color(0xFF404040))
                // Pinch zoom: only multi-touch is consumed, so single-finger
                // vertical scrolling still belongs to the LazyColumn.
                .pointerInput(Unit) {
                    awaitEachGesture {
                        awaitFirstDown(requireUnconsumed = false)
                        do {
                            val event = awaitPointerEvent()
                            if (event.changes.size >= 2) {
                                val change = event.calculateZoom()
                                if (change != 1f) {
                                    zoom = (zoom * change).coerceIn(MIN_ZOOM, MAX_ZOOM)
                                    event.changes.forEach { it.consume() }
                                }
                            }
                        } while (event.changes.any { it.pressed })
                    }
                }
                ,
        ) {
            val density = LocalDensity.current
            val containerWidthPx = with(density) { maxWidth.toPx() }
            val pageWidthDp = maxWidth * zoom

            // Re-render the visible ±2 window whenever scroll position or zoom
            // settles; debounce keeps pinch gestures from spamming the engine.
            LaunchedEffect(pageSizes) {
                snapshotFlow {
                    val info = listState.layoutInfo.visibleItemsInfo
                    Triple(
                        info.firstOrNull()?.index ?: 0,
                        info.lastOrNull()?.index ?: 0,
                        (containerWidthPx * zoom).toInt(),
                    )
                }
                    .distinctUntilChanged()
                    .debounce(200)
                    .collect { (first, last, widthPx) ->
                        onRenderWindowChange(first, last, widthPx)
                    }
            }

            // Bring the current hit itself into view, not merely its page (#28).
            // Zoomed in, a page can be several screens tall and wider than the
            // display, so "the page is visible" says nothing about whether the match
            // is: scrolling only when the page was entirely off screen left hits
            // sitting below the fold, or off to the side, with the view never moving.
            LaunchedEffect(currentHitIndex, searchHits, zoom, pageSizes) {
                val hit = searchHits.getOrNull(currentHitIndex) ?: return@LaunchedEffect
                val page = pageSizes.getOrNull(hit.pageIndex) ?: return@LaunchedEffect
                val rect = hit.rects.firstOrNull() ?: return@LaunchedEffect

                val pageWidthPx = containerWidthPx * zoom
                val pageHeightPx = pageWidthPx * (page.heightPoints / page.widthPoints).toFloat()
                // Page points are bottom-left origin; the overlay flips them the same way.
                val hitTopPx = ((page.heightPoints - rect.top) / page.heightPoints).toFloat() * pageHeightPx
                val hitHeightPx = ((rect.top - rect.bottom) / page.heightPoints).toFloat() * pageHeightPx
                val viewportHeightPx = listState.layoutInfo.viewportSize.height.toFloat()
                val margin = viewportHeightPx * 0.15f

                // Scroll vertically unless the hit already sits comfortably on screen.
                val itemOffset = listState.layoutInfo.visibleItemsInfo
                    .firstOrNull { it.index == hit.pageIndex }?.offset?.toFloat()
                val hitY = itemOffset?.plus(hitTopPx)
                if (hitY == null || hitY < margin || hitY + hitHeightPx > viewportHeightPx - margin) {
                    listState.animateScrollToItem(
                        hit.pageIndex,
                        (hitTopPx - margin).toInt().coerceAtLeast(0),
                    )
                }

                // And horizontally, which nothing did before: when zoom > 1 the page is
                // wider than the display and the match can be entirely off to one side.
                if (zoom > 1f && hScroll.maxValue > 0) {
                    val hitCentreX = ((rect.left + rect.right) / 2.0 / page.widthPoints).toFloat() * pageWidthPx
                    val target = (hitCentreX - containerWidthPx / 2f)
                        .toInt()
                        .coerceIn(0, hScroll.maxValue)
                    if (kotlin.math.abs(target - hScroll.value) > containerWidthPx * 0.1f) {
                        hScroll.animateScrollTo(target)
                    }
                }
            }

            LazyColumn(
                state = listState,
                modifier = Modifier
                    .fillMaxSize()
                    .horizontalScroll(hScroll, enabled = zoom > 1f),
                horizontalAlignment = Alignment.CenterHorizontally,
                // Centred, not top-packed (#48): the 0xFF404040 behind the list is
                // the document surround every PDF viewer draws so you can see where
                // the page ends. Top-packed and full-bleed, it could only ever show
                // below the last page — a third of the viewport in one dead block on
                // a one-page document. For a LazyColumn the arrangement applies only
                // while the content is shorter than the viewport, so a multi-page
                // document still packs from the top and scrolls unchanged.
                verticalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterVertically),
            ) {
                items(pageSizes.size, key = { it }) { index ->
                    val size = pageSizes[index]
                    val bitmap = pageBitmaps[index]
                    // onTap defers ~300ms when onDoubleTap is present — that's the
                    // built-in disambiguation between check-a-box and zoom.
                    val pageModifier = Modifier
                        .width(pageWidthDp)
                        .aspectRatio((size.widthPoints / size.heightPoints).toFloat())
                        .background(Color.White)
                        .pointerInput(index) {
                            detectTapGestures(
                                onTap = { offset ->
                                    onPageTap(
                                        index,
                                        offset.x / this.size.width,
                                        offset.y / this.size.height,
                                    )
                                },
                                onDoubleTap = { zoom = if (zoom < 1.5f) 2f else 1f },
                            )
                        }
                    androidx.compose.foundation.layout.Box(pageModifier) {
                        if (bitmap != null) {
                            Image(
                                bitmap = bitmap.asImageBitmap(),
                                contentDescription = "Page ${index + 1}",
                                modifier = Modifier.fillMaxSize(),
                                contentScale = ContentScale.Fit,
                            )
                        }
                        val pageHits = searchHits.withIndex()
                            .filter { it.value.pageIndex == index }
                        if (pageHits.isNotEmpty()) {
                            SearchHighlightOverlay(
                                hits = pageHits,
                                currentHitIndex = currentHitIndex,
                                pageSize = size,
                            )
                        }
                        if (selectedStamp != null && selectedStamp.pageIndex == index) {
                            SelectionOverlay(
                                key = selectedStamp,
                                rect = selectedStamp.rect,
                                pageSize = size,
                                onCommit = onCommitStampRect,
                                onRemove = onRemoveStamp,
                            )
                        }
                        if (selectedTextBox != null && selectedTextBox.pageIndex == index) {
                            SelectionOverlay(
                                key = selectedTextBox,
                                rect = selectedTextBox.rect,
                                pageSize = size,
                                // No resize handle: resizing text means changing its
                                // font size, and SDD §3.1 keeps formatting out (#36).
                                resizable = false,
                                onCommit = onCommitTextBoxRect,
                                onRemove = onRemoveTextBox,
                                onEdit = onEditTextBox,
                            )
                        }
                    }
                }
            }
        }
    }
}

/**
 * Search-mode top bar (#26): as-you-type query field, "N of M" match count
 * ("No results" when the sweep comes back empty), previous/next chevrons
 * (wrapping around document ends), and close, which clears all highlights.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SearchTopBar(
    query: String,
    hitCount: Int,
    currentHitIndex: Int,
    isSearching: Boolean,
    onQueryChange: (String) -> Unit,
    onPrevious: () -> Unit,
    onNext: () -> Unit,
    onClose: () -> Unit,
    screenshotMode: Boolean = false,
) {
    val focusRequester = remember { FocusRequester() }
    TopAppBar(
        title = {
            TextField(
                value = query,
                onValueChange = onQueryChange,
                placeholder = { Text("Search") },
                singleLine = true,
                modifier = Modifier
                    .fillMaxWidth()
                    .focusRequester(focusRequester),
                colors = TextFieldDefaults.colors(
                    focusedContainerColor = Color.Transparent,
                    unfocusedContainerColor = Color.Transparent,
                ),
            )
        },
        navigationIcon = {
            IconButton(onClick = onClose) {
                Icon(Icons.Filled.Close, contentDescription = "Close search")
            }
        },
        actions = {
            Text(
                when {
                    query.isEmpty() || isSearching -> ""
                    hitCount == 0 -> "No results"
                    else -> "${currentHitIndex + 1} of $hitCount"
                },
                maxLines = 1,
            )
            IconButton(onClick = onPrevious, enabled = hitCount > 0) {
                Icon(Icons.Filled.KeyboardArrowUp, contentDescription = "Previous match")
            }
            IconButton(onClick = onNext, enabled = hitCount > 0) {
                Icon(Icons.Filled.KeyboardArrowDown, contentDescription = "Next match")
            }
        },
    )
    // Screenshot mode skips the focus grab: whether the soft keyboard would
    // then cover the page depends on the emulator's hw.keyboard setting, and a
    // marketing capture may not depend on that.
    LaunchedEffect(Unit) { if (!screenshotMode) focusRequester.requestFocus() }
}

/**
 * Translucent fills over every search hit on this page; the current hit gets
 * the distinct color. Hit rects are PDF points (bottom-left origin) mapped
 * into the page box's pixel space, same transform as [SelectionOverlay].
 */
@Composable
private fun SearchHighlightOverlay(
    hits: List<IndexedValue<SearchHit>>,
    currentHitIndex: Int,
    pageSize: PageSize,
) {
    androidx.compose.foundation.Canvas(Modifier.fillMaxSize()) {
        val sx = size.width / pageSize.widthPoints.toFloat()
        val sy = size.height / pageSize.heightPoints.toFloat()
        for ((hitIndex, hit) in hits) {
            val color =
                if (hitIndex == currentHitIndex) CURRENT_MATCH_HIGHLIGHT else MATCH_HIGHLIGHT
            for (rect in hit.rects) {
                drawRect(
                    color = color,
                    topLeft = androidx.compose.ui.geometry.Offset(
                        (rect.left * sx).toFloat(),
                        ((pageSize.heightPoints - rect.top) * sy).toFloat(),
                    ),
                    size = androidx.compose.ui.geometry.Size(
                        ((rect.right - rect.left) * sx).toFloat(),
                        ((rect.top - rect.bottom) * sy).toFloat(),
                    ),
                )
            }
        }
    }
}

@Composable
private fun SignatureDialog(
    signatures: List<SignatureEntry>,
    onPick: (SignatureEntry) -> Unit,
    onAdd: () -> Unit,
    onDraw: () -> Unit,
    onDelete: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Signatures") },
        text = {
            androidx.compose.foundation.layout.Column {
                if (signatures.isEmpty()) {
                    Text("No signatures yet. Draw one with your finger, or add a photo of your signature on white paper — the background is removed automatically.")
                } else {
                    Text("Tap a signature, then tap the page where it should go.")
                    LazyColumn(
                        modifier = Modifier.heightIn(max = 280.dp),
                    ) {
                        items(signatures.size, key = { signatures[it].id }) { i ->
                            val entry = signatures[i]
                            androidx.compose.foundation.layout.Row(
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                TextButton(
                                    onClick = { onPick(entry) },
                                    modifier = Modifier.weight(1f),
                                ) { Text(entry.displayName) }
                                TextButton(onClick = { onDelete(entry.id) }) { Text("Delete") }
                            }
                        }
                    }
                }
            }
        },
        confirmButton = {
            androidx.compose.foundation.layout.Row {
                TextButton(onClick = onDraw) { Text("Draw") }
                TextButton(onClick = onAdd) { Text("Add from Photos") }
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Close") } },
    )
}

/**
 * What a base-14 face is called in the UI. The PDF names are exact and must not
 * change (SDD §6.2 contract 4); these are only what the chips say.
 */
private fun fontLabel(fontName: String): String = when (fontName) {
    "Times-Roman" -> "Times"
    else -> fontName
}

/**
 * One labelled row of single-choice chips — the size and face pickers (#43).
 * Horizontally scrollable so a narrow phone never clips the last option.
 */
@Composable
private fun <T> ChipRow(
    label: String,
    options: List<T>,
    selected: T,
    labelOf: (T) -> String,
    onSelect: (T) -> Unit,
) {
    Column {
        Text(label, style = MaterialTheme.typography.labelMedium)
        Row(
            horizontalArrangement = Arrangement.spacedBy(6.dp),
            modifier = Modifier.horizontalScroll(rememberScrollState()),
        ) {
            options.forEach { option ->
                FilterChip(
                    selected = option == selected,
                    onClick = { onSelect(option) },
                    label = { Text(labelOf(option)) },
                )
            }
        }
    }
}

/**
 * Selection chrome for something the user placed on the page: drag to move,
 * corner handle to resize (aspect locked), X to remove. Changes commit to the
 * engine on drag end; the page bitmap refreshes after each commit.
 *
 * Signatures and text boxes share it (#36) rather than growing a second
 * interaction model — [resizable] and [onEdit] are the only differences between
 * them. [key] is whatever identifies the current selection; the in-progress drag
 * resets whenever it changes.
 */
@Composable
private fun SelectionOverlay(
    key: Any,
    rect: com.megapdf.engine.PdfRect,
    pageSize: PageSize,
    onCommit: (com.megapdf.engine.PdfRect) -> Unit,
    onRemove: () -> Unit,
    resizable: Boolean = true,
    onEdit: (() -> Unit)? = null,
) {
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val density = LocalDensity.current
        val pxWidth = constraints.maxWidth.toFloat()
        val pxHeight = constraints.maxHeight.toFloat()
        val sx = pxWidth / pageSize.widthPoints.toFloat()
        val sy = pxHeight / pageSize.heightPoints.toFloat()

        var drag by remember(key) { mutableStateOf(androidx.compose.ui.geometry.Offset.Zero) }
        var widthDelta by remember(key) { mutableFloatStateOf(0f) }

        val baseX = (rect.left * sx).toFloat()
        val baseY = ((pageSize.heightPoints - rect.top) * sy).toFloat()
        val baseW = ((rect.right - rect.left) * sx).toFloat()
        val baseH = ((rect.top - rect.bottom) * sy).toFloat()
        // widthDelta only ever moves when the resize handle exists, so a
        // non-resizable selection commits at scale 1 — a pure translation.
        val scale = ((baseW + widthDelta) / baseW).coerceAtLeast(0.15f)

        fun commit() {
            val dxPt = drag.x / sx
            val dyPt = drag.y / sy
            val newLeft = rect.left + dxPt
            val newTop = rect.top - dyPt
            val newW = (rect.right - rect.left) * scale
            val newH = (rect.top - rect.bottom) * scale
            onCommit(com.megapdf.engine.PdfRect(newLeft, newTop - newH, newLeft + newW, newTop))
        }

        androidx.compose.foundation.layout.Box(
            Modifier
                .offset {
                    androidx.compose.ui.unit.IntOffset(
                        (baseX + drag.x).roundToInt(),
                        (baseY + drag.y).roundToInt(),
                    )
                }
                .size(
                    with(density) { (baseW * scale).toDp() },
                    with(density) { (baseH * scale).toDp() },
                )
                .border(2.dp, Color(0xFF1E88E5))
                .pointerInput(key) {
                    detectDragGestures(
                        onDrag = { change, delta -> change.consume(); drag += delta },
                        onDragEnd = { commit() },
                    )
                },
        ) {
            Text(
                "✕",
                color = Color.White,
                modifier = Modifier
                    .align(Alignment.TopEnd)
                    .background(Color(0xFFD32F2F))
                    .padding(horizontal = 6.dp, vertical = 2.dp)
                    .clickable { onRemove() },
            )
            if (onEdit != null) {
                Text(
                    "✎",
                    color = Color.White,
                    modifier = Modifier
                        .align(Alignment.TopStart)
                        .background(Color(0xFF1E88E5))
                        .padding(horizontal = 6.dp, vertical = 2.dp)
                        .clickable { onEdit() },
                )
            }
            if (resizable) {
                androidx.compose.foundation.layout.Box(
                    Modifier
                        .align(Alignment.BottomEnd)
                        .size(18.dp)
                        .background(Color(0xFF1E88E5))
                        .pointerInput(key) {
                            detectDragGestures(
                                onDrag = { change, delta ->
                                    change.consume(); widthDelta += delta.x
                                },
                                onDragEnd = { commit() },
                            )
                        },
                )
            }
        }
    }
}
