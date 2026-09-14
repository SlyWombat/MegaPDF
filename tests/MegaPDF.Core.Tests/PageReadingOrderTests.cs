using MegaPDF.Core.Engine;
using MegaPDF.Core.Viewing;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #2 — the order Tab walks a page in, and how the walk crosses pages.
///
/// Tabbing through a real form needs a window and a person; which region comes
/// next does not. Both desktops take their answer from here.
/// </summary>
public class PageReadingOrderTests
{
    private static (PdfRect, PageHitKind) Region(double x, double y, PageHitKind kind = PageHitKind.TextRun) =>
        (new PdfRect(x, y, 40, 10), kind);

    [Fact]
    public void AnEmptyPage_HasNoOrder() =>
        Assert.Empty(PageReadingOrder.Order([]));

    [Fact]
    public void RowsRunTopToBottom()
    {
        var ordered = PageReadingOrder.Order([Region(10, 300), Region(10, 100), Region(10, 200)]);

        Assert.Equal([100d, 200d, 300d], ordered.Select(r => r.Bounds.Y));
    }

    [Fact]
    public void BaselineWobbleWithinARow_StaysLeftToRight()
    {
        // One line of a form: the label sits a point lower than the field beside it,
        // and the checkbox a point higher. Sorting on raw Y would put the checkbox first.
        var ordered = PageReadingOrder.Order(
        [
            Region(300, 101, PageHitKind.FormCheckbox),
            Region(10, 103, PageHitKind.TextRun),
            Region(150, 102, PageHitKind.FormTextField),
        ]);

        Assert.Equal([10d, 150d, 300d], ordered.Select(r => r.Bounds.X));
    }

    [Fact]
    public void RegionsAtTheSamePlace_KeepHitTestPriority()
    {
        var ordered = PageReadingOrder.Order(
        [
            Region(50, 50, PageHitKind.StampAnnotation),
            Region(50, 50, PageHitKind.DrawnCheckbox),
        ]);

        Assert.Equal([PageHitKind.StampAnnotation, PageHitKind.DrawnCheckbox], ordered.Select(r => r.Kind));
    }

    // --- The walk across pages ---

    private static RegionPosition? Step(int[] counts, RegionPosition? current, bool forward,
                                        int entryPage = 0, bool wrap = true) =>
        PageReadingOrder.Step(counts.Length, p => counts[p], current, forward, entryPage, wrap);

    [Fact]
    public void Tab_MovesWithinAPage_ThenOntoTheNextPagesFirstRegion()
    {
        int[] counts = [2, 0, 3];

        Assert.Equal(new RegionPosition(0, 0), Step(counts, null, forward: true));
        Assert.Equal(new RegionPosition(0, 1), Step(counts, new(0, 0), forward: true));
        // Page 2 has nothing: skipped, not stopped on.
        Assert.Equal(new RegionPosition(2, 0), Step(counts, new(0, 1), forward: true));
    }

    [Fact]
    public void ShiftTab_EntersThePreviousPageAtItsLastRegion()
    {
        int[] counts = [3, 0, 2];

        Assert.Equal(new RegionPosition(0, 2), Step(counts, new(2, 0), forward: false));
        Assert.Equal(new RegionPosition(2, 1), Step(counts, null, forward: false, entryPage: 2));
    }

    [Fact]
    public void Wrapping_ContinuesAtTheOtherEnd()
    {
        int[] counts = [1, 1];

        Assert.Equal(new RegionPosition(0, 0), Step(counts, new(1, 0), forward: true));
        Assert.Equal(new RegionPosition(1, 0), Step(counts, new(0, 0), forward: false));
    }

    [Fact]
    public void WithoutWrapping_RunningOffEitherEndLetsFocusLeave()
    {
        int[] counts = [1, 1];

        Assert.Null(Step(counts, new(1, 0), forward: true, wrap: false));
        Assert.Null(Step(counts, new(0, 0), forward: false, wrap: false));
    }

    [Fact]
    public void EnteringMidDocument_WithWrapping_StillReachesEarlierPages()
    {
        // Focus arrives while page 3 is on screen, and only page 1 has anything.
        int[] counts = [1, 0, 0];

        Assert.Equal(new RegionPosition(0, 0), Step(counts, null, forward: true, entryPage: 2));
    }

    [Fact]
    public void ADocumentWithNothingInteractive_EndsInOneLap()
    {
        var asked = 0;
        var result = PageReadingOrder.Step(4, _ => { asked++; return 0; }, null, forward: true);

        Assert.Null(result);
        Assert.InRange(asked, 4, 5);
    }

    [Fact]
    public void AnEmptyDocument_HasNowhereToGo() =>
        Assert.Null(PageReadingOrder.Step(0, _ => 0, null, forward: true));
}
