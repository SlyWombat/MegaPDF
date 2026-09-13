using Avalonia.Controls;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// Asks once before a library signature is deleted (#100). A signature is a
/// person's own handwriting and cannot be recovered, so the destructive button
/// is neither the default nor the one Enter reaches.
/// </summary>
public partial class ConfirmDeleteWindow : Window
{
    internal bool Confirmed { get; private set; }

    public ConfirmDeleteWindow()
    {
        InitializeComponent();

        CancelButton.Click += (_, _) => Close();
        DeleteButton.Click += (_, _) =>
        {
            Confirmed = true;
            Close();
        };
    }

    internal void SetPrompt(string signatureName)
    {
        PromptText.Text = Strings.DeleteSignaturePrompt(signatureName);
    }
}
