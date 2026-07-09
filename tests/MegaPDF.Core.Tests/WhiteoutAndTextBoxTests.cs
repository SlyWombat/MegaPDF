using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Recovery;
using Xunit;

namespace MegaPDF.Core.Tests;

public class WhiteoutAndTextBoxTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-whiteout-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // The fixture image: 120x60pt at PDF 100,480 → top-left-space y = 792-540 = 252.
    private static readonly PdfRect ImageArea = new(100, 252, 120, 60);

    private string WritePdf()
    {
        var path = Path.Combine(_dir, $"img-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.BuildWithImage());
        return path;
    }

    [Fact]
    public void Whiteout_CoversAnImage_AndPersists()
    {
        var savedPath = Path.Combine(_dir, "covered.pdf");
        using (var doc = _engine.Open(WritePdf()))
        {
            using (var page = doc.GetPage(0))
            {
                // The image renders dark before the whiteout…
                Assert.False(RegionIsAllWhite(page.Render(612, 792), 105, 257, 40, 20));

                var index = page.AppendWhiteout(ImageArea);
                Assert.True(index >= 0);

                // …and pure white after.
                Assert.True(RegionIsAllWhite(page.Render(612, 792), 105, 257, 40, 20));
                var whiteout = Assert.Single(page.GetWhiteouts());
                Assert.Equal(ImageArea.X, whiteout.Bounds.X, 1);

                Assert.Equal(PageHitKind.Whiteout, page.HitTest(ImageArea.Center).Kind);
            }
            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        using var reopenedPage = reopened.GetPage(0);
        Assert.True(RegionIsAllWhite(reopenedPage.Render(612, 792), 105, 257, 40, 20));
        Assert.Single(reopenedPage.GetWhiteouts());
    }

    [Fact]
    public void TextBox_OverWhiteout_RendersAboveIt_AndStaysEditable()
    {
        using var doc = _engine.Open(WritePdf());
        using var page = doc.GetPage(0);

        page.AppendWhiteout(ImageArea);
        page.AppendTextBox("Corrected value", 14, new PdfPoint(105, 260));

        // Ink on top of the whiteout.
        Assert.False(RegionIsAllWhite(page.Render(612, 792), 105, 258, 100, 18));

        // Clicking the text hits the LINE, not the whiteout beneath it.
        var hit = page.HitTest(new PdfPoint(130, 270));
        Assert.Equal(PageHitKind.TextRun, hit.Kind);
        Assert.Equal("Corrected value", hit.TextLine!.Text);

        // And it's a normal editable run.
        page.SetTextRunText(hit.TextLine.Runs[0], "Edited again");
        Assert.Contains(page.GetTextLines(), l => l.Text == "Edited again");
    }

    [Fact]
    public void WhiteoutOperations_UndoRedo()
    {
        using var doc = _engine.Open(WritePdf());
        var stack = new UndoStack();

        stack.Do(new AddWhiteoutOperation(doc, 0, ImageArea));
        using (var page = doc.GetPage(0))
            Assert.Single(page.GetWhiteouts());

        stack.Undo();
        using (var page = doc.GetPage(0))
        {
            Assert.Empty(page.GetWhiteouts());
            Assert.False(RegionIsAllWhite(page.Render(612, 792), 105, 257, 40, 20), "image visible again");
        }

        stack.Redo();
        using (var page = doc.GetPage(0))
            Assert.Single(page.GetWhiteouts());

        // Remove the placed whiteout, then undo the removal.
        int index;
        using (var page = doc.GetPage(0))
            index = page.GetWhiteouts()[0].ObjectIndex;
        stack.Do(new RemoveWhiteoutOperation(doc, 0, index, ImageArea));
        using (var page = doc.GetPage(0))
            Assert.Empty(page.GetWhiteouts());
        stack.Undo();
        using (var page = doc.GetPage(0))
            Assert.Single(page.GetWhiteouts());
    }

    [Fact]
    public void TextBoxOperation_UndoRedo_AndSave()
    {
        var savedPath = Path.Combine(_dir, "textbox.pdf");
        using (var doc = _engine.Open(WritePdf()))
        {
            var stack = new UndoStack();
            stack.Do(new AddTextBoxOperation(doc, 0, "Sticky note", 12, new PdfPoint(200, 400)));

            using (var page = doc.GetPage(0))
                Assert.Contains(page.GetTextLines(), l => l.Text == "Sticky note");

            stack.Undo();
            using (var page = doc.GetPage(0))
                Assert.DoesNotContain(page.GetTextLines(), l => l.Text == "Sticky note");

            stack.Redo();
            using (var page = doc.GetPage(0))
                Assert.Contains(page.GetTextLines(), l => l.Text == "Sticky note");

            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        using var reopenedPage = reopened.GetPage(0);
        Assert.Contains(reopenedPage.GetTextLines(), l => l.Text == "Sticky note");
    }

    [Fact]
    public void Journal_WhiteoutAddAndRemove_Replay()
    {
        var docPath = WritePdf();
        using var doc = _engine.Open(docPath);
        var add = new AddWhiteoutOperation(doc, 0, ImageArea);
        add.Apply();
        var entries = new List<JournalEntry>
        {
            add.ToJournalEntry(inverse: false),
            add.ToJournalEntry(inverse: true), // an undo
        };

        using var fresh = _engine.Open(docPath);
        Assert.Equal(2, JournalReplayer.Replay(fresh, entries));
        using var freshPage = fresh.GetPage(0);
        Assert.Empty(freshPage.GetWhiteouts());
    }

    private static bool RegionIsAllWhite(RenderedPage rendered, int x, int y, int w, int h)
    {
        for (var row = y; row < y + h; row++)
            for (var col = x; col < x + w; col++)
            {
                var i = (row * rendered.PixelWidth + col) * 4;
                if (rendered.Bgra[i] != 0xFF || rendered.Bgra[i + 1] != 0xFF || rendered.Bgra[i + 2] != 0xFF)
                    return false;
            }
        return true;
    }
}
