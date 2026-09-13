using System.Text;
using Xunit;
using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;

namespace MegaPDF.Core.Tests;

/// <summary>
/// Undo after an edit that needed a substitute font must put the original run back —
/// its own font, not the standard face the edit brought in. SDD §3.1 and #112 promise a
/// byte-identical revert; setting the old text on the substitute object is not that.
/// </summary>
public sealed class SubstitutedEditUndoTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-undo-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void UndoingASubstitutedLineEdit_RestoresTheOriginalRun()
    {
        var path = Path.Combine(_dir, "symbol.pdf");
        File.WriteAllBytes(path, SymbolFontPdf());
        using var doc = _engine.Open(path);

        PdfTextLine line;
        using (var page = doc.GetPage(0))
            line = Assert.Single(page.GetTextLines());
        var original = line.Runs[0];

        var edit = new LineEditOperation(doc, 0, line, "Hello");
        edit.Apply();
        // Symbol's encoding has no Latin letters, so the edit cannot stay in place (#116).
        Assert.Equal(TextEditOutcome.EditedWithSubstitutedFont, edit.LastOutcome);

        edit.Revert();

        using var after = doc.GetPage(0);
        var run = Assert.Single(after.GetTextRuns());
        Assert.Equal(original.Text, run.Text);
        Assert.Equal(original.FontName, run.FontName);
        Assert.Equal(original.ObjectIndex, run.ObjectIndex);
    }

    [Fact]
    public void UndoingASubstitutedRunEdit_RestoresTheOriginalRun()
    {
        var path = Path.Combine(_dir, "symbol-run.pdf");
        File.WriteAllBytes(path, SymbolFontPdf());
        using var doc = _engine.Open(path);

        PdfTextRun original;
        using (var page = doc.GetPage(0))
            original = Assert.Single(page.GetTextRuns());

        var edit = new TextEditOperation(doc, 0, original, "Hello");
        edit.Apply();
        Assert.Equal(TextEditOutcome.EditedWithSubstitutedFont, edit.LastOutcome);

        edit.Revert();

        using var after = doc.GetPage(0);
        var run = Assert.Single(after.GetTextRuns());
        Assert.Equal(original.Text, run.Text);
        Assert.Equal(original.FontName, run.FontName);
    }

    /// <summary>One page, one run in the non-embedded Symbol font.</summary>
    private static byte[] SymbolFontPdf()
    {
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Add(string body)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        const string content = "BT /F1 24 Tf 72 700 Td (abgd) Tj ET";
        Add("<< /Type /Catalog /Pages 2 0 R >>");
        Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>");
        Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
