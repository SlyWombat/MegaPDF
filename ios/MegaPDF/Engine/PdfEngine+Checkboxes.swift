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

    /// Checkbox and radio widgets on the page, with checked state.
    func formFields(_ document: PdfDocument, pageIndex: Int) throws -> [PdfFormField] {
        try withPage(document, index: pageIndex) { page in
            guard let form = document.formHandle else { return [] }
            let crop = cropOrigin(page)
            var result: [PdfFormField] = []
            for i in 0..<FPDFPage_GetAnnotCount(page) {
                guard let annot = FPDFPage_GetAnnot(page, i) else { continue }
                defer { FPDFPage_CloseAnnot(annot) }
                guard FPDFAnnot_GetSubtype(annot) == FPDF_ANNOT_WIDGET else { continue }
                let type = FPDFAnnot_GetFormFieldType(form, annot)
                guard type == FPDF_FORMFIELD_CHECKBOX || type == FPDF_FORMFIELD_RADIOBUTTON
                else { continue }
                var r = FS_RECTF()
                guard FPDFAnnot_GetRect(annot, &r) != 0 else { continue }
                result.append(PdfFormField(
                    isRadio: type == FPDF_FORMFIELD_RADIOBUTTON,
                    isChecked: FPDFAnnot_IsChecked(form, annot) != 0,
                    rect: PdfRect(left: Double(r.left), bottom: Double(r.bottom),
                                  right: Double(r.right), top: Double(r.top)).toCrop(crop)))
            }
            return result
        }
    }

    /// Simulated click at page coordinates (points, bottom-left origin) —
    /// toggles the field under the point via PDFium's form machinery, keeping
    /// `/V`, `/AS`, and radio-group siblings consistent.
    func clickAt(_ document: PdfDocument, pageIndex: Int, x: Double, y: Double) throws {
        try withPage(document, index: pageIndex) { page in
            guard let form = document.formHandle else { return }
            let crop = cropOrigin(page)
            FORM_OnLButtonDown(form, page, 0, x + crop.x, y + crop.y)
            FORM_OnLButtonUp(form, page, 0, x + crop.x, y + crop.y)
            FORM_ForceToKillFocus(form)
        }
    }

    /// Drawn (non-form) checkbox candidates: stroked-not-filled path objects,
    /// 6–24 pt on both axes, squareness within 25%.
    func detectCheckboxSquares(_ document: PdfDocument, pageIndex: Int) throws -> [PdfRect] {
        try withPage(document, index: pageIndex) { page in
            let crop = cropOrigin(page)
            var result: [PdfRect] = []
            for i in 0..<FPDFPage_CountObjects(page) {
                guard let obj = FPDFPage_GetObject(page, i),
                      FPDFPageObj_GetType(obj) == FPDF_PAGEOBJ_PATH else { continue }
                var l: Float = 0, b: Float = 0, r: Float = 0, t: Float = 0
                guard FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) != 0 else { continue }
                let w = r - l, h = t - b
                guard w >= 6, w <= 24, h >= 6, h <= 24 else { continue }
                guard abs(w - h) <= 0.25 * max(w, h) else { continue }
                var fillmode: Int32 = 0
                var stroke: FPDF_BOOL = 0
                guard FPDFPath_GetDrawMode(obj, &fillmode, &stroke) != 0,
                      stroke != 0, fillmode == FPDF_FILLMODE_NONE else { continue }
                result.append(PdfRect(left: Double(l), bottom: Double(b),
                                      right: Double(r), top: Double(t)).toCrop(crop))
            }
            return result
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
