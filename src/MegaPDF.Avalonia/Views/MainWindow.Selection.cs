using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using MegaPDF.Avalonia.Rendering;
using MegaPDF.Avalonia.ViewModels;
using MegaPDF.Core.Engine;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// Selection chrome for things placed on the page: signatures, added text and
/// covers (SDD §3.3 — place it, then adjust it).
///
/// One mechanism for all three, because the difference between them is only which
/// handles they offer. A signature moves and resizes, added text moves (its size
/// is a font size, not a rectangle), a cover does neither and simply offers
/// removal. That is the same split the Windows app makes.
///
/// The chrome lives in the page's overlay panel, in device-independent pixels,
/// and converts to page points only when a drag is committed — so a zoom change
/// while something is selected costs nothing and cannot drift the geometry.
/// </summary>
public partial class MainWindow
{
    private Border? _chrome;
    private Control? _chromeHost;

    /// <summary>Which corner is being dragged, or null while the body is dragged.</summary>
    private (bool Left, bool Top)? _resizingCorner;
    private Point _dragStart;
    private Rect _dragOriginal;
    private bool _dragging;

    private const double HandleSize = 10;

    /// <summary>Nothing smaller than this is a signature; it is a mis-drag.</summary>
    private const double MinSizeDip = 16;

    private void OnSelectionChanged()
    {
        RemoveChrome();
        if (ViewModel is not { Selection: { } sel } vm)
            return;
        if (ContainerFor(sel.PageIndex) is not ContentPresenter presenter)
            return;
        if (OverlayOf(presenter) is not { } overlay)
            return;

        var scale = PageBitmap.PointsToPixels * vm.Zoom;
        var rect = new Rect(sel.Bounds.X * scale, sel.Bounds.Y * scale,
                            sel.Bounds.Width * scale, sel.Bounds.Height * scale);

        var body = new Border
        {
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brand.Brush("BrandAccent"),
            Background = Brushes.Transparent,
            Margin = new Thickness(rect.X, rect.Y, 0, 0),
            Width = rect.Width,
            Height = rect.Height,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
            Cursor = new Cursor(sel.CanMove ? StandardCursorType.SizeAll : StandardCursorType.Arrow),
        };

        if (sel.CanMove)
        {
            body.PointerPressed += (_, e) => BeginDrag(e, presenter, corner: null);
            // Double-click on added text opens the editor on it, which is how you
            // fix a typo without deleting and retyping.
            body.DoubleTapped += (_, e) =>
            {
                if (sel.Kind == MainViewModel.SelectionKind.TextBox && sel.Run is { } run)
                {
                    e.Handled = true;
                    ShowTextBoxEditor(sel.PageIndex, run);
                }
            };
        }

        var host = new Panel();
        host.Children.Add(body);

        if (sel.CanResize)
        {
            foreach (var (left, top) in new[] { (true, true), (false, true), (true, false), (false, false) })
            {
                var handle = new Rectangle
                {
                    Width = HandleSize,
                    Height = HandleSize,
                    Fill = Brushes.White,
                    Stroke = Brand.Brush("BrandAccent"),
                    StrokeThickness = 1.5,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
                    Margin = new Thickness(
                        (left ? rect.X : rect.Right) - (HandleSize / 2),
                        (top ? rect.Y : rect.Bottom) - (HandleSize / 2), 0, 0),
                    Cursor = new Cursor(left == top
                        ? StandardCursorType.TopLeftCorner
                        : StandardCursorType.TopRightCorner),
                };
                var corner = (left, top);
                handle.PointerPressed += (_, e) => BeginDrag(e, presenter, corner);
                host.Children.Add(handle);
            }
        }

        _chrome = new Border { Child = host };
        _chromeHost = presenter;
        overlay.Children.Add(_chrome);
    }

    private void BeginDrag(PointerPressedEventArgs e, Control host, (bool Left, bool Top)? corner)
    {
        if (_chrome?.Child is not Panel panel || panel.Children.FirstOrDefault() is not Border body)
            return;

        _dragging = true;
        _resizingCorner = corner;
        _dragStart = e.GetPosition(host);
        _dragOriginal = new Rect(body.Margin.Left, body.Margin.Top, body.Width, body.Height);
        e.Pointer.Capture(host);
        e.Handled = true;   // do not let the page treat this as a fresh click
    }

    private void OnSelectionPointerMoved(PointerEventArgs e)
    {
        if (!_dragging || _chromeHost is null || _chrome?.Child is not Panel panel)
            return;

        var delta = e.GetPosition(_chromeHost) - _dragStart;
        var r = _dragOriginal;

        if (_resizingCorner is { } corner)
        {
            var left = corner.Left ? r.X + delta.X : r.X;
            var top = corner.Top ? r.Y + delta.Y : r.Y;
            var right = corner.Left ? r.Right : r.Right + delta.X;
            var bottom = corner.Top ? r.Bottom : r.Bottom + delta.Y;
            r = new Rect(Math.Min(left, right), Math.Min(top, bottom),
                         Math.Max(MinSizeDip, Math.Abs(right - left)),
                         Math.Max(MinSizeDip, Math.Abs(bottom - top)));
        }
        else
        {
            r = new Rect(r.X + delta.X, r.Y + delta.Y, r.Width, r.Height);
        }

        ApplyChromeRect(panel, r);
    }

    private static void ApplyChromeRect(Panel panel, Rect r)
    {
        if (panel.Children.FirstOrDefault() is not Border body)
            return;

        body.Margin = new Thickness(r.X, r.Y, 0, 0);
        body.Width = r.Width;
        body.Height = r.Height;

        var i = 0;
        foreach (var (left, top) in new[] { (true, true), (false, true), (true, false), (false, false) })
        {
            i++;
            if (panel.Children.Count <= i || panel.Children[i] is not Rectangle handle)
                continue;
            handle.Margin = new Thickness(
                (left ? r.X : r.Right) - (HandleSize / 2),
                (top ? r.Y : r.Bottom) - (HandleSize / 2), 0, 0);
        }
    }

    private void OnSelectionPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_dragging || ViewModel is not { Selection: { } sel } vm)
            return;

        _dragging = false;
        _resizingCorner = null;
        e.Pointer.Capture(null);

        if (_chrome?.Child is not Panel panel || panel.Children.FirstOrDefault() is not Border body)
            return;

        var scale = PageBitmap.PointsToPixels * vm.Zoom;
        var moved = new PdfRect(
            body.Margin.Left / scale, body.Margin.Top / scale,
            body.Width / scale, body.Height / scale);

        // A click that never moved is a selection, not a drag — committing it would
        // put a no-op on the undo stack. All FOUR bounds, including Height: dragging
        // a bottom corner straight down changes height and nothing else, so a guard
        // without it silently discarded the commonest resize gesture (#63).
        if (Math.Abs(moved.X - sel.Bounds.X) < 0.5 && Math.Abs(moved.Y - sel.Bounds.Y) < 0.5
            && Math.Abs(moved.Width - sel.Bounds.Width) < 0.5
            && Math.Abs(moved.Height - sel.Bounds.Height) < 0.5)
            return;

        vm.CommitSelectionBounds(moved);
    }

    private void RemoveChrome()
    {
        if (_chrome is not null)
            (_chrome.Parent as Panel)?.Children.Remove(_chrome);
        _chrome = null;
        _chromeHost = null;
        _dragging = false;
        _resizingCorner = null;
    }

    /// <summary>
    /// Editing an added text box in place. Committing goes through Restyle rather
    /// than a plain edit, because the box keeps its id across the change — that id
    /// is what the mobile apps address it by (SDD §6.2 contract 4).
    /// </summary>
    private void ShowTextBoxEditor(int pageIndex, PdfTextRun box)
    {
        if (ViewModel is not { } vm || ContainerFor(pageIndex) is not { } container)
            return;

        var dip = PageBitmap.PointsToPixels * vm.Zoom;
        var face = box.TextBoxFont ?? MegaPDF.Core.Engine.StandardTextBoxFonts.Default;

        ShowInlineEditor(
            container,
            new Point(box.Bounds.X * dip, box.Bounds.Y * dip),
            box.FontSize, FamilyFor(face),
            box.Text, box.Bounds.Width * dip,
            text =>
            {
                if (string.IsNullOrWhiteSpace(text))
                    vm.DeleteSelection();
                else
                    vm.RestyleTextBox(pageIndex, box, text, face, box.FontSize);
            });
    }
}
