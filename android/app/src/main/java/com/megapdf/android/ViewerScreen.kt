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
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
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
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.MoreVert
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.flow.distinctUntilChanged

private const val MIN_ZOOM = 1f
private const val MAX_ZOOM = 4f

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
    onRenderWindowChange: (firstVisible: Int, lastVisible: Int, targetWidthPx: Int) -> Unit,
    onPageTap: (pageIndex: Int, xFraction: Float, yFraction: Float) -> Unit,
    onStartPlacement: (SignatureEntry) -> Unit,
    onAddSignature: () -> Unit,
    onDeleteSignature: (String) -> Unit,
    onCommitStampRect: (com.megapdf.engine.PdfRect) -> Unit,
    onRemoveStamp: () -> Unit,
    onSave: () -> Unit,
    onSaveAs: () -> Unit,
    onClose: () -> Unit,
) {
    var zoom by remember { mutableFloatStateOf(1f) }
    val listState = rememberLazyListState()
    var menuOpen by remember { mutableStateOf(false) }
    var confirmDiscard by remember { mutableStateOf(false) }
    var signDialogOpen by remember { mutableStateOf(false) }
    val requestClose = { if (isDirty) confirmDiscard = true else onClose() }
    androidx.activity.compose.BackHandler { requestClose() }

    if (signDialogOpen) {
        SignatureDialog(
            signatures = signatures,
            onPick = { signDialogOpen = false; onStartPlacement(it) },
            onAdd = onAddSignature,
            onDelete = onDeleteSignature,
            onDismiss = { signDialogOpen = false },
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
            TopAppBar(
                title = { Text((if (isDirty) "• " else "") + displayName, maxLines = 1) },
                navigationIcon = {
                    IconButton(onClick = requestClose) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Close document")
                    }
                },
                actions = {
                    TextButton(onClick = { signDialogOpen = true }) { Text("Sign") }
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
                    }
                },
            )
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

            LazyColumn(
                state = listState,
                modifier = Modifier
                    .fillMaxSize()
                    .horizontalScroll(rememberScrollState(), enabled = zoom > 1f),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(8.dp),
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
                        if (selectedStamp != null && selectedStamp.pageIndex == index) {
                            StampSelectionOverlay(
                                stamp = selectedStamp,
                                pageSize = size,
                                onCommit = onCommitStampRect,
                                onRemove = onRemoveStamp,
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun SignatureDialog(
    signatures: List<SignatureEntry>,
    onPick: (SignatureEntry) -> Unit,
    onAdd: () -> Unit,
    onDelete: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Signatures") },
        text = {
            androidx.compose.foundation.layout.Column {
                if (signatures.isEmpty()) {
                    Text("No signatures yet. Add a photo of your signature on white paper — the background is removed automatically.")
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
        confirmButton = { TextButton(onClick = onAdd) { Text("Add from Photos") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Close") } },
    )
}

/**
 * Selection chrome for a placed signature: drag to move, corner handle to
 * resize (aspect locked), X to remove. Changes commit to the engine on
 * drag end; the page bitmap refreshes after each commit.
 */
@Composable
private fun StampSelectionOverlay(
    stamp: SelectedStamp,
    pageSize: PageSize,
    onCommit: (com.megapdf.engine.PdfRect) -> Unit,
    onRemove: () -> Unit,
) {
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val density = LocalDensity.current
        val pxWidth = constraints.maxWidth.toFloat()
        val pxHeight = constraints.maxHeight.toFloat()
        val sx = pxWidth / pageSize.widthPoints.toFloat()
        val sy = pxHeight / pageSize.heightPoints.toFloat()

        var drag by remember(stamp) { mutableStateOf(androidx.compose.ui.geometry.Offset.Zero) }
        var widthDelta by remember(stamp) { mutableFloatStateOf(0f) }

        val rect = stamp.rect
        val baseX = (rect.left * sx).toFloat()
        val baseY = ((pageSize.heightPoints - rect.top) * sy).toFloat()
        val baseW = ((rect.right - rect.left) * sx).toFloat()
        val baseH = ((rect.top - rect.bottom) * sy).toFloat()
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
                .pointerInput(stamp) {
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
            androidx.compose.foundation.layout.Box(
                Modifier
                    .align(Alignment.BottomEnd)
                    .size(18.dp)
                    .background(Color(0xFF1E88E5))
                    .pointerInput(stamp) {
                        detectDragGestures(
                            onDrag = { change, delta -> change.consume(); widthDelta += delta.x },
                            onDragEnd = { commit() },
                        )
                    },
            )
        }
    }
}
