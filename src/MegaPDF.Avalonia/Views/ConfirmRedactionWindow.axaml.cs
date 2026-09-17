using Avalonia.Controls;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// The confirmation before a redaction is applied (#173, SDD §3.8). Save as a copy is the
/// default action and Overwrite the second, because redaction cannot be undone after saving
/// and the reversible choice should be the one a Return key lands on. Cancel, Escape and
/// closing the dialog change nothing: the marks stay, and nothing has been removed.
/// </summary>
public partial class ConfirmRedactionWindow : Window
{
    public enum Decision { Cancel, SaveAsCopy, Overwrite }

    internal Decision Choice { get; private set; } = Decision.Cancel;

    public ConfirmRedactionWindow()
    {
        InitializeComponent();

        CancelButton.Click += (_, _) => Close();
        SaveCopyButton.Click += (_, _) =>
        {
            Choice = Decision.SaveAsCopy;
            Close();
        };
        OverwriteButton.Click += (_, _) =>
        {
            Choice = Decision.Overwrite;
            Close();
        };
    }

    /// <summary>How many areas are about to go, so the number is never a surprise.</summary>
    internal void SetMarkCount(int count) =>
        MarkCountText.Text = count == 1 ? Strings.RedactMarkCountOne : Strings.RedactMarkCount(count);
}
