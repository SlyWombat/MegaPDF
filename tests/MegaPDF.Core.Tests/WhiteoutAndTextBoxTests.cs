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

    /// <summary>
    /// #43: the face is carried on the mark, not inferred from the font resource —
    /// pdfium is free to normalise a standard font's reported name, so the only
    /// thing that can be a cross-platform contract is what we wrote down.
    /// </summary>
    [Fact]
    public void TextBox_RecordsTheChosenFaceAndSize_AndSurvivesSave()
    {
        var savedPath = Path.Combine(_dir, "faced.pdf");
        using (var doc = _engine.Open(WritePdf()))
        {
            using (var page = doc.GetPage(0))
            {
                page.AppendTextBox("Eighteen point Times", 18, new PdfPoint(105, 260),
                    StandardTextBoxFonts.Serif);
                var box = Assert.Single(page.GetTextBoxes());
                Assert.Equal(StandardTextBoxFonts.Serif, box.TextBoxFont);
                Assert.Equal(18, box.FontSize, 1);
            }
            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        using var reopenedPage = reopened.GetPage(0);
        var reloaded = Assert.Single(reopenedPage.GetTextBoxes());
        Assert.Equal(StandardTextBoxFonts.Serif, reloaded.TextBoxFont);
        Assert.Equal(18, reloaded.FontSize, 1);
    }

    /// <summary>
    /// Restyling (#43) keeps the box's id and its bottom-left corner. The size change
    /// is where the anchor choice shows: growing 12 pt to 18 pt makes the glyphs
    /// taller, and the box must grow *upward* from the rule it sits on rather than
    /// sinking through it. Undo restores the original object byte-identically.
    /// </summary>
    [Fact]
    public void RestyleTextBox_KeepsItsIdAndCorner_AndGrowsUpward()
    {
        using var doc = _engine.Open(WritePdf());
        var stack = new UndoStack();

        int objectIndex;
        PdfTextRun before;
        string id;
        using (var page = doc.GetPage(0))
        {
            page.AppendTextBox("Paying agent", 12, new PdfPoint(105, 260));
            before = Assert.Single(page.GetTextBoxes());
            objectIndex = before.ObjectIndex;
            id = Assert.IsType<string>(before.TextBoxId);
        }

        stack.Do(new RestyleTextBoxOperation(doc, 0, objectIndex, before,
            "Paying agent", StandardTextBoxFonts.Serif, 18));

        using (var page = doc.GetPage(0))
        {
            var after = Assert.Single(page.GetTextBoxes());
            Assert.Equal(id, after.TextBoxId);
            Assert.Equal(StandardTextBoxFonts.Serif, after.TextBoxFont);
            Assert.Equal(18, after.FontSize, 1);
            Assert.Equal(before.Bounds.X, after.Bounds.X, 1);
            // Page space is top-left, so "grew upward" is a smaller Y with the
            // bottom edge pinned.
            Assert.Equal(before.Bounds.Bottom, after.Bounds.Bottom, 1);
            Assert.True(after.Bounds.Y < before.Bounds.Y,
                "bigger text must grow upward, not downward");
        }

        stack.Undo();

        using (var page = doc.GetPage(0))
        {
            var reverted = Assert.Single(page.GetTextBoxes());
            Assert.Equal(id, reverted.TextBoxId);
            Assert.Equal(StandardTextBoxFonts.Default, reverted.TextBoxFont);
            Assert.Equal(12, reverted.FontSize, 1);
            Assert.Equal(before.Bounds.Y, reverted.Bounds.Y, 1);
        }
    }

    /// <summary>A box written before #43 carries no face, and every one of them is Helvetica.</summary>
    [Fact]
    public void TextBox_WithNoRecordedFace_ReadsAsHelvetica()
    {
        using var doc = _engine.Open(WritePdf());
        using var page = doc.GetPage(0);

        page.AppendTextBox("Default face", 12, new PdfPoint(105, 260));
        var box = Assert.Single(page.GetTextBoxes());
        Assert.Equal(StandardTextBoxFonts.Default, box.TextBoxFont);
        Assert.Equal(StandardTextBoxFonts.Sans, box.TextBoxFont);
    }

    /// <summary>
    /// Deliberately strict: the app passes one of three constants, so anything else
    /// is a bug and should fail loudly rather than silently render in the wrong face.
    /// </summary>
    [Fact]
    public void TextBox_RejectsAFaceOutsideTheThree()
    {
        using var doc = _engine.Open(WritePdf());
        using var page = doc.GetPage(0);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => page.AppendTextBox("Nope", 12, new PdfPoint(105, 260), "Comic Sans MS"));
        Assert.Empty(page.GetTextBoxes());
    }

    /// <summary>
    /// A journal written before #43 has no face recorded; replaying it must not
    /// throw, and must produce the Helvetica box it described.
    /// </summary>
    [Fact]
    public void TextBoxAddEntry_WithoutAFace_DefaultsToHelvetica()
    {
        var entry = new TextBoxAddEntry(0, "Recovered", 12, 150, 300);
        Assert.Equal(StandardTextBoxFonts.Default, entry.FontName);
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

        // Clicking the text selects the movable text box, not the whiteout beneath it.
        var hit = page.HitTest(new PdfPoint(130, 270));
        Assert.Equal(PageHitKind.TextBox, hit.Kind);
        Assert.Equal("Corrected value", hit.TextLine!.Text);

        // And it's still an editable run — edited in place keeps its movable tag.
        page.SetTextRunText(hit.TextLine.Runs[0], "Edited again");
        var box = Assert.Single(page.GetTextBoxes());
        Assert.Equal("Edited again", box.Text);
        Assert.Equal(PageHitKind.TextBox, page.HitTest(box.Bounds.Center).Kind);
    }

    [Fact]
    public void AddedText_IsTaggedAsMovableTextBox_NotBodyText()
    {
        using var doc = _engine.Open(WritePdf());
        using var page = doc.GetPage(0);

        page.AppendTextBox("Movable note", 14, new PdfPoint(105, 260));

        var box = Assert.Single(page.GetTextBoxes());
        Assert.Equal("Movable note", box.Text);

        var hit = page.HitTest(new PdfPoint(130, 268));
        Assert.Equal(PageHitKind.TextBox, hit.Kind);
        Assert.Equal(box.ObjectIndex, hit.ObjectIndex);
        Assert.Equal("Movable note", hit.TextLine!.Text);
    }

    /// <summary>
    /// SDD §6.2 contract 4: a box written here must be addressable by id on the
    /// phones, which work by id because page-object indices shift under them.
    /// </summary>
    [Fact]
    public void AddedText_CarriesAnAddressableId_ThatSurvivesSave()
    {
        var savedPath = Path.Combine(_dir, "text-box-id.pdf");
        string id;
        using (var doc = _engine.Open(WritePdf()))
        {
            using (var page = doc.GetPage(0))
            {
                page.AppendTextBox("Movable note", 14, new PdfPoint(105, 260));
                var box = Assert.Single(page.GetTextBoxes());
                Assert.NotNull(box.TextBoxId);
                Assert.StartsWith("text:", box.TextBoxId);
                id = box.TextBoxId!;
            }
            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        using var reopenedPage = reopened.GetPage(0);
        var after = Assert.Single(reopenedPage.GetTextBoxes());
        Assert.Equal("Movable note", after.Text);
        Assert.Equal(id, after.TextBoxId);
    }

    [Fact]
    public void MoveTextBoxOperation_MovesInPlace_AndUndoes()
    {
        using var doc = _engine.Open(WritePdf());
        var stack = new UndoStack();
        stack.Do(new AddTextBoxOperation(doc, 0, "Drag me", 12, new PdfPoint(150, 300)));

        PdfRect before;
        int index;
        using (var page = doc.GetPage(0))
        {
            var box = Assert.Single(page.GetTextBoxes());
            before = box.Bounds;
            index = box.ObjectIndex;
        }

        var moved = new PdfRect(before.X + 40, before.Y + 25, before.Width, before.Height);
        stack.Do(new MoveTextBoxOperation(doc, 0, index, before, moved));
        using (var page = doc.GetPage(0))
        {
            var box = Assert.Single(page.GetTextBoxes());
            Assert.Equal(before.X + 40, box.Bounds.X, 1);
            Assert.Equal(before.Y + 25, box.Bounds.Y, 1);
            // In-place translation keeps the object index (and text) stable.
            Assert.Equal(index, box.ObjectIndex);
            Assert.Equal("Drag me", box.Text);
        }

        stack.Undo();
        using (var page = doc.GetPage(0))
        {
            var box = Assert.Single(page.GetTextBoxes());
            Assert.Equal(before.X, box.Bounds.X, 1);
            Assert.Equal(before.Y, box.Bounds.Y, 1);
        }
    }

    [Fact]
    public void MoveTextBox_PersistsAcrossSave()
    {
        var savedPath = Path.Combine(_dir, "moved-text.pdf");
        PdfRect moved;
        using (var doc = _engine.Open(WritePdf()))
        {
            using (var page = doc.GetPage(0))
            {
                page.AppendTextBox("Relocate", 12, new PdfPoint(150, 300));
                var box = page.GetTextBoxes()[0];
                moved = new PdfRect(box.Bounds.X + 60, box.Bounds.Y + 30, box.Bounds.Width, box.Bounds.Height);
                page.MoveTextBox(box.ObjectIndex, moved);
            }
            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        using var reopenedPage = reopened.GetPage(0);
        var reopenedBox = Assert.Single(reopenedPage.GetTextBoxes());
        Assert.Equal(moved.X, reopenedBox.Bounds.X, 1);
        Assert.Equal(moved.Y, reopenedBox.Bounds.Y, 1);
    }

    [Fact]
    public void RemoveTextBoxOperation_UndoRedo()
    {
        using var doc = _engine.Open(WritePdf());
        var stack = new UndoStack();
        stack.Do(new AddTextBoxOperation(doc, 0, "Delete me", 12, new PdfPoint(150, 300)));

        PdfTextRun run;
        using (var page = doc.GetPage(0))
            run = Assert.Single(page.GetTextBoxes());

        stack.Do(new RemoveTextBoxOperation(doc, 0, run.ObjectIndex, run));
        using (var page = doc.GetPage(0))
            Assert.Empty(page.GetTextBoxes());

        stack.Undo();
        using (var page = doc.GetPage(0))
        {
            var box = Assert.Single(page.GetTextBoxes());
            Assert.Equal("Delete me", box.Text);
        }
    }

    [Fact]
    public void Journal_TextBoxAddAndMove_Replay()
    {
        var docPath = WritePdf();
        List<JournalEntry> entries;
        PdfRect moved;
        using (var doc = _engine.Open(docPath))
        {
            var add = new AddTextBoxOperation(doc, 0, "Journaled", 12, new PdfPoint(150, 300));
            add.Apply();

            PdfRect before;
            int index;
            using (var page = doc.GetPage(0))
            {
                var box = page.GetTextBoxes()[0];
                before = box.Bounds;
                index = box.ObjectIndex;
            }
            moved = new PdfRect(before.X + 30, before.Y + 20, before.Width, before.Height);
            var move = new MoveTextBoxOperation(doc, 0, index, before, moved);
            move.Apply();

            entries =
            [
                add.ToJournalEntry(inverse: false),
                move.ToJournalEntry(inverse: false),
            ];
        }

        using var fresh = _engine.Open(docPath);
        Assert.Equal(2, JournalReplayer.Replay(fresh, entries));
        using var freshPage = fresh.GetPage(0);
        var replayed = Assert.Single(freshPage.GetTextBoxes());
        Assert.Equal("Journaled", replayed.Text);
        Assert.Equal(moved.X, replayed.Bounds.X, 1);
        Assert.Equal(moved.Y, replayed.Bounds.Y, 1);
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
