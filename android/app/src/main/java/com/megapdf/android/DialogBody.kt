package com.megapdf.android

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp

/**
 * The body of a dialog, which gives way when there is not enough room for it.
 *
 * Material gives an `AlertDialog`'s body `weight(1f, fill = false)`, so it yields
 * to the buttons — but only if it *can*. A plain Column of fixed-height children
 * cannot shrink, so instead the dialog grows past the space it has and the button
 * row goes off the bottom of the screen.
 *
 * That is not hypothetical. At the largest system text size on a 360 dp phone,
 * with the keyboard up (#146):
 *
 * - **Add text** put Cancel and Add below the keyboard;
 * - **Set a password** put the second field half under the keyboard and both
 *   buttons off the screen entirely — you could not finish protecting a document.
 *
 * Scrolling makes the body shrinkable, so the buttons stay put and the content
 * moves instead. Nothing changes when everything already fits.
 *
 * The signature pad is deliberately *not* wrapped in this: it is a fixed-height
 * drawing surface, and a vertical scroller around it would compete with the
 * strokes for the same drag.
 */
@Composable
fun DialogBody(
    modifier: Modifier = Modifier,
    spacing: Int = 8,
    content: @Composable ColumnScope.() -> Unit,
) {
    Column(
        modifier = modifier.verticalScroll(rememberScrollState()),
        verticalArrangement = Arrangement.spacedBy(spacing.dp),
        content = content,
    )
}
