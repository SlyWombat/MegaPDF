using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MegaPDF.Avalonia.Rendering;
using MegaPDF.Core.Imaging;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// Draw-your-signature surface (SDD §3.3, mobile parity: on-screen drawing with
/// transparent ink and trim-only cleanup).
///
/// Transparent ink matters beyond looks: <see cref="SignatureCleanup.HasTransparency"/>
/// is what tells the pipeline this was drawn rather than photographed, so the
/// white-background removal is skipped and only the trim runs. Drawing on an opaque
/// white field would send it down the scan path and eat thin strokes.
/// </summary>
internal sealed class SignaturePad : Control
{
    private readonly List<List<Point>> _strokes = [];
    private List<Point>? _current;

    /// <summary>Near-black rather than pure black — the same ink the ✗ mark is stroked in.</summary>
    private static readonly Color InkColour = Color.FromRgb(0x20, 0x20, 0x20);

    private const double StrokeWidth = 2.4;

    internal bool IsEmpty => _strokes.Count == 0;

    internal event EventHandler? StrokesChanged;

    internal void Clear()
    {
        _strokes.Clear();
        _current = null;
        InvalidateVisual();
        StrokesChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _current = [e.GetPosition(this)];
        _strokes.Add(_current);
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_current is null)
            return;

        var point = e.GetPosition(this);
        // Skip sub-pixel jitter: a trackpad reports far more points than the curve
        // needs, and every one of them costs a segment at render time.
        if (_current.Count > 0 && Distance(_current[^1], point) < 1.0)
            return;

        _current.Add(point);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_current is null)
            return;

        // A tap with no movement is a dot, and a dot is a legitimate mark — give it
        // a second point so the polyline renders something.
        if (_current.Count == 1)
            _current.Add(_current[0] + new Point(0.6, 0));

        _current = null;
        e.Pointer.Capture(null);
        StrokesChanged?.Invoke(this, EventArgs.Empty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // Transparent, but hit-testable: without a background brush the control never
        // receives pointer events at all.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        DrawStrokes(context);
    }

    private void DrawStrokes(DrawingContext context)
    {
        var pen = new Pen(new SolidColorBrush(InkColour), StrokeWidth)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        foreach (var stroke in _strokes)
        {
            if (stroke.Count < 2)
                continue;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(stroke[0], isFilled: false);
                for (var i = 1; i < stroke.Count; i++)
                    ctx.LineTo(stroke[i]);
                ctx.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }

    /// <summary>
    /// Rasterises the strokes on a transparent field and runs the shared cleanup —
    /// which, because the ink is transparent, trims to the drawing and nothing else.
    /// Returns null when nothing has been drawn.
    /// </summary>
    internal SignatureBitmap? ToSignature()
    {
        if (IsEmpty || Bounds.Width < 1 || Bounds.Height < 1)
            return null;

        // 2x so the stored signature has resolution to spare when it is scaled up on
        // a page; the trim shrinks it back to just the ink.
        const double scale = 2.0;
        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Round(Bounds.Width * scale)),
            Math.Max(1, (int)Math.Round(Bounds.Height * scale)));

        using var target = new RenderTargetBitmap(pixelSize, new Vector(96 * scale, 96 * scale));
        using (var context = target.CreateDrawingContext())
        {
            context.PushTransform(Matrix.CreateScale(scale, scale));
            DrawStrokes(context);
        }

        using var stream = new MemoryStream();
        target.Save(stream);
        stream.Position = 0;

        var decoded = SignatureImages.LoadBgra(stream);
        return SignatureCleanup.HasTransparency(decoded.Bgra)
            ? SignatureCleanup.TrimToInk(decoded.Bgra, decoded.Width, decoded.Height)
            : SignatureCleanup.Clean(decoded);
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
