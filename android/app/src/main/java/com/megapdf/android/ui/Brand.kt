package com.megapdf.android.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/**
 * The MegaPDF design tokens, Android leg. `docs/design-tokens.md` is the spec;
 * this file is the only place in the app that names a colour.
 *
 * There was no theme layer before this: no `colors.xml`, no `themes.xml`, and
 * every colour was an inline `Color(0xFF…)` literal in the screen that drew it,
 * mostly Material's own blue. The launcher icon was already on-brand while the
 * UI inside it was not.
 *
 * The one colour outside this file is `#202020`, the SDD §6.2 ink contract —
 * what the user's mark is drawn in. It is the same in every theme and in the
 * saved PDF, so it is not a token and it does not invert.
 */
object Brand {
    // §1.1 — the identity, from assets/branding/*.svg.
    val Blue = Color(0xFF0E6FD8)
    val Cyan = Color(0xFF18B6C8)
    val TileStart = Color(0xFF0A5BC4)
    val TileEnd = Color(0xFF0FA8C6)
    val Ink = Color(0xFF16324F)
    val InkOnDark = Color(0xFFF2F7FC)

    // §1.2 — what the screens actually reference.
    val Accent = Blue
    val AccentPressed = TileStart
    val AccentSubtle = Color(0x210E6FD8)
    val AccentOn = Color(0xFFFFFFFF)

    /**
     * Hue, not opacity, separates "one of forty hits" from "the hit you are on".
     * This replaces Material blue for hits and amber for the current one — amber
     * is not a colour the product owns, and two brand hues tell the states apart
     * without importing one.
     */
    val FindMatch = Color(0x4D18B6C8)
    val FindMatchCurrent = Color(0x730E6FD8)

    val Danger = Color(0xFFC0362C)

    /** The wall the page sits on. Neutral shade, not a brand hue. */
    val Backdrop = Color(0xFF404040)

    /**
     * The signature pad. Near-white by contract: the drawing is rasterised off
     * this surface and background-removed at luminance > 235 (SDD §6.2), so a
     * dark pad would survive the cleanup as a black rectangle.
     */
    val SignaturePad = Color(0xFFF6F6F6)
}

/**
 * Surfaces, not just the accent.
 *
 * Overriding `primary` alone leaves Material's baseline tonal palette in place
 * for everything else, and that palette is purple: the search bar came out
 * #FEF7FF and the signature dialog #ECE6F0, both measured from the Play
 * screenshot run, with brand-blue text sitting on them (#80). Two palettes on
 * one screen is the same defect the desktop apps had with their framework
 * accents.
 *
 * These neutrals are cool rather than true grey — pulled toward the brand ink
 * #16324F — so chrome sits under the blue rather than fighting it.
 */
private val LightColours = lightColorScheme(
    primary = Brand.Accent,
    onPrimary = Brand.AccentOn,
    primaryContainer = Color(0xFFD7E7FA),
    onPrimaryContainer = Brand.Ink,
    secondary = Brand.Cyan,
    onSecondary = Brand.AccentOn,
    error = Brand.Danger,
    onError = Brand.AccentOn,

    background = Color(0xFFFFFFFF),
    onBackground = Brand.Ink,
    surface = Color(0xFFFFFFFF),
    onSurface = Brand.Ink,
    surfaceVariant = Color(0xFFEEF2F6),
    onSurfaceVariant = Color(0xFF46586B),
    surfaceContainerLowest = Color(0xFFFFFFFF),
    surfaceContainerLow = Color(0xFFF8FAFC),
    surfaceContainer = Color(0xFFF4F7FA),
    surfaceContainerHigh = Color(0xFFEDF2F7),
    surfaceContainerHighest = Color(0xFFE7EEF4),
    outline = Color(0xFF7A8B9C),
    outlineVariant = Color(0xFFC9D6E2),
)

/**
 * Defined, but not wired up. The app is light-only on purpose — `MainActivity`
 * forces both system bars to the light style because a dark scheme would paint
 * white icons onto a white app (#40). Adding dark mode means revisiting that
 * decision, not just handing this scheme to [MegaPdfTheme].
 */
@Suppress("unused")
private val DarkColours = darkColorScheme(
    primary = Color(0xFF4F9BEA),
    onPrimary = Color(0xFF0B1B2B),
    secondary = Brand.Cyan,
    error = Color(0xFFE2685E),

    background = Color(0xFF0F1720),
    onBackground = Brand.InkOnDark,
    surface = Color(0xFF0F1720),
    onSurface = Brand.InkOnDark,
    surfaceVariant = Color(0xFF27333F),
    onSurfaceVariant = Color(0xFFB6C4D2),
    outline = Color(0xFF7A8B9C),
    outlineVariant = Color(0xFF3A4753),
)

/**
 * Wraps the app in the brand scheme. Light only, deliberately — see
 * [DarkColours] and the note in `MainActivity.onCreate`.
 */
@Composable
fun MegaPdfTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = LightColours, content = content)
}
