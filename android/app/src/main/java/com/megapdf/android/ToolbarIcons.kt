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

    // Reading mode's floating bar (#507, #513). `material-icons-core` has none of these
    // either, and the bar is icon-only for the reason the toolbar is: a phone's row has no
    // room for four words. Each carries its name as a content description, so TalkBack reads
    // a label rather than guessing at a glyph (#237).

    /** Two side rails with a double-headed arrow between them: the page across the screen. */
    val FitWidth: ImageVector = icon(
        "FitWidth",
        "M3,4h1.6v16H3zM19.4,4H21v16h-1.6zM8.9,8.5L5.4,12l3.5,3.5v-2.6h6.2v2.6L18.6,12l-3.5,-3.5v2.6H8.9z",
    )

    /** The same, turned: two rails top and bottom, the whole page between them. */
    val FitPage: ImageVector = icon(
        "FitPage",
        "M4,3h16v1.6H4zM4,19.4h16V21H4zM15.5,8.9L12,5.4L8.5,8.9h2.6v6.2H8.5L12,18.6l3.5,-3.5h-2.6V8.9z",
    )

    /** Material "add". */
    val ZoomIn: ImageVector = icon("ZoomIn", "M19,13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z")

    /** Material "remove". */
    val ZoomOut: ImageVector = icon("ZoomOut", "M19,13H5v-2h14v2z")

    // The page tools' own glyphs (#174). Material Symbols' rotate_left, rotate_right and
    // delete, built here like the rest: `material-icons-core` carries none of the three.

    /** Material "rotate_left". */
    val RotateLeft: ImageVector = icon(
        "RotateLeft",
        "M7.11,8.53L5.7,7.11C4.8,8.27 4.24,9.61 4.07,11h2.02C6.24,10.12 6.58,9.28 7.11,8.53z" +
            "M6.09,13H4.07c0.17,1.39 0.72,2.73 1.62,3.89l1.41,-1.42C6.58,14.73 6.23,13.88 6.09,13z" +
            "M7.1,18.32C8.26,19.22 9.61,19.76 11,19.93v-2.02c-0.87,-0.15 -1.71,-0.49 -2.46,-1.03L7.1,18.32z" +
            "M13,4.07V1L8.45,5.55L13,10V6.09c2.84,0.48 5,2.94 5,5.91s-2.16,5.43 -5,5.91v2.02" +
            "c3.95,-0.49 7,-3.85 7,-7.93S16.95,4.56 13,4.07z",
    )

    /** Material "rotate_right". */
    val RotateRight: ImageVector = icon(
        "RotateRight",
        "M15.55,5.55L11,1v3.07C7.06,4.56 4,7.92 4,12s3.05,7.44 7,7.93v-2.02c-2.84,-0.48 -5,-2.94 " +
            "-5,-5.91s2.16,-5.43 5,-5.91V10l4.55,-4.45zM19.93,11c-0.17,-1.39 -0.72,-2.73 -1.62,-3.89" +
            "l-1.42,1.42c0.54,0.75 0.88,1.6 1.02,2.47h2.02zM13,17.9v2.02c1.39,-0.17 2.74,-0.71 3.9,-1.61" +
            "l-1.44,-1.44c-0.75,0.54 -1.59,0.89 -2.46,1.03zM16.89,15.48l1.42,1.41c0.9,-1.16 1.45,-2.5 " +
            "1.62,-3.89h-2.02c-0.14,0.87 -0.48,1.72 -1.02,2.48z",
    )

    /** Material "delete": the wastebasket, for taking pages out. */
    val DeletePages: ImageVector = icon(
        "DeletePages",
        "M6,19c0,1.1 0.9,2 2,2h8c1.1,0 2,-0.9 2,-2V7H6v12zM19,4h-3.5l-1,-1h-5l-1,1H5v2h14V4z",
    )

    /** Material "settings": the cog, for the Home screen's entry to Settings (#513). */
    val Settings: ImageVector = icon(
        "Settings",
        "M19.14,12.94c0.04,-0.3 0.06,-0.61 0.06,-0.94c0,-0.32 -0.02,-0.64 -0.07,-0.94l2.03,-1.58" +
            "c0.18,-0.14 0.23,-0.41 0.12,-0.61l-1.92,-3.32c-0.12,-0.22 -0.37,-0.29 -0.59,-0.22" +
            "l-2.39,0.96c-0.5,-0.38 -1.03,-0.7 -1.62,-0.94L14.4,2.81c-0.04,-0.24 -0.24,-0.41 -0.48,-0.41" +
            "h-3.84c-0.24,0 -0.43,0.17 -0.47,0.41L9.25,5.35C8.66,5.59 8.12,5.92 7.63,6.29L5.24,5.33" +
            "c-0.22,-0.08 -0.47,0 -0.59,0.22L2.74,8.87C2.62,9.08 2.66,9.34 2.86,9.48l2.03,1.58" +
            "C4.84,11.36 4.8,11.69 4.8,12s0.02,0.64 0.07,0.94l-2.03,1.58c-0.18,0.14 -0.23,0.41 -0.12,0.61" +
            "l1.92,3.32c0.12,0.22 0.37,0.29 0.59,0.22l2.39,-0.96c0.5,0.38 1.03,0.7 1.62,0.94l0.36,2.54" +
            "c0.05,0.24 0.24,0.41 0.48,0.41h3.84c0.24,0 0.44,-0.17 0.47,-0.41l0.36,-2.54" +
            "c0.59,-0.24 1.13,-0.56 1.62,-0.94l2.39,0.96c0.22,0.08 0.47,0 0.59,-0.22l1.92,-3.32" +
            "c0.12,-0.22 0.07,-0.47 -0.12,-0.61L19.14,12.94zM12,15.6c-1.98,0 -3.6,-1.62 -3.6,-3.6" +
            "s1.62,-3.6 3.6,-3.6s3.6,1.62 3.6,3.6S13.98,15.6 12,15.6z",
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
