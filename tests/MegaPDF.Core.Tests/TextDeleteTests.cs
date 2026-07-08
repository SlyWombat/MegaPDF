using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Recovery;
using Xunit;

namespace MegaPDF.Core.Tests;

public class TextDeleteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-textdelete-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Delete_RemovesRun_AndSurvivesSave()
    {
        var savedPath = Path.Combine(_dir, "deleted.pdf");
        using (var doc = _engine.Open(WritePdf()))
        {
            using (var page = doc.GetPage(0))
            {
                var run = page.GetTextRuns()[0];
                page.DetachTextRun(run);
                Assert.Empty(page.GetTextRuns());
            }
            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        using var reopenedPage = reopened.GetPage(0);
        Assert.Empty(reopenedPage.GetTextRuns());
    }

    [Fact]
    public void DeleteOperation_UndoRestoresOriginalTextAndFont()
    {
        using var doc = _engine.Open(WritePdf());
        var stack = new UndoStack();
        PdfTextRun run;
        using (var page = doc.GetPage(0))
            run = page.GetTextRuns()[0];

        stack.Do(new DeleteTextOperation(doc, 0, run));
        using (var page = doc.GetPage(0))
            Assert.Empty(page.GetTextRuns());

        stack.Undo();
        using (var page = doc.GetPage(0))
        {
            var restored = Assert.Single(page.GetTextRuns());
            Assert.Equal("Hello MegaPDF", restored.Text);
            Assert.Equal(run.FontSize, restored.FontSize, 1);
            Assert.Equal(run.Bounds.X, restored.Bounds.X, 1);
        }

        stack.Redo();
        using (var page = doc.GetPage(0))
            Assert.Empty(page.GetTextRuns());
    }

    [Fact]
    public void Journal_DeleteAndRestore_Replay()
    {
        var docPath = WritePdf();
        using var doc = _engine.Open(docPath);
        PdfTextRun run;
        using (var page = doc.GetPage(0))
            run = page.GetTextRuns()[0];

        var op = new DeleteTextOperation(doc, 0, run);
        var entries = new List<JournalEntry>();
        op.Apply();
        entries.Add(op.ToJournalEntry(inverse: false));
        op.Revert();
        entries.Add(op.ToJournalEntry(inverse: true));

        // Replay delete + restore onto a fresh copy: text present again (standard font).
        using var fresh = _engine.Open(docPath);
        Assert.Equal(2, JournalReplayer.Replay(fresh, entries));
        using var freshPage = fresh.GetPage(0);
        Assert.Equal("Hello MegaPDF", Assert.Single(freshPage.GetTextRuns()).Text);
    }

    private string WritePdf()
    {
        var path = Path.Combine(_dir, $"doc-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.Build());
        return path;
    }
}
