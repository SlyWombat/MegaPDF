using System.Runtime.InteropServices.WindowsRuntime;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Services;
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
///
/// Pages are rasterised off the UI thread (#145). Every page at 150 DPI used to be rendered
/// inside the AddPages event, on the UI thread, which froze the window for the whole
/// document, and a failure there went unhandled. The PrintDocument itself stays on the UI
/// thread: only the engine work moves, and the pages are handed over as each is ready.
/// </summary>
public sealed class PdfPrinter(Window window, Func<IPdfDocument?> getDocument, Func<string> getDocumentName,
                               BusyState busy, Func<string, string, Task> showError)
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
        if (getDocument() is null || busy.IsBusy)
            return;

        try
        {
            _printDocument = new PrintDocument();
            _printDocument.Paginate += OnPaginate;
            _printDocument.GetPreviewPage += OnGetPreviewPage;
            _printDocument.AddPages += OnAddPages;
            _documentSource = _printDocument.DocumentSource;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            await PrintManagerInterop.ShowPrintUIForWindowAsync(hwnd);
        }
        catch (Exception ex)
        {
            // No printer support on this machine, or the dialog could not be shown.
            Cleanup();
            await showError(Strings.CouldNotPrintTitle, UserFacing.Describe(ex));
        }
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

    private async void OnGetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        var printDocument = _printDocument;
        var pageNumber = e.PageNumber;
        try
        {
            if (await BuildPageVisualAsync(pageNumber - 1, PreviewDpi) is { } visual && ReferenceEquals(printDocument, _printDocument))
                printDocument?.SetPreviewPage(pageNumber, visual);
        }
        catch (Exception ex)
        {
            // A page that cannot be previewed leaves its preview blank; printing reports it.
            System.Diagnostics.Debug.WriteLine($"print preview of page {pageNumber} failed: {ex}");
        }
    }

    private async void OnAddPages(object sender, AddPagesEventArgs e)
    {
        var printDocument = _printDocument;
        if (printDocument is null)
            return;
        try
        {
            var document = getDocument();
            if (document is null)
                return;
            // "Preparing to print…", with editing waiting: the pages come from the live document.
            using (busy.Begin(Strings.BusyPrinting))
            {
                for (var i = 0; i < document.PageCount; i++)
                {
                    if (!ReferenceEquals(printDocument, _printDocument))
                        return;
                    if (await BuildPageVisualAsync(i, PrintDpi) is { } visual)
                        printDocument.AddPage(visual);
                }
            }
        }
        catch (Exception ex)
        {
            await showError(Strings.CouldNotPrintTitle, UserFacing.Describe(ex));
        }
        finally
        {
            if (ReferenceEquals(printDocument, _printDocument))
                printDocument.AddPagesComplete();
        }
    }

    /// <summary>One printed page: rendered off the UI thread, then centred on the paper on it.</summary>
    private async Task<UIElement?> BuildPageVisualAsync(int pageIndex, double dpi)
    {
        var document = getDocument();
        if (document is null || pageIndex < 0 || pageIndex >= document.PageCount)
            return null;

        var rendered = await Task.Run(() =>
        {
            using var page = document.GetPage(pageIndex);
            return page.Render((int)(page.Width / 72 * dpi), (int)(page.Height / 72 * dpi));
        });

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
