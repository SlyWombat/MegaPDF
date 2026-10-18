using System.Runtime.InteropServices.WindowsRuntime;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Viewing;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using CoreOcrLine = MegaPDF.Core.Viewing.OcrLine;
using CoreOcrWord = MegaPDF.Core.Viewing.OcrWord;

namespace MegaPDF.App;

/// <summary>
/// Find on scanned pages (#676) on Windows: <c>Windows.Media.Ocr</c>, in the box and on the device,
/// with no network and nothing written anywhere. <see cref="TryCreate"/> answers null when the
/// person has no OCR language installed, and then Find on a scan behaves exactly as it always has.
///
/// Measured in phase 0 (#676, en-GB recogniser, 31 textless pages): cost scales with pixels, 169 ms
/// a page at 100 dpi to 931 ms at 300, and <see cref="OcrEngine.MaxImageDimension"/> is a hard
/// 10,000 px that <c>RecognizeAsync</c> throws past (4 of 31 pages at 300 dpi), which is why the
/// reader caps its render by <see cref="MaxPixelsPerSide"/> and why 150 dpi is the default.
/// </summary>
internal sealed class WindowsOcrRecogniser : IPageRecogniser
{
    private readonly OcrEngine _engine;
    private readonly object _gate = new();

    private WindowsOcrRecogniser(OcrEngine engine) => _engine = engine;

    /// <summary>
    /// The engine for the languages the person already uses (their profile languages), so a French
    /// scan is read by the French recogniser on a French machine and nothing is downloaded.
    /// </summary>
    public static IPageRecogniser? TryCreate()
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages() is { } engine
                ? new WindowsOcrRecogniser(engine)
                : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"no OCR engine: {ex.Message}");
            return null;
        }
    }

    public int MaxPixelsPerSide => (int)OcrEngine.MaxImageDimension;

    public OcrPage? Read(RenderedPage image, double pageWidth, double pageHeight)
    {
        // One read at a time: a search that has been superseded may still be inside its last page
        // when the next one starts, and the engine is one object.
        lock (_gate)
        {
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                image.Bgra.AsBuffer(), BitmapPixelFormat.Bgra8,
                image.PixelWidth, image.PixelHeight, BitmapAlphaMode.Ignore);
            // On a pool thread (Find walks pages off the UI thread), so blocking on the WinRT call is safe.
            var result = _engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();

            var lines = new List<CoreOcrLine>();
            foreach (var line in result.Lines)
            {
                var text = line.Text;
                var cursor = 0;
                var words = new List<CoreOcrWord>();
                foreach (var word in line.Words)
                {
                    var start = text.IndexOf(word.Text, cursor, StringComparison.Ordinal);
                    if (start < 0)
                        continue;
                    cursor = start + word.Text.Length;
                    // Pixel box, top-left origin, of a render that is the page scaled to fit: it
                    // scales straight to points in the page space PdfRect already uses.
                    var box = word.BoundingRect;
                    words.Add(new CoreOcrWord(start, cursor, new PdfRect(
                        box.X / image.PixelWidth * pageWidth,
                        box.Y / image.PixelHeight * pageHeight,
                        box.Width / image.PixelWidth * pageWidth,
                        box.Height / image.PixelHeight * pageHeight)));
                }
                if (words.Count > 0)
                    lines.Add(new CoreOcrLine(text, words));
            }
            return new OcrPage(lines);
        }
    }
}
