import CoreGraphics
import Foundation

/// Marketing/screenshot support (`-screenshot <state>` launch argument):
/// seeds believable content so App Store captures show the real app doing
/// real work. Never active in normal launches.
enum DemoContent {

    static var requestedState: String? {
        let args = ProcessInfo.processInfo.arguments
        guard let i = args.firstIndex(of: "-screenshot"), i + 1 < args.count else { return nil }
        return args[i + 1]
    }

    /// A cursive-ish squiggle used for the seeded library entry and the
    /// pre-filled draw canvas.
    static func squiggleStrokes(width: CGFloat, height: CGFloat) -> [[CGPoint]] {
        var points: [CGPoint] = []
        let n = 80
        for i in 0...n {
            let t = CGFloat(i) / CGFloat(n)
            let x = width * (0.05 + 0.9 * (t + 0.04 * sin(6 * .pi * t)))
            let y = height * (0.5
                + 0.32 * sin(2 * .pi * (1.7 * t + 0.1)) * (1 - 0.5 * t)
                + 0.14 * sin(2 * .pi * 5 * t))
            points.append(CGPoint(x: x, y: y))
        }
        return [points]
    }

    /// Renders the squiggle to a transparent CGImage (for seeding the library).
    static func squiggleImage(width: Int = 600, height: Int = 200) -> CGImage? {
        guard let ctx = CGContext(
            data: nil, width: width, height: height, bitsPerComponent: 8,
            bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue |
                CGBitmapInfo.byteOrder32Little.rawValue) else { return nil }
        ctx.translateBy(x: 0, y: CGFloat(height))
        ctx.scaleBy(x: 1, y: -1)
        ctx.setStrokeColor(red: 0.10, green: 0.12, blue: 0.35, alpha: 1)
        ctx.setLineWidth(5)
        ctx.setLineCap(.round)
        ctx.setLineJoin(.round)
        for stroke in squiggleStrokes(width: CGFloat(width), height: CGFloat(height)) {
            guard stroke.count > 1 else { continue }
            ctx.move(to: stroke[0])
            for p in stroke.dropFirst() { ctx.addLine(to: p) }
            ctx.strokePath()
        }
        return ctx.makeImage()
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
