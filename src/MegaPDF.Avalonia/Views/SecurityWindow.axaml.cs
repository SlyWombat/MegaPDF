using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// Sets, changes or removes a document's password (#131, ADR-004 §5). Only asks: the
/// caller does the save. The new password lives in <see cref="NewPassword"/> for as long
/// as the window object does, and nowhere else.
/// </summary>
public partial class SecurityWindow : Window
{
    public enum Decision { None, Set, Change, Remove }

    internal Decision Choice { get; private set; }

    internal string? NewPassword { get; private set; }

    private string _fileName = "";
    private bool _changing;

    public SecurityWindow()
    {
        InitializeComponent();

        ManageCancelButton.Click += (_, _) => Close();
        EntryCancelButton.Click += (_, _) => Close();
        RemoveButton.Click += (_, _) =>
        {
            Choice = Decision.Remove;
            Close();
        };
        ChangeButton.Click += (_, _) => ShowEntry(changing: true);
        ConfirmButton.Click += (_, _) => Confirm();
    }

    /// <summary>An unprotected document goes straight to the entry fields.</summary>
    internal void Configure(string fileName, bool isEncrypted)
    {
        _fileName = fileName;
        if (isEncrypted)
        {
            ManageText.Text = Strings.ProtectedPrompt(fileName);
            ManagePanel.IsVisible = true;
            ChangeButton.IsDefault = true;
            ManageCancelButton.IsCancel = true;
        }
        else
        {
            ShowEntry(changing: false);
        }
    }

    private void ShowEntry(bool changing)
    {
        _changing = changing;
        // IsDefault and IsCancel move with the visible panel: a hidden default button
        // would still take Enter.
        ChangeButton.IsDefault = false;
        ManageCancelButton.IsCancel = false;
        ManagePanel.IsVisible = false;

        Title = changing ? Strings.ChangePasswordTitle : Strings.SetPasswordTitle;
        EntryText.Text = Strings.SetPasswordPrompt(_fileName);
        ConfirmButton.Content = changing ? Strings.ChangePasswordButton : Strings.SetPasswordButton;
        ConfirmButton.IsDefault = true;
        EntryCancelButton.IsCancel = true;
        EntryPanel.IsVisible = true;
        NewBox.Focus();
    }

    private void Confirm()
    {
        var first = NewBox.Text ?? "";
        var second = ConfirmBox.Text ?? "";
        var problem = first.Length == 0 ? Strings.PasswordEmpty
            : first != second ? Strings.PasswordsDoNotMatch
            : null;
        if (problem is not null)
        {
            ProblemText.Text = problem;
            ProblemText.IsVisible = true;
            return;
        }

        NewPassword = first;
        Choice = _changing ? Decision.Change : Decision.Set;
        Close();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (EntryPanel.IsVisible)
            NewBox.Focus();
    }
}
