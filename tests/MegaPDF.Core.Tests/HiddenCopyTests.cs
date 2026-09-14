using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Recovery;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #136: producers draw a line twice for fake bold, an outline or a shadow. PDFium's text layer
/// reads one copy and the other extracts as empty text, so it is never a run; a delete or an edit
/// that took only the runs left the old line drawn. The page is SamplePdf.BuildDoubledLines.
/// </summary>
public class HiddenCopyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-hidden-copy-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WritePdf()
    {
        var path = Path.Combine(_dir, $"doubled-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.BuildDoubledLines());
        return path;
    }

    private static PdfTextLine Line(IPdfPage page, string start) => page.GetTextLines().Single(l => l.Text.StartsWith(start));

    private static byte[] Shot(IPdfPage page) => page.Render(612, 792).Bgra;

    /// <summary>Dark pixels inside <paramref name="area"/> at one pixel per point: whatever is still drawn there.</summary>
    private static int InkIn(IPdfPage page, PdfRect area)
    {
        var shot = page.Render(612, 792);
        var ink = 0;
        for (var y = Math.Max(0, (int)area.Y); y < Math.Min(shot.PixelHeight, (int)Math.Ceiling(area.Y + area.Height)); y++)
        for (var x = Math.Max(0, (int)area.X); x < Math.Min(shot.PixelWidth, (int)Math.Ceiling(area.X + area.Width)); x++)
        {
            var i = (y * shot.PixelWidth + x) * 4;
            if (shot.Bgra[i] + shot.Bgra[i + 1] + shot.Bgra[i + 2] < 600)
                ink++;
        }
        return ink;
    }

    private static List<string> LineTexts(IPdfDocument doc)
    {
        using var page = doc.GetPage(0);
        return page.GetTextLines().Select(l => l.Text).ToList();
    }

    [Theory]
    [InlineData("Fake bold heading")]
    [InlineData("Filled then stroked")]
    [InlineData("Shadowed line")]
    [InlineData("Two runs")]
    public void DeleteLine_TakesTheHiddenCopy_UndoPutsBothBack(string start)
    {
        var saved = Path.Combine(_dir, $"deleted-{Guid.NewGuid():N}.pdf");
        PdfTextLine line;
        using (var doc = _engine.Open(WritePdf()))
        {
            var stack = new UndoStack();
            byte[] before;
            using (var page = doc.GetPage(0))
            {
                Assert.Equal(6, page.GetTextLines().Count);
                line = Line(page, start);
                before = Shot(page);
            }

            stack.Do(new DeleteLineOperation(doc, 0, line));
            using (var page = doc.GetPage(0))
            {
                Assert.Equal(0, InkIn(page, line.Bounds));
                Assert.Equal(5, page.GetTextLines().Count);
            }

            stack.Undo();
            using (var page = doc.GetPage(0))
                Assert.Equal(before, Shot(page));

            stack.Redo();
            using var stream = File.Create(saved);
            doc.Save(stream);
        }

        // Before #136 the copy surfaced here as the line itself.
        using var reopened = _engine.Open(saved);
        using var reopenedPage = reopened.GetPage(0);
        Assert.Equal(0, InkIn(reopenedPage, line.Bounds));
        Assert.DoesNotContain(reopenedPage.GetTextLines(), l => l.Text.StartsWith(start));
        Assert.Equal(5, reopenedPage.GetTextLines().Count);
    }

    [Theory]
    [InlineData("Fake bold heading")]
    [InlineData("Filled then stroked")]
    [InlineData("Shadowed line")]
    [InlineData("Two runs")]
    public void LineEdit_TakesTheHiddenCopy_UndoPutsBothBack(string start)
    {
        var saved = Path.Combine(_dir, $"edited-{Guid.NewGuid():N}.pdf");
        PdfTextLine line;
        using (var doc = _engine.Open(WritePdf()))
        {
            var stack = new UndoStack();
            byte[] before;
            using (var page = doc.GetPage(0))
            {
                line = Line(page, start);
                before = Shot(page);
            }

            var edit = new LineEditOperation(doc, 0, line, "Retyped");
            stack.Do(edit);
            Assert.Equal(TextEditOutcome.EditedInPlace, edit.LastOutcome);
            stack.Undo();
            using (var page = doc.GetPage(0))
                Assert.Equal(before, Shot(page));

            stack.Redo();
            using var stream = File.Create(saved);
            doc.Save(stream);
        }

        // One run where the line was, and nothing of the old text drawn beside or under it.
        using var reopened = _engine.Open(saved);
        using var reopenedPage = reopened.GetPage(0);
        var edited = Line(reopenedPage, "Retyped");
        Assert.Equal("Retyped", edited.Text);
        Assert.Single(edited.Runs);
        Assert.DoesNotContain(reopenedPage.GetTextLines(), l => l.Text.StartsWith(start));
        var beyond = new PdfRect(edited.Bounds.Right + 2, line.Bounds.Y, line.Bounds.Right - edited.Bounds.Right - 2, line.Bounds.Height);
        Assert.True(beyond.Width > 20);
        Assert.Equal(0, InkIn(reopenedPage, beyond));
    }

    [Fact]
    public void Journal_DeleteUndoAndLaterEdits_ReplayOntoTheSameObjects()
    {
        var path = WritePdf();
        var entries = new List<JournalEntry>();
        List<string> live;
        using (var doc = _engine.Open(path))
        {
            PdfTextLine bold, closing;
            using (var page = doc.GetPage(0))
            {
                bold = Line(page, "Fake bold heading");
                closing = Line(page, "The closing line");
            }

            var delete = new DeleteLineOperation(doc, 0, bold);
            delete.Apply();
            entries.Add(delete.ToJournalEntry(inverse: false));
            delete.Revert();
            entries.Add(delete.ToJournalEntry(inverse: true));
            // Named by the index it has with both copies of the heading back on the page.
            var edit = new LineEditOperation(doc, 0, closing, "Closing, retyped");
            edit.Apply();
            entries.Add(edit.ToJournalEntry(inverse: false));
            // And the heading again: replay must find its recreated copy as the live page found the real one.
            delete.Apply();
            entries.Add(delete.ToJournalEntry(inverse: false));
            live = LineTexts(doc);
        }

        var restore = Assert.IsType<LineRestoreEntry>(entries[1]);
        Assert.Equal([1, 2], restore.Restores.Select(r => r.Index));

        using var fresh = _engine.Open(path);
        Assert.Equal(entries.Count, JournalReplayer.Replay(fresh, entries));
        Assert.Equal(live, LineTexts(fresh));
        Assert.DoesNotContain(live, t => t.StartsWith("Fake bold heading"));
        Assert.Contains("Closing, retyped", live);
    }

    [Fact]
    public void Journal_EditOfADoubledRunAndItsUndo_ReplayOntoTheSameObjects()
    {
        var path = WritePdf();
        var entries = new List<JournalEntry>();
        List<string> live;
        using (var doc = _engine.Open(path))
        {
            PdfTextLine shadow, two, closing;
            using (var page = doc.GetPage(0))
            {
                shadow = Line(page, "Shadowed line");
                two = Line(page, "Two runs");
                closing = Line(page, "The closing line");
            }

            var edit = new LineEditOperation(doc, 0, shadow, "Shadow, retyped");
            edit.Apply();
            entries.Add(edit.ToJournalEntry(inverse: false));
            edit.Revert();
            entries.Add(edit.ToJournalEntry(inverse: true));
            var twoEdit = new LineEditOperation(doc, 0, two, "One run now");
            twoEdit.Apply();
            entries.Add(twoEdit.ToJournalEntry(inverse: false));
            twoEdit.Revert();
            entries.Add(twoEdit.ToJournalEntry(inverse: true));
            var closingEdit = new LineEditOperation(doc, 0, closing, "Closing, retyped");
            closingEdit.Apply();
            entries.Add(closingEdit.ToJournalEntry(inverse: false));
            live = LineTexts(doc);
        }

        // The edited run had a hidden copy, so its text is not set back: the line is recreated.
        var shadowRestore = Assert.IsType<LineRestoreEntry>(entries[1]);
        Assert.Null(shadowRestore.FirstText);
        Assert.Equal([5, 6], shadowRestore.Restores.Select(r => r.Index));

        using var fresh = _engine.Open(path);
        Assert.Equal(entries.Count, JournalReplayer.Replay(fresh, entries));
        Assert.Equal(live, LineTexts(fresh));
        Assert.Contains("Closing, retyped", live);
    }

    [Fact]
    public void SingleRunOperations_TakeTheHiddenCopy_AndJournalIt()
    {
        var path = WritePdf();
        var entries = new List<JournalEntry>();
        List<string> live;
        using (var doc = _engine.Open(path))
        {
            PdfTextRun bold, stroked, closing;
            byte[] before;
            using (var page = doc.GetPage(0))
            {
                bold = Line(page, "Fake bold heading").Runs.Single();
                stroked = Line(page, "Filled then stroked").Runs.Single();
                closing = Line(page, "The closing line").Runs.Single();
                before = Shot(page);
            }

            var delete = new DeleteTextOperation(doc, 0, bold);
            delete.Apply();
            entries.Add(delete.ToJournalEntry(inverse: false));
            using (var page = doc.GetPage(0))
                Assert.Equal(0, InkIn(page, bold.Bounds));
            delete.Revert();
            entries.Add(delete.ToJournalEntry(inverse: true));
            Assert.IsType<LineRestoreEntry>(entries[^1]);

            var edit = new TextEditOperation(doc, 0, stroked, "Stroked, retyped");
            edit.Apply();
            entries.Add(edit.ToJournalEntry(inverse: false));
            edit.Revert();
            entries.Add(edit.ToJournalEntry(inverse: true));
            using (var page = doc.GetPage(0))
                Assert.Equal(before, Shot(page));

            var closingEdit = new TextEditOperation(doc, 0, closing, "Closing, retyped");
            closingEdit.Apply();
            entries.Add(closingEdit.ToJournalEntry(inverse: false));
            live = LineTexts(doc);
        }

        using var fresh = _engine.Open(path);
        Assert.Equal(entries.Count, JournalReplayer.Replay(fresh, entries));
        Assert.Equal(live, LineTexts(fresh));
    }

    [Fact]
    public void PlainLine_TakesOnlyItsOwnRun()
    {
        using var doc = _engine.Open(WritePdf());
        using var page = doc.GetPage(0);
        var plain = Line(page, "A plain body line");
        var detached = page.DetachTextRuns(plain.Runs);
        Assert.Equal([new DetachedPart(plain.Runs[0].ObjectIndex, -1)], detached.Parts);
        page.RestoreTextRun(detached, plain.Runs[0].ObjectIndex);
        Assert.Equal(6, page.GetTextLines().Count);
    }
}
