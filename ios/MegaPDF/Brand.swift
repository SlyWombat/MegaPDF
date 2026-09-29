import SwiftUI

/// The MegaPDF design tokens, iOS leg. `docs/design-tokens.md` is the spec;
/// these names and `Assets.xcassets` are the only places in the app that decide
/// a colour.
///
/// Before this the asset catalog held nothing but the app icon, so every
/// `Color.accentColor` resolved to Apple's system blue — the app was wearing
/// iOS's colour rather than its own. `AccentColor.colorset` now exists and
/// `project.yml` points `ASSETCATALOG_COMPILER_GLOBAL_ACCENT_COLOR_NAME` at it,
/// which is what makes `.accentColor` and the default tint brand blue app-wide.
///
/// Each colorset carries an Any and a Dark appearance, so these follow the
/// system automatically and nothing here needs to branch on colour scheme.
enum Brand {
    static let accent = Color("AccentColor")
    static let accentPressed = Color("BrandAccentPressed")
    static let accentSubtle = Color("BrandAccentSubtle")
    static let accentOn = Color("BrandAccentOn")

    /// Hue, not opacity, separates "one of forty hits" from "the hit you are
    /// on": cyan for every match, brand blue for the current one. Both carry
    /// their own alpha, so callers do not add opacity on top.
    static let findMatch = Color("BrandFindMatch")
    static let findMatchCurrent = Color("BrandFindMatchCurrent")

    // A redaction mark (#173): translucent, so what is about to be removed can still be
    // read, over an outline in the same hue so the edge is unambiguous on a white page and
    // on a dark scan alike. Deliberately not the find hue and not the danger red: it is
    // neither a search result nor an error, and it becomes a black box.
    static let redactionMark = Color("BrandRedactionMark")
    static let redactionMarkOutline = Color("BrandRedactionMarkOutline")

    static let danger = Color("BrandDanger")

    /// The wall the page sits on. A neutral shade rather than a brand hue —
    /// anything with a cast in it tints the white paper in front of it.
    static let backdrop = Color(white: 0.25)
    static let ink = Color("BrandInk")

    /// The signature pad. Near-white by contract: the drawing is rasterised off
    /// this surface and background-removed at luminance > 235 (SDD §6.2), so a
    /// dark pad would survive the cleanup as a black rectangle. It is the one
    /// colorset here whose dark appearance is not a straight inversion.
    static let signaturePad = Color("BrandSignaturePad")

    /// The ink the user's marks are drawn in: `#202020`, an SDD §6.2 contract
    /// shared with the engine's check-mark stroke and the other three apps. Not
    /// a colorset, because it must not change with the appearance — it is ink on
    /// paper inside the document, and it ends up in the saved PDF.
    static let inkLevel: Double = Double(0x20) / 255.0

    /// Reading mode's page colours (#512, `docs/reading-mode-plan.md` §2 tier 2).
    ///
    /// Literal colours, not colorsets, and for the same reason `inkLevel` is a literal:
    /// these must not follow the system appearance. The page itself is tinted by the
    /// engine (`MEGAPDF_RENDER_SEPIA` / `_NIGHT`, #509), and the chrome around it — the
    /// gutter the pages sit on and the floating bar — has to match *that*, not whether
    /// iOS is in light or dark mode. A sepia page on a dark-mode phone is still a sepia
    /// page, and a gutter that followed the appearance would frame it in the wrong colour
    /// exactly half the time.
    ///
    /// The values are the Avalonia leg's, to the byte (`src/MegaPDF.Avalonia/Brand.axaml`,
    /// #505/#511), so the two platforms wear one palette: sepia's surface is the engine's
    /// own paper white (`#F4ECD8`, `core/megapdf_core.cpp` `kSepia*`) with the gutter a
    /// step darker so a page edge is still visible; night's surface is a step up from the
    /// engine's `#1A1A1A` page and its gutter a step below. Normal has no colours of its
    /// own — that is the app wearing its own `backdrop`, which is what these two replace.
    enum Reading {
        static let sepiaGutter = Color(red: 0xDC / 255, green: 0xD0 / 255, blue: 0xB4 / 255)
        static let sepiaSurface = Color(red: 0xF4 / 255, green: 0xEC / 255, blue: 0xD8 / 255)
        static let sepiaInk = Color(red: 0x2B / 255, green: 0x24 / 255, blue: 0x18 / 255)
        static let nightGutter = Color(red: 0x0E / 255, green: 0x0E / 255, blue: 0x0E / 255)
        static let nightSurface = Color(red: 0x26 / 255, green: 0x26 / 255, blue: 0x26 / 255)
        static let nightInk = Color(red: 0xE5 / 255, green: 0xE5 / 255, blue: 0xE5 / 255)

        /// The wall the pages sit on.
        static func gutter(_ tint: PageTint) -> Color {
            switch tint {
            case .normal: return Brand.backdrop
            case .sepia: return sepiaGutter
            case .night: return nightGutter
            }
        }

        /// The floating bar's own ground, or nil where it should stay the system material.
        static func surface(_ tint: PageTint) -> Color? {
            switch tint {
            case .normal: return nil
            case .sepia: return sepiaSurface
            case .night: return nightSurface
            }
        }

        /// The ink on that ground, chosen against the surface rather than the appearance.
        static func ink(_ tint: PageTint) -> Color? {
            switch tint {
            case .normal: return nil
            case .sepia: return sepiaInk
            case .night: return nightInk
            }
        }
    }

    /// Type. The semantic steps of `docs/design-tokens.md` §2, mapped to
    /// SwiftUI's own scale so Dynamic Type keeps working. Nothing in the app
    /// sets a fixed point size.
    ///
    /// These four are the steps the other three platforms are held to, named
    /// here so the mapping is written down rather than assumed. They are not a
    /// ceiling: SwiftUI's intermediate steps — `.callout`, `.footnote`,
    /// `.title2`, `.title3` — stay in use where a screen needs a level between
    /// them. The scale exists to stop raw point sizes accumulating, which is
    /// what happened on Windows and macOS, and Apple's scale already solves
    /// that. Flattening a good seven-step system into four would lose hierarchy
    /// to no end.
    enum Text {
        static let caption = Font.caption
        static let body = Font.body
        static let subtitle = Font.headline
        static let title = Font.largeTitle
    }
}
