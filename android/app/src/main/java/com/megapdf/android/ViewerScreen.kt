package com.megapdf.android

import android.graphics.Bitmap
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
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
    onRenderWindowChange: (firstVisible: Int, lastVisible: Int, targetWidthPx: Int) -> Unit,
    onPageTap: (pageIndex: Int, xFraction: Float, yFraction: Float) -> Unit,
    onSave: () -> Unit,
    onSaveAs: () -> Unit,
    onClose: () -> Unit,
) {
    var zoom by remember { mutableFloatStateOf(1f) }
    val listState = rememberLazyListState()
    var menuOpen by remember { mutableStateOf(false) }
    var confirmDiscard by remember { mutableStateOf(false) }
    val requestClose = { if (isDirty) confirmDiscard = true else onClose() }
    androidx.activity.compose.BackHandler { requestClose() }

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
                    if (bitmap != null) {
                        Image(
                            bitmap = bitmap.asImageBitmap(),
                            contentDescription = "Page ${index + 1}",
                            modifier = pageModifier,
                            contentScale = ContentScale.Fit,
                        )
                    } else {
                        // Placeholder keeps layout stable until the sharp render lands.
                        androidx.compose.foundation.layout.Box(pageModifier)
                    }
                }
            }
        }
    }
}
