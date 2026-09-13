import CPdfium
import Foundation

// Signature stamps (#22), placed and read back by the shared core (#108).
// Pixels are logical ARGB UInt32s; on little-endian, their memory layout is
// exactly the BGRA byte order the core takes, so buffers pass through without
// a swizzle.

extension PdfEngine {

    /// Places an image stamp over `rect` (crop space), tagged `MegaPDF_Id` = `id` ("sig:...").
    func addImageStamp(_ document: PdfDocument, pageIndex: Int,
                       pixels: [UInt32], pixelWidth: Int, pixelHeight: Int,
                       rect: PdfRect, id: String) throws {
        guard pixels.count == pixelWidth * pixelHeight else { throw PdfError.editFailed }
        try withCorePage(document, index: pageIndex) { page in
            var bounds = megapdf_rect(left: rect.left, bottom: rect.bottom, right: rect.right, top: rect.top)
            let wide = Array(id.utf16) + [0]
            let status = pixels.withUnsafeBytes { raw in
                wide.withUnsafeBufferPointer { idPtr in
                    megapdf_add_image_stamp(page, raw.baseAddress?.assumingMemoryBound(to: UInt8.self),
                                            Int32(pixelWidth), Int32(pixelHeight), &bounds, idPtr.baseAddress)
                }
            }
            guard status == MEGAPDF_OK else { throw PdfError.editFailed }
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

    /// Reads a stamp's image back at native pixel resolution (the core's rule, so
    /// repeated moves never lose resolution, including stamps placed by other
    /// platforms). Returns nil when the annot has no image object.
    func stampImage(_ document: PdfDocument, pageIndex: Int, annotIndex: Int)
        throws -> (width: Int, height: Int, pixels: [UInt32])? {
        try withCorePage(document, index: pageIndex) { page in
            guard let image = megapdf_stamp_image_load(page, Int32(annotIndex)) else { return nil }
            defer { megapdf_image_free(image) }
            let w = Int(megapdf_image_width(image))
            let h = Int(megapdf_image_height(image))
            guard w > 0, h > 0 else { return nil }
            var pixels = [UInt32](repeating: 0, count: w * h)
            _ = pixels.withUnsafeMutableBytes { dst in
                megapdf_image_pixels(image, dst.baseAddress?.assumingMemoryBound(to: UInt8.self), dst.count)
            }
            return (w, h, pixels)
        }
    }
}
