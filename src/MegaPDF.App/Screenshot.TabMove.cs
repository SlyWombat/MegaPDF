using MegaPDF.Core.Engine;

namespace MegaPDF.App;

internal static partial class Screenshot
{
    /// <summary>
    /// A tab moved to a window of its own, and back (#711), through
    /// <see cref="MainWindow.MoveDocument"/>: the one code path a drag off the strip, a drop on
    /// another window's strip and "Move tab to new window" all end in. A WinUI process cannot
    /// synthesise a drag, so the drag itself is checked by hand; this is everything after the
    /// pointer lets go, which is where the work is.
    ///
    /// What has to hold, because each is a way a moved tab goes wrong while both windows still
    /// look fine:
    ///
    ///   * the document that arrives is the one that left: the same engine document (the file was
    ///     not read again), its unsaved edit, its undo history, zoom, scroll position and find;
    ///   * both windows draw their pages, and the toolbar is live in both, because a WinUI element
    ///     cannot change window and the tab gets a new view that has to wire itself up;
    ///   * the window a tab leaves keeps working and the window it arrives in keeps working when
    ///     the first one closes, which is when anything still pointing at a dead window shows;
    ///   * a window whose last tab leaves closes.
    ///
    /// The first window here is one this check makes, not the harness window, because closing it
    /// is part of the check and the harness still has to photograph its own window afterwards.
    /// The harness window is where the tab comes back to.
    /// </summary>
    private static async Task<bool> CheckTabMoveAsync(MainWindow harness)
    {
        if (harness.Shell.Active is not { IsDocumentOpen: true } harnessTab || harnessTab.DocumentPath is not { } fixturePath)
        {
            Console.Error.WriteLine("--screenshot-state tab-move needs a document.");
            return false;
        }

        var ok = true;
        void Check(string what, bool passed)
        {
            Console.Error.WriteLine($"{(passed ? "PASS" : "FAIL")}: {what}");
            ok &= passed;
        }

        static async Task<bool> UntilAsync(Func<bool> done, double seconds = 8)
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

        static bool ThrowsDisposed(IPdfDocument doc)
        {
            try
            {
                // Disposed: a page handed out and left open would hold the document's own
                // Dispose waiting for it for good.
                using var page = doc.GetPage(0);
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        // What the toolbar of a window should say for the tab it shows: every button as that
        // tab's own verdict, and the verdict "allowed" so that "all off" cannot pass.
        void CheckToolbar(string where, MainWindow window, DocumentViewModel tab, bool undoShouldBeOn)
        {
            var states = window.DocumentCommandEnabledStatesForTest();
            Check($"{where}: the tab allows signing, text, editing and printing",
                  tab.IsSigningAllowed && tab.IsTextBoxAllowed && tab.IsEditingAllowed && tab.IsPrintAllowed);
            foreach (var (button, expected) in new[]
                     {
                         ("SignaturesToolbarButton", tab.IsSigningAllowed),
                         ("AddTextButton", tab.IsTextBoxAllowed),
                         ("WhiteoutButton", tab.IsEditingAllowed),
                         ("RedactButton", tab.IsEditingAllowed),
                         ("PrintButton", tab.IsPrintAllowed),
                         ("FindButton", tab.IsDocumentOpen),
                         ("ZoomInButton", true),
                         ("UndoButton", undoShouldBeOn),
                     })
                Check($"{where}: {button} is {(expected ? "enabled" : "disabled")}", states[button] == expected);
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "megapdf-selftest-tab-move");
        Directory.CreateDirectory(tempDir);
        var copyP = Path.Combine(tempDir, "p.pdf");
        var copyQ = Path.Combine(tempDir, "q.pdf");
        File.Copy(fixturePath, copyP, overwrite: true);
        File.Copy(fixturePath, copyQ, overwrite: true);

        MainWindow? w0 = null;
        MainWindow? w2 = null;
        try
        {
            // --- Two documents in a window of their own (the "first window") ---
            w0 = new MainWindow();
            w0.Activate();
            var tabP = await w0.Shell.OpenInTabAsync(copyP);
            var tabQ = await w0.Shell.OpenInTabAsync(copyQ);
            Check("two documents open in the first window", w0.Shell.Documents.Count == 2);
            await UntilAsync(() => tabQ.View is { DrawsPagesForTest: true });
            await Task.Delay(500);

            // --- Give Q the things that must follow it ---
            var engineDocument = tabQ.CurrentDocument;
            await tabQ.AddWhiteoutAsync(0, new PdfRect(100, 100, 120, 30));
            Check("an unsaved edit is made in Q", tabQ.HasUnsavedChanges && tabQ.UndoCommand.CanExecute(null));
            await tabQ.SetZoomPercentAsync(150);
            await Task.Delay(500);
            var zoom = tabQ.ZoomPercent;
            tabQ.View!.PageScroller.ChangeView(null, 200, null, disableAnimation: true);
            await UntilAsync(() => Math.Abs(tabQ.CurrentScrollOffset - 200) < 5);
            var scroll = tabQ.CurrentScrollOffset;
            Check("Q is scrolled down the page", scroll > 100);
            w0.Shell.Active = tabQ;
            tabQ.View.ShowFindBar();
            await tabQ.SearchAsync("a");
            var matches = tabQ.SearchMatchCount;
            var findQuery = tabQ.View.FindBarState;
            Check("a find is open in Q with matches", findQuery.Open && matches > 0);
            var oldView = tabQ.View;

            // --- Move Q to a window of its own ---
            w2 = w0.MoveTabToNewWindow(tabQ);
            Check("Move tab to new window made a window", w2 is not null && w2 != w0);
            if (w2 is null)
                return false;
            await UntilAsync(() => tabQ.View is { DrawsPagesForTest: true } && !ReferenceEquals(tabQ.View, oldView));
            await Task.Delay(1000);

            Check("the new window holds Q and only Q", w2.Shell.Documents.Count == 1 && w2.Shell.Documents[0] == tabQ);
            Check("the first window still holds P and only P", w0.Shell.Documents.Count == 1 && w0.Shell.Documents[0] == tabP);
            Check("Q is the same document: the engine document was not opened again",
                  ReferenceEquals(tabQ.CurrentDocument, engineDocument));
            Check("Q got a new view, since an element cannot change window",
                  tabQ.View is not null && !ReferenceEquals(tabQ.View, oldView) && ReferenceEquals(tabQ.View, tabQ.TabContent));
            Check("the new window shows that view", ReferenceEquals(w2.TabContentForTest, tabQ.TabContent));
            Check("the old view no longer answers Q", !ReferenceEquals(w0.TabContentForTest, oldView));
            Check("Q's unsaved edit came with it", tabQ.HasUnsavedChanges);
            Check("  and so did its undo history", tabQ.UndoCommand.CanExecute(null) && !tabQ.RedoCommand.CanExecute(null));
            Check("  and its zoom", tabQ.ZoomPercent == zoom);
            Check("  and its scroll position (within 40 DIPs)",
                  tabQ.View is { } qView && Math.Abs(qView.PageScroller.VerticalOffset - scroll) < 40);
            Check("  and its find, matches and all",
                  tabQ.View!.FindBarState.Open && tabQ.View.FindBarState.Query == findQuery.Query
                  && tabQ.SearchMatchCount == matches);
            Check("both windows draw their pages",
                  tabQ.View is { DrawsPagesForTest: true } && tabP.View is { DrawsPagesForTest: true });
            var scaleNow = w2.Content.XamlRoot.RasterizationScale;
            Check($"Q renders at the new window's display scale ({scaleNow:0.##})",
                  Math.Abs(tabQ.RenderScaleForTest - scaleNow) < 0.001);
            CheckToolbar("second window", w2, tabQ, undoShouldBeOn: true);
            CheckToolbar("first window", w0, tabP, undoShouldBeOn: false);
            Check("the window title follows the tab", w2.Title == tabQ.WindowTitle && w0.Title == tabP.WindowTitle);

            // --- Q still edits in its new home ---
            tabQ.UndoCommand.Execute(null);
            var undone = await UntilAsync(() => tabQ.RedoCommand.CanExecute(null));
            Check("Undo in the new window takes the moved edit back (Redo is offered, Undo is spent)",
                  undone && !tabQ.UndoCommand.CanExecute(null));
            tabQ.RedoCommand.Execute(null);
            var redone = await UntilAsync(() => tabQ.UndoCommand.CanExecute(null));
            Check("Redo puts it back (Undo is offered again)", redone && !tabQ.RedoCommand.CanExecute(null));

            // --- Closing the first window leaves the second working ---
            var docP = tabP.CurrentDocument;
            w0.Close();
            var closed = await UntilAsync(() => !App.Windows.Contains(w0));
            var firstWindow = w0;
            w0 = null;
            Check("the first window closed", closed);
            Check("  and it disposed the document it still held", docP is not null && ThrowsDisposed(docP));
            await Task.Delay(500);
            Check("the second window kept Q, open, with its edit",
                  w2.Shell.Documents.Contains(tabQ) && tabQ.IsDocumentOpen && tabQ.HasUnsavedChanges
                  && !ThrowsDisposed(engineDocument!));
            Check("  and still draws its pages", tabQ.View is { DrawsPagesForTest: true });
            CheckToolbar("second window after the first closed", w2, tabQ, undoShouldBeOn: true);
            await tabQ.SetZoomPercentAsync(125);
            Check("  and still takes a command (zoom)", tabQ.ZoomPercent == 125);
            _ = firstWindow;

            // --- Move Q back to the harness window; the window it leaves closes with it ---
            var preBack = harness.Shell.Documents.Count;
            var viewInSecond = tabQ.View;
            MainWindow.MoveDocument(tabQ, w2, harness, int.MaxValue);
            var secondClosed = await UntilAsync(() => !App.Windows.Contains(w2));
            w2 = null;
            Check("a window whose last tab leaves closes", secondClosed);
            await UntilAsync(() => tabQ.View is { DrawsPagesForTest: true } && !ReferenceEquals(tabQ.View, viewInSecond));
            await Task.Delay(1000);
            Check("Q is back in the first window of the process, after its tab",
                  harness.Shell.Documents.Count == preBack + 1 && harness.Shell.Documents[^1] == tabQ);
            Check("  with its edit and its engine document", tabQ.HasUnsavedChanges && ReferenceEquals(tabQ.CurrentDocument, engineDocument));
            Check("  drawn again", tabQ.View is { DrawsPagesForTest: true });
            CheckToolbar("harness window", harness, tabQ, undoShouldBeOn: true);

            // Leave the window as this check found it: Q is closed without saving, and its
            // journal ended the way a "Don't save" close ends it.
            tabQ.EndJournalSession();
            harness.Shell.RemoveDocument(tabQ);
            harness.Shell.Active = harnessTab;
            await UntilAsync(() => harnessTab.View is { DrawsPagesForTest: true });
            Check("  and the tab it joined draws when it is shown again", harnessTab.View is { DrawsPagesForTest: true });
        }
        finally
        {
            try { w0?.Close(); } catch { /* best-effort */ }
            try { w2?.Close(); } catch { /* best-effort */ }
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort scratch cleanup */ }
        }

        Console.Error.WriteLine($"tab-move: finished, {(ok ? "all passed" : "with failures")}");
        return ok;
    }
}
