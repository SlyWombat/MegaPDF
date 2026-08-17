import CPdfium
import Foundation

// Signature stamps (#22) — Swift port of the Android shim's stamp half.
// Pixels are logical ARGB UInt32s; on little-endian, their memory layout is
// exactly PDFium's BGRA byte order, so buffers copy row-by-row without a
// swizzle (unlike the JNI path, which received Java's big-endian view).

extension PdfEngine {

    /// Places an image stamp over `rect`: STAMP annot, image object positioned
    /// via the unit-square matrix (A=w, D=h, E=left, F=bottom, PDF points),
    /// tagged `MegaPDF_Id` = `id` ("sig:...").
    func addImageStamp(_ document: PdfDocument, pageIndex: Int,
                       pixels: [UInt32], pixelWidth: Int, pixelHeight: Int,
                       rect: PdfRect, id: String) throws {
        guard pixels.count == pixelWidth * pixelHeight else { throw PdfError.editFailed }
        try withPage(document, index: pageIndex) { page in
            // Placement arrives in crop space from the UI (#30); pdfium wants user space.
            let rect = rect.toUser(cropOrigin(page))
            guard let bmp = FPDFBitmap_Create(Int32(pixelWidth), Int32(pixelHeight), 1),
                  let buffer = FPDFBitmap_GetBuffer(bmp) else { throw PdfError.editFailed }
            defer { FPDFBitmap_Destroy(bmp) }
            let stride = Int(FPDFBitmap_GetStride(bmp))
            pixels.withUnsafeBytes { src in
                for y in 0..<pixelHeight {
                    memcpy(buffer.advanced(by: y * stride),
                           src.baseAddress!.advanced(by: y * pixelWidth * 4),
                           pixelWidth * 4)
                }
            }

            guard let annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_STAMP) else {
                throw PdfError.editFailed
            }
            defer { FPDFPage_CloseAnnot(annot) }

            var r = FS_RECTF(left: Float(rect.left), top: Float(rect.top),
                             right: Float(rect.right), bottom: Float(rect.bottom))
            guard FPDFAnnot_SetRect(annot, &r) != 0,
                  let img = FPDFPageObj_NewImageObj(document.docHandle),
                  FPDFImageObj_SetBitmap(nil, 0, img, bmp) != 0
            else { throw PdfError.editFailed }

            var m = FS_MATRIX(a: Float(rect.right - rect.left), b: 0, c: 0,
                              d: Float(rect.top - rect.bottom),
                              e: Float(rect.left), f: Float(rect.bottom))
            guard FPDFPageObj_SetMatrix(img, &m) != 0,
                  FPDFAnnot_AppendObject(annot, img) != 0,
                  Self.setMegaPdfId(annot, id: id)
            else { throw PdfError.editFailed }
        }
    }

    /// Removes the annotation carrying `MegaPDF_Id` == `id`, wherever it now sits.
    /// Annotation indices shift as annots come and go, so every reversible edit
    /// addresses its target by id instead (#34). A no-op when it is already gone,
    /// so an undo cannot throw on a document someone else has since changed.
    func removeAnnot(_ document: PdfDocument, pageIndex: Int, id: String) throws {
        let found = try stamps(document, pageIndex: pageIndex).first { $0.id == id }
        guard let found else { return }
        try removeAnnot(document, pageIndex: pageIndex, annotIndex: found.annotIndex)
    }

    /// Reads a stamp's image back at native pixel resolution via the temporary
    /// 1pt-per-pixel matrix (the shared move/resize pattern — repeated moves
    /// never lose resolution, including stamps placed by other platforms).
    /// Returns nil when the annot has no image object.
    func stampImage(_ document: PdfDocument, pageIndex: Int, annotIndex: Int)
        throws -> (width: Int, height: Int, pixels: [UInt32])? {
        try withPage(document, index: pageIndex) { page in
            guard let annot = FPDFPage_GetAnnot(page, Int32(annotIndex)) else { return nil }
            defer { FPDFPage_CloseAnnot(annot) }

            for i in 0..<FPDFAnnot_GetObjectCount(annot) {
                guard let obj = FPDFAnnot_GetObject(annot, i),
                      FPDFPageObj_GetType(obj) == FPDF_PAGEOBJ_IMAGE else { continue }
                var pw: UInt32 = 0, ph: UInt32 = 0
                guard FPDFImageObj_GetImagePixelSize(obj, &pw, &ph) != 0,
                      pw > 0, ph > 0 else { continue }

                var original = FS_MATRIX()
                guard FPDFPageObj_GetMatrix(obj, &original) != 0 else { continue }
                var native = FS_MATRIX(a: Float(pw), b: 0, c: 0, d: Float(ph), e: 0, f: 0)
                FPDFPageObj_SetMatrix(obj, &native)
                let bmp = FPDFImageObj_GetRenderedBitmap(document.docHandle, page, obj)
                FPDFPageObj_SetMatrix(obj, &original)
                guard let bmp else { continue }
                defer { FPDFBitmap_Destroy(bmp) }

                let w = Int(FPDFBitmap_GetWidth(bmp))
                let h = Int(FPDFBitmap_GetHeight(bmp))
                let stride = Int(FPDFBitmap_GetStride(bmp))
                guard let buffer = FPDFBitmap_GetBuffer(bmp) else { continue }
                var pixels = [UInt32](repeating: 0, count: w * h)
                pixels.withUnsafeMutableBytes { dst in
                    for y in 0..<h {
                        memcpy(dst.baseAddress!.advanced(by: y * w * 4),
                               buffer.advanced(by: y * stride),
                               w * 4)
                    }
                }
                return (w, h, pixels)
            }
            return nil
        }
    }
}
