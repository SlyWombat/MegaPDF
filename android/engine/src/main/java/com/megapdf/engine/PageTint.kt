package com.megapdf.engine

/**
 * Page colours (#513, reading mode tier 2): how the page is *drawn*, never what is in it.
 *
 * The tint is a post-pass the core runs over the rendered pixels (contract 7,
 * `MEGAPDF_RENDER_SEPIA` / `MEGAPDF_RENDER_NIGHT`, #509) — nothing is written to the file,
 * and the same document saved from a night-mode session is byte-identical to one saved from
 * a normal one. Shared with every other platform precisely because it is render policy and
 * ADR-003 decision 4 keeps render policy in the core; #115 is what happened the last time
 * the phones decided a render question for themselves.
 *
 * One choice of three, not a set of bits a caller can combine: the core refuses both tint
 * bits at once, and this enum is what makes that unrepresentable on Android.
 */
enum class PageTint(
    /** The contract-7 flag bit this tint ORs onto the buffer's byte order. */
    internal val renderFlag: Int,
    /**
     * What this choice is called in `DataStore` (and in the desktops' `settings.json`, which
     * uses the same three words): the empty string for the default, so a preferences file
     * that has never been written reads back as [NORMAL].
     */
    val storedName: String,
) {
    /** The page as the document draws it. */
    NORMAL(0, ""),

    /** Warm paper: white becomes #F4ECD8, black stays black. */
    SEPIA(2, "Sepia"),

    /**
     * Inverted luminance with the hue kept: white becomes #1A1A1A, brand blue stays blue.
     * Pictures invert too — the decision recorded on #168, and what the settings copy says.
     */
    NIGHT(4, "Night");

    companion object {
        /**
         * The tint [stored] names, or [NORMAL] for anything this version does not know —
         * a preferences store written by a later version must not leave the page blank or
         * crash the viewer, it must simply show the page as drawn.
         */
        fun of(stored: String?): PageTint =
            entries.firstOrNull { it.storedName == stored } ?: NORMAL
    }
}
