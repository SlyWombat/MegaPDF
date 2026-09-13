using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// Renames a library signature (#100). Returns null when cancelled or left blank,
/// which the caller treats as "leave it as it was".
/// </summary>
public partial class RenameSignatureWindow : Window
{
    internal string? NewName { get; private set; }

    public RenameSignatureWindow()
    {
        InitializeComponent();

        CancelButton.Click += (_, _) => Close();
        RenameButton.Click += (_, _) =>
        {
            var text = NameBox.Text?.Trim();
            NewName = string.IsNullOrEmpty(text) ? null : text;
            Close();
        };
    }

    internal void SetName(string current)
    {
        NameBox.Text = current;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Prefilled and fully selected, so typing replaces the old name outright.
        NameBox.Focus();
        NameBox.SelectAll();
    }
}
