using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Recovery;
using Xunit;

namespace MegaPDF.Core.Tests;

public class LineEditingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-line-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WritePdf()
    {
        var path = Path.Combine(_dir, $"multirun-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.BuildMultiRun());
        return path;
    }

    [Fact]
    public void GetTextLines_MergesFragments_KeepsSeparateBaselines()
    {
        using var doc = _engine.Open(WritePdf());
        using var page = doc.GetPage(0);

        Assert.Equal(4, page.GetTextRuns().Count); // the raw fragmentation
        var lines = page.GetTextLines();

        Assert.Equal(2, lines.Count);
        Assert.Equal("Hello cruel world", lines[0].Text);
        Assert.Equal(3, lines[0].Runs.Count);
        Assert.Equal("Second line", lines[1].Text);
    }

    [Fact]
    public void HitTest_AnywhereOnLine_ReturnsTheWholeLine()
    {
        using var doc = _engine.Open(WritePdf());
        using var page = doc.GetPage(0);
        var line = page.GetTextLines()[0];

        // Click near the line's right end — inside "world", the third fragment.
        var hit = page.HitTest(new PdfPoint(line.Bounds.Right - 5, line.Bounds.Center.Y));

        Assert.Equal(PageHitKind.TextRun, hit.Kind);
        Assert.Equal("Hello cruel world", hit.TextLine!.Text);
    }

    [Fact]
    public void LineEdit_ReplacesWholeLine_AndSurvivesSave()
    {
        var savedPath = Path.Combine(_dir, "edited.pdf");
        using (var doc = _engine.Open(WritePdf()))
        {
            PdfTextLine line;
            using (var page = doc.GetPage(0))
                line = page.GetTextLines()[0];

            var op = new LineEditOperation(doc, 0, line, "Kind regards, MegaPDF");
            op.Apply();

            using (var page = doc.GetPage(0))
            {
                var lines = page.GetTextLines();
                Assert.Equal(2, lines.Count);
                Assert.Equal("Kind regards, MegaPDF", lines[0].Text);
                Assert.Single(lines[0].Runs); // fragments consolidated
                Assert.Equal("Second line", lines[1].Text);
            }

            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        using var reopenedPage = reopened.GetPage(0);
        Assert.Equal("Kind regards, MegaPDF", reopenedPage.GetTextLines()[0].Text);
    }

    [Fact]
    public void LineEdit_Undo_RestoresOriginalFragmentsExactly()
    {
        using var doc = _engine.Open(WritePdf());
        var stack = new UndoStack();
        PdfTextLine line;
        using (var page = doc.GetPage(0))
            line = page.GetTextLines()[0];

        stack.Do(new LineEditOperation(doc, 0, line, "Changed"));
        stack.Undo();

        using (var page = doc.GetPage(0))
        {
            Assert.Equal(4, page.GetTextRuns().Count);
            var restored = page.GetTextLines()[0];
            Assert.Equal("Hello cruel world", restored.Text);
            Assert.Equal(3, restored.Runs.Count);
        }

        stack.Redo();
        using (var page = doc.GetPage(0))
            Assert.Equal("Changed", page.GetTextLines()[0].Text);
    }

    [Fact]
    public void DeleteLine_RemovesAllFragments_UndoBringsThemBack()
    {
        using var doc = _engine.Open(WritePdf());
        var stack = new UndoStack();
        PdfTextLine line;
        using (var page = doc.GetPage(0))
            line = page.GetTextLines()[0];

        stack.Do(new DeleteLineOperation(doc, 0, line));
        using (var page = doc.GetPage(0))
        {
            var lines = page.GetTextLines();
            Assert.Equal("Second line", Assert.Single(lines).Text);
        }

        stack.Undo();
        using (var page = doc.GetPage(0))
            Assert.Equal("Hello cruel world", page.GetTextLines()[0].Text);
    }

    [Fact]
    public void Journal_LineEditAndInverse_ReplayCorrectly()
    {
        var docPath = WritePdf();
        using var doc = _engine.Open(docPath);
        PdfTextLine line;
        using (var page = doc.GetPage(0))
            line = page.GetTextLines()[0];

        var op = new LineEditOperation(doc, 0, line, "Journaled");
        var entries = new List<JournalEntry>();
        op.Apply();
        entries.Add(op.ToJournalEntry(inverse: false));
        op.Revert();
        entries.Add(op.ToJournalEntry(inverse: true));

        // Edit + undo replayed onto a fresh copy ends where it started (text-wise).
        using var fresh = _engine.Open(docPath);
        Assert.Equal(2, JournalReplayer.Replay(fresh, entries));
        using var freshPage = fresh.GetPage(0);
        Assert.Equal("Hello cruel world", freshPage.GetTextLines()[0].Text);
    }
}
