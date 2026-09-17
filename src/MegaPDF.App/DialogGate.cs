using Microsoft.UI.Xaml.Controls;

namespace MegaPDF.App;

/// <summary>
/// Shows ContentDialogs one at a time (#163).
///
/// WinUI allows a single open ContentDialog per thread, and a second ShowAsync throws
/// "Only a single ContentDialog can be open at any time". The dialog's smoke layer blocks
/// pointer and keyboard input to the page, but not everything: the title bar's close
/// button still starts the unsaved-changes prompt, and UI Automation (a screen reader's
/// scan mode, the capture harness) can still invoke toolbar commands behind a dialog.
/// Invoking Password… behind the crash-recovery prompt crashed the app; closing the window
/// over an open dialog asked nothing at all. Every dialog goes through here instead and
/// waits its turn.
///
/// Never show a dialog from inside another dialog's button handler: it would wait for the
/// dialog that is running the handler, which cannot close first.
/// </summary>
internal static class DialogGate
{
    private static readonly SemaphoreSlim Turn = new(1, 1);

    public static async Task<ContentDialogResult> ShowOneAtATimeAsync(this ContentDialog dialog)
    {
        await Turn.WaitAsync();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            Turn.Release();
        }
    }
}
