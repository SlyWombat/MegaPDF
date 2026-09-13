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

    /// Places the ✗ check-mark stamp over a drawn square (crop space): geometry
    /// and ink are the core's (#108), tagged `MegaPDF_Id` = id.
    func addCheckMark(_ document: PdfDocument, pageIndex: Int,
                      square: PdfRect, id: String) throws {
        try withCorePage(document, index: pageIndex) { page in
            var rect = megapdf_rect(left: square.left, bottom: square.bottom, right: square.right, top: square.top)
            let wide = Array(id.utf16) + [0]
            let status = wide.withUnsafeBufferPointer {
                megapdf_add_check_mark(page, &rect, MEGAPDF_MARK_CROSS, $0.baseAddress)
            }
            guard status == MEGAPDF_OK else { throw PdfError.editFailed }
        }
    }

    /// Removes the annotation at `annotIndex` (from `stamps`).
    func removeAnnot(_ document: PdfDocument, pageIndex: Int, annotIndex: Int) throws {
        try withCorePage(document, index: pageIndex) { page in
            guard megapdf_remove_annotation(page, Int32(annotIndex)) == MEGAPDF_OK else {
                throw PdfError.editFailed
            }
        }
    }

}
