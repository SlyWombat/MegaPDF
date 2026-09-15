using Avalonia.Controls;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// Asks once per page before the first whiteout or text box change on a page PDFium's
/// rewrite would alter elsewhere (#139). Continue applies the change; Cancel, Escape or
/// closing the window leaves the page as it is.
/// </summary>
public partial class ConfirmPageRewriteWindow : Window
{
    internal bool Confirmed { get; private set; }

    public ConfirmPageRewriteWindow()
    {
        InitializeComponent();

        CancelButton.Click += (_, _) => Close();
        ContinueButton.Click += (_, _) =>
        {
            Confirmed = true;
            Close();
        };
    }
}
