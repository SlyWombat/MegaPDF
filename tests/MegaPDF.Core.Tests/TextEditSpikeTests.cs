using System.Text;
using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// The SDD §4.3 spike gate: click → hit-test text run → mutate content stream →
/// save → reopens correctly. Uses the same sample-PDF builder as the engine tests.
/// </summary>
public class TextEditSpikeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-textedit-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void GetTextRuns_FindsHelloRun_WithFontAndBounds()
    {
        using var doc = _engine.Open(WriteSamplePdf());
        using var page = doc.GetPage(0);

        var runs = page.GetTextRuns();

        var run = Assert.Single(runs);
        Assert.Equal("Hello MegaPDF", run.Text);
        Assert.Equal(36, run.FontSize, 1);
        // PDFium substitutes the standard-14 Helvetica with a system face (Arial on Windows).
        Assert.False(string.IsNullOrEmpty(run.FontName), "font family should be resolved");
        // Text is drawn at baseline y=700pt on a 792pt page → top-left-origin y ≈ 792-736.
        Assert.InRange(run.Bounds.Y, 40, 80);
        Assert.InRange(run.Bounds.X, 60, 80);
        Assert.True(run.Bounds.Width > 100, "run should span the drawn text");
    }

    [Fact]
    public void HitTest_InsideRun_ReturnsTextRun_OutsideReturnsNone()
    {
        using var doc = _engine.Open(WriteSamplePdf());
        using var page = doc.GetPage(0);
        var run = page.GetTextRuns()[0];

        var hit = page.HitTest(run.Bounds.Center);
        Assert.Equal(PageHitKind.TextRun, hit.Kind);
        Assert.Equal(run.Text, hit.TextRun!.Text);

        var miss = page.HitTest(new PdfPoint(306, 500));
        Assert.Equal(PageHitKind.None, miss.Kind);
    }

    [Fact]
    public void SetTextRunText_Edit_Save_Reopen_ShowsNewText()
    {
        var editedPath = Path.Combine(_dir, "edited.pdf");
        using (var doc = _engine.Open(WriteSamplePdf()))
        {
            using (var page = doc.GetPage(0))
            {
                var run = page.GetTextRuns()[0];
                page.SetTextRunText(run, "Goodbye Acrobat");
            }
            using var stream = File.Create(editedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(editedPath);
        using var reopenedPage = reopened.GetPage(0);
        var newRun = Assert.Single(reopenedPage.GetTextRuns());
        Assert.Equal("Goodbye Acrobat", newRun.Text);

        // And the edited text actually rasterizes as ink.
        var rendered = reopenedPage.Render(306, 396);
        Assert.Contains(false, PixelIsWhite(rendered));
    }

    [Fact]
    public void TextEditOperation_UndoRestoresOriginal()
    {
        using var doc = _engine.Open(WriteSamplePdf());
        var stack = new UndoStack();
        PdfTextRun run;
        using (var page = doc.GetPage(0))
            run = page.GetTextRuns()[0];

        stack.Do(new TextEditOperation(doc, 0, run, "Changed"));
        using (var page = doc.GetPage(0))
            Assert.Equal("Changed", page.GetTextRuns()[0].Text);

        stack.Undo();
        using (var page = doc.GetPage(0))
            Assert.Equal("Hello MegaPDF", page.GetTextRuns()[0].Text);

        stack.Redo();
        using (var page = doc.GetPage(0))
            Assert.Equal("Changed", page.GetTextRuns()[0].Text);
    }

    private static IEnumerable<bool> PixelIsWhite(RenderedPage rendered)
    {
        for (var i = 0; i < rendered.Bgra.Length; i += 4)
            yield return rendered.Bgra[i] == 0xFF && rendered.Bgra[i + 1] == 0xFF && rendered.Bgra[i + 2] == 0xFF;
    }

    private string WriteSamplePdf()
    {
        var path = Path.Combine(_dir, $"sample-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, BuildSamplePdf());
        return path;
    }

    private static byte[] BuildSamplePdf()
    {
        var content = "BT /F1 36 Tf 72 700 Td (Hello MegaPDF) Tj ET\n";
        string[] objects =
        [
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        ];

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
