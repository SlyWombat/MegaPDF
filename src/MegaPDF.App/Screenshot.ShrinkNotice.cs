using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MegaPDF.App;

internal static partial class Screenshot
{
    /// <summary>
    /// Shrink for email reports its outcome in a notice bar, never in a dialog (#632). Nothing
    /// failed and there is nothing to decide, so a modal that has to be dismissed was the wrong
    /// shape for "Was 4.2 MB, now 1.1 MB".
    ///
    /// Two halves, because the save picker cannot be driven headless:
    ///
    ///   * the real command on a document with no pictures (fixture.pdf has none), which ends at
    ///     "nothing to shrink" before the picker is ever reached, and must do so without opening a
    ///     dialog;
    ///   * the success half, raised directly through
    ///     <see cref="DocumentViewModel.ReportSmallerCopySaved"/>, then looked for in the real
    ///     visual tree so a bar that is bound to nothing cannot pass.
    ///
    /// The command is started without being awaited, and the check polls
    /// <see cref="DialogGate.IsShowing"/>: awaiting a command that opens a ContentDialog hangs the
    /// harness rather than failing it, which is the one failure worse than no check at all. The
    /// bar is left open so the PNG shows it. The not-smaller half (#6) goes through the real byte
    /// gate rather than a raised notice alone. Needs a document; the exit code is the test.
    /// </summary>
    private static async Task<bool> CheckShrinkNoticeAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } tab || tab.DocumentPath is not { } fixturePath)
        {
            Console.Error.WriteLine("--screenshot-state shrink-notice needs a document.");
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

        // 1. The real command, on a document with no pictures.
        tab.HasUnsavedChanges = false;   // "Save first" is a dialog by design; never ask about the fixture
        var run = tab.ShrinkForEmailCommand.ExecuteAsync(null);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!run.IsCompleted && !DialogGate.IsShowing && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        var dialogOpened = DialogGate.IsShowing;
        Check("Shrink on a document with no pictures opens no dialog", !dialogOpened);
        if (dialogOpened)
            DialogGate.Current?.Hide();   // fail, do not hang
        await run;

        Check("  and the notice is open", tab.IsShrinkNoticeOpen);
        Check("  titled Nothing to shrink", tab.ShrinkNoticeTitle == Strings.NothingToShrinkTitle);
        Check("  saying the pictures are already small", tab.ShrinkNotice == Strings.NothingToShrinkBody);
        Check("  as Informational, because nothing was saved", tab.ShrinkNoticeSeverity == InfoBarSeverity.Informational);
        await Task.Delay(120);   // the announcement is posted to the dispatcher
        Check($"  and it is announced (\"{tab.View?.LastAnnouncement}\")", tab.View?.LastAnnouncement == Strings.NothingToShrinkBody);

        // 2. Opening a document in the tab closes the notice.
        tab.HasUnsavedChanges = false;
        await tab.OpenDocumentAsync(fixturePath);
        await Task.Delay(1200);
        Check("opening a document in the tab closes the notice", !tab.IsShrinkNoticeOpen);

        // 3. The success half.
        const long was = 4_404_019;
        const long now = 1_153_434;
        const string name = "fixture - smaller.pdf";
        tab.ReportSmallerCopySaved(was, now, name);

        var expected = Strings.SmallerCopySavedNotice(was / 1024.0 / 1024, now / 1024.0 / 1024, name);
        Check("a saved smaller copy opens no dialog", !DialogGate.IsShowing);
        Check("  the notice is open", tab.IsShrinkNoticeOpen);
        Check("  titled Smaller copy saved", tab.ShrinkNoticeTitle == Strings.SmallerCopySavedTitle);
        Check($"  with both sizes and the file name (\"{tab.ShrinkNotice}\")", tab.ShrinkNotice == expected);
        Check("  as Success", tab.ShrinkNoticeSeverity == InfoBarSeverity.Success);
        Check("  on one line", !tab.ShrinkNotice.Contains('\n'));
        await Task.Delay(300);   // let the bindings settle before reading the control
        var bar = tab.View is { } v ? FindByAutomationId<InfoBar>(v, "ShrinkNoticeBar") : null;
        Check("  and ShrinkNoticeBar is in the page", bar is not null);
        Check("  open", bar?.IsOpen == true);
        Check("  Success", bar?.Severity == InfoBarSeverity.Success);
        Check("  showing the title", bar?.Title == Strings.SmallerCopySavedTitle);
        Check("  showing the message", bar?.Message == expected);

        // 4. A copy that is not smaller than the file on disk is never offered (#6). Driven through
        //    the real byte gate (the same method the command calls before it opens the picker), on a
        //    copy of the fixture against a baseline no document can be under: it must come back
        //    null, leave no temporary file, and raise the Informational notice. Then against a
        //    baseline it is under, where it must come back staged and say nothing.
        tab.IsShrinkNoticeOpen = false;
        var stagingBefore = Directory.GetFiles(Path.GetTempPath(), "megapdf-staged-*.pdf").Length;
        using (var engine = new MegaPDF.Core.Engine.Pdfium.PdfiumEngine())
        using (var copy = engine.Open(fixturePath))
        {
            var refused = await tab.StageSmallerCopyAsync(copy, baselineBytes: 1);
            Check("a copy that is not smaller than the file on disk is not offered", refused is null);
            refused?.Dispose();
            Check("  no save picker was opened", !DialogGate.IsShowing);
            Check("  the temporary copy is deleted", Directory.GetFiles(Path.GetTempPath(), "megapdf-staged-*.pdf").Length == stagingBefore);
            Check("  the notice is open", tab.IsShrinkNoticeOpen);
            Check("  titled Couldn't shrink", tab.ShrinkNoticeTitle == Strings.CouldNotShrinkTitle);
            Check($"  saying shrinking would not make it smaller (\"{tab.ShrinkNotice}\")", tab.ShrinkNotice == Strings.ShrinkNotSmallerBody);
            Check("  as Informational, because nothing failed", tab.ShrinkNoticeSeverity == InfoBarSeverity.Informational);
            await Task.Delay(300);   // let the bindings settle before reading the control
            var notSmallerBar = tab.View is { } nv ? FindByAutomationId<InfoBar>(nv, "ShrinkNoticeBar") : null;
            Check("  and ShrinkNoticeBar shows it", notSmallerBar?.IsOpen == true
                  && notSmallerBar.Severity == InfoBarSeverity.Informational
                  && notSmallerBar.Title == Strings.CouldNotShrinkTitle
                  && notSmallerBar.Message == Strings.ShrinkNotSmallerBody);

            tab.IsShrinkNoticeOpen = false;
            using var accepted = await tab.StageSmallerCopyAsync(copy, baselineBytes: long.MaxValue);
            Check("a copy that is smaller than the file on disk is staged for the picker", accepted is not null && File.Exists(accepted.Path));
            Check("  and says nothing", !tab.IsShrinkNoticeOpen);

            // Leave the PNG showing the new notice.
            tab.ReportShrinkNotSmaller();
        }
        await Task.Delay(300);

        Console.Error.WriteLine($"shrink-notice: {passed} passed, {failed} failed ({passed + failed} checks)");
        return failed == 0;
    }

    /// <summary>The first <typeparamref name="T"/> under <paramref name="root"/> whose
    /// AutomationId is <paramref name="id"/>. DocumentView's own FindDescendant is private and
    /// matches by type alone.</summary>
    private static T? FindByAutomationId<T>(DependencyObject root, string id) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit && AutomationProperties.GetAutomationId(child) == id)
                return hit;
            if (FindByAutomationId<T>(child, id) is { } deeper)
                return deeper;
        }
        return null;
    }
}
