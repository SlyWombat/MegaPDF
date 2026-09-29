using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Recovery;
using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// The shared page-tools layer (#174, core contract 10) — the half both desktops build on.
///
/// <see cref="PageShift"/> is pure arithmetic and is tested as such: it is the one thing both
/// apps use to renumber their own index-keyed state after a page operation, and the class of bug
/// it exists to prevent (one page's state landing on another page after a delete) is silent, not
/// a crash. The engine half is tested against a real document, because "the page that came back
/// is the page that went" cannot be asserted against a mock.
/// </summary>
public class PageToolsTests
{
    private readonly PdfiumEngine _engine = new();

    /// <summary>
    /// A PDF on disk for the length of a test. Page operations need real files — an import
    /// reads one and an extract writes one — so these are files rather than byte arrays.
    /// </summary>
    private sealed class TempPdf : IDisposable
    {
        public TempPdf(byte[] bytes)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"megapdf-pages-{Guid.NewGuid():N}.pdf");
            File.WriteAllBytes(Path, bytes);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                    File.Delete(Path);
            }
            catch (IOException)
            {
                // A temporary file that will not go is not worth failing a test over.
            }
        }
    }

    /// <summary>Two pages, the first carrying a drawn square so the two can be told apart.</summary>
    private static TempPdf TwoPages() => new(SamplePdf.BuildTwoPagesFirstWithSquare());

    /// <summary>One page with an AcroForm: a text field and a checkbox.</summary>
    private static TempPdf Form() => new(SamplePdf.BuildWithForm());

    // --- PageShift: the renumbering both desktops share ----------------------

    [Fact]
    public void Removing_ShiftsLaterPagesDown_AndDropsTheOneThatWent()
    {
        var shift = PageShift.Removed(2);
        Assert.Equal(0, shift.Map(0));
        Assert.Equal(1, shift.Map(1));
        Assert.Null(shift.Map(2));         // the page that went maps to nothing, never to 0
        Assert.Equal(2, shift.Map(3));
        Assert.Equal([2], shift.RemovedIndices);
        Assert.True(shift.Renumbers);
    }

    [Fact]
    public void RemovingSeveral_DropsTheWholeRun()
    {
        var shift = PageShift.Removed(1, 3);
        Assert.Equal(0, shift.Map(0));
        Assert.Null(shift.Map(1));
        Assert.Null(shift.Map(3));
        Assert.Equal(1, shift.Map(4));
    }

    [Fact]
    public void Inserting_PushesEverythingFromThereDown()
    {
        var shift = PageShift.Inserted(1, 2);
        Assert.Equal(0, shift.Map(0));
        Assert.Equal(3, shift.Map(1));
        Assert.Equal([1, 2], shift.AddedIndices);
    }

    [Fact]
    public void MovingForward_ShiftsThePagesItPassedBack()
    {
        var shift = PageShift.Moved(1, 3);
        Assert.Equal(3, shift.Map(1));
        Assert.Equal(1, shift.Map(2));
        Assert.Equal(2, shift.Map(3));
        Assert.Equal(0, shift.Map(0));
        Assert.Equal(4, shift.Map(4));
    }

    [Fact]
    public void MovingBackward_ShiftsThePagesItPassedForward()
    {
        var shift = PageShift.Moved(3, 1);
        Assert.Equal(1, shift.Map(3));
        Assert.Equal(2, shift.Map(1));
        Assert.Equal(3, shift.Map(2));
        Assert.Equal(0, shift.Map(0));
    }

    [Fact]
    public void RotationRenumbersNothing()
    {
        var shift = PageShift.InPlace(2);
        Assert.False(shift.Renumbers);
        Assert.Equal(2, shift.Map(2));
        Assert.Equal(5, shift.Map(5));
    }

    [Fact]
    public void EveryShiftIsUndoneByItsInverse()
    {
        foreach (var shift in new[]
                 {
                     PageShift.Removed(2), PageShift.Removed(0, 3),
                     PageShift.Inserted(1, 2), PageShift.Moved(1, 4), PageShift.Moved(4, 1),
                 })
        {
            var inverse = shift.Inverse;
            for (var page = 0; page < 8; page++)
            {
                // A page the shift did not remove comes back to where it started.
                if (shift.Map(page) is { } moved && inverse.Map(moved) is { } back)
                    Assert.Equal(page, back);
            }
        }
    }

    [Fact]
    public void ASequenceOfShiftsIsUndoneInReverse()
    {
        // A selection delete: two removals, highest first, as DeletePagesOperation performs them.
        IReadOnlyList<PageShift> shifts = [PageShift.Removed(3), PageShift.Removed(1)];
        Assert.Equal(0, PageShift.Map(shifts, 0));
        Assert.Null(PageShift.Map(shifts, 1));
        Assert.Equal(1, PageShift.Map(shifts, 2));
        Assert.Null(PageShift.Map(shifts, 3));
        Assert.Equal(2, PageShift.Map(shifts, 4));

        var inverse = PageShift.Invert(shifts);
        Assert.Equal([PageShiftKind.Inserted, PageShiftKind.Inserted], inverse.Select(s => s.Kind));
        // Page 4 became page 2; undone, page 2 is page 4 again.
        Assert.Equal(4, PageShift.Map(inverse, 2));
    }

    [Fact]
    public void RemapKeepsTheSurvivorsAndDropsTheRest()
    {
        var shift = PageShift.Removed(1);
        Assert.Equal([0, 1, 2], shift.Remap([0, 1, 2, 3]));
    }

    // --- The once-per-page #139 warnings follow the pages -------------------

    [Fact]
    public void SettledPagesFollowARenumbering()
    {
        var warnings = new PageRegenerationWarnings();
        warnings.Settle(0);
        warnings.Settle(2);

        warnings.Renumber([PageShift.Removed(0)]);

        // Page 2 became page 1; page 0 is gone and must not leave its "already warned about"
        // sitting on the page that took its index — that would skip a warning that was owed.
        Assert.False(warnings.IsSettled(0));
        Assert.True(warnings.IsSettled(1));
        Assert.False(warnings.IsSettled(2));
    }

    [Fact]
    public void ARotationSettlesNothingNewAndMovesNothing()
    {
        var warnings = new PageRegenerationWarnings();
        warnings.Settle(1);
        warnings.Renumber([PageShift.InPlace(1)]);
        Assert.True(warnings.IsSettled(1));
    }

    // --- Permissions: page assembly is its own bit --------------------------

    [Fact]
    public void AssemblePermissionAloneAllowsPageTools_ButNotContentEditing()
    {
        var security = new PdfSecurity(IsEncrypted: true, Revision: 4,
            Permissions: PdfPermissions.Assemble, HasFullAccess: false);
        var caps = DocumentCapabilities.From(security);

        Assert.True(caps.CanAssemblePages);
        Assert.False(caps.CanEditContent);
        Assert.False(caps.CanExtractPages);   // extracting is the copy bit, not assemble
    }

    [Fact]
    public void ModifyAlsoAllowsPageTools()
    {
        var security = new PdfSecurity(true, 4, PdfPermissions.Modify, false);
        Assert.True(DocumentCapabilities.From(security).CanAssemblePages);
    }

    [Fact]
    public void CopyPermissionIsWhatExtractingNeeds()
    {
        var security = new PdfSecurity(true, 4, PdfPermissions.Copy, false);
        var caps = DocumentCapabilities.From(security);
        Assert.True(caps.CanExtractPages);
        Assert.False(caps.CanAssemblePages);
    }

    [Fact]
    public void APageOperationIsGatedOnAssemble_NotOnModify()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        var caps = DocumentCapabilities.From(
            new PdfSecurity(true, 4, PdfPermissions.Assemble, false));
        Assert.True(caps.Allows(new RotatePagesOperation(document, [0], 1)));
        Assert.True(caps.Allows(new MovePageOperation(document, 0, 1)));
        Assert.True(caps.Allows(new InsertBlankPageOperation(document, 1, 300, 400)));
    }

    // --- The engine half, against a real document ---------------------------

    [Fact]
    public void RotatingSetsRotateAndSwapsTheReportedSize()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        double width, height;
        using (var page = document.GetPage(0))
            (width, height) = (page.Width, page.Height);

        Assert.Equal(0, document.GetPageRotation(0));
        document.RotatePage(0, 1);
        Assert.Equal(1, document.GetPageRotation(0));

        // The core reports the *rotated* size, which is the whole reason the view models have
        // to be told (#439): the page is what it was, shown the other way up.
        using (var page = document.GetPage(0))
        {
            Assert.Equal(height, page.Width, 1);
            Assert.Equal(width, page.Height, 1);
        }

        document.RotatePage(0, -1);
        Assert.Equal(0, document.GetPageRotation(0));
    }

    [Fact]
    public void ADeletedPageComesBackAsItself()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        var before = document.PageCount;
        var squares = CountSquares(document, 0);
        Assert.True(squares > 0, "the fixture's first page has drawn squares on it");

        var removed = document.DeletePage(0);
        Assert.Equal(before - 1, document.PageCount);
        Assert.True(removed.IsHeld);

        document.RestorePage(removed, 0);
        Assert.Equal(before, document.PageCount);
        Assert.False(removed.IsHeld);   // the core consumed the handle
        Assert.Equal(squares, CountSquares(document, 0));
    }

    [Fact]
    public void TheLastPageCannotBeDeleted()
    {
        using var file = Form();
        using var document = _engine.Open(file.Path);
        Assert.Equal(1, document.PageCount);
        var refusal = Assert.Throws<PageToolException>(() => document.DeletePage(0));
        // Told apart from a bad index, because it is the one refusal a person can act on.
        Assert.Equal(PageToolFailure.LastPage, refusal.Reason);
        Assert.Equal(1, document.PageCount);
    }

    [Fact]
    public void AnOutOfRangePageIsToldApartFromTheLastPage()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        var refusal = Assert.Throws<PageToolException>(() => document.DeletePage(9));
        Assert.Equal(PageToolFailure.OutOfRange, refusal.Reason);
    }

    [Fact]
    public void MovingAPageTakesItsRotationWithIt()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        document.RotatePage(0, 1);
        document.MovePage(0, 1);
        Assert.Equal(0, document.GetPageRotation(0));
        Assert.Equal(1, document.GetPageRotation(1));
    }

    [Fact]
    public void AnOpenPageHandleFollowsItsPage()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        using var page = document.GetPage(1);
        Assert.Equal(1, page.Index);
        document.MovePage(1, 0);
        // Contract 10: the handle's index follows the page, which is why it is read from the
        // core rather than remembered.
        Assert.Equal(0, page.Index);
    }

    [Fact]
    public void AHandleOnADeletedPageAnswersMinusOneAndStillRenders()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        using var page = document.GetPage(0);
        var removed = document.DeletePage(0);
        try
        {
            Assert.Equal(-1, page.Index);
            // Still renders what was on the page, so a view holding it does not crash.
            var raster = page.Render(40, 50);
            Assert.Equal(40 * 50 * 4, raster.Bgra.Length);
        }
        finally
        {
            document.DiscardRemovedPage(removed);
        }
    }

    [Fact]
    public void ABlankPageIsInsertedAtTheSizeAsked()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        document.InsertBlankPage(document.PageCount, 300, 400);
        Assert.Equal(3, document.PageCount);
        using var page = document.GetPage(2);
        Assert.Equal(300, page.Width, 1);
        Assert.Equal(400, page.Height, 1);
    }

    [Fact]
    public void ImportingRenamesAFieldWhoseNameIsAlreadyTaken()
    {
        using var file = Form();
        using var document = _engine.Open(file.Path);
        string original;
        using (var page = document.GetPage(0))
            original = page.GetFormFields()[0].Name;

        var imported = document.ImportPages(file.Path, null, null, 1);
        Assert.Equal(1, imported);

        using var copied = document.GetPage(1);
        // Renamed rather than merged: two fields with one name would fill each other in.
        Assert.Equal($"{original}_2", copied.GetFormFields()[0].Name);
    }

    [Fact]
    public void ImportingFromAFileThatIsNotThereIsAFileFailure()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        var refusal = Assert.Throws<PageToolException>(
            () => document.ImportPages(Path.Combine(Path.GetTempPath(), "megapdf-no-such.pdf"), null, null, 0));
        Assert.Equal(PageToolFailure.File, refusal.Reason);
        Assert.Equal(2, document.PageCount);
    }

    [Fact]
    public void ExtractingWritesTheListedPagesAndChangesNothing()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        var target = Path.Combine(Path.GetTempPath(), $"megapdf-extract-{Guid.NewGuid():N}.pdf");
        try
        {
            document.ExtractPages([1], target);
            Assert.True(File.Exists(target));
            using var copy = _engine.Open(target);
            Assert.Equal(1, copy.PageCount);
            Assert.Equal(2, document.PageCount);
        }
        finally
        {
            if (File.Exists(target))
                File.Delete(target);
        }
    }

    // --- The operations: one undo step each ---------------------------------

    [Fact]
    public void DeletingASelectionIsOneUndoStepThatPutsEveryPageBackWhereItWas()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        // Four pages: the fixture's two, then its two again, so pages 0 and 2 are the marked one.
        document.ImportPages(file.Path, null, null, 2);
        var marked = CountSquares(document, 0);

        var stack = new UndoStack();
        var operation = new DeletePagesOperation(document, [1, 3]);
        stack.Do(operation);
        Assert.Equal(2, document.PageCount);
        Assert.Equal([PageShiftKind.Removed, PageShiftKind.Removed], operation.Shifts.Select(s => s.Kind));
        Assert.Equal([3, 1], operation.Shifts.Select(s => s.At));   // highest first, as applied

        stack.Undo();
        Assert.Equal(4, document.PageCount);
        Assert.Equal(marked, CountSquares(document, 0));
        Assert.Equal(marked, CountSquares(document, 2));
    }

    [Fact]
    public void ImportingKnowsHowManyPagesArrivedOnlyAfterItHasRun()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        var operation = new ImportPagesOperation(document, file.Path, null, null, 2);
        Assert.Empty(operation.Shifts);

        var stack = new UndoStack();
        stack.Do(operation);
        Assert.Equal(2, operation.Imported);
        Assert.Equal([PageShift.Inserted(2, 2)], operation.Shifts);
        Assert.Equal(4, document.PageCount);

        stack.Undo();
        Assert.Equal(2, document.PageCount);
        stack.Redo();
        Assert.Equal(4, document.PageCount);
    }

    [Fact]
    public void AnUndoStackClearedLetsGoOfThePagesItWasHolding()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        var operation = new DeletePagesOperation(document, [0]);
        var stack = new UndoStack();
        stack.Do(operation);

        // What the app does when the history is thrown away: a removed page nothing can undo is
        // a whole page held in the core until the document closes.
        foreach (var held in stack.All.OfType<DeletePagesOperation>())
            held.DiscardHeldPages();
        stack.Clear();
        Assert.Equal(1, document.PageCount);
    }

    // --- The recovery journal -----------------------------------------------

    [Fact]
    public void AnInsertThatAppendsReplays()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        // The index is the page count itself, which is not a page to load — the trap a replayer
        // written the obvious way falls into.
        var applied = JournalReplayer.Replay(document, [new PageInsertBlankEntry(2, 300, 400)]);
        Assert.Equal(1, applied);
        Assert.Equal(3, document.PageCount);
    }

    [Fact]
    public void PageEntriesReplayInOrderWithNoIndexRewriting()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        JournalEntry[] entries =
        [
            new PagesRotateEntry(0, [0], 1),
            new PageInsertBlankEntry(2, 300, 400),
            new PageMoveEntry(2, 0),
            new PagesDeleteEntry(0, [0]),
        ];
        Assert.Equal(entries.Length, JournalReplayer.Replay(document, entries, file.Path));
        Assert.Equal(2, document.PageCount);
        Assert.Equal(1, document.GetPageRotation(0));
    }

    [Fact]
    public void ARestoreEntryWithNoFileToReadFromIsSkipped()
    {
        using var file = TwoPages();
        using var document = _engine.Open(file.Path);
        JournalEntry[] entries = [new PagesDeleteEntry(0, [0]), new PagesRestoreEntry(0, [0])];
        // One applied, one skipped — a recovery is not failed by an entry that cannot resolve.
        Assert.Equal(1, JournalReplayer.Replay(document, entries, documentPath: null));
        Assert.Equal(1, document.PageCount);
    }

    private static int CountSquares(IPdfDocument document, int pageIndex)
    {
        using var page = document.GetPage(pageIndex);
        return page.DetectCheckboxSquares().Count;
    }
}
