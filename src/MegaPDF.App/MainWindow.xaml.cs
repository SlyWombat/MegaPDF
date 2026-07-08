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

    /// <summary>The document is the interface (SDD §2.2): a click on text starts an in-place edit.</summary>
    private async void OnPageTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Grid pageGrid || pageGrid.DataContext is not PageView pageView)
            return;

        // A tap while an editor is open just commits it (via LostFocus); don't open another.
        if (_activeEditor is not null)
            return;

        var position = e.GetPosition(pageGrid);
        var pagePoint = new PdfPoint(position.X * 72 / 96, position.Y * 72 / 96);

        var run = await Task.Run(() => ViewModel.HitTestText(pageView.Index, pagePoint));
        if (run is null)
            return; // Clicking empty page space does nothing silently harmful (SDD §3.2 miss behavior).

        ShowTextEditor(pageGrid, pageView, run);
    }

    private void ShowTextEditor(Grid pageGrid, PageView pageView, PdfTextRun run)
    {
        var editor = new TextBox
        {
            Text = run.Text,
            FontSize = Math.Max(run.FontSize * 96 / 72, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(run.Bounds.X * 96 / 72 - 6, run.Bounds.Y * 96 / 72 - 8, 0, 0),
            MinWidth = Math.Max(run.Bounds.Width * 96 / 72 + 28, 140),
            AcceptsReturn = false,
        };

        async Task CommitAsync()
        {
            if (!pageGrid.Children.Contains(editor))
                return; // already committed or cancelled
            var newText = editor.Text;
            CloseEditor(pageGrid, editor);
            await ViewModel.ApplyTextEditAsync(pageView.Index, run, newText);
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
}
