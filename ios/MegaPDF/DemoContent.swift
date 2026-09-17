import CoreGraphics
import Foundation
import UIKit

/// Marketing/screenshot support (`-screenshot <state>` launch argument):
/// seeds believable content so App Store captures show the real app doing
/// real work. Never active in normal launches.
enum DemoContent {

    static var requestedState: String? {
        let args = ProcessInfo.processInfo.arguments
        guard let i = args.firstIndex(of: "-screenshot"), i + 1 < args.count else { return nil }
        return args[i + 1]
    }

    /// Everything the screenshots show follows the app language through the
    /// String Catalog (#91): a `-AppleLanguages (fr-CA)` launch opens the French
    /// agreement (`demo-fr.pdf`) under French names, and searches for a French
    /// word. The keys are the English values.

    /// The bundled demo agreement's resource name, without extension.
    static var demoResource: String { String(localized: "demo", comment: "screenshot demo PDF resource name") }

    /// The same agreement with nothing filled in (`demo-blank.pdf`,
    /// `demo-fr-blank.pdf`): what `-screenshot story` opens, so the preview
    /// video can tick, sign and type on camera instead of over a finished page.
    static var blankDemoResource: String { demoResource + "-blank" }

    /// The demo agreement's display name in the title bar and the recents list.
    static var documentName: String { String(localized: "Rental Agreement.pdf") }

    /// Term seeded into the find bar by `-screenshot search`. "rental" is the
    /// most-repeated word on the demo agreement's only page — it hits the
    /// "Equipment Rental Agreement" heading, "Sunrise Tool Rental" and "the
    /// rental equipment" — so the capture shows three highlights clustered
    /// under the bar with the counter reading "1 of 3". Search is
    /// case-insensitive, so the lower-case term matches the capitalized ones.
    /// The French page repeats "location" the same three times.
    static var searchTerm: String { String(localized: "rental", comment: "screenshot search term; must occur three times on the demo page") }

    /// What the Redact capture marks (#173): a line of the demo agreement with something on
    /// it worth removing. Marked by what it says rather than by a rectangle, so the shot
    /// lands on a sentence in every language.
    static var redactedWord: String {
        String(localized: "customer named",
               comment: "#173: what the Redact capture marks; a phrase on the demo agreement's first line")
    }

    /// Marketing "Add text" shot (#43): the name the customer would print under
    /// the signature rule the demo agreement draws at y=400, and where it sits.
    ///
    /// Localised like everything else here (#146 §3). Dave, 2026-09-15: the
    /// French captures must not show "Jane Whitfield", and the French names
    /// carry accents — Hélène Bélanger for fr-CA, Céline Lefèvre for fr — which
    /// is also what proves the accented glyphs survive the face the demo picks.
    /// `MegaPDFUITests/DemoFlowUITests.swift` varies its copy the same way.
    static var printedName: String {
        String(localized: "Jane Whitfield", comment: "screenshot demo person; French captures use French names, with accents (#146 §3)")
    }
    static let printedNameX: Double = 72
    static let printedNameY: Double = 372

    /// The handwritten "MegaWoman" demo signature (bundled transparent PNG,
    /// rendered from a script typeface at build time).
    static func signatureImage() -> CGImage? {
        guard let url = Bundle.main.url(forResource: "demo-signature", withExtension: "png"),
              let data = try? Data(contentsOf: url) else { return nil }
        return UIImage(data: data)?.cgImage
    }

    /// Somewhere believable for a demo document to live (#165). Localised like the
    /// rest of the demo content: a French capture reads "Téléchargements", because
    /// that is what the Files app would have called the folder.
    static var iCloudDrive: String { String(localized: "iCloud Drive") }
    static var downloadsFolder: String {
        String(localized: "Downloads", comment: "#165: demo recents; the Files app's Downloads folder")
    }
    static var clientFolder: String {
        String(localized: "Clients", comment: "#165: demo recents; a folder of client paperwork")
    }
    static var archiveFolder: String {
        String(localized: "Archive", comment: "#165: demo recents; a folder of older paperwork")
    }

    static func demoRecents() -> [RecentEntry] {
        let now = Int64(Date().timeIntervalSince1970 * 1000)
        let day: Int64 = 86_400_000
        return [
            (documentName, now - day / 2, [iCloudDrive, clientFolder]),
            (String(localized: "Field Trip Permission.pdf"), now - 2 * day,
             [RecentLocation.deviceName(), downloadsFolder]),
            (String(localized: "Insurance Claim Form.pdf"), now - 6 * day, [iCloudDrive]),
        ].map { name, at, segments in
            RecentEntry(bookmarkBase64: Data(name.utf8).base64EncodedString(),
                        displayName: name, lastOpenedEpochMs: at,
                        location: RecentLocation(segments: segments))
        }
    }

    /// `-screenshot recents`: one file name from four places, one of them gone.
    ///
    /// The whole of #165 on a single screen — four rows nobody could have told apart
    /// before, and the greyed-out one the Files app would show for a file that has
    /// moved. Made up rather than real, because no simulator has an iCloud account or
    /// a second file provider, and because a capture has to look the same every time
    /// it is taken.
    static func recentsScenario() -> (entries: [RecentEntry], unavailable: Set<String>) {
        let now = Int64(Date().timeIntervalSince1970 * 1000)
        let day: Int64 = 86_400_000
        // Surnames are not translated; the folders around them are.
        let places: [(segments: [String], at: Int64)] = [
            ([iCloudDrive, "Smith"], now - day / 4),
            ([iCloudDrive, "Jones"], now - day),
            ([RecentLocation.deviceName(), downloadsFolder], now - 3 * day),
            ([iCloudDrive, archiveFolder], now - 9 * day),
        ]
        let entries = places.enumerated().map { index, place in
            RecentEntry(bookmarkBase64: Data("recents-\(index)".utf8).base64EncodedString(),
                        displayName: documentName,
                        lastOpenedEpochMs: place.at,
                        location: RecentLocation(segments: place.segments))
        }
        // The last one has moved since it was opened.
        return (entries, Set(entries.suffix(1).map(\.id)))
    }
}
