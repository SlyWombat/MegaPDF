import CPdfium
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

// Shrink for email (#593): the core's megapdf_shrink_images_cancellable, with ImageIO as the
// JPEG encoder. The rules are the core's (150 dpi of the placed size, quality 0.75, the size
// skips and the 10% guard); this file is the encoder and the plumbing.
//
// `PdfEngine` is an actor, so a shrink run on it would hold up every other engine call for the
// whole run. This one runs off the actor on its own queue, as the page check does (#145): the
// core lets go of its lock while each picture is encoded, so renders of the open document go on
// meanwhile. Registered with the document's check registry, so a close of the copy mid-run
// raises the flag and waits for the run to end, exactly as it does for a page check.

/// How a shrink ended: every picture looked at (`stopped` false), or stopped by its flag.
struct ShrinkOutcome: Equatable {
    let considered: Int
    let replaced: Int
    let stopped: Bool
}

extension PdfEngine {

    /// Shrinks `document`'s pictures in place, off the actor (#593). Only ever on a private copy:
    /// a stopped run leaves it part-shrunk. `progress` is told "pictures done of total" from the
    /// shrink's own thread. Nil when the core could not finish for any reason but the flag.
    nonisolated func shrinkImages(_ document: PdfDocument, stop: CoreCancelFlag?,
                                  progress: @escaping @Sendable (Int, Int) -> Void) async -> ShrinkOutcome? {
        // A flag of our own when none is given, so the run can always join the registry.
        guard let flag = stop ?? CoreCancelFlag() else { return nil }
        guard document.beginCheck(flag) else { return ShrinkOutcome(considered: 0, replaced: 0, stopped: true) }
        return await withCheckedContinuation { (continuation: CheckedContinuation<ShrinkOutcome?, Never>) in
            Self.shrinkQueue.async {
                let outcome = Self.runShrink(document, flag: flag, progress: progress)
                document.endCheck(flag)
                continuation.resume(returning: outcome)
            }
        }
    }

    private static let shrinkQueue = DispatchQueue(label: "megapdf.shrink", qos: .userInitiated)

    /// The context the C callbacks get back.
    private final class ShrinkBox {
        let progress: @Sendable (Int, Int) -> Void
        init(progress: @escaping @Sendable (Int, Int) -> Void) { self.progress = progress }
    }

    /// The blocking part. Never on the actor, never on the main thread.
    private static func runShrink(_ document: PdfDocument, flag: CoreCancelFlag,
                                  progress: @escaping @Sendable (Int, Int) -> Void) -> ShrinkOutcome? {
        let box = ShrinkBox(progress: progress)
        let encode: megapdf_jpeg_encode_fn = { _, bgra, width, height, quality, outJpeg, outLength in
            guard let bgra, let outJpeg, let outLength, width > 0, height > 0 else { return 0 }
            guard let jpeg = PdfEngine.jpeg(bgra: bgra, width: Int(width), height: Int(height), quality: quality) else { return 0 }
            let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: jpeg.count)
            jpeg.copyBytes(to: buffer, count: jpeg.count)
            outJpeg.pointee = buffer
            outLength.pointee = jpeg.count
            return 1
        }
        let release: megapdf_jpeg_release_fn = { _, jpeg in jpeg?.deallocate() }
        let report: megapdf_progress_fn = { context, done, total in
            guard let context else { return }
            Unmanaged<ShrinkBox>.fromOpaque(context).takeUnretainedValue().progress(Int(done), Int(total))
        }
        var counts = [Int32](repeating: 0, count: Int(MEGAPDF_SHRINK_COUNTS))
        let status = withExtendedLifetime(box) { () -> Int32 in
            counts.withUnsafeMutableBufferPointer { slots in
                megapdf_shrink_images_cancellable(document.core, encode, release, report,
                                                  Unmanaged.passUnretained(box).toOpaque(), flag.handle,
                                                  slots.baseAddress, slots.count)
            }
        }
        let considered = Int(counts[Int(MEGAPDF_SHRINK_CONSIDERED)])
        let replaced = Int(counts[Int(MEGAPDF_SHRINK_REPLACED)])
        if status == MEGAPDF_ERR_CANCELLED { return ShrinkOutcome(considered: considered, replaced: replaced, stopped: true) }
        guard status == MEGAPDF_OK else { return nil }
        return ShrinkOutcome(considered: considered, replaced: replaced, stopped: false)
    }

    /// BGRA in memory (little-endian 32-bit, alpha first and ignored) to a JPEG at `quality` 0...1.
    static func jpeg(bgra: UnsafePointer<UInt8>, width: Int, height: Int, quality: Double) -> Data? {
        let bytesPerRow = width * 4
        let pixels = Data(bytes: bgra, count: bytesPerRow * height)
        guard let provider = CGDataProvider(data: pixels as CFData),
              let space = CGColorSpace(name: CGColorSpace.sRGB),
              let image = CGImage(
                  width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: bytesPerRow,
                  space: space,
                  bitmapInfo: CGBitmapInfo(rawValue: CGBitmapInfo.byteOrder32Little.rawValue |
                      CGImageAlphaInfo.noneSkipFirst.rawValue),
                  provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)
        else { return nil }
        let out = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(out as CFMutableData, UTType.jpeg.identifier as CFString, 1, nil)
        else { return nil }
        CGImageDestinationAddImage(destination, image, [kCGImageDestinationLossyCompressionQuality: quality] as CFDictionary)
        guard CGImageDestinationFinalize(destination) else { return nil }
        return out as Data
    }
}
