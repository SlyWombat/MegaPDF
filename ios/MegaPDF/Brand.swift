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
