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

    /// The handwritten "MegaWoman" demo signature (bundled transparent PNG,
    /// rendered from a script typeface at build time).
    static func signatureImage() -> CGImage? {
        guard let url = Bundle.main.url(forResource: "demo-signature", withExtension: "png"),
              let data = try? Data(contentsOf: url) else { return nil }
        return UIImage(data: data)?.cgImage
    }

    static func demoRecents() -> [RecentEntry] {
        let now = Int64(Date().timeIntervalSince1970 * 1000)
        let day: Int64 = 86_400_000
        return [
            ("Rental Agreement.pdf", now - day / 2),
            ("Field Trip Permission.pdf", now - 2 * day),
            ("Insurance Claim Form.pdf", now - 6 * day),
        ].map { name, at in
            RecentEntry(bookmarkBase64: Data(name.utf8).base64EncodedString(),
                        displayName: name, lastOpenedEpochMs: at)
        }
    }
}
