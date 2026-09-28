using Avalonia.Controls;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// The warning before Save overwrites a signed original (#476, #481, SDD-adjacent to
/// #173's ConfirmRedactionWindow). Save a copy is the default action and Overwrite the
/// second, deliberate one — Dave's framing is that the common path, saving a copy under
/// your own name, is already safe, so the destructive path is the one that needs the
/// extra click. Cancel, Escape and closing the window change nothing: nothing is saved.
/// </summary>
public partial class ConfirmSignedSaveWindow : Window
{
    public enum Decision { Cancel, SaveAsCopy, Overwrite }

    internal Decision Choice { get; private set; } = Decision.Cancel;

    public ConfirmSignedSaveWindow()
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

    /// <summary>
    /// Sets the title/body for whichever case applies. A certification signature
    /// (`/DocMDP`) gets different wording — measured (#476/#481) as the common case on
    /// real signed documents, not the rare one: its own author declared the document
    /// closed to modification outright, a stronger statement than "this will invalidate
    /// the signature".
    /// </summary>
    internal void SetCertification(bool isCertified)
    {
        Title = isCertified ? Strings.CertifiedSaveWarningTitle : Strings.SignedSaveWarningTitle;
        PromptText.Text = Title;
        BodyText.Text = isCertified ? Strings.CertifiedSaveWarningBody : Strings.SignedSaveWarningBody;
    }
}
