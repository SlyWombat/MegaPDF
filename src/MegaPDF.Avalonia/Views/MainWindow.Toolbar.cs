using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MegaPDF.Avalonia.ViewModels;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// One toolbar row at every window size (#143).
///
/// The row used to be a StackPanel that clipped whatever did not fit: at the old
/// 1000-wide default window, Fit width, Fit page and Options were off the edge and
/// could not be reached. Wrapping onto a second row was tried and rejected, because
/// on a small window the buttons already take too much of the page's space. So the
/// row follows the Mac toolbar convention instead, in three steps:
///
///   1. Full: icon over label, when every item fits.
///   2. Icons only: the labels go (NSToolbar's icon-only display mode). Every
///      button keeps its tooltip and gets its label as its accessible name, so
///      nothing is lost to VoiceOver.
///   3. Overflow: the least-used commands leave the row, in
///      <see cref="ToolbarOverflowOrder"/>, into one More menu at the end.
///
/// The thresholds are not constants. Each item is measured once, with and without
/// its label, in whatever language the app is running — French labels are wider —
/// and the row picks the first step whose measured width fits. The row never wraps,
/// so its height never grows as the window shrinks; icon-only it is shorter.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The order commands leave the row once icons alone no longer fit, first to go
    /// first. The rarely used go early; Open, Save, Sign, Add text, Undo and the zoom
    /// controls stay as long as possible. Font and size leave together. Open is not
    /// listed, so it never leaves.
    /// </summary>
    private Control[][] ToolbarOverflowOrder =>
    [
        [PrintButton],
        [ShrinkButton],
        [SecurityButton],
        [WhiteoutButton],
        [FontBox, SizeBox],
        [SettingsButton],
        [SaveAsButton],
        [RedoButton],
        [FitPageButton],
        [ZoomResetButton],
        [FitWidthButton],
        [ZoomInButton, ZoomOutButton],
        [UndoButton],
        [AddTextButton],
        [SignButton],
        [SaveButton],
    ];

    private enum ToolbarMode { Full, IconsOnly, Overflow }

    /// <summary>Each item's measured width with its label, and without; null until measured.</summary>
    private Dictionary<Control, double>? _labelledWidths;
    private Dictionary<Control, double>? _iconWidths;
    private double _moreButtonWidth;

    private ToolbarMode? _toolbarMode;
    private readonly List<Control> _overflowed = [];

    /// <summary>The row's items in toolbar order, separators included, the More button not.</summary>
    private IEnumerable<Control> ToolbarChildren => ToolbarItems.Children.Where(c => c != MoreButton);

    /// <summary>Called from the constructor, after the shortcut tooltips are set.</summary>
    private void WireToolbar()
    {
        foreach (var item in ToolbarChildren)
        {
            if (LabelOf(item)?.Text is not { } label)
                continue;
            // Read by VoiceOver whether or not the label is showing.
            AutomationProperties.SetName(item, label);
            // The shortcut tooltips from BindShortcuts are richer; only fill the gaps.
            if (ToolTip.GetTip(item) is null)
                ToolTip.SetTip(item, label);
        }

        AutomationProperties.SetName(FontBox, Strings.TextFontName);
        ToolTip.SetTip(FontBox, Strings.TextFontName);
        AutomationProperties.SetName(SizeBox, Strings.TextSizeName);
        ToolTip.SetTip(SizeBox, Strings.TextSizeName);
        AutomationProperties.SetName(MoreButton, Strings.ToolbarMore);
        ToolTip.SetTip(MoreButton, Strings.ToolbarMore);

        if (MoreButton.Flyout is MenuFlyout menu)
            menu.Opening += (_, _) => FillOverflowMenu(menu);

        ToolbarHost.SizeChanged += (_, _) => ApplyToolbarLayout();
    }

    /// <summary>
    /// Picks the step for the width the row has, and shows it. Cheap: the widths are
    /// measured once, so this is arithmetic plus, when the step changes, visibility.
    /// </summary>
    private void ApplyToolbarLayout()
    {
        var available = ToolbarHost.Bounds.Width
                        - ToolbarHost.Padding.Left - ToolbarHost.Padding.Right
                        - ToolbarHost.BorderThickness.Left - ToolbarHost.BorderThickness.Right;
        if (available <= 0)
            return;

        if (_labelledWidths is null || _iconWidths is null)
            MeasureToolbar();

        var hidden = new List<Control>();
        var mode = ToolbarMode.Full;
        if (RowWidth(_labelledWidths!, hidden) > available)
        {
            mode = ToolbarMode.IconsOnly;
            foreach (var group in ToolbarOverflowOrder)
            {
                if (RowWidth(_iconWidths!, hidden) <= available)
                    break;
                hidden.AddRange(group);
                mode = ToolbarMode.Overflow;
            }
        }

        if (mode == _toolbarMode && hidden.SequenceEqual(_overflowed))
            return;
        _toolbarMode = mode;
        _overflowed.Clear();
        _overflowed.AddRange(hidden);

        var shown = ShownItems(hidden).ToHashSet();
        foreach (var item in ToolbarChildren)
        {
            item.IsVisible = shown.Contains(item);
            if (LabelOf(item) is { } label)
                label.IsVisible = mode == ToolbarMode.Full;
        }
        MoreButton.IsVisible = hidden.Count > 0;
    }

    /// <summary>
    /// Measures every item with its label and without, and the More button. Done
    /// once: the labels are fixed for the life of the window, and so are the widths
    /// of the two pickers.
    /// </summary>
    private void MeasureToolbar()
    {
        _labelledWidths = MeasureItems(showLabels: true);
        _iconWidths = MeasureItems(showLabels: false);

        MoreButton.IsVisible = true;
        _moreButtonWidth = MeasureWidth(MoreButton);
        MoreButton.IsVisible = false;
        _toolbarMode = null;
    }

    private Dictionary<Control, double> MeasureItems(bool showLabels)
    {
        var widths = new Dictionary<Control, double>();
        foreach (var item in ToolbarChildren)
        {
            item.IsVisible = true;
            if (LabelOf(item) is { } label)
                label.IsVisible = showLabels;
            widths[item] = MeasureWidth(item);
        }
        return widths;
    }

    /// <summary>Desired width including margin, measured afresh rather than from a cached pass.</summary>
    private static double MeasureWidth(Control control)
    {
        control.InvalidateMeasure();
        foreach (var descendant in control.GetVisualDescendants().OfType<Layoutable>())
            descendant.InvalidateMeasure();
        control.Measure(Size.Infinity);
        return control.DesiredSize.Width;
    }

    /// <summary>
    /// The width the row needs with <paramref name="hidden"/> gone: the items that
    /// show, the spacing between them, and the More button when anything is in it.
    /// </summary>
    private double RowWidth(Dictionary<Control, double> widths, ICollection<Control> hidden)
    {
        var total = 0.0;
        var count = 0;
        foreach (var item in ShownItems(hidden))
        {
            total += widths[item];
            count++;
        }
        if (hidden.Count > 0)
        {
            total += _moreButtonWidth;
            count++;
        }
        return total + (ToolbarItems.Spacing * Math.Max(0, count - 1));
    }

    /// <summary>
    /// What shows when <paramref name="hidden"/> has left the row: every other
    /// command, and a separator only where it still has a command on both sides.
    /// </summary>
    private IEnumerable<Control> ShownItems(ICollection<Control> hidden)
    {
        Control? separator = null;
        var anyBefore = false;
        foreach (var item in ToolbarChildren)
        {
            if (item is Rectangle)
            {
                if (anyBefore)
                    separator = item;
                continue;
            }
            if (hidden.Contains(item))
                continue;
            if (separator is not null)
            {
                yield return separator;
                separator = null;
            }
            anyBefore = true;
            yield return item;
        }
    }

    /// <summary>The label under a toolbar button's icon, if it has one.</summary>
    private static TextBlock? LabelOf(Control item) =>
        item is ContentControl { Content: Panel panel } ? panel.Children.OfType<TextBlock>().FirstOrDefault() : null;

    /// <summary>
    /// Rebuilds the More menu from the commands hidden right now, in toolbar order.
    /// Each entry does exactly what its button does: the same command, the same
    /// click handler, or the same flyout.
    /// </summary>
    private void FillOverflowMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        foreach (var control in ToolbarChildren.Where(_overflowed.Contains))
        {
            if (OverflowEntryFor(control) is { } entry)
                menu.Items.Add(entry);
        }
    }

    private MenuItem? OverflowEntryFor(Control control)
    {
        switch (control)
        {
            // The font and size pickers become submenus of their choices.
            case ComboBox picker:
            {
                var submenu = new MenuItem { Header = picker == FontBox ? Strings.TextFontName : Strings.TextSizeName };
                submenu[!IsEnabledProperty] = picker[!IsEnabledProperty];
                foreach (var choice in picker.ItemsSource?.Cast<object>() ?? [])
                {
                    var option = new MenuItem
                    {
                        Header = choice is FontChoice font ? font.Label : choice.ToString(),
                        ToggleType = MenuItemToggleType.Radio,
                        IsChecked = Equals(choice, picker.SelectedItem),
                    };
                    option.Click += (_, _) => picker.SelectedItem = choice;
                    submenu.Items.Add(option);
                }
                return submenu;
            }

            // ToggleButton is a Button, so Add text and Cover land here too.
            case Button button:
            {
                var entry = new MenuItem { Header = LabelOf(button)?.Text };
                if (button.Content is Panel panel && panel.Children.OfType<ShapePath>().FirstOrDefault() is { Data: { } data })
                {
                    var icon = new ShapePath
                    {
                        Data = data,
                        Width = 16,
                        Height = 16,
                        Stretch = global::Avalonia.Media.Stretch.Uniform,
                        StrokeThickness = 1.6,
                        StrokeLineCap = global::Avalonia.Media.PenLineCap.Round,
                        StrokeJoin = global::Avalonia.Media.PenLineJoin.Round,
                    };
                    icon[!global::Avalonia.Controls.Shapes.Shape.StrokeProperty] = entry[!TemplatedControl.ForegroundProperty];
                    entry.Icon = icon;
                }

                if (button is ToggleButton toggle)
                {
                    entry.ToggleType = MenuItemToggleType.CheckBox;
                    entry.IsChecked = toggle.IsChecked == true;
                }

                if (button.Command is { } command)
                {
                    entry.Command = command;
                }
                else
                {
                    entry[!IsEnabledProperty] = button[!IsEnabledProperty];
                    entry.Click += (_, _) =>
                    {
                        if (button.Flyout is { } flyout)
                            // After the menu has closed, or its light-dismiss takes the flyout with it.
                            Dispatcher.UIThread.Post(() => flyout.ShowAt(MoreButton));
                        else
                            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    };
                }
                return entry;
            }

            default:
                return null;
        }
    }
}
