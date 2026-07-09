using System.Runtime.InteropServices.WindowsRuntime;
using MegaPDF.Core.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics.Printing;

namespace MegaPDF.App;

/// <summary>
/// Printing (SDD §3.5): Ctrl+P opens the standard Windows print dialog with preview.
/// Preview renders at screen resolution; printed pages at 150 DPI, scaled uniformly
/// to the paper. Prints the live document — unsaved edits included.
/// </summary>
public sealed class PdfPrinter(Window window, Func<IPdfDocument?> getDocument, Func<string> getDocumentName)
{
    private const double PrintDpi = 150;
    private const double PreviewDpi = 96;

    private PrintDocument? _printDocument;
    private IPrintDocumentSource? _documentSource;
    private PrintPageDescription _pageDescription;

    public void Register()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        PrintManagerInterop.GetForWindow(hwnd).PrintTaskRequested += OnPrintTaskRequested;
    }

    public async Task ShowPrintUiAsync()
    {
        if (getDocument() is null)
            return;

        _printDocument = new PrintDocument();
        _printDocument.Paginate += OnPaginate;
        _printDocument.GetPreviewPage += OnGetPreviewPage;
        _printDocument.AddPages += OnAddPages;
        _documentSource = _printDocument.DocumentSource;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        await PrintManagerInterop.ShowPrintUIForWindowAsync(hwnd);
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs e)
    {
        // Fires on a printing thread; the source was created on the UI thread.
        var source = _documentSource;
        if (source is null)
            return;
        var task = e.Request.CreatePrintTask(getDocumentName(), args => args.SetSource(source));
        task.Completed += (_, _) => window.DispatcherQueue.TryEnqueue(Cleanup);
    }

    private void OnPaginate(object sender, PaginateEventArgs e)
    {
        _pageDescription = e.PrintTaskOptions.GetPageDescription(0);
        var document = getDocument();
        _printDocument?.SetPreviewPageCount(document?.PageCount ?? 0, PreviewPageCountType.Final);
    }

    private void OnGetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        if (BuildPageVisual(e.PageNumber - 1, PreviewDpi) is { } visual)
            _printDocument?.SetPreviewPage(e.PageNumber, visual);
    }

    private void OnAddPages(object sender, AddPagesEventArgs e)
    {
        var document = getDocument();
        if (document is not null && _printDocument is not null)
        {
            for (var i = 0; i < document.PageCount; i++)
            {
                if (BuildPageVisual(i, PrintDpi) is { } visual)
                    _printDocument.AddPage(visual);
            }
        }
        _printDocument?.AddPagesComplete();
    }

    /// <summary>One printed page: the PDF page rendered to a bitmap, centered on the paper.</summary>
    private UIElement? BuildPageVisual(int pageIndex, double dpi)
    {
        var document = getDocument();
        if (document is null || pageIndex < 0 || pageIndex >= document.PageCount)
            return null;

        RenderedPage rendered;
        using (var page = document.GetPage(pageIndex))
            rendered = page.Render((int)(page.Width / 72 * dpi), (int)(page.Height / 72 * dpi));

        var bitmap = new WriteableBitmap(rendered.PixelWidth, rendered.PixelHeight);
        using (var pixelStream = bitmap.PixelBuffer.AsStream())
            pixelStream.Write(rendered.Bgra, 0, rendered.Bgra.Length);
        bitmap.Invalidate();

        return new Grid
        {
            Width = _pageDescription.PageSize.Width,
            Height = _pageDescription.PageSize.Height,
            Children =
            {
                new Image
                {
                    Source = bitmap,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
    }

    private void Cleanup()
    {
        if (_printDocument is not null)
        {
            _printDocument.Paginate -= OnPaginate;
            _printDocument.GetPreviewPage -= OnGetPreviewPage;
            _printDocument.AddPages -= OnAddPages;
        }
        _printDocument = null;
        _documentSource = null;
    }
}
