import CPdfium
import Foundation

// Checkbox surface (#22) — the Swift port of Android's engine.cpp checkbox
// half; the heuristic constants and MegaPDF_Id tagging are SDD §6.2 contracts.

/// A checkbox or radio-button widget on a page.
struct PdfFormField: Equatable {
    let isRadio: Bool
    let isChecked: Bool
    let rect: PdfRect
}

extension PdfRect {
    func contains(x: Double, y: Double) -> Bool {
        x >= left && x <= right && y >= bottom && y <= top
    }
    var centerX: Double { (left + right) / 2 }
    var centerY: Double { (bottom + top) / 2 }

    /// The same rect with `margin` points added on every side — touch targets.
    func grown(by margin: Double) -> PdfRect {
        PdfRect(left: left - margin, bottom: bottom - margin,
                right: right + margin, top: top + margin)
    }
}

extension PdfEngine {

    /// Checkbox and radio widgets on the page, with checked state — read through
    /// the core's form environment (#107); surfacing only buttons is this app's choice.
    func formFields(_ document: PdfDocument, pageIndex: Int) throws -> [PdfFormField] {
        try withCorePage(document, index: pageIndex) { page in
            guard let fields = megapdf_form_fields_load(page) else { return [] }
            defer { megapdf_form_fields_free(fields) }
            var result: [PdfFormField] = []
            for i in 0..<megapdf_form_field_count(fields) {
                var f = megapdf_form_field()
                guard megapdf_form_field_get(fields, i, &f) == MEGAPDF_OK else { continue }
                guard f.kind == MEGAPDF_FIELD_CHECKBOX.rawValue || f.kind == MEGAPDF_FIELD_RADIO.rawValue
                else { continue }
                result.append(PdfFormField(
                    isRadio: f.kind == MEGAPDF_FIELD_RADIO.rawValue,
                    isChecked: f.is_checked != 0,
                    rect: PdfRect(left: f.bounds.left, bottom: f.bounds.bottom,
                                  right: f.bounds.right, top: f.bounds.top)))
            }
            return result
        }
    }

    /// Simulated click at page coordinates (points, bottom-left origin, crop
    /// space) — toggles the field under the point via PDFium's form machinery,
    /// keeping `/V`, `/AS`, and radio-group siblings consistent. In the core.
    func clickAt(_ document: PdfDocument, pageIndex: Int, x: Double, y: Double) throws {
        try withCorePage(document, index: pageIndex) { page in
            _ = megapdf_form_click(page, x, y)
        }
    }

    /// Drawn (non-form) checkbox candidates, from the shared engine core (#33):
    /// stroked-not-filled path objects, 6–24 pt, squareness within 25% (SDD §6.2
    /// contract 2). The heuristic itself is no longer written here — one
    /// implementation now serves all three platforms, which is what stops the
    /// next #30 from happening.
    func detectCheckboxSquares(_ document: PdfDocument, pageIndex: Int) throws -> [PdfRect] {
        try withCorePage(document, index: pageIndex) { page in
            let count = megapdf_detect_checkbox_squares(page, nil, 0)
            guard count > 0 else { return [] }
            var buffer = [megapdf_rect](repeating: megapdf_rect(), count: count)
            _ = buffer.withUnsafeMutableBufferPointer {
                megapdf_detect_checkbox_squares(page, $0.baseAddress, count)
            }
            // The core already returns crop-space rects.
            return buffer.map {
                PdfRect(left: $0.left, bottom: $0.bottom, right: $0.right, top: $0.top)
            }
        }
    }

    /// Places the ✗ check-mark stamp over a drawn square: STAMP annot, 10%
    /// inset, 0x202020 stroke at width max(1.2, w·0.11), `MegaPDF_Id` = id.
    func addCheckMark(_ document: PdfDocument, pageIndex: Int,
                      square: PdfRect, id: String) throws {
        try withPage(document, index: pageIndex) { page in
            // The square arrives in crop space from the UI; pdfium wants user space.
            let square = square.toUser(cropOrigin(page))
            let w = square.right - square.left
            let h = square.top - square.bottom
            let il = Float(square.left + 0.10 * w)
            let ib = Float(square.bottom + 0.10 * h)
            let ir = Float(square.right - 0.10 * w)
            let it = Float(square.top - 0.10 * h)

            guard let annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_STAMP) else {
                throw PdfError.editFailed
            }
            defer { FPDFPage_CloseAnnot(annot) }

            var rect = FS_RECTF(left: il, top: it, right: ir, bottom: ib)
            guard FPDFAnnot_SetRect(annot, &rect) != 0,
                  let path = FPDFPageObj_CreateNewPath(il, ib),
                  FPDFPath_LineTo(path, ir, it) != 0,
                  FPDFPath_MoveTo(path, il, it) != 0,
                  FPDFPath_LineTo(path, ir, ib) != 0
            else { throw PdfError.editFailed }

            FPDFPageObj_SetStrokeColor(path, 0x20, 0x20, 0x20, 0xFF)
            FPDFPageObj_SetStrokeWidth(path, Float(max(1.2, w * 0.11)))
            FPDFPath_SetDrawMode(path, FPDF_FILLMODE_NONE, 1)
            guard FPDFAnnot_AppendObject(annot, path) != 0,
                  Self.setMegaPdfId(annot, id: id)
            else { throw PdfError.editFailed }
        }
    }

    /// Removes the annotation at `annotIndex` (from `stamps`).
    func removeAnnot(_ document: PdfDocument, pageIndex: Int, annotIndex: Int) throws {
        try withPage(document, index: pageIndex) { page in
            guard FPDFPage_RemoveAnnot(page, Int32(annotIndex)) != 0 else {
                throw PdfError.editFailed
            }
        }
    }

    static func setMegaPdfId(_ annot: FPDF_ANNOTATION, id: String) -> Bool {
        let wide = Array(id.utf16) + [0]
        return wide.withUnsafeBufferPointer {
            FPDFAnnot_SetStringValue(annot, "MegaPDF_Id", $0.baseAddress) != 0
        }
    }
}
