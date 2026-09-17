package com.megapdf.android

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.addPathNodes
import androidx.compose.ui.unit.dp

/**
 * The viewer toolbar's glyphs (#144). `material-icons-core` carries none of them
 * and one screen's worth is not worth pulling in `material-icons-extended`
 * (SDD §4.5 keeps the footprint small), so each is built here from its standard
 * 24dp Material Symbols path. Tinting comes from the Icon that draws them.
 */
internal object ToolbarIcons {
    /** Material "undo". */
    val Undo: ImageVector = icon(
        "Undo",
        "M12.5,8c-2.65,0 -5.05,0.99 -6.9,2.6L2,7v9h9l-3.62,-3.62c1.39,-1.16 3.16,-1.88 " +
            "5.12,-1.88c3.54,0 6.55,2.31 7.6,5.5l2.37,-0.78C21.08,11.03 17.15,8 12.5,8z",
    )

    /**
     * Redact (#173): the block Whiteout draws, with the lines it removes struck out of it.
     * Cover is a blank block; Redact is a block with the content gone, which is the whole
     * difference between the two tools.
     */
    val Redact: ImageVector = icon(
        "Redact",
        "M3,5h18v14H3zM5,7v10h14V7zM6.5,9.5h11v1.2h-11zM6.5,12h6.5v1.2H6.5zM6.5,14.5h11v1.2h-11z",
    )

    /** Material "redo" — the undo arrow mirrored. */
    val Redo: ImageVector = icon(
        "Redo",
        "M18.4,10.6C16.55,8.99 14.15,8 11.5,8c-4.65,0 -8.58,3.03 -9.96,7.22L3.9,16c1.05,-3.19 " +
            "4.05,-5.5 7.6,-5.5c1.95,0 3.73,0.72 5.12,1.88L13,16h9V7L18.4,10.6z",
    )

    /** Material "gesture": a hand-drawn stroke, the usual sign glyph. */
    val Sign: ImageVector = icon(
        "Sign",
        "M4.59,6.89c0.7,-0.71 1.4,-1.35 1.71,-1.22c0.5,0.2 0,1.03 -0.3,1.52c-0.25,0.42 -2.86,3.89 " +
            "-2.86,6.31c0,1.28 0.48,2.34 1.34,2.98c0.75,0.56 1.74,0.73 2.64,0.46c1.07,-0.31 1.95,-1.4 " +
            "3.06,-2.77c1.21,-1.49 2.83,-3.44 4.08,-3.44c1.63,0 1.65,1.01 1.76,1.79c-3.78,0.64 -5.38,3.67 " +
            "-5.38,5.37c0,1.7 1.44,3.09 3.21,3.09c1.63,0 4.29,-1.33 4.69,-6.1H21v-2.5h-2.47c-0.15,-1.65 " +
            "-1.09,-4.2 -4.03,-4.2c-2.25,0 -4.18,1.91 -4.94,2.84c-0.58,0.73 -2.06,2.48 -2.29,2.72c-0.25,0.3 " +
            "-0.68,0.84 -1.11,0.84c-0.45,0 -0.72,-0.83 -0.36,-1.92c0.35,-1.09 1.4,-2.86 1.85,-3.52c0.78,-1.14 " +
            "1.3,-1.92 1.3,-3.28C8.95,3.69 7.31,3 6.44,3C5.12,3 3.97,4 3.72,4.25c-0.36,0.36 -0.66,0.66 " +
            "-0.88,0.93l1.75,1.71zM13.88,18.55c-0.31,0 -0.74,-0.26 -0.74,-0.72c0,-0.6 0.73,-2.2 " +
            "2.87,-2.76c-0.3,2.69 -1.43,3.48 -2.13,3.48z",
    )

    /** Material "text_fields": a large and a small T, the usual add-text glyph. */
    val AddText: ImageVector = icon(
        "AddText",
        "M2.5,4v3h5v12h3V7h5V4H2.5zM21.5,9h-9v3h3v7h3v-7h3V9z",
    )

    private fun icon(name: String, path: String): ImageVector =
        ImageVector.Builder(
            name = "MegaPdf.$name",
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f,
        ).addPath(pathData = addPathNodes(path), fill = SolidColor(Color.Black)).build()
}
