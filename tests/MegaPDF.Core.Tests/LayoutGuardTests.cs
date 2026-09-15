using System.Text;
using Xunit;
using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;

namespace MegaPDF.Core.Tests;

/// <summary>
/// When PDFium rewrites a page it can still lose things the patched writer does not
/// write back (#118, #119) — a form XObject whose text inherits the font set before it is one — so an
/// edit on such a page would quietly change what the person never touched. The engine
/// must refuse and leave the page exactly as it was. Character spacing was the example
/// here until patch 0001 taught the writer <c>Tc</c>/<c>Tw</c>; it is now editable.
/// </summary>
public sealed class LayoutGuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-layout-").FullName;
    private readonly PdfiumEngine _engine = new();

    /// <summary>
    /// A plain heading above a form XObject whose text sets no font and draws in the one set before
    /// it. The writer never writes the text state a form inherits, so a rewrite loses that text.
    /// (Text used as a clip, <c>7 Tr</c>, was this page until PDFium patch 0018 taught the writer text clips.)
    /// </summary>
    private const string FormInheritingText =
        "BT /F1 24 Tf 72 700 Td (Spaced report) Tj ET q /F1 72 Tf 0 0 1 rg /Fm1 Do Q";

    /// <summary>The form <see cref="FormInheritingText"/> draws, written as object 6.</summary>
    private const string InheritingForm =
        "<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Length 24 >>\nstream\nBT 72 480 Td (FORM) Tj ET\nendstream";

    private static byte[] FormPdf() => Pdf(FormInheritingText, resources: "/XObject << /Fm1 6 0 R >>", extraObject: InheritingForm);

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void TextOnAPageWithAFormInheritingText_IsNotEditable_AndAnEditIsRefusedWithThePageUnchanged()
    {
        using var doc = Open(FormPdf(), "clipped.pdf");

        PdfTextLine line;
        IReadOnlyList<PdfTextRun> before;
        using (var page = doc.GetPage(0))
        {
            line = page.GetTextLines()[0];
            before = page.GetTextRuns();
            Assert.False(page.IsTextEditable(line.Runs[0].ObjectIndex));

            // #128: refused for the render, off text, with the numbers the dry run saw.
            var verdict = page.GetLayoutVerdict(line.Runs[0].ObjectIndex);
            Assert.NotNull(verdict);
            Assert.False(verdict.Editable);
            Assert.Equal(LayoutCause.Render, verdict.Cause);
            Assert.True(verdict.Where.HasFlag(LayoutArea.NonText));
            Assert.InRange(verdict.ChangedPixels, verdict.TotalPixels / 2000 + 1, verdict.TotalPixels);
        }

        var edit = new LineEditOperation(doc, 0, line, "Annual report");
        var refusal = Assert.Throws<TextEditException>(edit.Apply);
        Assert.Equal(TextEditFailure.LayoutWouldChange, refusal.Reason);
        Assert.Equal(LayoutCause.Render, refusal.Layout?.Cause);

        using var after = doc.GetPage(0);
        var runs = after.GetTextRuns();
        Assert.Equal(before.Select(r => (r.Text, r.Bounds)), runs.Select(r => (r.Text, r.Bounds)));
    }

    [Fact]
    public void APageWithAFormInheritingText_WarnsOnceBeforeAWhiteout_WhichStillAppliesAndUndoes()
    {
        using var doc = Open(FormPdf(), "clipped-whiteout.pdf");
        var warnings = new PageRegenerationWarnings();

        // #139: regenerating the page alone changes it, for the render, off text.
        using (var page = doc.GetPage(0))
        {
            var verdict = page.GetPageRegenerationVerdict();
            Assert.False(verdict.Editable);
            Assert.Equal(LayoutCause.Render, verdict.Cause);
            Assert.True(verdict.Where.HasFlag(LayoutArea.NonText));
            Assert.False(verdict.Where.HasFlag(LayoutArea.EditedText));
        }

        var whiteout = new AddWhiteoutOperation(doc, 0, new PdfRect(500, 20, 40, 30));
        Assert.True(warnings.ShouldWarn(doc, whiteout));
        Assert.True(warnings.ShouldWarn(doc, whiteout)); // Cancel settles nothing: asked again, it warns again

        // Body-text edits have their own guard, and annotations never regenerate the page.
        var line = GetFirstLine(doc);
        Assert.False(warnings.ShouldWarn(doc, new LineEditOperation(doc, 0, line, "Annual report")));
        Assert.False(PageRegenerationWarnings.RegeneratesUnjudged(new AddMarkOperation(doc, 0, new PdfRect(100, 100, 12, 12))));

        // Continue: the change applies, and the page is not asked about again.
        warnings.Settle(0);
        var stack = new UndoStack();
        stack.Do(whiteout);
        using (var page = doc.GetPage(0))
            Assert.Single(page.GetWhiteouts());
        Assert.False(warnings.ShouldWarn(doc, new AddTextBoxOperation(doc, 0, "Note", 12, new PdfPoint(300, 300))));

        stack.Undo();
        using (var page = doc.GetPage(0))
            Assert.Empty(page.GetWhiteouts());

        // Another document starts again.
        warnings.Reset();
        using var fresh = Open(FormPdf(), "clipped-whiteout-again.pdf");
        Assert.True(warnings.ShouldWarn(fresh, new AddWhiteoutOperation(fresh, 0, new PdfRect(500, 20, 40, 30))));
    }

    [Fact]
    public void APlainPage_KeepsItsLookWhenRegenerated_AndNeedsNoWarning()
    {
        using var doc = Open(Pdf("BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET 0 0 1 rg 72 500 200 40 re f"), "plain-whiteout.pdf");
        using (var page = doc.GetPage(0))
        {
            var verdict = page.GetPageRegenerationVerdict();
            Assert.True(verdict.Editable);
            Assert.Equal(LayoutCause.Ok, verdict.Cause);
            Assert.Equal(0, verdict.ChangedPixels);
        }
        Assert.False(new PageRegenerationWarnings().ShouldWarn(doc, new AddWhiteoutOperation(doc, 0, new PdfRect(500, 20, 40, 30))));
    }

    /// <summary>Thousands of small filled squares: every stage of the page check takes a while.</summary>
    private static string HeavyContent()
    {
        var content = new StringBuilder("BT /F1 18 Tf 72 740 Td (Heavy page) Tj ET 0 0 1 rg ");
        for (var i = 0; i < 20000; i++)
            content.Append($"{20 + (i % 560)} {20 + ((i / 560) % 700)} 0.8 0.8 re f ");
        return content.ToString();
    }

    [Fact]
    public async Task ThePageCheck_StartedEarly_AnswersAtTheChange()
    {
        // #145: started when the page is shown, asked at the change.
        using var clipped = Open(FormPdf(), "early-clipped.pdf");
        var warnings = new PageRegenerationWarnings();
        warnings.Prepare(clipped, 0);
        Assert.Equal(PageCheckAnswer.WouldChange, await warnings.AskAsync(clipped, 0, TimeSpan.FromSeconds(30)));
        Assert.False(warnings.IsSettled(0)); // Cancel settles nothing
        Assert.Equal(PageCheckAnswer.WouldChange, await warnings.AskAsync(clipped, 0, TimeSpan.FromSeconds(30)));
        warnings.Settle(0); // Continue
        Assert.Equal(PageCheckAnswer.KeepsLook, await warnings.AskAsync(clipped, 0, TimeSpan.FromSeconds(30)));

        using var plain = Open(Pdf("BT /F1 18 Tf 72 700 Td (Plain heading) Tj ET"), "early-plain.pdf");
        var plainWarnings = new PageRegenerationWarnings();
        Assert.Equal(PageCheckAnswer.KeepsLook,
            await plainWarnings.AskAsync(plain, new AddWhiteoutOperation(plain, 0, new PdfRect(500, 20, 40, 30)), TimeSpan.FromSeconds(30)));
        Assert.True(plainWarnings.IsSettled(0));
        Assert.Equal(PageCheckAnswer.KeepsLook,
            await plainWarnings.AskAsync(plain, new AddMarkOperation(plain, 0, new PdfRect(100, 100, 12, 12))));
    }

    [Fact]
    public async Task ACheckOverItsBudget_IsCancelled_AndTheChangeAppliesWithoutAWarning()
    {
        using var heavy = Open(Pdf(HeavyContent()), "budget-heavy.pdf");
        var warnings = new PageRegenerationWarnings();
        Assert.Equal(PageCheckAnswer.OverBudget, await warnings.AskAsync(heavy, 0, TimeSpan.FromMilliseconds(1)));
        Assert.True(warnings.IsSettled(0));
        Assert.False(warnings.IsChecking(0));

        // Nothing is ever refused: the whiteout applies.
        new UndoStack().Do(new AddWhiteoutOperation(heavy, 0, new PdfRect(500, 20, 40, 30)));
        using var page = heavy.GetPage(0);
        Assert.Single(page.GetWhiteouts());
    }

    [Fact]
    public void APageCheck_CanBeCancelled_AndCachesWhatItAnswers()
    {
        using var doc = Open(FormPdf(), "cancel-check.pdf");
        using var page = doc.GetPage(0);
        Assert.Null(page.GetCachedPageRegenerationVerdict());

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => page.GetPageRegenerationVerdict(cancelled.Token));
        }
        Assert.Null(page.GetCachedPageRegenerationVerdict());

        var verdict = page.GetPageRegenerationVerdict(CancellationToken.None);
        Assert.False(verdict.Editable);
        Assert.Equal(verdict, page.GetCachedPageRegenerationVerdict());
    }

    [Fact]
    public async Task ClosingADocument_WhileItsPageIsChecked_StopsTheCheckCleanly()
    {
        var heavy = Open(Pdf(HeavyContent()), "close-while-checking.pdf");
        var started = new TaskCompletionSource();
        var check = Task.Run(() =>
        {
            using var page = heavy.GetPage(0);
            started.SetResult();
            return page.GetPageRegenerationVerdict(CancellationToken.None);
        });
        await started.Task;
        await Task.Delay(20);
        heavy.Dispose(); // waits for the check, which stops at its next stage

        try
        {
            await check;
        }
        catch (OperationCanceledException)
        {
            // The expected outcome, unless the check finished first.
        }
        Assert.Throws<ObjectDisposedException>(() => heavy.GetPage(0));

        // A scheduler whose document went away answers without a warning.
        var warnings = new PageRegenerationWarnings();
        Assert.Equal(PageCheckAnswer.KeepsLook, await warnings.AskAsync(heavy, 0, TimeSpan.FromSeconds(5)));
    }

    private static PdfTextLine GetFirstLine(IPdfDocument doc)
    {
        using var page = doc.GetPage(0);
        return page.GetTextLines()[0];
    }

    [Fact]
    public void DeletingTextOnAPageWithAFormInheritingText_IsRefusedToo()
    {
        using var doc = Open(FormPdf(), "clipped-delete.pdf");

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
        Assert.Equal(LayoutCause.Render, refusal.Layout?.Cause);

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
            var verdict = page.GetLayoutVerdict(line.Runs[0].ObjectIndex);
            Assert.Equal(new LayoutVerdict(true, LayoutCause.Ok, LayoutArea.None, 0, verdict!.TotalPixels, 0), verdict);
        }

        new LineEditOperation(doc, 0, line, "Annual report").Apply();

        using var after = doc.GetPage(0);
        Assert.Equal("Annual report", Assert.Single(after.GetTextRuns()).Text.TrimEnd());
    }

    /// <summary>
    /// #141: a font written straight into the page's resources with its own widths. Before
    /// PDFium patch 0014 the writer rebuilt it without /Widths, so its runs moved and the edit
    /// was refused as text moving (#128). The patched writer keeps the dictionary, so the page
    /// is editable and the line under the edit stays where it was.
    /// </summary>
    [Fact]
    public void TextInADirectFontWithItsOwnWidths_IsEditable_AndOtherTextStaysPut()
    {
        var widths = string.Join(' ', Enumerable.Repeat("1000", 95));
        var font = $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 126 /Widths [{widths}] >>";
        using var doc = Open(Pdf("BT /F1 18 Tf 72 700 Td (Heading) Tj ET BT /F1 12 Tf 72 660 Td (Body line here) Tj ET", font), "direct-font.pdf");

        PdfTextLine line;
        PdfTextRun bodyBefore;
        using (var page = doc.GetPage(0))
        {
            var lines = page.GetTextLines();
            line = lines[0];
            bodyBefore = lines[1].Runs[0];
            var verdict = page.GetLayoutVerdict(line.Runs[0].ObjectIndex);
            Assert.NotNull(verdict);
            Assert.True(verdict.Editable);
            Assert.Equal(LayoutCause.Ok, verdict.Cause);
        }

        new LineEditOperation(doc, 0, line, "Annual report").Apply();

        using var after = doc.GetPage(0);
        var body = after.GetTextRuns().Single(r => r.Text.TrimEnd() == "Body line here");
        // Unpatched, the body came back at Helvetica's advances, tens of points narrower.
        Assert.Equal(bodyBefore.Bounds.X, body.Bounds.X, 0.01);
        Assert.Equal(bodyBefore.Bounds.Y, body.Bounds.Y, 0.01);
        Assert.Equal(bodyBefore.Bounds.Width, body.Bounds.Width, 0.01);
    }

    private IPdfDocument Open(byte[] bytes, string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return _engine.Open(path);
    }

    /// <summary>
    /// One page with a Helvetica /F1 (or <paramref name="font"/>, written into the page's resources) and the given
    /// content stream; <paramref name="resources"/> adds entries to the page's resources and
    /// <paramref name="extraObject"/> becomes object 6.
    /// </summary>
    private static byte[] Pdf(string content, string font = "4 0 R", string resources = "", string? extraObject = null)
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
        Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 " + font + " >> " + resources + " >> /Contents 5 0 R >>");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        if (extraObject is not null)
            Add(extraObject);
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
