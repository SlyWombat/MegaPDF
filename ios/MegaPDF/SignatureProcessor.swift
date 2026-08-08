import Foundation

/// Signature cleanup — the SDD §6.2 contract pixel math over logical-ARGB
/// UInt32 arrays, matching desktop `SignatureImageProcessor` and Android
/// exactly (verified by the mirrored unit tests).
enum SignatureProcessor {

    private static let whiteLuminanceCutoff = 235.0
    private static let inkAlphaCutoff: UInt32 = 16
    private static let trimMargin = 4

    /// True if the image already carries meaningful transparency (skip cleanup).
    static func hasTransparency(_ pixels: [UInt32]) -> Bool {
        pixels.contains { ($0 >> 24) < 250 }
    }

    /// Photographed/scanned signatures: near-white becomes transparent.
    /// Luminance = 0.114·B + 0.587·G + 0.299·R; if > 235, alpha := 0.
    static func removeWhiteBackground(_ pixels: [UInt32]) -> [UInt32] {
        pixels.map { p in
            let r = Double((p >> 16) & 0xFF)
            let g = Double((p >> 8) & 0xFF)
            let b = Double(p & 0xFF)
            let luminance = 0.114 * b + 0.587 * g + 0.299 * r
            return luminance > whiteLuminanceCutoff ? (p & 0x00FF_FFFF) : p
        }
    }

    /// Crops to the bounding box of visible ink (alpha > 16) plus a 4px margin.
    /// Returns the input unchanged when nothing is visible.
    static func trimToInk(_ pixels: [UInt32], width: Int, height: Int)
        -> (pixels: [UInt32], width: Int, height: Int) {
        var minX = width, minY = height, maxX = -1, maxY = -1
        for y in 0..<height {
            for x in 0..<width where (pixels[y * width + x] >> 24) > inkAlphaCutoff {
                minX = min(minX, x)
                maxX = max(maxX, x)
                minY = min(minY, y)
                maxY = max(maxY, y)
            }
        }
        guard maxX >= 0 else { return (pixels, width, height) }

        minX = max(minX - trimMargin, 0)
        minY = max(minY - trimMargin, 0)
        maxX = min(maxX + trimMargin, width - 1)
        maxY = min(maxY + trimMargin, height - 1)

        let w = maxX - minX + 1
        let h = maxY - minY + 1
        var out = [UInt32](repeating: 0, count: w * h)
        for y in 0..<h {
            out.replaceSubrange(y * w..<(y * w + w),
                                with: pixels[(minY + y) * width + minX..<(minY + y) * width + minX + w])
        }
        return (out, w, h)
    }
}
