using Avalonia.Controls;
using MegaPDF.Avalonia.ViewModels;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// The question before a save or a password change takes away the undo history (#633). One
/// fact, two wordings: the flatten reason and the password reason share a title and buttons.
/// The button that goes on (Save, or Continue for the password) is the default, because the
/// document is saved either way and Enter must never silently fail to save; Cancel, Escape or
/// closing the window leaves everything as it was.
/// </summary>
public partial class ConfirmUndoLossWindow : Window
{
    internal bool Confirmed { get; private set; }

    public ConfirmUndoLossWindow()
    {
        InitializeComponent();

        CancelButton.Click += (_, _) => Close();
        ContinueButton.Click += (_, _) =>
        {
            Confirmed = true;
            Close();
        };
    }

    internal void SetReason(UndoLossReason reason)
    {
        BodyText.Text = reason == UndoLossReason.Flatten ? Strings.UndoLossFlattenBody : Strings.UndoLossPasswordBody;
        ContinueButton.Content = reason == UndoLossReason.Flatten ? Strings.Save : Strings.Continue;
    }
}
