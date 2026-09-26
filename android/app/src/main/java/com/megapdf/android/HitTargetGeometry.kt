package com.megapdf.android

/**
 * Where a bigger, invisible touch host sits so that it is centred on the smaller
 * control it carries the tap for (#347).
 *
 * The selected mark's ✕ chip draws at about 26 x 22 dp, and its `clickable` used
 * to cover only the glyph inside the padding. Compose then *reported* a 48 dp
 * touch box for it (the minimum-touch-target expansion in semantics) but only
 * honoured that expansion when nothing else was hit directly — and the page
 * underneath always is, so a tap in the reported box fell through and edited
 * the text line under a thin mark. The fix is the one Windows made for its chip
 * (8e2515b): the tap lands on a host at least [target] square, centred on the
 * chip, and the chip itself neither moves nor grows.
 *
 * Pure arithmetic, pinned by a JVM test, so the geometry does not depend on an
 * emulator to be believed.
 */
object HitTargetGeometry {
    /**
     * A host [width] x [height] placed at ([x], [y]) relative to the top-left of
     * the control it centres on. A negative offset reaches outside the control.
     */
    data class Host(val width: Int, val height: Int, val x: Int, val y: Int)

    /**
     * The host for a control measuring [contentWidth] x [contentHeight] px, at
     * least [target] px on each side; a side already at or over the target is
     * left as it is, so the host is never smaller than the control.
     */
    fun centredHost(contentWidth: Int, contentHeight: Int, target: Int): Host =
        centredHost(contentWidth, contentHeight, target, target)

    /** As above, with the target given per axis. */
    fun centredHost(contentWidth: Int, contentHeight: Int, targetWidth: Int, targetHeight: Int): Host {
        val width = maxOf(contentWidth, targetWidth)
        val height = maxOf(contentHeight, targetHeight)
        return Host(width, height, (contentWidth - width) / 2, (contentHeight - height) / 2)
    }

    /**
     * The widest host two chips at opposite ends of one edge can each have without
     * either host reaching the other chip: a text box narrow enough that ✎ and ✕
     * nearly touch keeps each tap on the chip it lands on, and only a box wider than
     * [contentWidth] + [target] gives both the full [target].
     */
    fun sharedHostWidth(boxWidth: Int, contentWidth: Int, target: Int): Int =
        minOf(target, maxOf(contentWidth, boxWidth - contentWidth))
}
