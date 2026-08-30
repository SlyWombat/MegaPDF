using Avalonia;
using Avalonia.Automation;
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
/// Driving the page from the keyboard (SDD §2.2 — required, #2).
///
/// The toolbar was already reachable: buttons take focus and have shortcuts. What
/// was missing is the page itself — there was no way to reach a checkbox, a form
/// field or a line of text without a pointer, which makes the whole fill-check-sign
/// task impossible for anyone who does not use one.
///
/// Tab walks the interactive regions in reading order, Enter or Space activates
/// whatever is focused, and the focused region gets a ring that is visible against
/// both light and dark pages. Activation is routed through the same handler a click
/// uses, aimed at the region's centre, so the keyboard cannot drift away from the
/// mouse as either changes.
/// </summary>
public partial class MainWindow
{
    private Rectangle? _focusRing;
    private Control? _focusRingHost;

    private void OnPageFocusChanged()
    {
        RemoveFocusRing();
        if (ViewModel is not { PageFocus: { } focus } vm)
            return;
        if (ContainerFor(focus.PageIndex) is not ContentPresenter presenter)
            return;
        if (OverlayOf(presenter) is not { } overlay)
            return;

        var scale = PageBitmap.PointsToPixels * vm.Zoom;

        _focusRing = new Rectangle
        {
            Width = Math.Max(6, focus.Bounds.Width * scale),
            Height = Math.Max(6, focus.Bounds.Height * scale),
            Margin = new Thickness(focus.Bounds.X * scale, focus.Bounds.Y * scale, 0, 0),
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
            // Two-tone: a light halo under a dark stroke, so the ring is visible on
            // white paper and on a dark scan alike. A single colour disappears
            // against one or the other.
            Stroke = new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8)),
            StrokeThickness = 2,
            StrokeDashArray = [3, 2],
            Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x3B, 0x82, 0xF6)),
            IsHitTestVisible = false,
        };

        // What VoiceOver reads when focus lands here.
        AutomationProperties.SetName(_focusRing, DescribeFocus(vm, focus));
        AutomationProperties.SetAutomationId(_focusRing, $"page-{focus.PageIndex}-region-{focus.RegionIndex}");

        overlay.Children.Add(_focusRing);
        _focusRingHost = presenter;
    }

    private static string DescribeFocus(MainViewModel vm, MainViewModel.FocusedRegion focus)
    {
        var what = focus.Describe(false);
        return $"Page {focus.PageIndex + 1}, {what}";
    }

    private void RemoveFocusRing()
    {
        if (_focusRing is not null)
            (_focusRing.Parent as Panel)?.Children.Remove(_focusRing);
        _focusRing = null;
        _focusRingHost = null;
    }

    /// <summary>
    /// Tab and Shift+Tab move through the page's regions; Enter and Space activate.
    ///
    /// Handled at the tunnelling stage so Tab reaches here at all — Avalonia's own
    /// focus manager consumes it otherwise, and moves focus to the next control
    /// rather than the next thing on the page. Escape hands focus back, so the
    /// toolbar is still reachable and nobody is trapped on the page.
    /// </summary>
    private bool HandlePageKey(KeyEventArgs e)
    {
        if (ViewModel is not { IsDocumentOpen: true } vm)
            return false;

        // While an editor is open the keys belong to it.
        if (_inlineEditor is not null || FindBox.IsFocused)
            return false;

        switch (e.Key)
        {
            case Key.Tab:
                vm.MoveFocus(forward: !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                return true;

            case Key.Enter or Key.Space when vm.PageFocus is not null:
                vm.ActivateFocus();
                return true;

            case Key.Escape when vm.PageFocus is not null:
                vm.ClearPageFocus();
                OpenButton.Focus();
                return true;

            default:
                return false;
        }
    }
}
