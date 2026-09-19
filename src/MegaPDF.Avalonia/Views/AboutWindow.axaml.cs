using Avalonia.Controls;
using Avalonia.Input;
using MegaPDF.Core.Services;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// About MegaPDF (#176): the name, the version, the copyright, the credit the
/// other three platforms carry, a link to the source, and the way to the
/// third-party notices. It replaces Avalonia's own About panel, which named the
/// framework and never the app, in the first slot of the first menu.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        VersionText.Text = AppInfo.VersionLabel;
        ProjectLink.NavigateUri = new Uri(AppInfo.ProjectUrl);

        var install = LinuxInstall.Current(AppContext.BaseDirectory);
        if (AppInfo.UpdatesLine(install) is { } updates)
        {
            UpdatesText.Text = updates;
            UpdatesPanel.IsVisible = true;
            DownloadPageLink.IsVisible = LinuxInstall.NeedsDownloadPage(install);
            DownloadPageLink.NavigateUri = new Uri(LinuxInstall.DownloadPage);
        }
        // Not a modal dialog: a licence is something to read beside the app, and a
        // Mac About panel does not hold the app hostage. App owns the one instance,
        // because Help reaches the same window without going through here.
        NoticesButton.Click += (_, _) => App.ShowNotices(this);
    }

    /// <summary>
    /// Escape and Cmd+W close it. A Mac panel with no buttons still has to answer
    /// the keyboard, and Cmd+W is what a person reaches for first.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape
            || (e.Key == Key.W && e.KeyModifiers.HasFlag(MainWindow.CommandModifier)))
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
