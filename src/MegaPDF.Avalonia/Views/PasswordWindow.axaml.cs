using Avalonia.Controls;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// Asks for a document password. Returns null when the user cancels, which the
/// caller treats as "leave the document closed" rather than as a failure.
/// </summary>
public partial class PasswordWindow : Window
{
    internal string? Password { get; private set; }

    public PasswordWindow()
    {
        InitializeComponent();

        CancelButton.Click += (_, _) => Close();
        OpenButton.Click += (_, _) =>
        {
            Password = PasswordBox.Text;
            Close();
        };
    }

    internal void SetPrompt(string fileName, bool retry)
    {
        PromptText.Text = retry
            ? $"That password did not open \"{fileName}\". Try again?"
            : $"\"{fileName}\" is password-protected. Enter its password to open it.";
    }
}
