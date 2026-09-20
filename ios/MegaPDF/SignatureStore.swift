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
    /// The desktop store's `SoftLimit`: the 21st signature is refused, and `add`
    /// returning nil is how this store refuses (#333).
    static let softLimit = 20

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
        // An entry whose image has gone is a broken thumbnail; the desktop store
        // drops it on load and so does this one (#333).
        return entries.filter { FileManager.default.fileExists(atPath: url(for: $0).path) }
    }

    /// Whether the library is full: [add] will refuse another one (#333).
    var isFull: Bool { load().count >= Self.softLimit }

    func add(displayName: String, image: CGImage) -> SignatureEntry? {
        guard !isFull else { return nil }
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
        // The index must not name an image that is not there: if it cannot be written,
        // the image goes rather than becoming a broken row (#333).
        guard write(load() + [entry]) else {
            try? FileManager.default.removeItem(at: dir.appendingPathComponent(fileName))
            return nil
        }
        return entry
    }

    /// Takes the entry out of the index and then deletes the image — the desktop
    /// store's order, and the one all three platforms use (#333). The image is only
    /// removed once the index no longer names it, so a failed index write leaves the
    /// pair intact rather than a row pointing at nothing.
    func delete(id: String) {
        let entries = load()
        guard write(entries.filter { $0.id != id }) else { return }
        if let entry = entries.first(where: { $0.id == id }) {
            try? FileManager.default.removeItem(at: url(for: entry))
        }
    }

    /// Renames an entry in place; order and the PNG are untouched (#100).
    func rename(id: String, displayName: String) {
        write(load().map { entry in
            entry.id == id
                ? SignatureEntry(id: entry.id, displayName: displayName, fileName: entry.fileName,
                                 pixelWidth: entry.pixelWidth, pixelHeight: entry.pixelHeight,
                                 createdEpochMs: entry.createdEpochMs)
                : entry
        })
    }

    func loadImage(_ entry: SignatureEntry) -> CGImage? {
        UIImage(contentsOfFile: url(for: entry).path)?.cgImage
    }

    private func url(for entry: SignatureEntry) -> URL {
        dir.appendingPathComponent(entry.fileName)
    }

    @discardableResult
    private func write(_ entries: [SignatureEntry]) -> Bool {
        guard let data = try? JSONEncoder().encode(entries) else { return false }
        do {
            try data.write(to: indexURL, options: .atomic)
            return true
        } catch {
            return false
        }
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
