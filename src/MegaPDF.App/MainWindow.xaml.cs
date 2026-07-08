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

    private bool _allowClose;

    public MainWindow()
    {
        ViewModel = new MainViewModel(this);
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "megapdf.ico"));
        ViewModel.LoadSignatures();
        AppWindow.Closing += OnAppWindowClosing;
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
        var dipToPoint = 72.0 / 96 / ViewModel.ZoomFactor;
        var pagePoint = new PdfPoint(position.X * dipToPoint, position.Y * dipToPoint);

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
        var toDip = 96.0 / 72 * ViewModel.ZoomFactor;
        var editor = new TextBox
        {
            Text = initialText,
            FontSize = Math.Max(fontSizePoints * toDip, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(bounds.X * toDip - 6, bounds.Y * toDip - 8, 0, 0),
            MinWidth = Math.Max(bounds.Width * toDip + 28, 140),
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

    // --- Hover affordances (SDD §2.2: the document teaches what's clickable) ---

    private FrameworkElement? _hoverOverlay;
    private PageCanvas? _hoverCanvas;

    private void OnPagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not PageCanvas canvas || canvas.DataContext is not PageView pageView)
            return;
        if (_activeEditor is not null)
            return;

        var position = e.GetCurrentPoint(canvas).Position;
        var dipToPoint = 72.0 / 96 / ViewModel.ZoomFactor;
        var point = new PdfPoint(position.X * dipToPoint, position.Y * dipToPoint);

        InteractiveRegion? region = null;
        if (ViewModel.PendingSignature is null)
        {
            foreach (var candidate in pageView.Regions)
            {
                if (candidate.Bounds.Contains(point))
                {
                    region = candidate;
                    break;
                }
            }
        }

        canvas.SetCursorShape(ViewModel.PendingSignature is not null
            ? Microsoft.UI.Input.InputSystemCursorShape.Cross
            : region?.Kind switch
            {
                PageHitKind.TextRun or PageHitKind.FormTextField => Microsoft.UI.Input.InputSystemCursorShape.IBeam,
                PageHitKind.FormCheckbox or PageHitKind.DrawnCheckbox or PageHitKind.StampAnnotation
                    => Microsoft.UI.Input.InputSystemCursorShape.Hand,
                _ => Microsoft.UI.Input.InputSystemCursorShape.Arrow,
            });

        UpdateHoverOverlay(canvas, region);
    }

    private void OnPagePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not PageCanvas canvas)
            return;
        canvas.SetCursorShape(null);
        UpdateHoverOverlay(canvas, null);
    }

    private void UpdateHoverOverlay(PageCanvas canvas, InteractiveRegion? region)
    {
        if (_hoverOverlay is not null)
        {
            _hoverCanvas?.Children.Remove(_hoverOverlay);
            _hoverOverlay = null;
            _hoverCanvas = null;
        }
        if (region is null)
            return;

        var toDip = 96.0 / 72 * ViewModel.ZoomFactor;
        var bounds = region.Bounds;
        var accent = new Microsoft.UI.Xaml.Media.SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]) { Opacity = 0.75 };

        _hoverOverlay = region.Kind == PageHitKind.TextRun
            // Faint dotted underline beneath hovered text (SDD §2.2).
            ? new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = 0, Y1 = 0, X2 = bounds.Width * toDip, Y2 = 0,
                Stroke = accent,
                StrokeThickness = 1.4,
                StrokeDashArray = [2, 2],
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(bounds.X * toDip, bounds.Bottom * toDip + 2, 0, 0),
                IsHitTestVisible = false,
            }
            // Light accent outline on checkboxes, fields, and stamps.
            : new Border
            {
                Width = bounds.Width * toDip + 8,
                Height = bounds.Height * toDip + 8,
                BorderBrush = accent,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(bounds.X * toDip - 4, bounds.Y * toDip - 4, 0, 0),
                IsHitTestVisible = false,
            };

        canvas.Children.Add(_hoverOverlay);
        _hoverCanvas = canvas;
    }

    // --- Scroll-tracking page indicator ---

    private void OnPagesScrollViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (ViewModel.Pages.Count == 0)
            return;

        var viewTop = PagesScroll.VerticalOffset;
        var viewBottom = viewTop + PagesScroll.ViewportHeight;
        var midline = viewTop + PagesScroll.ViewportHeight / 2;

        var firstVisible = -1;
        var lastVisible = 0;
        var currentPage = ViewModel.Pages.Count;
        var y = 24d; // ItemsPanel top padding
        for (var i = 0; i < ViewModel.Pages.Count; i++)
        {
            var pageHeight = ViewModel.Pages[i].Height;
            if (y + pageHeight >= viewTop && y <= viewBottom)
            {
                if (firstVisible < 0)
                    firstVisible = i;
                lastVisible = i;
            }
            if (midline <= y + pageHeight + 8 && currentPage == ViewModel.Pages.Count)
                currentPage = i + 1;
            y += pageHeight + 16; // panel spacing
        }

        ViewModel.CurrentPage = currentPage;
        if (firstVisible >= 0)
            _ = ViewModel.UpdateViewportAsync(firstVisible, lastVisible);
    }

    // --- Crash recovery offer (SDD §3.4: one-click restore after an unclean exit) ---

    public async Task OfferCrashRecoveryAsync()
    {
        var sessions = ViewModel.FindRecoverableSessions();
        if (sessions.Count == 0)
            return;
        var session = sessions[0];

        // Right after Activate the visual tree may not be loaded yet, and
        // ContentDialog needs a live XamlRoot.
        if (Content is FrameworkElement { IsLoaded: false } root)
        {
            var loaded = new TaskCompletionSource();
            root.Loaded += (_, _) => loaded.TrySetResult();
            await loaded.Task;
        }

        var dialog = new ContentDialog
        {
            Title = "Restore unsaved changes?",
            Content = $"MegaPDF closed unexpectedly with unsaved changes to {Path.GetFileName(session.DocumentPath)}.",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Discard",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.RestoreSessionAsync(session);
        else
            Core.Recovery.RecoveryJournal.Discard(session.JournalPath);
    }

    // --- Unsaved-changes close prompt (SDD §2.2 forgiveness, P3) ---

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_allowClose || !ViewModel.HasUnsavedChanges)
        {
            // Consented close — nothing left to recover (SDD §3.4).
            ViewModel.EndJournalSession();
            return;
        }
        args.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        var dialog = new ContentDialog
        {
            Title = $"Save changes to {ViewModel.OpenDocumentName}?",
            Content = "Your changes will be lost if you don't save them.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                await ViewModel.SaveCommand.ExecuteAsync(null);
                if (!ViewModel.HasUnsavedChanges) // save succeeded
                {
                    _allowClose = true;
                    Close();
                }
                break;
            case ContentDialogResult.Secondary:
                _allowClose = true;
                Close();
                break;
        }
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
