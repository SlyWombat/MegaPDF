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
        nameof(ViewModel), typeof(DocumentViewModel), typeof(SignatureLibraryPanel), new PropertyMetadata(null));

    public SignatureLibraryPanel()
    {
        InitializeComponent();
        // The three add buttons carry their label in a child TextBlock, which leaves the
        // button itself unnamed: Narrator announced "button" three times (#146 RC). The
        // name is set here rather than through an x:Uid property path, which MRT did not
        // apply to AutomationProperties.Name on these buttons.
        Loaded += (_, _) =>
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DrawSignatureButton, Strings.DrawSignatureName);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TypeSignatureButton, Strings.TypeSignatureName);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AddSignatureFromImageButton, Strings.AddFromImageName);
        };
    }

    public DocumentViewModel? ViewModel
    {
        get => (DocumentViewModel?)GetValue(ViewModelProperty);
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
