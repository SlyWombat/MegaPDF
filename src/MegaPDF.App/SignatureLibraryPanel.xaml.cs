using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MegaPDF.App;

/// <summary>
/// The signature library panel (#100): cards for the stored signatures, an overflow
/// per card, and the three ways to add one. Raises intent; the window decides what
/// a pick, a rename or a delete does, because it owns the dialogs and the flyout.
/// </summary>
public sealed partial class SignatureLibraryPanel : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(MainViewModel), typeof(SignatureLibraryPanel), new PropertyMetadata(null));

    public SignatureLibraryPanel()
    {
        InitializeComponent();
    }

    public MainViewModel? ViewModel
    {
        get => (MainViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>A card was clicked: arm this signature for placement.</summary>
    public event EventHandler<SignatureItem>? Picked;

    public event EventHandler<SignatureItem>? RenameRequested;
    public event EventHandler<SignatureItem>? DeleteRequested;
    public event EventHandler? DrawRequested;
    public event EventHandler? TypeRequested;
    public event EventHandler? PhotoRequested;

    private void OnCardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SignatureItem item })
            Picked?.Invoke(this, item);
    }

    private void OnRenameClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: SignatureItem item })
            RenameRequested?.Invoke(this, item);
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: SignatureItem item })
            DeleteRequested?.Invoke(this, item);
    }

    private void OnDrawClicked(object sender, RoutedEventArgs e) => DrawRequested?.Invoke(this, EventArgs.Empty);
    private void OnTypeClicked(object sender, RoutedEventArgs e) => TypeRequested?.Invoke(this, EventArgs.Empty);
    private void OnPhotoClicked(object sender, RoutedEventArgs e) => PhotoRequested?.Invoke(this, EventArgs.Empty);
}
