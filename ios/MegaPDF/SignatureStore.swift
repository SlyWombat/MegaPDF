import CoreGraphics
import Foundation
import UIKit

/// App-private signature library — PNGs plus index.json in Application
/// Support, mirroring the desktop `SignatureLibrary.cs` and Android's store.
struct SignatureEntry: Codable, Equatable, Identifiable {
    let id: String
    let displayName: String
    let fileName: String
    let pixelWidth: Int
    let pixelHeight: Int
    let createdEpochMs: Int64
}

final class SignatureStore {
    private let dir: URL
    private var indexURL: URL { dir.appendingPathComponent("index.json") }

    init(dir: URL? = nil) {
        if let dir {
            self.dir = dir
        } else {
            self.dir = FileManager.default.urls(
                for: .applicationSupportDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("signatures")
        }
    }

    func load() -> [SignatureEntry] {
        guard let data = try? Data(contentsOf: indexURL),
              let entries = try? JSONDecoder().decode([SignatureEntry].self, from: data)
        else { return [] }
        return entries
    }

    func add(displayName: String, image: CGImage) -> SignatureEntry? {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let id = UUID().uuidString
        let fileName = "\(id).png"
        guard let png = UIImage(cgImage: image).pngData() else { return nil }
        do {
            try png.write(to: dir.appendingPathComponent(fileName), options: .atomic)
        } catch { return nil }
        let entry = SignatureEntry(
            id: id, displayName: displayName, fileName: fileName,
            pixelWidth: image.width, pixelHeight: image.height,
            createdEpochMs: Int64(Date().timeIntervalSince1970 * 1000))
        write(load() + [entry])
        return entry
    }

    func delete(id: String) {
        let entries = load()
        if let entry = entries.first(where: { $0.id == id }) {
            try? FileManager.default.removeItem(at: dir.appendingPathComponent(entry.fileName))
        }
        write(entries.filter { $0.id != id })
    }

    func loadImage(_ entry: SignatureEntry) -> CGImage? {
        UIImage(contentsOfFile: dir.appendingPathComponent(entry.fileName).path)?.cgImage
    }

    private func write(_ entries: [SignatureEntry]) {
        guard let data = try? JSONEncoder().encode(entries) else { return }
        try? data.write(to: indexURL, options: .atomic)
    }
}

// MARK: - pixel plumbing shared by capture and placement

enum PixelBuffers {
    /// Draws any CGImage into a BGRA-little context and returns logical-ARGB
    /// UInt32 pixels — the engine/processor convention.
    static func argbPixels(from image: CGImage) -> (pixels: [UInt32], width: Int, height: Int)? {
        let w = image.width, h = image.height
        var pixels = [UInt32](repeating: 0, count: w * h)
        let ok = pixels.withUnsafeMutableBytes { raw -> Bool in
            guard let ctx = CGContext(
                data: raw.baseAddress, width: w, height: h, bitsPerComponent: 8,
                bytesPerRow: w * 4, space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue |
                    CGBitmapInfo.byteOrder32Little.rawValue) else { return false }
            ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
            return true
        }
        return ok ? (pixels, w, h) : nil
    }

    /// Wraps logical-ARGB pixels back into a CGImage (BGRA-little memory).
    static func image(from pixels: [UInt32], width: Int, height: Int) -> CGImage? {
        let data = pixels.withUnsafeBytes { Data($0) }
        guard let provider = CGDataProvider(data: data as CFData) else { return nil }
        return CGImage(
            width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32,
            bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(
                rawValue: CGImageAlphaInfo.premultipliedFirst.rawValue |
                    CGBitmapInfo.byteOrder32Little.rawValue),
            provider: provider, decode: nil, shouldInterpolate: true,
            intent: .defaultIntent)
    }
}
