using Avalonia.Controls;
using Avalonia.Input;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// The licences of everything MegaPDF ships (#176). Its own window rather than a
/// sheet: it is a long document, people resize it and scroll it, and Avalonia,
/// SkiaSharp, HarfBuzzSharp and PDFium all require their notices to travel with
/// the binary, so this has to be a place the text can actually be read.
///
/// The text comes from the bundle's own Contents/Resources/THIRD-PARTY-NOTICES.txt
/// whenever there is one, so what this window shows is what shipped rather than a
/// second copy that could drift from it.
/// </summary>
public partial class ThirdPartyNoticesWindow : Window
{
    /// <summary>The loaded text, for the self-test to compare against the file on disk.</summary>
    internal string? Notices { get; private set; }

    public ThirdPartyNoticesWindow()
    {
        InitializeComponent();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var text = await AppInfo.LoadNoticesAsync();
        // LoadNow may have got there first, from a test that has no message loop to
        // await on; the file is the same either way, so the first one in wins.
        if (Notices is not null)
            return;
        Notices = text;
        Paragraphs.ItemsSource = AppInfo.NoticeParagraphs(text);
        Paragraphs.IsVisible = true;
        Loading.IsVisible = false;
    }

    /// <summary>Loads synchronously, for the headless self-test, which has no message loop to await on.</summary>
    internal void LoadNow()
    {
        var text = AppInfo.LoadNotices();
        Notices = text;
        Paragraphs.ItemsSource = AppInfo.NoticeParagraphs(text);
        Paragraphs.IsVisible = true;
        Loading.IsVisible = false;
    }

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
