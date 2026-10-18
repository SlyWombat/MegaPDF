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

    /// Whether the page carries text of its own -- real or an invisible OCR layer -- **not
    /// counting boxes the person added in MegaPDF** (#676). The trigger for recognition is "no
    /// text layer", never "has an image": a scan that has already been through OCR has both, and
    /// Find already works on it (design point 2). A text box laid over a scan is not a text layer:
    /// the scan under it still needs reading, and Find searches the box as ordinary text.
    func pageHasTextLayer(_ document: PdfDocument, index: Int) -> Bool {
        (try? withCorePage(document, index: index) { page -> Bool in
            guard let text = megapdf_text_load(page, UInt32(MEGAPDF_TEXT_ALL)) else { return true }
            defer { megapdf_text_free(text) }
            for index in 0..<megapdf_text_run_count(text) {
                var run = megapdf_text_run()
                if megapdf_text_run_get(text, index, &run) == MEGAPDF_OK, run.is_text_box == 0 { return true }
            }
            return false
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
        // A sheet with nothing on it is read as empty without being sent to the recogniser (about
        // 0.14 s a page, for nothing). It is still a *read* page, so it is not looked at again.
        if Self.isBlank(image) { return OcrPage(lines: []) }
        return await Self.runVision(on: image, pageSize: size)
    }

    /// Whether the rendered page is a uniform sheet: a sampled grid in which almost nothing differs
    /// from the corner pixel. A few specks of scanner noise stay under the line; a ruled form or a
    /// drawing goes over it and is read, as is any real scan.
    nonisolated static func isBlank(_ image: CGImage) -> Bool {
        guard let data = image.dataProvider?.data, let bytes = CFDataGetBytePtr(data),
              image.bitsPerPixel == 32 else { return false }
        let rowBytes = image.bytesPerRow
        let corner = (Int(bytes[0]), Int(bytes[1]), Int(bytes[2]))
        var sampled = 0, different = 0
        for y in Swift.stride(from: 0, to: image.height, by: 4) {
            for x in Swift.stride(from: 0, to: image.width, by: 4) {
                let p = bytes + y * rowBytes + x * 4
                sampled += 1
                let d = abs(Int(p[0]) - corner.0) + abs(Int(p[1]) - corner.1) + abs(Int(p[2]) - corner.2)
                if d > 60 { different += 1 }
            }
        }
        return sampled > 0 && Double(different) / Double(sampled) < 0.0002
    }

    /// Off the actor and off the main thread: recognition holds a thread for a tenth of a second
    /// or more, and the actor serialises every render and edit.
    private nonisolated static func runVision(on image: CGImage, pageSize: CGSize) async -> OcrPage? {
        await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .utility).async {
                let request = VNRecognizeTextRequest()
                request.recognitionLevel = .accurate
                request.usesLanguageCorrection = true
                // Not assumed English: this app ships in English and French and its documents are
                // in whatever their authors wrote. Vision picks the language per page.
                request.automaticallyDetectsLanguage = true
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
