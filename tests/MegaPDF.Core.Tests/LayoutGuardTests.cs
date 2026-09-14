using System.Text;
using Xunit;
using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;

namespace MegaPDF.Core.Tests;

/// <summary>
/// When PDFium rewrites a page it can still lose things the patched writer does not
/// write back (#118, #119) — text used as a clipping path (<c>7 Tr</c>) is one — so an
/// edit on such a page would quietly change what the person never touched. The engine
/// must refuse and leave the page exactly as it was. Character spacing was the example
/// here until patch 0001 taught the writer <c>Tc</c>/<c>Tw</c>; it is now editable.
/// </summary>
public sealed class LayoutGuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-layout-").FullName;
    private readonly PdfiumEngine _engine = new();

    /// <summary>A plain heading above a box clipped by invisible text, which a rewrite drops.</summary>
    private const string ClippedByText =
        "BT /F1 24 Tf 72 700 Td (Spaced report) Tj ET q BT 7 Tr /F1 72 Tf 72 480 Td (CLIP) Tj ET 0 0 1 rg 60 460 400 100 re f Q";

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void TextOnAPageClippedByText_IsNotEditable_AndAnEditIsRefusedWithThePageUnchanged()
    {
        using var doc = Open(Pdf(ClippedByText), "clipped.pdf");

        PdfTextLine line;
        IReadOnlyList<PdfTextRun> before;
        using (var page = doc.GetPage(0))
        {
            line = page.GetTextLines()[0];
            before = page.GetTextRuns();
            Assert.False(page.IsTextEditable(line.Runs[0].ObjectIndex));
        }

        var edit = new LineEditOperation(doc, 0, line, "Annual report");
        var refusal = Assert.Throws<TextEditException>(edit.Apply);
        Assert.Equal(TextEditFailure.LayoutWouldChange, refusal.Reason);

        using var after = doc.GetPage(0);
        var runs = after.GetTextRuns();
        Assert.Equal(before.Select(r => (r.Text, r.Bounds)), runs.Select(r => (r.Text, r.Bounds)));
    }

    [Fact]
    public void DeletingTextOnAPageClippedByText_IsRefusedToo()
    {
        using var doc = Open(Pdf(ClippedByText), "clipped-delete.pdf");

        PdfTextLine line;
        int runCount;
        using (var page = doc.GetPage(0))
        {
            line = page.GetTextLines()[0];
            runCount = page.GetTextRuns().Count;
        }

        var delete = new DeleteLineOperation(doc, 0, line);
        var refusal = Assert.Throws<TextEditException>(delete.Apply);
        Assert.Equal(TextEditFailure.LayoutWouldChange, refusal.Reason);

        using var after = doc.GetPage(0);
        Assert.Equal(runCount, after.GetTextRuns().Count);
    }

    [Fact]
    public void TextUnderCharacterSpacing_IsEditable_NowThatTheWriterKeepsIt()
    {
        using var doc = Open(Pdf("BT /F1 24 Tf 4 Tc 72 700 Td (Spaced report) Tj ET"), "spaced.pdf");

        PdfTextLine line;
        using (var page = doc.GetPage(0))
        {
            line = Assert.Single(page.GetTextLines());
            Assert.True(page.IsTextEditable(line.Runs[0].ObjectIndex));
        }

        new LineEditOperation(doc, 0, line, "Annual report").Apply();

        using var after = doc.GetPage(0);
        Assert.Equal("Annual report", Assert.Single(after.GetTextRuns()).Text.TrimEnd());
    }

    [Fact]
    public void PlainText_IsEditable_AndTheEditTakes()
    {
        using var doc = Open(Pdf("BT /F1 24 Tf 72 700 Td (Quarterly report) Tj ET"), "plain.pdf");

        PdfTextLine line;
        using (var page = doc.GetPage(0))
        {
            line = Assert.Single(page.GetTextLines());
            Assert.True(page.IsTextEditable(line.Runs[0].ObjectIndex));
        }

        new LineEditOperation(doc, 0, line, "Annual report").Apply();

        using var after = doc.GetPage(0);
        Assert.Equal("Annual report", Assert.Single(after.GetTextRuns()).Text.TrimEnd());
    }

    private IPdfDocument Open(byte[] bytes, string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return _engine.Open(path);
    }

    /// <summary>One page with a Helvetica /F1 and the given content stream.</summary>
    private static byte[] Pdf(string content)
    {
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Add(string body)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        Add("<< /Type /Catalog /Pages 2 0 R >>");
        Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
