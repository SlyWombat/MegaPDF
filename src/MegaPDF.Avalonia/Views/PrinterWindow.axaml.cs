using Avalonia.Controls;
using MegaPDF.Avalonia.Platform;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// Which printer, and how many copies (#158). CUPS' <c>lp</c> would print to the
/// default queue without asking, and printing to the wrong printer is the one
/// mistake in this app you cannot undo — so MegaPDF asks, the way the Windows and
/// macOS print panels do.
///
/// With no destinations configured the dialog still opens, says so, and offers
/// only Cancel: an empty printer list with a live Print button would send the job
/// nowhere and report success.
/// </summary>
public partial class PrinterWindow : Window
{
    /// <summary>What was chosen, or null when the dialog was cancelled or closed.</summary>
    internal LinuxPrinter.Choice? Chosen { get; private set; }

    public PrinterWindow()
    {
        InitializeComponent();

        CancelButton.Click += (_, _) => Close();
        PrintButton.Click += (_, _) =>
        {
            if (PrinterBox.SelectedItem is not LinuxPrinter.Destination destination)
                return;
            Chosen = new LinuxPrinter.Choice(destination.Name, (int)(CopiesBox.Value ?? 1));
            Close();
        };
    }

    /// <summary>
    /// Fills the dialog in. The default destination is preselected, because that is
    /// the one a bare `lp` would have used and the one people mean.
    /// </summary>
    internal void Present(string documentName, IReadOnlyList<LinuxPrinter.Destination> destinations)
    {
        DocumentText.Text = documentName;

        PrinterBox.ItemsSource = destinations;
        PrinterBox.DisplayMemberBinding = new global::Avalonia.Data.Binding(nameof(LinuxPrinter.Destination.Label));
        PrinterBox.SelectedIndex = destinations.Count == 0 ? -1 : 0;

        var any = destinations.Count > 0;
        PrinterBox.IsVisible = any;
        CopiesPanel.IsVisible = any;
        NoPrintersText.IsVisible = !any;
        PrintButton.IsEnabled = any;
    }
}
