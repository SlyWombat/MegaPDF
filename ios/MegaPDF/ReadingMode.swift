import SwiftUI

/// Reading mode's vocabulary (#168, #506, #512): the page colours, where the two
/// preferences are kept, the two zoom presets' arithmetic, and the rule that decides
/// whether the floating bar is allowed to fade.
///
/// Deliberately free of any view and of `ViewerModel`: every one of these is a decision
/// that can be got wrong quietly — an unknown settings value, a fit-page scale, a fade
/// timer armed with VoiceOver running — and each is worth a test that does not need a
/// simulator, a document or a window. `MegaPDFTests/ReadingModeTests.swift` is that test.

/// How the page is drawn: as the document has it, or through one of the engine's two
/// reading tints (#509, contract 7).
///
/// The raw values are the strings the desktops already write into `settings.json`
/// (`AppSettings.PageColours`), not a second vocabulary — one concept, one spelling,
/// whatever store a platform keeps it in.
enum PageTint: String, CaseIterable, Identifiable {
    case normal = ""
    case sepia = "Sepia"
    case night = "Night"

    var id: String { rawValue }

    /// The bits this tint adds to `megapdf_render`'s `flags` (#509). `MEGAPDF_RENDER_BGRA`
    /// is the pixel order and is the caller's to add; nothing here touches it.
    var renderFlag: UInt32 {
        switch self {
        case .normal: return 0
        case .sepia: return UInt32(MEGAPDF_RENDER_SEPIA)
        case .night: return UInt32(MEGAPDF_RENDER_NIGHT)
        }
    }

    /// The name in the Settings picker. Three literals rather than one interpolation, so
    /// all three are keys in the String Catalog.
    var pickerLabel: LocalizedStringKey {
        switch self {
        case .normal: return "Normal"
        case .sepia: return "Sepia"
        case .night: return "Night"
        }
    }
}

/// Where the two reading preferences live (#512).
///
/// `@AppStorage` — `UserDefaults` — rather than a fifth JSON file beside recents and
/// signatures: these are two scalars a Settings screen binds straight to, which is the
/// case `@AppStorage` exists for, and the phones have no preferences file to grow. The
/// keys are named in one place because two very different readers share them: the
/// Settings sheet's property wrappers, and `ViewerModel`, which has no view to hang a
/// wrapper off and reads `UserDefaults` directly at the moment a document opens.
enum ReadingDefaults {
    static let pageColoursKey = "readingPageColours"
    static let openInReadingModeKey = "openInReadingMode"

    /// The stored page colours. An unknown value reads back as `.normal` rather than
    /// throwing or crashing: a defaults database is user data, and a value written by a
    /// later version that grows a fourth tint must not stop a document opening. The same
    /// posture as the desktops' `AppSettings.TintOf`.
    static func pageTint(_ defaults: UserDefaults = .standard) -> PageTint {
        PageTint(rawValue: defaults.string(forKey: pageColoursKey) ?? "") ?? .normal
    }

    /// Whether a document opens straight into reading mode (#168 decision 2, which dropped
    /// per-document memory for this one app-level switch). Off by default — `UserDefaults`
    /// answers false for a key nobody has written, which is the default we want.
    static func openInReadingMode(_ defaults: UserDefaults = .standard) -> Bool {
        defaults.bool(forKey: openInReadingModeKey)
    }
}

/// The two zoom presets on the reading bar (#512).
///
/// The phones' zoom has always been width-relative — 1.0 *is* fit width, because the page
/// is laid out at `containerWidth * zoom` — so fit width needs no arithmetic and fit page
/// is the only new number. It is a viewport computation, not an engine call: nothing about
/// the document changes, only how wide the page is asked to be.
enum ReadingZoom {
    /// Fit width, which is the zoom the viewer has always opened at.
    static let fitWidth: CGFloat = 1

    /// The ceiling every zoom on this screen shares.
    static let maximum: CGFloat = 4

    /// The floor a *fit page* preset may push the zoom down to. Below this a page is
    /// unreadable and the scroll view is mostly gutter.
    static let minimum: CGFloat = 0.25

    /// The zoom at which `page` is exactly as tall as `viewport`.
    ///
    /// The page's laid-out height is `viewport.width * zoom * page.height / page.width`, so
    /// the zoom that makes that equal `viewport.height` falls straight out. Clamped, and a
    /// degenerate viewport or page (either one zero, which is what a `GeometryReader`
    /// reports before the first layout pass) answers fit width rather than a division by
    /// zero.
    static func fitPage(viewport: CGSize, page: CGSize) -> CGFloat {
        guard viewport.width > 0, viewport.height > 0,
              page.width > 0, page.height > 0 else { return fitWidth }
        let scale = (viewport.height * page.width) / (viewport.width * page.height)
        return min(max(scale, minimum), maximum)
    }
}

/// When the floating bar is allowed to fade (#506).
enum ReadingBarFade {
    /// How long the bar stays up after it is shown or tapped.
    static let idle: TimeInterval = 2

    /// Whether a fade timer may be armed at all.
    ///
    /// **Never while VoiceOver is running.** A bar that fades is a bar a VoiceOver user
    /// cannot swipe back to: there is no pointer to move and no way to ask for it again
    /// except by tapping the page, which is exactly the gesture they would be using to
    /// read it. With VoiceOver on the bar is pinned — it does not auto-hide at all — and
    /// this is the one place that decides so, so the rule can be tested without a screen
    /// reader in the room.
    static func armsTimer(voiceOverRunning: Bool) -> Bool { !voiceOverRunning }
}
