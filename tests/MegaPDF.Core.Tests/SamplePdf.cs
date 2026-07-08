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
