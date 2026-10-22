using System.Runtime.InteropServices;
using MegaPDF.Core.Engine;
using Windows.ApplicationModel.DataTransfer;

namespace MegaPDF.App;

internal static partial class Screenshot
{
    /// <summary>
    /// Selecting a document's text and copying it (#710), in a real window, over the core's
    /// two-column fixture <c>structure/selection.pdf</c> (the document on the command line; CI
    /// passes the copy tools/gen_test_fixtures.py makes) and the no-copy <c>owner-only.pdf</c>
    /// beside it.
    ///
    /// WinUI cannot synthesise a pointer, so the press, the moves and the release are fed to the
    /// page's own three pointer handlers as readings (<see cref="DocumentView.DragForTest"/>) —
    /// the drag threshold, the hit checks, the walk across pages and the tap swallowing are the
    /// real ones.
    ///
    /// The clipboard is the person's, so this never touches it (#717). In an automation launch
    /// Copy keeps the package it would have put on the clipboard
    /// (<see cref="DocumentView.AutomationClipboard"/>), built by the same code a person's Copy
    /// runs, and the checks read that. Only the final hand-over, <c>Clipboard.SetContent</c>
    /// and <c>Flush</c>, is not exercised here. The system clipboard's sequence number, which
    /// Windows bumps on every write by anyone, is read before and after to prove it.
    /// </summary>
    private static async Task<bool> CheckTextSelectionAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } tab || tab.DocumentPath is not { } path
            || !string.Equals(Path.GetFileName(path), "selection.pdf", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("--screenshot-state select-copy needs structure/selection.pdf as its document.");
            return false;
        }

        var passed = 0;
        var failed = 0;
        void Check(string what, bool ok)
        {
            Console.Error.WriteLine($"{(ok ? "PASS" : "FAIL")}: {what}");
            if (ok)
                passed++;
            else
                failed++;
        }

        var sequenceBefore = GetClipboardSequenceNumber();
        try
        {
            await RunTextSelectionChecksAsync(window, tab, path, Check);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: the select-copy check threw: {ex}");
            failed++;
        }
        finally
        {
            foreach (var other in window.Shell.Documents.Where(t => !ReferenceEquals(t, tab)).ToList())
            {
                other.HasUnsavedChanges = false;
                await window.CloseTabAsync(other);
            }
        }
        var sequenceAfter = GetClipboardSequenceNumber();
        Console.Error.WriteLine($"  the system clipboard's sequence number: {sequenceBefore} before, {sequenceAfter} after");
        Check("the person's clipboard was never written (#717)", sequenceAfter == sequenceBefore);

        // For the photograph: the same two-column selection, on the first page at the top.
        if (failed == 0 && tab.View is { } shown && tab.SelectableText is { } text)
        {
            shown.ScrollPagesToTopForTest();
            await Task.Delay(300);
            var lines = text.Get(0).GetRects(0, text.Get(0).Length);
            shown.DragForTest(0, new PdfPoint(lines[2].X + 0.5, lines[2].Y + lines[2].Height / 2),
                              0, new PdfPoint(lines[7].Right + 30, lines[7].Y + lines[7].Height / 2));
        }

        Console.Error.WriteLine($"select-copy: {passed} passed, {failed} failed ({passed + failed} checks)");
        return failed == 0;
    }

    private static async Task RunTextSelectionChecksAsync(
        MainWindow window, DocumentViewModel tab, string path, Action<string, bool> Check)
    {
        static async Task<bool> UntilAsync(Func<bool> done, double seconds = 10)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!done())
            {
                if (DateTime.UtcNow > deadline)
                    return false;
                await Task.Delay(100);
            }
            return true;
        }
        // What Copy put where the clipboard would have been (#717): the package itself, read the
        // way a paste would read it.
        static async Task<string?> ClipboardText()
        {
            if (DocumentView.AutomationClipboard is not { } package)
                return null;
            var content = package.GetView();
            return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
        }
        static string Shown(string? s) => s is null ? "(none)" : s.Replace("\r", "\\r").Replace("\n", "\\n");

        Check("the tab has a view", await UntilAsync(() => tab.View is not null));
        if (tab.View is not { } view || tab.SelectableText is not { } cache)
            return;
        Check("both pages are drawn", await UntilAsync(() => tab.Pages.Count == 2 && tab.Pages.All(p => p.Source is not null && p.Regions.Count > 0)));
        Check("page 1's text was read ahead as it was drawn, off the UI thread", await UntilAsync(() => cache.Peek(0) is not null));

        var page = cache.Get(0);
        var lines = page.GetRects(0, page.Length);
        Check("the fixture's first page has its title and ten column lines", lines.Count == 11);
        if (lines.Count != 11)
            return;
        static PdfPoint Mid(PdfRect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

        // --- a drag across the two columns: reading order, and CR LF on the clipboard ---
        var from = new PdfPoint(lines[2].X + 0.5, Mid(lines[2]).Y);           // the start of "Left two bravo and more"
        var to = new PdfPoint(lines[7].Right + 30, Mid(lines[7]).Y);          // past the end of "Right two hotel"
        Check("a press on a line that moves past the drag threshold becomes a selection", view.DragForTest(0, from, 0, to));
        Check("  and the Tapped its release may raise is not a click", view.NextTapIsSwallowedForTest);
        Check("  no editor opened", !view.HasActiveEditorForTest);
        Check("  it is drawn: one highlight per line selected", view.SelectionRectsShownForTest(0) == 6);
        Check("  and the menu's Copy is on, with no reason shown", view.TextMenuStateForTest() == (true, false));
        // Narrator is told what was selected, once the release has settled it (#710).
        await view.TextAnnouncementForTest;
        Check("the release announces the selection: \"Selected: \" and its first 80 characters",
              view.LastAnnouncement == Strings.TextSelectionAnnounced(
                  "Left two bravo and more Left three charlie Left four delta Left five echo Right\u2026"));
        Console.Error.WriteLine($"  announced: {view.LastAnnouncement}");
        Check("Copy succeeds", await view.CopySelectedTextAsync());
        Check("  and the copy announces \"Copied\"", view.LastAnnouncement == Strings.TextCopiedAnnounced);
        var copied = await ClipboardText();
        Console.Error.WriteLine($"  clipboard: {Shown(copied)}");
        Check("the clipboard runs down the left column and then the right, with CR LF line ends",
              copied == "Left two bravo and more\r\nLeft three charlie\r\nLeft four delta\r\nLeft five echo\r\n"
                        + "Right one golf\r\nRight two hotel");
        Check("  and no bare LF anywhere in it", copied is not null && !copied.Replace("\r\n", "").Contains('\n'));

        // --- the keyboard: Shift+Right one character, Escape lets go ---
        Check("Shift+Right extends the selection by a character", view.StepTextSelectionForTest(+1));
        Check("  and says nothing yet: the keys are debounced", view.LastAnnouncement == Strings.TextCopiedAnnounced);
        Check("  then, 300 ms after the last key, it announces the selection",
              await UntilAsync(() => view.LastAnnouncement != Strings.TextCopiedAnnounced, 3)
              && view.LastAnnouncement == Strings.TextSelectionAnnounced(
                  "Left two bravo and more Left three charlie Left four delta Left five echo Right\u2026"));
        await view.CopySelectedTextAsync();
        var stepped = await ClipboardText();
        Console.Error.WriteLine($"  clipboard: {Shown(stepped)}");
        // A line end is a break between carets, not a character of its own: one step over it takes
        // the first character of the next line, and the break comes with it.
        Check("  over the line end, which the copy shows as its CR LF",
              stepped == "Left two bravo and more\r\nLeft three charlie\r\nLeft four delta\r\nLeft five echo\r\n"
                         + "Right one golf\r\nRight two hotel\r\nR");
        Check("Escape lets go of the selection", view.PressSelectionKeyForTest(Windows.System.VirtualKey.Escape) && !view.HasTextSelection);
        Check("  and its highlight", view.SelectionRectsShownForTest(0) == 0);

        // --- a drag that never passes the threshold is a click: the editor opens as before ---
        var onLine = Mid(lines[3]);
        await view.ClickForTest(0, onLine);
        Check("a plain click on a line still opens the editor on it", await UntilAsync(() => view.HasActiveEditorForTest));
        Check("  and selected nothing", !view.HasTextSelection);
        await view.PressEditorKeyForTest(Windows.System.VirtualKey.Escape);
        Check("  Escape closes the editor, unchanged", await UntilAsync(() => !view.HasActiveEditorForTest) && !tab.HasUnsavedChanges);

        // --- a double-click selects the word, and takes back the editor its first click opened ---
        var charlie = new PdfPoint(lines[3].Right - 2, Mid(lines[3]).Y);
        var clickStarted = Environment.TickCount64;
        await view.ClickForTest(0, charlie);
        Console.Error.WriteLine($"  the first click's editor took {Environment.TickCount64 - clickStarted} ms to open (open: {view.HasActiveEditorForTest})");
        Check("a double-click on a word selects it", view.DoubleClickForTest(0, charlie) && view.HasTextSelection);
        await view.TextAnnouncementForTest;
        Check("  and announces the word", view.LastAnnouncement == Strings.TextSelectionAnnounced("charlie"));
        Check("  and the editor its first click opened is gone, with nothing changed", !view.HasActiveEditorForTest && !tab.HasUnsavedChanges);
        await view.CopySelectedTextAsync();
        Check("  the clipboard holds that word", (await ClipboardText()) == "charlie");
        // The second click arriving before the first click's editor has opened: the open is called off.
        var opening = view.ClickForTest(0, charlie);
        Check("a double-click while the first click is still on its way also selects the word", view.DoubleClickForTest(0, charlie));
        await opening;
        await Task.Delay(300);
        Check("  and no editor opens behind it", !view.HasActiveEditorForTest && view.HasTextSelection);
        view.PressSelectionKeyForTest(Windows.System.VirtualKey.Escape);

        // --- across the page break, and the whole document ---
        var lastLeft = new PdfPoint(lines[10].X + 0.2, Mid(lines[10]).Y);    // the start of "Right five kilo"
        var second = cache.Get(1);
        var secondLines = second.GetRects(0, second.Length);
        var the = second.GetRects(0, 3)[0];                                  // "The", the first word of page 2
        var afterThe = new PdfPoint(the.Right + 0.3, Mid(the).Y);
        Check("a drag from the last line of page 1 onto page 2 is a selection", view.DragForTest(0, lastLeft, 1, afterThe, steps: 24));
        await view.CopySelectedTextAsync();
        var crossed = await ClipboardText();
        Console.Error.WriteLine($"  clipboard: {Shown(crossed)}");
        Check("  which copies across the page break as one line break",
              crossed is not null && crossed.StartsWith("Right five kilo\r\nThe", StringComparison.Ordinal));
        Check("  and both pages show their share of it", view.SelectionRectsShownForTest(0) == 1 && view.SelectionRectsShownForTest(1) >= 1);

        Check("Ctrl+A selects the whole document", view.SelectAllText() && view.HasTextSelection);
        await view.TextAnnouncementForTest;
        Check("  and announces its first 80 characters",
              view.LastAnnouncement == Strings.TextSelectionAnnounced(
                  "Selection Columns Left one alpha Left two bravo and more Left three charlie Left\u2026"));
        await view.CopySelectedTextAsync();
        var all = await ClipboardText() ?? "";
        Check("  and copies both pages, ligatures expanded and line-end hyphens kept",
              all.StartsWith("Selection Columns\r\nLeft one alpha\r\n", StringComparison.Ordinal)
              && all.Contains("Right five kilo\r\nThe final flow test\r\nWe test hyphen-\r\nation here.", StringComparison.Ordinal)
              && all.EndsWith("known word.", StringComparison.Ordinal));

        // --- a drag held still past the bottom edge goes on scrolling and selecting (#710) ---
        // The pointer sends nothing while it is still, so only a timer can do it. The moves that
        // brought the pointer there scrolled once each; the baseline is taken after the last of
        // them, and what happens over the next 500 ms with NO further sample is the timer's.
        view.PressSelectionKeyForTest(Windows.System.VirtualKey.Escape);
        view.ScrollPagesToTopForTest();
        await Task.Delay(300);
        Check($"the pages can scroll (room: {view.PagesScrollableHeightForTest:0} DIP)", view.PagesScrollableHeightForTest > 200);
        Check("a drag taken 40 DIP below the pages is a selection",
              await view.HoldDragPastBottomForTest(0, new PdfPoint(lines[2].X + 0.5, Mid(lines[2]).Y), belowViewportDip: 40));
        await Task.Delay(100);
        var heldBaseline = view.PagesScrollOffsetForTest;
        await Task.Delay(500);
        var heldAfter = view.PagesScrollOffsetForTest;
        Console.Error.WriteLine($"  held still for 500 ms: offset {heldBaseline:0} -> {heldAfter:0} DIP; the selection ends on page {(view.TextSelectionFocusPageForTest ?? -1) + 1}");
        Check("  held still for 500 ms, the pages kept scrolling", heldAfter > heldBaseline + 20);
        // Page 2 is reached once the pages have scrolled past the foot of page 1, a distance that
        // depends on the window; a timer starved by a busy UI thread gets there later, never not at
        // all. So it is given up to 3 s more of the same stillness before the check calls it.
        Check("  and the selection's end moved onto page 2",
              await UntilAsync(() => view.TextSelectionFocusPageForTest == 1, 3));
        Check("  the release lets go, and the timer with it", view.ReleaseHeldDragForTest() && !view.IsTextAutoScrollRunningForTest);
        // The last tick's ChangeView may still be on its way to the scroller when the release lands:
        // let that one settle, then nothing else may move the pages.
        await Task.Delay(150);
        var stopped = view.PagesScrollOffsetForTest;
        await Task.Delay(250);
        Check("  and the pages stay where they stopped", view.PagesScrollOffsetForTest == stopped);
        view.PressSelectionKeyForTest(Windows.System.VirtualKey.Escape);
        view.ScrollPagesToTopForTest();
        await Task.Delay(300);

        // --- an armed tool keeps its drag ---
        tab.IsWhiteoutMode = true;
        Check("with Whiteout armed, a drag on text is the tool's, not a selection", !view.DragForTest(0, from, 0, to));
        tab.IsWhiteoutMode = false;
        tab.IsRedactMode = true;
        Check("  and the same with Redact armed", !view.DragForTest(0, from, 0, to));
        tab.IsRedactMode = false;

        // --- an edit to a page drops the selection on it: its carets indexed the old text ---
        view.DragForTest(0, from, 0, to);
        await tab.AddWhiteoutAsync(0, new PdfRect(400, 700, 40, 20));
        Check("an edit to the page lets go of the selection on it", await UntilAsync(() => !view.HasTextSelection));
        if (tab.UndoCommand.CanExecute(null))
            tab.UndoCommand.Execute(null);
        await UntilAsync(() => !tab.Busy.IsBusy, 5);

        // --- a document that asks not to be copied from: it selects, Copy is off and says why ---
        // Beside the document, where tools/gen_test_fixtures.py copies it, or where the repository keeps it.
        var folder = Path.GetDirectoryName(path) ?? "";
        var noCopy = new[] { Path.Combine(folder, "owner-only.pdf"), Path.Combine(folder, "..", "security", "owner-only.pdf") }
            .Select(Path.GetFullPath).FirstOrDefault(File.Exists) ?? Path.Combine(folder, "owner-only.pdf");
        if (!File.Exists(noCopy))
        {
            Check($"owner-only.pdf is at {noCopy}", false);
            return;
        }
        await view.CopySelectedTextAsync(); // a known clipboard to compare against
        var knownClipboard = await ClipboardText();
        var knownPackage = DocumentView.AutomationClipboard;
        var restricted = await window.Shell.OpenInTabAsync(noCopy);
        Check("the no-copy document opens in a tab of its own", await UntilAsync(() => restricted.View is not null && restricted.IsDocumentOpen));
        if (restricted.View is not { } restrictedView || restricted.SelectableText is not { } restrictedText)
            return;
        Check("  and its first page is drawn", await UntilAsync(() => restricted.Pages.Count > 0 && restricted.Pages[0].Source is not null
                                                                    && restrictedView.HasPageCanvasForTest(0)));
        var first = restrictedText.Get(0);
        var firstLine = first.GetRects(0, first.Length).FirstOrDefault();
        Check("  it has text", first.Length > 0);
        var restrictedDrag = restrictedView.DragForTest(0, new PdfPoint(firstLine.X + 0.2, Mid(firstLine).Y), 0, new PdfPoint(firstLine.Right, Mid(firstLine).Y));
        if (!restrictedDrag)
            Console.Error.WriteLine($"  the press was refused: {restrictedView.LastTextPressRefusalForTest ?? "(it was accepted; the drag did not start)"}; first line {firstLine}");
        Check("its text still selects", restrictedDrag && restrictedView.HasTextSelection);
        Check("  the menu's Copy is off, and says why", restrictedView.TextMenuStateForTest() == (false, true));
        Check("  Copy copies nothing", !await restrictedView.CopySelectedTextAsync());
        Check("  and the clipboard is as it was",
              ReferenceEquals(DocumentView.AutomationClipboard, knownPackage) && (await ClipboardText()) == knownClipboard);
        Check("  the notice saying why is open", restricted.IsCopyWithheldNoticeOpen);
    }

    /// <summary>Windows bumps this on every change to the clipboard, by any app (#717).</summary>
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
