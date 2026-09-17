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
    /// Script with Windows; the next two are common extras. Whatever is missing is
    /// skipped, and if none exists the default face is used in italic rather than
    /// failing — the same fallback chain the preview's FontFamily declares.
    ///
    /// The last two are the chancery face from the URW base-35 set, under the name
    /// it has now (Z003) and the one Debian used before (#158). No Linux
    /// distribution ships any of the first four, so a typed signature came out in
    /// DejaVu Sans — legible, and not a signature. The base-35 set arrives with
    /// ghostscript, CUPS' filters and LibreOffice, so it is on a desktop Linux far
    /// more often than not; when it is not, the italic fallback still applies.
    /// Appended rather than inserted, so Windows and macOS match as they did before
    /// and never reach these.
    /// </summary>
    private static readonly string[] ScriptFaces =
        ["Snell Roundhand", "Segoe Script", "Brush Script MT", "Apple Chancery",
         "Z003", "URW Chancery L"];

    /// <summary>
    /// The face a typed signature is drawn in: the first of <see cref="ScriptFaces"/>
    /// the system has, or the default in bold italic. Never null.
    ///
    /// Separated from <see cref="Render"/> so a diagnostic can ask what a machine
    /// would actually use without drawing anything — on Linux none of the four is
    /// guaranteed, and a typed signature in the body face is a real difference from
    /// what Windows and the Mac produce, not a crash anything would notice (#158).
    /// The caller disposes it.
    /// </summary>
    /// <summary>Whether the system has any of <see cref="ScriptFaces"/> at all.</summary>
    internal static bool HasScriptFace()
    {
        var manager = SKFontManager.Default;
        foreach (var face in ScriptFaces)
            if (manager.MatchFamily(face, SKFontStyle.Bold) is { } found)
            {
                found.Dispose();
                return true;
            }
        return false;
    }

    internal static SKTypeface ResolveScriptTypeface()
    {
        var manager = SKFontManager.Default;
        foreach (var face in ScriptFaces)
            // MatchFamily returns null for a family the system does not have. It is
            // matchFamilyStyle underneath, not the legacy path, so it does not quietly
            // hand back the default — which is what makes the loop meaningful.
            if (manager.MatchFamily(face, SKFontStyle.Bold) is { } found)
                return found;

        return manager.MatchTypeface(SKTypeface.Default, SKFontStyle.BoldItalic) ?? SKTypeface.Default;
    }

    /// <summary>
    /// The typed name as ink on a transparent BGRA raster, large enough to stay
    /// sharp when placed: 160 px glyphs with a 32 px margin, which the library's
    /// trim-to-ink step then tightens.
    /// </summary>
    internal static SignatureBitmap Render(string text)
    {
        var typeface = ResolveScriptTypeface();

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
