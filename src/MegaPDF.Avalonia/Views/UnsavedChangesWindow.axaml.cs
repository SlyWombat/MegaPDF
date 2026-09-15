using Avalonia.Controls;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// Save, Don't Save or Cancel before unsaved changes would be lost (#145, D1): quitting,
/// closing the window, and opening or unlocking another copy of the document. Cancel, Escape
/// and closing the dialog change nothing, the recovery journal included.
/// </summary>
public partial class UnsavedChangesWindow : Window
{
    public enum Decision { Cancel, Save, DontSave }

    internal Decision Choice { get; private set; } = Decision.Cancel;

    public UnsavedChangesWindow()
    {
        InitializeComponent();

        CancelButton.Click += (_, _) => Close();
        SaveButton.Click += (_, _) =>
        {
            Choice = Decision.Save;
            Close();
        };
        DontSaveButton.Click += (_, _) =>
        {
            Choice = Decision.DontSave;
            Close();
        };
    }

    internal void SetDocument(string documentName) =>
        PromptText.Text = Strings.UnsavedChangesPrompt(documentName);
}
