using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// The mark lifecycle against the real engine (#173, #329 — ADR-005 decision 4): one gesture
/// is one history step however many marks it made, Undo takes the marks off the core, Redo
/// puts the same rectangles back, and nothing about a mark ever reaches the file.
///
/// These are the properties no eye can check: a mark looks the same whether it is on the undo
/// stack or beside it, and the file it is saved into is the file it would have been without
/// it. Every assertion here is about the core, so all four platforms are covered by one run.
/// </summary>
public class RedactionMarkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-redactmark-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>The engine opens files, not bytes, so a fixture is written out first.</summary>
    private string WritePdf(byte[] bytes)
    {
        var path = Path.Combine(_dir, $"sample-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private IPdfDocument Open() => _engine.Open(WritePdf(SamplePdf.Build()));

    // "Hello MegaPDF" is drawn at 36pt from PDF 72,700 — one line of text with room
    // below it, and an empty area to the right of it for the no-text case.

    /// <summary>The drawn line's own box: a drag over it marks the text it covers.</summary>
    private static PdfRect OverTheLine(IPdfDocument doc)
    {
        using var page = doc.GetPage(0);
        return page.GetTextRuns().First().Bounds;
    }

    /// <summary>An area with nothing drawn in it, so the drag is marked as a rectangle.</summary>
    private static readonly PdfRect EmptyArea = new(400, 500, 80, 60);

    /// <summary>Every mark on a page, in a stable order, so two states can be compared.</summary>
    private static List<PdfRect> Rectangles(IPdfDocument doc, int pageIndex = 0)
    {
        using var page = doc.GetPage(pageIndex);
        return [.. page.GetRedactionMarks().Select(m => m.Bounds).OrderBy(r => r.Y).ThenBy(r => r.X)];
    }

    /// <summary>The rectangle the page carries for one mark by id, or null when it has none.</summary>
    private static PdfRect? BoundsOf(IPdfDocument doc, int markId, int pageIndex = 0)
    {
        using var page = doc.GetPage(pageIndex);
        foreach (var mark in page.GetRedactionMarks())
        {
            if (mark.MarkId == markId)
                return mark.Bounds;
        }
        return null;
    }

    /// <summary>A mark's rectangle goes through the core as a float; half a point is the same area.</summary>
    private static bool Same(PdfRect a, PdfRect b) =>
        Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5
        && Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;

    private static bool Same(IReadOnlyList<PdfRect> a, IReadOnlyList<PdfRect> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => Same(pair.First, pair.Second));

    [Fact]
    public void Place_MarksTheAreaTheGestureCovered_AsOneOperation()
    {
        using var doc = Open();
        var drag = OverTheLine(doc);

        var op = MarkForRedactionOperation.Place(doc, 0, drag);

        Assert.NotNull(op);
        Assert.NotEmpty(op!.MarkIds);
        // One gesture, however many marks it made: the ids it reports are the marks that
        // are actually on the page, and the whole set is a single operation.
        Assert.Equal(op.MarkIds.Count, doc.RedactionMarkCount);
        Assert.False(op.ChangesTheFile);
    }

    [Fact]
    public void Place_OverNothing_MarksTheRectangleItself()
    {
        using var doc = Open();

        var op = MarkForRedactionOperation.Place(doc, 0, EmptyArea);

        Assert.NotNull(op);
        var mark = Assert.Single(Rectangles(doc));
        Assert.True(Same(mark, EmptyArea), $"marked {mark}, expected {EmptyArea}");
    }

    [Fact]
    public void Record_ThenUndo_LeavesTheCoreEmpty_AndRedo_RestoresTheSameRectangles()
    {
        using var doc = Open();
        var op = MarkForRedactionOperation.Place(doc, 0, OverTheLine(doc))!;
        var marked = Rectangles(doc);
        var stack = new UndoStack();

        // The gesture is already in the core: the call that answered "did this cover text?"
        // made the marks as it answered, so it is RECORDED rather than applied again (#329).
        stack.Record(op);

        Assert.True(stack.CanUndo);
        Assert.True(Same(Rectangles(doc), marked));

        stack.Undo();
        Assert.Equal(0, doc.RedactionMarkCount);
        Assert.Empty(Rectangles(doc));

        stack.Redo();
        Assert.Equal(marked.Count, doc.RedactionMarkCount);
        // Redo replays the recorded rectangles rather than re-running the text selection,
        // which would re-derive glyph runs from the page as it is now: the person is owed
        // the area they saw.
        Assert.True(Same(Rectangles(doc), marked));
    }

    [Fact]
    public void Remove_IsUndoable_AndPutsTheMarkBackWhereItWas()
    {
        using var doc = Open();
        var op = MarkForRedactionOperation.Place(doc, 0, OverTheLine(doc))!;
        var markId = op.MarkIds[0];
        var bounds = BoundsOf(doc, markId)!.Value;
        var stack = new UndoStack();

        var remove = new RemoveRedactionMarkOperation(doc, 0, markId, bounds);
        Assert.False(remove.ChangesTheFile);
        stack.Do(remove);

        Assert.Equal(op.MarkIds.Count - 1, doc.RedactionMarkCount);
        Assert.Null(BoundsOf(doc, markId));
        Assert.DoesNotContain(Rectangles(doc), r => Same(r, bounds));

        stack.Undo();

        // A re-mark takes a fresh id — the core never reuses one — so the mark comes back
        // where it was under a new name, which is why a view must not keep the old one.
        Assert.Equal(op.MarkIds.Count, doc.RedactionMarkCount);
        Assert.Contains(Rectangles(doc), r => Same(r, bounds));
    }

    [Fact]
    public void Move_IsUndoable_AndTakesTheMarkBackToWhereItWas()
    {
        using var doc = Open();
        var op = MarkForRedactionOperation.Place(doc, 0, OverTheLine(doc))!;
        var id = op.MarkIds[0];
        var from = BoundsOf(doc, id)!.Value;
        var to = new PdfRect(from.X + 40, from.Y + 25, from.Width, from.Height);
        var stack = new UndoStack();

        var move = new MoveRedactionMarkOperation(doc, 0, id, from, to);
        Assert.False(move.ChangesTheFile);
        stack.Do(move);

        Assert.True(Same(BoundsOf(doc, id)!.Value, to));

        stack.Undo();
        Assert.True(Same(BoundsOf(doc, id)!.Value, from));

        // A move is an absolute rectangle, not a delta, so a redo that replays it lands in
        // the same place however many times it is undone and redone.
        stack.Redo();
        stack.Undo();
        stack.Redo();
        Assert.True(Same(BoundsOf(doc, id)!.Value, to));
    }

    [Fact]
    public void Resize_KeepsTheRectangleItIsGiven_RatherThanAShapeItHad()
    {
        using var doc = Open();
        var op = MarkForRedactionOperation.Place(doc, 0, OverTheLine(doc))!;
        var id = op.MarkIds[0];
        var from = BoundsOf(doc, id)!.Value;
        // A corner drag on one edge: the aspect changes, and that is the point (#329) — an
        // area is what the person is deciding to cover.
        var to = new PdfRect(from.X, from.Y, from.Width * 2, from.Height + 30);
        var stack = new UndoStack();

        stack.Do(new MoveRedactionMarkOperation(doc, 0, id, from, to));

        Assert.True(Same(BoundsOf(doc, id)!.Value, to));
        Assert.True(Math.Abs(to.Width / to.Height - from.Width / from.Height) > 0.1);
    }

    [Fact]
    public void ClearAll_DropsEveryMarkOnEveryPage_AsOneStep()
    {
        using var doc = Open();
        MarkForRedactionOperation.Place(doc, 0, OverTheLine(doc));
        MarkForRedactionOperation.Place(doc, 0, EmptyArea);
        var marked = Rectangles(doc);
        Assert.True(marked.Count >= 2, $"expected two gestures to leave two marks, got {marked.Count}");
        var stack = new UndoStack();

        var clear = ClearRedactionMarksOperation.Capture(doc, 0);
        Assert.NotNull(clear);
        Assert.False(clear!.ChangesTheFile);
        stack.Do(clear);

        Assert.Equal(0, doc.RedactionMarkCount);
        Assert.Empty(Rectangles(doc));

        // One press of Undo brings every one of them back, on every page, where they were.
        stack.Undo();
        Assert.Equal(marked.Count, doc.RedactionMarkCount);
        Assert.True(Same(Rectangles(doc), marked));
    }

    [Fact]
    public void ClearAll_WithNothingToClear_IsNotAnOperation()
    {
        using var doc = Open();

        Assert.Null(ClearRedactionMarksOperation.Capture(doc, 0));
    }

    [Fact]
    public void ASavedCopyCarriesNoMarks_AndTheTextItStillHas()
    {
        var path = Path.Combine(Path.GetTempPath(), $"megapdf-redactmarks-{Guid.NewGuid():N}.pdf");
        var drag = default(PdfRect);
        try
        {
            using (var doc = Open())
            using (var measure = doc.GetPage(0))
            {
                drag = measure.GetTextRuns().First().Bounds;
                MarkForRedactionOperation.Place(doc, 0, drag);
                Assert.True(doc.RedactionMarkCount > 0);
                using var file = File.Create(path);
                doc.Save(file);
            }

            using var reopened = _engine.Open(path);
            // A mark is the core's own and is never written (ADR-005 decision 1): the file it
            // was saved into is the file it would have been without it.
            Assert.Equal(0, reopened.RedactionMarkCount);
            using var page = reopened.GetPage(0);
            Assert.Empty(page.GetRedactionMarks());
            // And nothing was removed either: marking is not a redaction.
            Assert.Contains("MegaPDF", string.Join(" ", page.GetTextRuns().Select(r => r.Text)));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void MarksAreCountedAcrossTheDocument_NotPerPage()
    {
        using var doc = _engine.Open(WritePdf(SamplePdf.BuildTwoPages()));
        var pageOne = MarkForRedactionOperation.Place(doc, 0, OverTheLine(doc))!;

        Assert.Equal(pageOne.MarkIds.Count, doc.RedactionMarkCount);
        Assert.Empty(Rectangles(doc, 1));

        // A mark belongs to the page it was made on, and the count is the document's.
        using (var page = doc.GetPage(1))
            page.MarkForRedaction(EmptyArea);

        Assert.Equal(pageOne.MarkIds.Count + 1, doc.RedactionMarkCount);
        Assert.Single(Rectangles(doc, 1));

        doc.ClearRedactionMarks();
        Assert.Equal(0, doc.RedactionMarkCount);
        Assert.Empty(Rectangles(doc, 0));
        Assert.Empty(Rectangles(doc, 1));
    }
}
