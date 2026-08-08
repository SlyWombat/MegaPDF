// ADR-001 spike: does PDFKit satisfy the MegaPDF_Id interop contract?
// Runs on macOS (PDFKit is the same framework family as iOS). Usage:
//   swift ios/spike/PdfKitSpike.swift <stamped.pdf>
//
// Answers, printed as SPIKE: lines for the CI log:
//  (a) Can PDFKit READ the custom /MegaPDF_Id key from PDFium-style stamps?
//  (b) Does the key SURVIVE a PDFKit save (dataRepresentation)?
//  (c) Are appearance streams rewritten on save (byte-length comparison)?
//  (d) Can PDFKit WRITE a custom key that lands in the raw annot dict?

import Foundation
import PDFKit

let fixturePath = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "stamped.pdf"
let resavedPath = NSTemporaryDirectory() + "/resaved.pdf"

func fail(_ msg: String) -> Never {
    print("SPIKE: FATAL \(msg)")
    exit(1)
}

// Raw annotation-dictionary dump via CGPDF (ground truth, independent of PDFKit).
struct RawAnnot {
    var subtype: String = "?"
    var megaId: String? = nil
    var apLength: Int = -1
}

func rawAnnots(_ path: String) -> [RawAnnot] {
    guard let doc = CGPDFDocument(URL(fileURLWithPath: path) as CFURL),
          let page = doc.page(at: 1),
          let dict = page.dictionary else { return [] }
    var result: [RawAnnot] = []
    var annotsRef: CGPDFArrayRef?
    guard CGPDFDictionaryGetArray(dict, "Annots", &annotsRef), let annots = annotsRef else {
        return []
    }
    for i in 0..<CGPDFArrayGetCount(annots) {
        var dRef: CGPDFDictionaryRef?
        guard CGPDFArrayGetDictionary(annots, i, &dRef), let d = dRef else { continue }
        var a = RawAnnot()
        var nameRef: UnsafePointer<CChar>?
        if CGPDFDictionaryGetName(d, "Subtype", &nameRef), let n = nameRef {
            a.subtype = String(cString: n)
        }
        var strRef: CGPDFStringRef?
        if CGPDFDictionaryGetString(d, "MegaPDF_Id", &strRef), let s = strRef,
           let cf = CGPDFStringCopyTextString(s) {
            a.megaId = cf as String
        }
        var apRef: CGPDFDictionaryRef?
        if CGPDFDictionaryGetDictionary(d, "AP", &apRef), let ap = apRef {
            var streamRef: CGPDFStreamRef?
            if CGPDFDictionaryGetStream(ap, "N", &streamRef), let st = streamRef {
                var fmt = CGPDFDataFormat.raw
                if let data = CGPDFStreamCopyData(st, &fmt) {
                    a.apLength = CFDataGetLength(data)
                }
            }
        }
        result.append(a)
    }
    return result
}

// --- (a) PDFKit read of the custom key ---
guard let doc = PDFDocument(url: URL(fileURLWithPath: fixturePath)) else {
    fail("PDFKit could not open \(fixturePath)")
}
guard let page = doc.page(at: 0) else { fail("no page 0") }
print("SPIKE: pdfkit-annotation-count=\(page.annotations.count)")
var readIds: [String] = []
for annot in page.annotations {
    let key = PDFAnnotationKey(rawValue: "/MegaPDF_Id")
    let value = annot.value(forAnnotationKey: key)
    let id = (value as? String) ?? "nil"
    readIds.append(id)
    print("SPIKE: pdfkit-read type=\(annot.type ?? "?") MegaPDF_Id=\(id)")
}

// --- (d) PDFKit write of a custom key ---
if let firstPage = doc.page(at: 0) {
    let newAnnot = PDFAnnotation(
        bounds: CGRect(x: 300, y: 300, width: 60, height: 40),
        forType: .stamp, withProperties: nil)
    newAnnot.setValue("sig:pdfkit-written" as NSString,
                      forAnnotationKey: PDFAnnotationKey(rawValue: "/MegaPDF_Id"))
    firstPage.addAnnotation(newAnnot)
}

// --- (b)/(c) save through PDFKit, then raw-inspect both files ---
guard let data = doc.dataRepresentation() else { fail("dataRepresentation returned nil") }
try? data.write(to: URL(fileURLWithPath: resavedPath))

let before = rawAnnots(fixturePath)
let after = rawAnnots(resavedPath)
print("SPIKE: raw-before-count=\(before.count) raw-after-count=\(after.count)")
for (i, a) in before.enumerated() {
    print("SPIKE: raw-before[\(i)] subtype=\(a.subtype) MegaPDF_Id=\(a.megaId ?? "nil") APlen=\(a.apLength)")
}
for (i, a) in after.enumerated() {
    print("SPIKE: raw-after[\(i)] subtype=\(a.subtype) MegaPDF_Id=\(a.megaId ?? "nil") APlen=\(a.apLength)")
}

// --- verdict lines ---
let readOk = readIds.contains("sig:interop-1") && readIds.contains("mark:interop-2")
let survivedIds = Set(after.compactMap { $0.megaId })
let surviveOk = survivedIds.contains("sig:interop-1") && survivedIds.contains("mark:interop-2")
let writeOk = survivedIds.contains("sig:pdfkit-written")
let beforeAp = before.compactMap { $0.megaId != nil ? $0.apLength : nil }
let afterAp = after.compactMap { ($0.megaId != nil && $0.megaId != "sig:pdfkit-written") ? $0.apLength : nil }
let apPreserved = beforeAp.sorted() == afterAp.sorted() && !beforeAp.isEmpty

print("SPIKE: VERDICT read-custom-key=\(readOk)")
print("SPIKE: VERDICT key-survives-save=\(surviveOk)")
print("SPIKE: VERDICT write-custom-key=\(writeOk)")
print("SPIKE: VERDICT appearance-streams-preserved=\(apPreserved)")
