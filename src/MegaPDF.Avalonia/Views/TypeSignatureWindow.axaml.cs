using Avalonia.Controls;
using Avalonia.Interactivity;
using MegaPDF.Core.Imaging;
using SkiaSharp;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// Type-a-name signature (#101): the third way to sign, alongside Draw and From
/// photo. The name is previewed live in a script face on a white sheet — what the
/// placed stamp will look like — and <see cref="Render"/> turns it into ink on a
/// transparent raster that goes through the same trim-and-store path as a drawn one.
/// Returns null when cancelled or left blank.
/// </summary>
public partial class TypeSignatureWindow : Window
{
    internal string? TypedName { get; private set; }

    public TypeSignatureWindow()
    {
        InitializeComponent();

        var hint = Preview.Text;
        NameBox.TextChanged += (_, _) =>
        {
            var text = NameBox.Text?.Trim() ?? "";
            var empty = text.Length == 0;
            Preview.Text = empty ? hint : text;
            Preview.Opacity = empty ? 0.45 : 1.0;
            AddButton.IsEnabled = !empty;
        };
        CancelButton.Click += (_, _) => Close();
        AddButton.Click += (_, _) =>
        {
            var text = NameBox.Text?.Trim();
            TypedName = string.IsNullOrEmpty(text) ? null : text;
            Close();
        };
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        NameBox.Focus();
    }

    /// <summary>
    /// Script faces in preference order: Snell Roundhand ships with macOS, Segoe
    /// Script with Windows; the last two are common extras. Whatever is missing is
    /// skipped, and if none exists the default face is used in italic rather than
    /// failing — the same fallback chain the preview's FontFamily declares.
    /// </summary>
    private static readonly string[] ScriptFaces =
        ["Snell Roundhand", "Segoe Script", "Brush Script MT", "Apple Chancery"];

    /// <summary>
    /// The typed name as ink on a transparent BGRA raster, large enough to stay
    /// sharp when placed: 160 px glyphs with a 32 px margin, which the library's
    /// trim-to-ink step then tightens.
    /// </summary>
    internal static SignatureBitmap Render(string text)
    {
        var manager = SKFontManager.Default;
        SKTypeface? typeface = null;
        foreach (var face in ScriptFaces)
        {
            typeface = manager.MatchFamily(face, SKFontStyle.Bold);
            if (typeface is not null)
                break;
        }
        typeface ??= manager.MatchTypeface(SKTypeface.Default, SKFontStyle.BoldItalic) ?? SKTypeface.Default;

        using (typeface)
        using (var paint = new SKPaint
        {
            Typeface = typeface,
            TextSize = 160,
            IsAntialias = true,
            SubpixelText = true,
            Color = new SKColor(0x20, 0x20, 0x20),
        })
        {
            const float margin = 32;
            var metrics = paint.FontMetrics;
            var width = (int)Math.Ceiling(paint.MeasureText(text) + margin * 2);
            var height = (int)Math.Ceiling(metrics.Descent - metrics.Ascent + margin * 2);
            width = Math.Max(width, 1);
            height = Math.Max(height, 1);

            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.DrawText(text, margin, margin - metrics.Ascent, paint);
            }
            return new SignatureBitmap(bitmap.Bytes, width, height);
        }
    }
}
