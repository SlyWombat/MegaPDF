package com.megapdf.android

import android.graphics.Bitmap
import android.graphics.Paint
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.padding
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp

private const val INK_COLOR = 0xFF1A1A1A

/**
 * Finger/stylus signature capture (#16). Strokes render onto a transparent
 * bitmap; the caller trims it to the ink bounding box (white-removal is
 * skipped — the background is already transparent).
 */
@Composable
fun DrawSignatureDialog(
    onSave: (Bitmap) -> Unit,
    onDismiss: () -> Unit,
    screenshotMode: Boolean = false,
) {
    val strokes = remember { mutableStateListOf<List<Offset>>() }
    var current by remember { mutableStateOf<List<Offset>>(emptyList()) }
    var canvasSize by remember { mutableStateOf(IntSize.Zero) }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Draw your signature") },
        text = {
            val context = LocalContext.current
            val demoSig = remember(screenshotMode) {
                if (screenshotMode) runCatching {
                    context.assets.open("demo-signature.png").use {
                        android.graphics.BitmapFactory.decodeStream(it)
                    }
                }.getOrNull() else null
            }
            Box {
            Canvas(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(180.dp)
                    .background(Color(0xFFF6F6F6))
                    .onSizeChanged { canvasSize = it }
                    .pointerInput(Unit) {
                        detectDragGestures(
                            onDragStart = { start -> current = listOf(start) },
                            onDrag = { change, _ ->
                                change.consume()
                                current = current + change.position
                            },
                            onDragEnd = {
                                if (current.size > 1) strokes.add(current)
                                current = emptyList()
                            },
                        )
                    },
            ) {
                val style = Stroke(
                    width = size.height / 36f,
                    cap = StrokeCap.Round,
                    join = StrokeJoin.Round,
                )
                (strokes + listOf(current)).forEach { points ->
                    if (points.size > 1) {
                        val path = Path().apply {
                            moveTo(points[0].x, points[0].y)
                            points.drop(1).forEach { lineTo(it.x, it.y) }
                        }
                        drawPath(path, Color(INK_COLOR), style = style)
                    }
                }
            }
            // Screenshot mode shows the demo signature as if just drawn.
            if (demoSig != null) {
                Image(
                    bitmap = demoSig.asImageBitmap(),
                    contentDescription = null,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(180.dp)
                        .align(Alignment.Center)
                        .padding(24.dp),
                )
            }
            }
        },
        confirmButton = {
            TextButton(
                enabled = (strokes.isNotEmpty() && canvasSize != IntSize.Zero) || screenshotMode,
                onClick = {
                    onSave(renderStrokes(strokes, canvasSize))
                    onDismiss()
                },
            ) { Text("Save") }
        },
        dismissButton = {
            Row {
                TextButton(onClick = { strokes.clear() }) { Text("Clear") }
                TextButton(onClick = onDismiss) { Text("Cancel") }
            }
        },
    )
}

private fun renderStrokes(strokes: List<List<Offset>>, size: IntSize): Bitmap {
    val bitmap = Bitmap.createBitmap(size.width, size.height, Bitmap.Config.ARGB_8888)
    val canvas = android.graphics.Canvas(bitmap)
    val paint = Paint().apply {
        color = INK_COLOR.toInt()
        style = Paint.Style.STROKE
        strokeWidth = size.height / 36f
        strokeCap = Paint.Cap.ROUND
        strokeJoin = Paint.Join.ROUND
        isAntiAlias = true
    }
    strokes.forEach { points ->
        if (points.size > 1) {
            val path = android.graphics.Path().apply {
                moveTo(points[0].x, points[0].y)
                points.drop(1).forEach { lineTo(it.x, it.y) }
            }
            canvas.drawPath(path, paint)
        }
    }
    return bitmap
}
