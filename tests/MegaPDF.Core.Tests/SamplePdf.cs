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

    /// <summary>
    /// The Build() document encrypted with the PDF standard security handler
    /// (V1/R2, RC4-40) so password handling can be tested without external files.
    /// </summary>
    public static byte[] BuildEncrypted(string userPassword)
    {
        var content = "BT /F1 36 Tf 72 700 Td (Hello MegaPDF) Tj ET\n";
        var id = new byte[16];
        for (var i = 0; i < 16; i++) id[i] = (byte)(i * 7 + 3);
        const int permissions = -3904;

        // Owner entry: RC4(MD5(pad(ownerPw))[0..5], pad(userPw)); we use user==owner.
        byte[] padded = PadPassword(userPassword);
        byte[] oEntry;
        using (var md5 = System.Security.Cryptography.MD5.Create())
            oEntry = Rc4(md5.ComputeHash(PadPassword(userPassword)).Take(5).ToArray(), padded);

        // File encryption key: MD5(pad(userPw) + O + P(le32) + ID)[0..5].
        byte[] fileKey;
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            var input = padded.Concat(oEntry).Concat(BitConverter.GetBytes(permissions)).Concat(id).ToArray();
            fileKey = md5.ComputeHash(input).Take(5).ToArray();
        }

        var uEntry = Rc4(fileKey, Padding);
        var encryptedContent = Rc4(ObjectKey(fileKey, objectNumber: 4), Encoding.ASCII.GetBytes(content));

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new long[6];
        void Append(int i, string text) { offsets[i] = sb.Length; sb.Append(text); }

        Append(0, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Append(1, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Append(2, "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Append(3, $"4 0 obj\n<< /Length {encryptedContent.Length} >>\nstream\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        var tailBuilder = new StringBuilder("\nendstream\nendobj\n");
        offsets[4] = head.Length + encryptedContent.Length + tailBuilder.Length;
        tailBuilder.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        offsets[5] = head.Length + encryptedContent.Length + "\nendstream\nendobj\n".Length +
                     "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n".Length;
        tailBuilder.Append($"6 0 obj\n<< /Filter /Standard /V 1 /R 2 /O <{Convert.ToHexString(oEntry)}> /U <{Convert.ToHexString(uEntry)}> /P {permissions} >>\nendobj\n");

        var xrefOffset = head.Length + encryptedContent.Length + tailBuilder.Length;
        tailBuilder.Append("xref\n0 7\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            tailBuilder.Append($"{offset:D10} 00000 n \n");
        tailBuilder.Append(
            $"trailer\n<< /Size 7 /Root 1 0 R /Encrypt 6 0 R /ID [<{Convert.ToHexString(id)}> <{Convert.ToHexString(id)}>] >>\n" +
            $"startxref\n{xrefOffset}\n%%EOF");

        return head.Concat(encryptedContent).Concat(Encoding.ASCII.GetBytes(tailBuilder.ToString())).ToArray();
    }

    private static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private static byte[] PadPassword(string password)
    {
        var bytes = Encoding.ASCII.GetBytes(password);
        return bytes.Concat(Padding).Take(32).ToArray();
    }

    private static byte[] ObjectKey(byte[] fileKey, int objectNumber)
    {
        var input = fileKey
            .Concat(new[] { (byte)objectNumber, (byte)(objectNumber >> 8), (byte)(objectNumber >> 16) })
            .Concat(new byte[] { 0, 0 }) // generation 0
            .ToArray();
        using var md5 = System.Security.Cryptography.MD5.Create();
        return md5.ComputeHash(input).Take(Math.Min(fileKey.Length + 5, 16)).ToArray();
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++) s[i] = (byte)i;
        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        int a = 0, b = 0;
        for (var i = 0; i < data.Length; i++)
        {
            a = (a + 1) & 0xFF;
            b = (b + s[a]) & 0xFF;
            (s[a], s[b]) = (s[b], s[a]);
            output[i] = (byte)(data[i] ^ s[(s[a] + s[b]) & 0xFF]);
        }
        return output;
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
