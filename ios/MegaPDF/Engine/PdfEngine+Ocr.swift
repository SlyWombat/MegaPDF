import CoreGraphics
import CPdfium
import Foundation
import Vision

// On-device text recognition for scanned pages (#676), through Vision.
//
// Measured before it was designed (#676, phase 0): `.accurate` costs about 0.14 s a page and
// barely moves with resolution (134 ms at 100 dpi, 154 ms at 300), so there is nothing to gain
// from rendering large; `.fast` is four to ten times quicker but reads 61-92% of the
// characters, which for Find is silent missing hits. Hence accurate, at 150 dpi.
//
// Nothing recognised leaves this process, and nothing is written anywhere: the caller keeps
// the result in memory for as long as the document is open.

extension PdfEngine {

    /// Render resolution for recognition. The knee on the other platforms; flat on Vision.
    static let ocrDPI = 150.0
    /// Past this many pixels a side the render is scaled down. Vision has no hard ceiling the way
    /// Windows' 10,000 px does, but a poster at 150 dpi is a very large bitmap for no gain.
    static let ocrMaxSide = 6_000.0

    /// Whether the page carries any text object at all -- real or an invisible OCR layer. The
    /// trigger for recognition is "no text layer", never "has an image": a scan that has already
    /// been through OCR has both, and Find already works on it (#676, design point 2).
    func pageHasTextLayer(_ document: PdfDocument, index: Int) -> Bool {
        (try? withCorePage(document, index: index) { page -> Bool in
            guard let text = megapdf_text_load(page, UInt32(MEGAPDF_TEXT_ALL)) else { return true }
            defer { megapdf_text_free(text) }
            return megapdf_text_run_count(text) > 0
        }) ?? true   // when in doubt, leave the page to the engine's own search
    }

    /// Reads the page's text. Nil when the page could not be rendered or recognised -- distinct
    /// from an empty `OcrPage`, which is a page that was read and has nothing on it.
    func recognise(_ document: PdfDocument, index: Int) async -> OcrPage? {
        guard let size = try? pageSize(document, index: index), size.width > 0, size.height > 0
        else { return nil }
        var scale = Self.ocrDPI / 72.0
        scale = min(scale, Self.ocrMaxSide / Double(max(size.width, size.height)))
        let pixelWidth = max(Int(Double(size.width) * scale), 1)
        let pixelHeight = max(Int(Double(size.height) * scale), 1)
        guard let image = try? render(document, index: index,
                                      pixelWidth: pixelWidth, pixelHeight: pixelHeight)
        else { return nil }
        return await Self.runVision(on: image, pageSize: size)
    }

    /// Off the actor and off the main thread: recognition holds a thread for a tenth of a second
    /// or more, and the actor serialises every render and edit.
    private nonisolated static func runVision(on image: CGImage, pageSize: CGSize) async -> OcrPage? {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .utility).async {
                let request = VNRecognizeTextRequest()
                request.recognitionLevel = .accurate
                request.usesLanguageCorrection = true
                let handler = VNImageRequestHandler(cgImage: image, options: [:])
                do { try handler.perform([request]) } catch {
                    continuation.resume(returning: nil)
                    return
                }
                let lines = (request.results ?? []).compactMap { observation in
                    observation.topCandidates(1).first.flatMap { line(from: $0, pageSize: pageSize) }
                }
                continuation.resume(returning: OcrPage(lines: lines))
            }
        }
    }

    /// One recognised line as words with page-space rects. Vision's boxes are normalised with a
    /// bottom-left origin, which is the page's own orientation, so a box scales straight to points.
    private nonisolated static func line(from candidate: VNRecognizedText, pageSize: CGSize) -> OcrLine? {
        let text = candidate.string
        var words: [OcrWord] = []
        var start: String.Index?
        func close(at end: String.Index) {
            guard let from = start else { return }
            start = nil
            guard let box = try? candidate.boundingBox(for: from..<end) else { return }
            let r = box.boundingBox
            words.append(OcrWord(
                range: text.distance(from: text.startIndex, to: from)
                    ..< text.distance(from: text.startIndex, to: end),
                rect: PdfRect(left: Double(r.minX) * Double(pageSize.width),
                              bottom: Double(r.minY) * Double(pageSize.height),
                              right: Double(r.maxX) * Double(pageSize.width),
                              top: Double(r.maxY) * Double(pageSize.height))))
        }
        for index in text.indices {
            if text[index].isWhitespace { close(at: index) } else if start == nil { start = index }
        }
        close(at: text.endIndex)
        return words.isEmpty ? nil : OcrLine(text: text, words: words)
    }
}
