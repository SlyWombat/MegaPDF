using Avalonia.Controls;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// Offers to restore work that was never saved (SDD §3.4).
///
/// Three answers rather than two. "Decide later" leaves the journal alone, so the
/// offer comes back next launch — which matters because discarding is
/// irreversible and someone opening the app to do something else should not have
/// to make that call on the spot.
/// </summary>
public partial class RecoveryWindow : Window
{
    internal enum Decision { Later, Restore, Discard }

    internal Decision Choice { get; private set; } = Decision.Later;

    public RecoveryWindow()
    {
        InitializeComponent();

        RestoreButton.Click += (_, _) => { Choice = Decision.Restore; Close(); };
        DiscardButton.Click += (_, _) => { Choice = Decision.Discard; Close(); };
        LaterButton.Click += (_, _) => { Choice = Decision.Later; Close(); };
    }

    internal void SetSession(string fileName, int edits, DateTime lastWriteUtc)
    {
        Headline.Text = $"MegaPDF closed with unsaved changes to \"{fileName}\".";
        Detail.Text = $"{edits} change{(edits == 1 ? "" : "s")} were made and never saved, "
                      + $"last on {lastWriteUtc.ToLocalTime():d MMMM 'at' h:mm tt}. "
                      + "Restoring reopens the document and puts them back; nothing is written "
                      + "to the file until you save.";
    }
}
