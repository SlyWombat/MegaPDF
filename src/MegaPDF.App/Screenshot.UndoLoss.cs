using Microsoft.UI.Xaml.Controls;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;

namespace MegaPDF.App;

internal static partial class Screenshot
{
    /// <summary>
    /// The question before a save or a password change takes away the undo history (#633).
    /// A password change clears it whether or not "Make marks &amp; signatures permanent" is
    /// on, because it writes the file and reopens it; a save clears it only when that setting
    /// is on. The flatten question is asked once per open document, so Ctrl+S is not a dialog
    /// every time.
    ///
    /// A ContentDialog cannot be clicked from inside the process (#462), so Cancel is proved
    /// by closing the real dialog the way Escape does (<c>DialogGate.Current.Hide()</c>), and
    /// Save and Continue through <see cref="DocumentViewModel.AnswerUndoLossForTest"/>. Every
    /// command that may show a dialog is started, not awaited, until the gate has been polled:
    /// awaiting it first would hang the check instead of failing it.
    ///
    /// Changes <c>FlattenOnSave</c>, which is the real user setting on a machine that is not
    /// running with an isolated data folder, so it is restored in <c>finally</c>. Needs a
    /// document; the exit code is the test.
    /// </summary>
    private static async Task<bool> CheckUndoLossAsync(MainWindow window)
    {
        if (window.Shell.Active is not { IsDocumentOpen: true } fixtureTab
            || fixtureTab.DocumentPath is not { } fixturePath)
        {
            Console.Error.WriteLine("--screenshot-state undo-loss needs a document.");
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

        var flattenAtStart = fixtureTab.FlattenOnSave;
        var scratch = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-undoloss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            await RunUndoLossChecksAsync(window, fixturePath, scratch, Check);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: the undo-loss check threw: {ex}");
            failed++;
        }
        finally
        {
            DialogGate.Current?.Hide();
            foreach (var tab in window.Shell.Documents.Where(t => !ReferenceEquals(t, fixtureTab)).ToList())
            {
                tab.AnswerUndoLossForTest = null;
                tab.HasUnsavedChanges = false;
                await window.CloseTabAsync(tab);
            }
            fixtureTab.FlattenOnSave = flattenAtStart;
            try { Directory.Delete(scratch, recursive: true); } catch { /* best-effort scratch cleanup */ }
        }

        Console.Error.WriteLine($"undo-loss: {passed} passed, {failed} failed ({passed + failed} checks)");
        return failed == 0;
    }

    private static async Task RunUndoLossChecksAsync(
        MainWindow window, string fixturePath, string scratch, Action<string, bool> Check)
    {
        var work = Path.Combine(scratch, "work.pdf");
        File.Copy(fixturePath, work, overwrite: true);

        var tab = await window.Shell.OpenInTabAsync(work);
        await Task.Delay(1000);
        Check("a working tab opened", tab.IsDocumentOpen);
        if (!tab.IsDocumentOpen)
            return;

        var edits = 0;
        async Task EditAsync()
        {
            edits++;
            await tab.AddTextBoxAsync(0, new PdfPoint(72, 500 - 30 * edits), $"undo-loss {edits}");
            await Task.Delay(500);
        }

        // --- 1. Something to undo ------------------------------------------------------
        tab.FlattenOnSave = true;
        await EditAsync();
        Check("there is something to undo", tab.UndoCommand.CanExecute(null));
        Check("the question has not been put yet", tab.UndoLossAsked == 0);

        // --- 2. Cancel through the real dialog -----------------------------------------
        var bytesBefore = await File.ReadAllBytesAsync(work);
        var run = tab.SaveCommand.ExecuteAsync(null);
        var opened = await PumpUntilAsync(() => DialogGate.Current is not null, TimeSpan.FromSeconds(5));
        Check("a dialog opened when Save was pressed with flatten on and an edit", opened);
        if (DialogGate.Current is { } dialog)
        {
            Check("its title is the undo-history title", Equals(dialog.Title, Strings.UndoLossTitle));
            Check("its body is the flatten wording", Equals(dialog.Content, Strings.UndoLossFlattenBody));
            Check("its primary button is Save", dialog.PrimaryButtonText == Strings.Save);
            Check("its close button is Cancel", dialog.CloseButtonText == Strings.Cancel);
            Check("Save, the action, is the default button", dialog.DefaultButton == ContentDialogButton.Primary);
            Check("it has no third button and no Don't-ask-again tick",
                  string.IsNullOrEmpty(dialog.SecondaryButtonText) && dialog.Content is string);
            dialog.Hide();   // None, exactly as Escape or Cancel would return
        }
        if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))) != run)
            Check("Save returned once the dialog closed (it did not, within 5s)", false);
        await Task.Delay(300);
        Check("Cancel keeps the undo history", tab.UndoCommand.CanExecute(null));
        Check("Cancel leaves the unsaved edit unsaved", tab.HasUnsavedChanges);
        Check("Cancel writes nothing", (await File.ReadAllBytesAsync(work)).AsSpan().SequenceEqual(bytesBefore));
        Check("the question was put once", tab.UndoLossAsked == 1);

        // --- 3. Save ---------------------------------------------------------------------
        tab.AnswerUndoLossForTest = _ => true;
        await tab.SaveCommand.ExecuteAsync(null);
        await Task.Delay(1000);
        Check("Save flattens, and the history is gone", !tab.UndoCommand.CanExecute(null));
        Check("Save clears the unsaved flag", !tab.HasUnsavedChanges);
        Check("Save writes the file", !(await File.ReadAllBytesAsync(work)).AsSpan().SequenceEqual(bytesBefore));
        Check("the question was put twice in all", tab.UndoLossAsked == 2);

        // --- 4. Once per open document -----------------------------------------------------
        await EditAsync();
        Check("a second edit can be undone", tab.UndoCommand.CanExecute(null));
        await tab.SaveCommand.ExecuteAsync(null);
        await Task.Delay(1000);
        Check("the second Save does not ask", tab.UndoLossAsked == 2);
        Check("the second Save still flattened, so the history is gone", !tab.UndoCommand.CanExecute(null));

        // --- 5. Flatten off never asks -------------------------------------------------------
        tab.FlattenOnSave = false;
        await EditAsync();
        await tab.SaveCommand.ExecuteAsync(null);
        await Task.Delay(1000);
        Check("with flatten off, Save does not ask", tab.UndoLossAsked == 2);
        Check("with flatten off, the history survives the save", tab.UndoCommand.CanExecute(null));

        // --- 6. Password, Cancel ---------------------------------------------------------------
        await EditAsync();
        UndoLossReason? asked = null;
        tab.AnswerUndoLossForTest = r => { asked = r; return false; };
        // Started, not awaited: before the change this opens the Set Password dialog, and
        // awaiting it would hang the check instead of failing it.
        var declined = tab.SecurityCommand.ExecuteAsync(null);
        await PumpUntilAsync(() => declined.IsCompleted || DialogGate.Current is not null, TimeSpan.FromSeconds(5));
        var strayDialog = DialogGate.Current is not null;
        DialogGate.Current?.Hide();
        if (await Task.WhenAny(declined, Task.Delay(TimeSpan.FromSeconds(5))) != declined)
            Check("the Password command returned (it did not, within 5s)", false);
        await Task.Delay(300);
        Check("the Password command asked, with flatten off", tab.UndoLossAsked == 3);
        Check("  for the password reason", asked == UndoLossReason.PasswordChange);
        Check("  and no password dialog followed Cancel", !strayDialog);
        Check("  the history is kept", tab.UndoCommand.CanExecute(null));
        using (var engine = new PdfiumEngine())
        {
            var opensWithoutPassword = true;
            try { using var reopened = engine.Open(work); }
            catch { opensWithoutPassword = false; }
            Check("  nothing was written: the file still opens with no password", opensWithoutPassword);
        }

        // --- 7. Password, Continue ---------------------------------------------------------------
        tab.AnswerUndoLossForTest = r => { asked = r; return true; };
        var security = tab.SecurityCommand.ExecuteAsync(null);
        var passwordDialog = await PumpUntilAsync(() => DialogGate.Current is not null, TimeSpan.FromSeconds(5));
        Check("Continue went on to a dialog", passwordDialog);
        if (DialogGate.Current is { } setPassword)
        {
            Check("  and it is the real Set Password dialog", Equals(setPassword.Title, Strings.SetPasswordTitle));
            setPassword.Hide();
        }
        if (await Task.WhenAny(security, Task.Delay(TimeSpan.FromSeconds(5))) != security)
            Check("the password command returned once the dialog closed (it did not, within 5s)", false);
        await Task.Delay(300);
        Check("the Password command asked again", tab.UndoLossAsked == 4);
        Check("cancelling the password dialog keeps the history", tab.UndoCommand.CanExecute(null));

        // --- 8. A reopened document asks again -----------------------------------------------------
        tab.HasUnsavedChanges = false;   // so the reopen does not raise its own Save-changes dialog
        await tab.OpenDocumentAsync(work);
        await Task.Delay(1000);
        tab.FlattenOnSave = true;
        await EditAsync();
        tab.AnswerUndoLossForTest = _ => true;
        await tab.SaveCommand.ExecuteAsync(null);
        await Task.Delay(1000);
        Check("a reopened document asks again", tab.UndoLossAsked == 5);
        Check("  and flattens", !tab.UndoCommand.CanExecute(null));
    }
}
