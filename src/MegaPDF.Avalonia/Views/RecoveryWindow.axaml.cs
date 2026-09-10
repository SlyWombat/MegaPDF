using System.Globalization;
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
        Headline.Text = Strings.RecoveryHeadline(fileName);
        // The person's own date and time conventions, not a hard-coded English
        // pattern: "f" is the culture's long date with its short time.
        var lastWrite = lastWriteUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
        Detail.Text = Strings.Plural(edits,
                          Strings.RecoveryChangesOne(edits, lastWrite),
                          Strings.RecoveryChangesOther(edits, lastWrite))
                      + " " + Strings.RecoveryExplanation;
    }
}
