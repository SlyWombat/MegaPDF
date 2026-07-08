using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

public class DrawnCheckboxTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-drawnbox-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // The 18pt square at PDF 100,500 → top-left-space y = 792-518 = 274.
    private static readonly PdfPoint SquareCenter = new(109, 283);

    [Fact]
    public void Detect_FindsSmallStrokedSquare_IgnoresBigAndFilled()
    {
        using var doc = _engine.Open(WritePdf());
        using var page = doc.GetPage(0);

        var squares = page.DetectCheckboxSquares();

        var square = Assert.Single(squares);
        Assert.InRange(square.X, 97, 101);
        Assert.InRange(square.Y, 271, 276);
        Assert.InRange(square.Width, 17, 21);
    }

    [Fact]
    public void HitTest_SquareReturnsDrawnCheckbox_TextStillWins()
    {
        using var doc = _engine.Open(WritePdf());
        using var page = doc.GetPage(0);

        var hit = page.HitTest(SquareCenter);
        Assert.Equal(PageHitKind.DrawnCheckbox, hit.Kind);
        Assert.NotNull(hit.Bounds);

        var textHit = page.HitTest(new PdfPoint(160, 283));
        Assert.Equal(PageHitKind.TextRun, textHit.Kind);
    }

    [Fact]
    public void AddMark_RendersInk_PersistsThroughSave_AndHitTestsAsStamp()
    {
        var savedPath = Path.Combine(_dir, "checked.pdf");
        string id;
        using (var doc = _engine.Open(WritePdf()))
        {
            using (var page = doc.GetPage(0))
            {
                var square = page.HitTest(SquareCenter).Bounds!.Value;
                id = page.AddCheckMarkStamp(square);

                var stampHit = page.HitTest(SquareCenter);
                Assert.Equal(PageHitKind.StampAnnotation, stampHit.Kind);
                Assert.Equal(id, stampHit.AnnotationId);

                // Ink inside the square area on render (306x396 = half scale).
                var rendered = page.Render(306, 396);
                Assert.True(RegionHasInk(rendered, 50, 137, 10, 10), "mark should draw inside the square");
            }
            using var stream = File.Create(savedPath);
            doc.Save(stream);
        }

        using var reopened = _engine.Open(savedPath);
        using var reopenedPage = reopened.GetPage(0);
        var hit = reopenedPage.HitTest(SquareCenter);
        Assert.Equal(PageHitKind.StampAnnotation, hit.Kind);
        Assert.Equal(id, hit.AnnotationId);
    }

    [Fact]
    public void RemoveMark_RestoresDrawnCheckboxHit()
    {
        using var doc = _engine.Open(WritePdf());
        using var page = doc.GetPage(0);
        var square = page.HitTest(SquareCenter).Bounds!.Value;
        var id = page.AddCheckMarkStamp(square);

        page.RemoveStampAnnotation(id);

        Assert.Equal(PageHitKind.DrawnCheckbox, page.HitTest(SquareCenter).Kind);
    }

    [Fact]
    public void MarkOperations_UndoRedo_ToggleCorrectly()
    {
        using var doc = _engine.Open(WritePdf());
        var stack = new UndoStack();
        PdfRect square;
        using (var page = doc.GetPage(0))
            square = page.HitTest(SquareCenter).Bounds!.Value;

        stack.Do(new AddMarkOperation(doc, 0, square));
        using (var page = doc.GetPage(0))
            Assert.Equal(PageHitKind.StampAnnotation, page.HitTest(SquareCenter).Kind);

        stack.Undo();
        using (var page = doc.GetPage(0))
            Assert.Equal(PageHitKind.DrawnCheckbox, page.HitTest(SquareCenter).Kind);

        stack.Redo();
        using (var page = doc.GetPage(0))
            Assert.Equal(PageHitKind.StampAnnotation, page.HitTest(SquareCenter).Kind);
    }

    private static bool RegionHasInk(RenderedPage rendered, int x, int y, int w, int h)
    {
        for (var row = y; row < y + h; row++)
        {
            for (var col = x; col < x + w; col++)
            {
                var i = (row * rendered.PixelWidth + col) * 4;
                if (rendered.Bgra[i] < 0x80 && rendered.Bgra[i + 1] < 0x80 && rendered.Bgra[i + 2] < 0x80)
                    return true;
            }
        }
        return false;
    }

    private string WritePdf()
    {
        var path = Path.Combine(_dir, $"squares-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.BuildWithDrawnSquares());
        return path;
    }
}
