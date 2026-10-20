using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Core;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #710: contract 11 (text selection) through the desktop binding — what both desktops call.
/// The core's own suite (core_tests.cpp, test_selection_*) holds the rules themselves; this holds
/// the binding to them: view-space coordinates (top-left) in and out, the page stitching both
/// desktops share, the copy gate, and a snapshot's lifetime.
///
/// selection.pdf is tools/gen_selection_fixture.py's: page 1 a title over two columns, page 2
/// ligatures and line-end hyphens.
/// </summary>
public sealed class TextSelectionTests : IDisposable
{
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => _engine.Dispose();

    private static string Structure(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "structure", name + ".pdf");

    private static string Security(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "security", name);

    private static PdfPoint Centre(PdfRect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    [Fact]
    public void TheWholePageReadsDownEachColumnInTurn()
    {
        using var doc = _engine.Open(Structure("selection"));
        using var page = doc.GetPage(0);
        using var text = page.LoadSelectableText();
        Assert.Equal(0, text.PageIndex);
        Assert.Equal(
            "Selection Columns\n" +
            "Left one alpha\nLeft two bravo and more\nLeft three charlie\nLeft four delta\nLeft five echo\n" +
            "Right one golf\nRight two hotel\nRight three india\nRight four juliet\nRight five kilo",
            text.GetText(0, text.Length));
    }

    [Fact]
    public void RectsAreViewSpace_AndADragAcrossTheColumnsFollowsThem()
    {
        using var doc = _engine.Open(Structure("selection"));
        using var page = doc.GetPage(0);
        using var text = page.LoadSelectableText();
        var lines = text.GetRects(0, text.Length);
        Assert.Equal(11, lines.Count);

        // Top-left origin: the title is the topmost line, so it has the smallest Y, and the
        // first left-column line (baseline 680 pt from the bottom of a 792 pt page) sits about
        // 100 pt from the top.
        Assert.True(lines[0].Y < lines[1].Y);
        Assert.InRange(lines[1].Y, 95, 105);
        Assert.InRange(lines[1].X, 71, 73);
        Assert.InRange(lines[6].X, 329, 331);   // "Right one golf"

        var left2 = lines[2];
        var right2 = lines[7];
        var anchor = text.CaretAt(new PdfPoint(left2.X + 0.5, Centre(left2).Y), tolerance: 4);
        var focus = text.CaretAt(new PdfPoint(right2.Right + 30, Centre(right2).Y));
        Assert.NotNull(anchor);
        Assert.NotNull(focus);
        Assert.Equal("Left two bravo and more\nLeft three charlie\nLeft four delta\nLeft five echo\n" +
                     "Right one golf\nRight two hotel", text.GetText(anchor!.Value, focus!.Value));
        Assert.Equal(6, text.GetRects(focus.Value, anchor.Value).Count);
    }

    [Fact]
    public void AWordALineAndNothing()
    {
        using var doc = _engine.Open(Structure("selection"));
        using var page = doc.GetPage(0);
        using var text = page.LoadSelectableText();
        var lines = text.GetRects(0, text.Length);

        var line = text.LineAt(Centre(lines[3]), tolerance: 4);
        Assert.Equal("Left three charlie", text.GetText(line!.Value));

        // The last word of "Left three charlie", from a point inside its line's rect near its end.
        var word = text.WordAt(new PdfPoint(lines[3].Right - 2, Centre(lines[3]).Y), tolerance: 4);
        Assert.Equal("charlie", text.GetText(word!.Value));
        Assert.Equal(7, word.Value.Length);

        // Far from any text: nothing within a tolerance, but a drag still has a caret.
        var blank = new PdfPoint(300, 600);
        Assert.Null(text.CaretAt(blank, tolerance: 4));
        Assert.Null(text.CharAt(blank, tolerance: 4));
        Assert.Null(text.WordAt(blank, tolerance: 4));
        Assert.NotNull(text.CaretAt(blank));
    }

    [Fact]
    public void LigaturesExpand_AndLineEndHyphensAreKept()
    {
        using var doc = _engine.Open(Structure("selection"));
        using var page = doc.GetPage(1);
        using var text = page.LoadSelectableText();
        Assert.Equal("The final flow test\nWe test hyphen-\nation here.\nA well-\nknown word.", text.GetText(0, text.Length));
    }

    [Fact]
    public void ASelectionAcrossPagesIsStitchedWithOneLineBreak()
    {
        using var doc = _engine.Open(Structure("selection"));
        using var first = doc.GetPage(0);
        using var second = doc.GetPage(1);
        using var a = first.LoadSelectableText();
        using var b = second.LoadSelectableText();
        var snapshots = new Dictionary<int, PdfSelectableText> { [0] = a, [1] = b };

        // From the start of "Right five kilo" (its own line's first caret) to just after "The".
        var lastLine = a.GetRects(0, a.Length)[^1];
        var anchor = new PdfTextPosition(0, a.CaretAt(new PdfPoint(lastLine.X + 0.2, Centre(lastLine).Y), 4)!.Value);
        var focus = new PdfTextPosition(1, 3);
        const string expected = "Right five kilo\nThe";
        Assert.Equal(expected, PdfTextSelection.GetText(anchor, focus, i => snapshots[i]));
        Assert.Equal(expected, PdfTextSelection.GetText(focus, anchor, i => snapshots[i]));

        Assert.Equal(new PdfTextRange(anchor.Caret, a.Length), PdfTextSelection.RangeOnPage(anchor, focus, 0, a.Length));
        Assert.Equal(new PdfTextRange(0, 3), PdfTextSelection.RangeOnPage(focus, anchor, 1, b.Length));
        Assert.Null(PdfTextSelection.RangeOnPage(anchor, focus, 2, 10));
        // A page between the two ends contributes the whole of itself.
        Assert.Equal(new PdfTextRange(0, 40), PdfTextSelection.RangeOnPage(new(0, 5), new(2, 1), 1, 40));
    }

    [Fact]
    public void AScanHasNothingToSelect_AndSaysSoCleanly()
    {
        using var doc = _engine.Open(Structure("scan"));
        using var page = doc.GetPage(0);
        using var text = page.LoadSelectableText();
        Assert.Equal(0, text.Length);
        Assert.Null(text.CaretAt(new PdfPoint(100, 100)));
        Assert.Null(text.WordAt(new PdfPoint(100, 100)));
        Assert.Equal("", text.GetText(0, 10));
        Assert.Empty(text.GetRects(0, 10));
    }

    [Fact]
    public void ASnapshotOutlivesItsPage_AndADisposedPageRefusesToLoadOne()
    {
        using var doc = _engine.Open(Structure("selection"));
        PdfSelectableText text;
        var page = doc.GetPage(0);
        text = page.LoadSelectableText();
        page.Dispose();
        using (text)
            Assert.Equal("Selection Columns", text.GetText(0, 16));
        Assert.Throws<ObjectDisposedException>(() => page.LoadSelectableText());
    }

    [Fact]
    public void CopyIsGatedOnTheCopyBit_AsTheCoreActsOnIt()
    {
        using (var open = _engine.Open(Security("owner-only.pdf")))
        {
            Assert.False(DocumentCapabilities.From(open.Security).CanCopyText);
            // It still selects: the gate is Copy's alone.
            using var page = open.GetPage(0);
            using var text = page.LoadSelectableText();
            Assert.True(text.Length > 0);
        }
        using (var owner = _engine.Open(Security("owner-only.pdf"), "o-restricted"))
            Assert.True(DocumentCapabilities.From(owner.Security).CanCopyText);
        Assert.True(DocumentCapabilities.Unprotected.CanCopyText);

        // The capability and the core's own answer agree (the desktops never override, so
        // the file's bits are the bits in effect).
        foreach (var (file, password) in new[] { ("owner-only.pdf", (string?)null), ("owner-only.pdf", "o-restricted") })
        {
            var core = CoreNative.megapdf_open_file(Security(file), password);
            Assert.NotEqual(IntPtr.Zero, core);
            try
            {
                using var doc = _engine.Open(Security(file), password);
                var inEffect = (PdfPermissions)CoreNative.megapdf_permissions_in_effect(core);
                Assert.Equal(inEffect.HasFlag(PdfPermissions.Copy), DocumentCapabilities.From(doc.Security).CanCopyText);
            }
            finally
            {
                CoreNative.megapdf_close(core);
            }
        }
    }
}
