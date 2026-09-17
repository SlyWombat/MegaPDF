package com.megapdf.android

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.graphics.Color as ComposeColor

/**
 * Type-a-name signature (#101): the third way to sign, alongside Draw and From
 * photo, so a phone user without a stylus still gets a signature that looks
 * like one. The name is shown live in the device's cursive face on a white
 * card — what the placed stamp will look like — and rendered to a transparent
 * bitmap that goes through the same trim-and-store path as a drawn signature.
 */
@Composable
fun TypeSignatureDialog(
    onSave: (Bitmap) -> Unit,
    onDismiss: () -> Unit,
) {
    var name by remember { mutableStateOf("") }
    val previewHint = stringResource(R.string.type_signature_preview_hint)

    AlertDialog(
        modifier = Modifier.imePadding(),
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.type_signature_title)) },
        text = {
            DialogBody(spacing = 16) {
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it },
                    singleLine = true,
                    label = { Text(stringResource(R.string.signature_name)) },
                    modifier = Modifier.fillMaxWidth(),
                )
                // White on purpose in both themes: ink on paper is what gets placed.
                Surface(
                    color = ComposeColor.White,
                    shape = MaterialTheme.shapes.medium,
                    modifier = Modifier.fillMaxWidth().height(96.dp),
                ) {
                    Box(contentAlignment = Alignment.Center, modifier = Modifier.padding(horizontal = 16.dp)) {
                        Text(
                            text = name.ifBlank { previewHint },
                            style = TextStyle(fontFamily = FontFamily.Cursive, fontSize = 36.sp),
                            color = if (name.isBlank()) ComposeColor.Gray else InkColor,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }
            }
        },
        confirmButton = {
            TextButton(
                enabled = name.isNotBlank(),
                onClick = { onSave(renderTypedSignature(name.trim())) },
            ) { Text(stringResource(R.string.type_signature_add)) }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) }
        },
    )
}

private val InkColor = ComposeColor(0xFF202020)

/**
 * The typed name as ink on a transparent bitmap, large enough that the stamp
 * stays sharp when placed: 160 px glyphs with a 32 px margin, which trimToInk
 * then tightens to the ink. Same face Compose's FontFamily.Cursive resolves to,
 * so the preview and the result match.
 */
internal fun renderTypedSignature(text: String): Bitmap {
    val paint = Paint(Paint.ANTI_ALIAS_FLAG or Paint.SUBPIXEL_TEXT_FLAG).apply {
        typeface = Typeface.create("cursive", Typeface.NORMAL)
        textSize = 160f
        color = Color.rgb(0x20, 0x20, 0x20)
    }
    val metrics = paint.fontMetrics
    val margin = 32f
    val width = (paint.measureText(text) + margin * 2).toInt().coerceAtLeast(1)
    val height = (metrics.descent - metrics.ascent + margin * 2).toInt().coerceAtLeast(1)
    val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
    Canvas(bitmap).drawText(text, margin, margin - metrics.ascent, paint)
    return bitmap
}
