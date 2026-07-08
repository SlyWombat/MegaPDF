using System.Runtime.InteropServices.WindowsRuntime;
using MegaPDF.Core.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace MegaPDF.App;

public sealed partial class MainWindow : Window
{
    private TextBox? _activeEditor;

    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainViewModel(this);
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "megapdf.ico"));
        ViewModel.LoadSignatures();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.WindowTitle))
                Title = ViewModel.WindowTitle;
        };
        Title = ViewModel.WindowTitle;
    }

    private void OnDocumentAreaDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private async void OnDocumentAreaDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        var pdf = items.OfType<StorageFile>()
            .FirstOrDefault(f => f.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf is not null)
            await ViewModel.OpenDocumentAsync(pdf.Path);
    }

    /// <summary>
    /// The document is the interface (SDD §2.2): what you click determines what happens —
    /// checkboxes toggle, form fields and body text edit in place, empty space does nothing.
    /// </summary>
    private async void OnPageTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Grid pageGrid || pageGrid.DataContext is not PageView pageView)
            return;

        // A tap while an editor is open just commits it (via LostFocus); don't open another.
        if (_activeEditor is not null)
            return;

        var position = e.GetPosition(pageGrid);
        var pagePoint = new PdfPoint(position.X * 72 / 96, position.Y * 72 / 96);

        // Signature placement mode: the next page click stamps the pending signature.
        if (ViewModel.PendingSignature is not null)
        {
            await ViewModel.PlacePendingSignatureAsync(pageView.Index, pagePoint);
            return;
        }

        var hit = await Task.Run(() => ViewModel.HitTestPage(pageView.Index, pagePoint));
        switch (hit.Kind)
        {
            case PageHitKind.FormCheckbox:
                await ViewModel.ToggleCheckboxAsync(pageView.Index, hit.Field!);
                break;

            case PageHitKind.DrawnCheckbox:
                await ViewModel.AddMarkAsync(pageView.Index, hit.Bounds!.Value);
                break;

            case PageHitKind.StampAnnotation:
                // Clicking a placed mark or signature removes it (undoable).
                await ViewModel.RemoveStampAsync(pageView.Index, hit.AnnotationId!, hit.Bounds!.Value);
                break;

            case PageHitKind.FormTextField:
            {
                var field = hit.Field!;
                ShowInlineEditor(pageGrid, field.Bounds, field.Value, fontSizePoints: 12,
                    newText => ViewModel.ApplyFormTextAsync(pageView.Index, field, newText));
                break;
            }

            case PageHitKind.TextRun:
            {
                var run = hit.TextRun!;
                ShowInlineEditor(pageGrid, run.Bounds, run.Text, run.FontSize,
                    newText => string.IsNullOrEmpty(newText)
                        ? Task.CompletedTask
                        : ViewModel.ApplyTextEditAsync(pageView.Index, run, newText));
                break;
            }
        }
    }

    private void ShowInlineEditor(Grid pageGrid, PdfRect bounds, string initialText, double fontSizePoints, Func<string, Task> commit)
    {
        var editor = new TextBox
        {
            Text = initialText,
            FontSize = Math.Max(fontSizePoints * 96 / 72, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(bounds.X * 96 / 72 - 6, bounds.Y * 96 / 72 - 8, 0, 0),
            MinWidth = Math.Max(bounds.Width * 96 / 72 + 28, 140),
            AcceptsReturn = false,
        };

        async Task CommitAsync()
        {
            if (!pageGrid.Children.Contains(editor))
                return; // already committed or cancelled
            var newText = editor.Text;
            CloseEditor(pageGrid, editor);
            await commit(newText);
        }

        editor.KeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                await CommitAsync();
            }
            else if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                CloseEditor(pageGrid, editor);
            }
        };
        editor.LostFocus += async (_, _) => await CommitAsync();

        pageGrid.Children.Add(editor);
        _activeEditor = editor;
        editor.Focus(FocusState.Programmatic);
        editor.SelectAll();
    }

    private void CloseEditor(Grid pageGrid, TextBox editor)
    {
        pageGrid.Children.Remove(editor);
        if (_activeEditor == editor)
            _activeEditor = null;
    }

    // --- Signature library & placement (SDD §3.3) ---

    private void OnSignatureClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SignatureItem item })
        {
            ViewModel.SelectSignatureForPlacement(item);
            SignaturesFlyout.Hide();
        }
    }

    private void OnRemoveSignatureClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: SignatureItem item })
            ViewModel.RemoveSignatureFromLibrary(item);
    }

    private void OnCancelPlacementClicked(InfoBar sender, object args) =>
        ViewModel.CancelSignaturePlacement();

    private async void OnAddSignatureFromImageClicked(object sender, RoutedEventArgs e)
    {
        SignaturesFlyout.Hide();
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        var image = await SignatureImageProcessor.LoadAndCleanAsync(file);
        await ViewModel.AddSignatureFromImageAsync(image, Path.GetFileNameWithoutExtension(file.Name));
    }

    private async void OnTypeSignatureClicked(object sender, RoutedEventArgs e)
    {
        SignaturesFlyout.Hide();
        var input = new TextBox { PlaceholderText = "Your name", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Script"), FontSize = 24 };
        var dialog = new ContentDialog
        {
            Title = "Type your signature",
            Content = input,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text))
            return;

        var image = await RenderTypedSignatureAsync(input.Text.Trim());
        await ViewModel.AddSignatureFromImageAsync(image, input.Text.Trim());
    }

    /// <summary>Renders the offscreen Segoe Script TextBlock to BGRA pixels.</summary>
    private async Task<SignatureImage> RenderTypedSignatureAsync(string text)
    {
        TypedSignatureText.Text = text;
        TypedSignatureHost.UpdateLayout();

        var target = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await target.RenderAsync(TypedSignatureHost);
        var buffer = await target.GetPixelsAsync();
        return new SignatureImage(buffer.ToArray(), target.PixelWidth, target.PixelHeight);
    }
}
