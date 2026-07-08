using System.Text;

namespace MegaPDF.Core.Tests;

/// <summary>Builds small, valid PDFs (correct xref offsets) for engine tests.</summary>
internal static class SamplePdf
{
    /// <summary>One-page US-Letter PDF drawing Helvetica text at 72,700.</summary>
    public static byte[] Build(string text = "Hello MegaPDF")
    {
        var content = $"BT /F1 36 Tf 72 700 Td ({text}) Tj ET\n";
        return Assemble(
        [
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        ]);
    }

    /// <summary>
    /// One-page PDF with an AcroForm: a text field (FullName, rect 100,600–300,630)
    /// and a checkbox (Agree, rect 100,500–118,518) with Yes/Off appearance streams.
    /// </summary>
    public static byte[] BuildWithForm()
    {
        var content = "BT /F1 12 Tf 72 700 Td (Application form) Tj ET\n";
        var yesAp = "0 G 2 w 4 4 m 14 14 l S 14 4 m 4 14 l S\n";
        var offAp = "q Q\n";
        return Assemble(
        [
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R 7 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 5 0 R >> >> /NeedAppearances true >> >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> /Annots [6 0 R 7 0 R] >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            "6 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Tx /T (FullName) /Rect [100 600 300 630] /F 4 /DA (/Helv 12 Tf 0 g) >>\nendobj\n",
            "7 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Btn /T (Agree) /Rect [100 500 118 518] /F 4 /V /Off /AS /Off /AP << /N << /Yes 8 0 R /Off 9 0 R >> >> >>\nendobj\n",
            $"8 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 18 18] /Length {yesAp.Length} >>\nstream\n{yesAp}endstream\nendobj\n",
            $"9 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 18 18] /Length {offAp.Length} >>\nstream\n{offAp}endstream\nendobj\n",
        ]);
    }

    /// <summary>
    /// One-page PDF with drawn (non-form) rectangles: an 18pt stroked square at
    /// 100,500 (a checkbox), a 100x50 stroked box (too big), and a 10pt filled
    /// square (decoration, not a checkbox).
    /// </summary>
    public static byte[] BuildWithDrawnSquares()
    {
        var content =
            "BT /F1 12 Tf 130 504 Td (I agree to the terms) Tj ET\n" +
            "1 w 100 500 18 18 re S\n" +
            "1 w 200 400 100 50 re S\n" +
            "0.8 g 100 300 10 10 re f\n";
        return Assemble(
        [
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        ]);
    }

    public static byte[] Assemble(string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new long[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(objects[i]);
        }

        var xrefOffset = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n");
        sb.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
