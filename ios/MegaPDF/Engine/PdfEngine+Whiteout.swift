import CPdfium
import Foundation

// Whiteouts (#3, SDD §6.2 contract 5). A whiteout is a white filled path carrying the
// MegaPDFWhiteout mark: it **covers** what is drawn under it. A redaction **removes**
// (#173) — the two are not alternatives, and the labels say which is which.
//
// This file only marshals. The core has had `megapdf_add_whiteout` and
// `megapdf_whiteouts` since contract 5; iOS had simply never bound them, which is why
// the whole tool was missing here (the same shape of gap Android turned out to have in
// #565). Removal and undo need nothing new either: a whiteout is an ordinary page
// object, so `detachObject`/`restoreObject` in PdfEngine+BodyText.swift already cover it.
//
// A whiteout is page content, not an annotation, so its handle is an **object index**,
// and an object index is not durable: anything that rewrites the page moves it. Every
// caller re-reads it rather than holding one across an edit — `WhiteoutOperation
// .currentObjectIndex` is where that is kept honest.

/// A whiteout on a page: where it is, and the page-object index it is at *now*.
struct PdfWhiteout: Equatable, Identifiable {
    let objectIndex: Int
    /// Crop space (#30), like every rectangle here.
    let rect: PdfRect

    var id: Int { objectIndex }
}

extension PdfEngine {

    /// Appends a whiteout covering `bounds` and answers the object index it landed at.
    func addWhiteout(_ document: PdfDocument, pageIndex: Int, bounds: PdfRect) throws -> Int {
        try withCorePage(document, index: pageIndex) { page in
            var rect = megapdf_rect(left: bounds.left, bottom: bounds.bottom,
                                    right: bounds.right, top: bounds.top)
            var index: Int32 = -1
            guard megapdf_add_whiteout(page, &rect, &index) == MEGAPDF_OK, index >= 0 else {
                throw PdfError.editFailed
            }
            return Int(index)
        }
    }

    /// Every whiteout on the page, in object order — so the **last** one is the one on top,
    /// which is what a tap should find first.
    func whiteouts(_ document: PdfDocument, pageIndex: Int) throws -> [PdfWhiteout] {
        try withCorePage(document, index: pageIndex) { page in
            let count = megapdf_whiteouts(page, nil, 0)
            guard count > 0 else { return [] }
            var buffer = [megapdf_object_rect](repeating: megapdf_object_rect(), count: count)
            let filled = megapdf_whiteouts(page, &buffer, count)
            return buffer.prefix(filled).map {
                PdfWhiteout(objectIndex: Int($0.object_index),
                            rect: PdfRect(left: $0.bounds.left, bottom: $0.bounds.bottom,
                                          right: $0.bounds.right, top: $0.bounds.top))
            }
        }
    }
}
