using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MegaPDF.Avalonia.ViewModels;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// One toolbar row, one fixed height, at every window size (#143, #144).
///
/// The row holds the everyday commands, in groups: File (Open, Save), Tools (Sign,
/// Add text, Cover), history (Undo, Redo) and one zoom control (−, the level with its
/// menu, +). Everything else — Save As, Password…, Print, Shrink, Options — lives in
/// the More menu at the end, which is always there. The font and size pickers are
/// contextual: they join the Tools group only while Add text is armed or an added
/// text box is selected or being edited.
///
/// As the window narrows the row sheds detail in the Mac toolbar's order:
///
///   1. Full: icon over label, when every item fits.
///   2. Icons only: the labels go (NSToolbar's icon-only display mode). Every
///      button keeps its tooltip and gets its label as its accessible name, so
///      nothing is lost to VoiceOver.
///   3. Overflow: commands leave the row, in <see cref="ToolbarOverflowOrder"/>,
///      into the More menu, ahead of its own commands.
///
/// The thresholds are not constants. Each item is measured once, with and without
/// its label, in whatever language the app is running — French labels are wider —
/// and the row picks the first step whose measured width fits. The row's height is
/// fixed in MainWindow.axaml, so it is the same in every step; and every command is
/// also in the menu bar (MainWindow.MenuBar.cs) with its shortcut.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The order commands leave the row once icons alone no longer fit, first to go
    /// first (#144): Redo and the zoom control, then the pickers when they are showing,
    /// then Cover, Undo, Add text, Sign and Save. Open is not listed, so it never leaves.
    /// </summary>
    private Control[][] ToolbarOverflowOrder =>
    [
        [RedoButton],
        [ZoomOutButton, ZoomMenuButton, ZoomInButton],
        [FontBox, SizeBox],
        [WhiteoutButton],
        [UndoButton],
        [AddTextButton],
        [SignButton],
        [SaveButton],
    ];

    /// <summary>Items that are on the row only while their context holds (#144).</summary>
    private Control[] ContextualItems => [FontBox, SizeBox];

    private enum ToolbarMode { Full, IconsOnly, Overflow }

    /// <summary>Each item's measured width with its label, and without; null until measured.</summary>
    private Dictionary<Control, double>? _labelledWidths;
    private Dictionary<Control, double>? _iconWidths;
    private double _moreButtonWidth;

    private ToolbarMode? _toolbarMode;
    private bool _toolbarContextShown;
    private readonly List<Control> _overflowed = [];

    /// <summary>The shortcut each toolbar command is bound to, for the menus to show (BindShortcuts).</summary>
    private readonly Dictionary<Control, KeyGesture> _toolbarGestures = [];

    /// <summary>The row's items in toolbar order, separators included, the More button not.</summary>
    private IEnumerable<Control> ToolbarChildren => ToolbarItems.Children.Where(c => c != MoreButton);

    /// <summary>Whether the font and size pickers belong on the row right now.</summary>
    private bool TextStyleContextShown => ViewModel?.IsTextStyleContext == true;

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

        AutomationProperties.SetName(ZoomOutButton, Strings.ZoomOut);
        AutomationProperties.SetName(ZoomInButton, Strings.ZoomIn);
        AutomationProperties.SetName(ZoomMenuButton, Strings.ZoomMenuName);
        ToolTip.SetTip(ZoomMenuButton, Strings.ZoomMenuName);
        AutomationProperties.SetName(FontBox, Strings.TextFontName);
        ToolTip.SetTip(FontBox, Strings.TextFontName);
        AutomationProperties.SetName(SizeBox, Strings.TextSizeName);
        ToolTip.SetTip(SizeBox, Strings.TextSizeName);
        AutomationProperties.SetName(MoreButton, Strings.ToolbarMore);
        ToolTip.SetTip(MoreButton, Strings.ToolbarMore);

        // Built afresh and filled before they show, on every click: see OpenToolbarMenu.
        MoreButton.Click += (_, _) => OpenToolbarMenu(MoreButton, PlacementMode.BottomEdgeAlignedRight, MoreMenuEntries());
        ZoomMenuButton.Click += (_, _) => OpenToolbarMenu(ZoomMenuButton, PlacementMode.BottomEdgeAlignedLeft, ZoomMenuEntries());

        // Back to typing once a picker has been used over an open editor.
        FontBox.DropDownClosed += (_, _) => _inlineEditor?.Focus();
        SizeBox.DropDownClosed += (_, _) => _inlineEditor?.Focus();

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

        var contextShown = TextStyleContextShown;
        // Out of context the pickers are simply absent: not on the row, not in More.
        var absent = contextShown ? [] : ContextualItems;
        var hidden = new List<Control>();
        var mode = ToolbarMode.Full;
        if (RowWidth(_labelledWidths!, hidden, absent) > available)
        {
            mode = ToolbarMode.IconsOnly;
            foreach (var group in ToolbarOverflowOrder)
            {
                if (RowWidth(_iconWidths!, hidden, absent) <= available)
                    break;
                var leaving = group.Except(absent).ToList();
                if (leaving.Count == 0)
                    continue;
                hidden.AddRange(leaving);
                mode = ToolbarMode.Overflow;
            }
        }

        if (mode == _toolbarMode && contextShown == _toolbarContextShown && hidden.SequenceEqual(_overflowed))
            return;
        _toolbarMode = mode;
        _toolbarContextShown = contextShown;
        _overflowed.Clear();
        _overflowed.AddRange(hidden);

        var shown = ShownItems(hidden, absent).ToHashSet();
        foreach (var item in ToolbarChildren)
        {
            item.IsVisible = shown.Contains(item);
            if (LabelOf(item) is { } label)
                label.IsVisible = mode == ToolbarMode.Full;
        }
        MoreButton.IsVisible = true;
    }

    /// <summary>
    /// Measures every item with its label and without, and the More button. Done
    /// once: the labels are fixed for the life of the window, and so are the widths
    /// of the pickers and the zoom control.
    /// </summary>
    private void MeasureToolbar()
    {
        _labelledWidths = MeasureItems(showLabels: true);
        _iconWidths = MeasureItems(showLabels: false);

        MoreButton.IsVisible = true;
        _moreButtonWidth = MeasureWidth(MoreButton);
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
    /// The width the row needs with <paramref name="hidden"/> gone to More and
    /// <paramref name="absent"/> out of context: the items that show, the More button,
    /// and the spacing between them.
    /// </summary>
    private double RowWidth(Dictionary<Control, double> widths, ICollection<Control> hidden, IReadOnlyCollection<Control> absent)
    {
        var total = _moreButtonWidth;
        var count = 1;
        foreach (var item in ShownItems(hidden, absent))
        {
            total += widths[item];
            count++;
        }
        return total + (ToolbarItems.Spacing * Math.Max(0, count - 1));
    }

    /// <summary>
    /// What shows when <paramref name="hidden"/> and <paramref name="absent"/> are off
    /// the row: every other command, and a separator only where it still has a command
    /// on both sides.
    /// </summary>
    private IEnumerable<Control> ShownItems(ICollection<Control> hidden, IReadOnlyCollection<Control> absent)
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
            if (hidden.Contains(item) || absent.Contains(item))
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

    /// <summary>The label under a toolbar button's icon, if it has one (TextBlock.label in the XAML).</summary>
    private static TextBlock? LabelOf(Control item) =>
        item is ContentControl { Content: Panel panel }
            ? panel.Children.OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("label"))
            : null;

    /// <summary>For --screenshot runs: which step the row is in and what has gone to More.</summary>
    internal string DescribeToolbar() =>
        $"toolbar: mode={_toolbarMode}, pickers={(_toolbarContextShown ? "shown" : "absent")}, "
        + $"height={ToolbarHost.Bounds.Height:F0} DIP, width={ToolbarHost.Bounds.Width:F0} DIP, "
        + $"in More=[{string.Join(", ", _overflowed.Select(c => c.Name))}]";

    // --- The More menu ---

    /// <summary>The menu each of More and the zoom level last opened, for the self-test to read.</summary>
    private readonly Dictionary<Button, MenuFlyout> _toolbarMenus = [];

    /// <summary>
    /// Opens a toolbar button's menu with its entries already in it (#144).
    ///
    /// These menus used to be a MenuFlyout on the button, emptied and refilled in its
    /// Opening event. On the Mac both opened as an empty 2×32 sliver, every time and at
    /// every width: Avalonia 11.2 creates the flyout's presenter before it raises
    /// Opening, and entries added to a presenter that already exists were held but
    /// never presented. A new flyout, filled and then shown, is the same as a menu
    /// written out in XAML, which presents. The self-test opens both menus in a window
    /// and fails on the sliver.
    /// </summary>
    internal MenuFlyout OpenToolbarMenu(Button owner, PlacementMode placement, IEnumerable<object> entries)
    {
        var menu = new MenuFlyout { Placement = placement };
        foreach (var entry in entries)
            menu.Items.Add(entry);
        _toolbarMenus[owner] = menu;
        menu.ShowAt(owner);
        return menu;
    }

    /// <summary>The menu <paramref name="owner"/> last opened, if any.</summary>
    internal MenuFlyout? ToolbarMenuOf(Button owner) => _toolbarMenus.GetValueOrDefault(owner);

    /// <summary>
    /// The More menu's entries, built as it opens: whatever has overflowed, in toolbar
    /// order, then the commands that always live here. Each entry does exactly what its
    /// button does: the same command, the same click handler, or the same flyout.
    /// </summary>
    private List<object> MoreMenuEntries()
    {
        var entries = new List<object>();
        var absent = TextStyleContextShown ? [] : ContextualItems;
        foreach (var control in ToolbarChildren.Where(c => _overflowed.Contains(c) && !absent.Contains(c)))
        {
            if (OverflowEntryFor(control) is { } entry)
                entries.Add(entry);
        }
        if (entries.Count > 0)
            entries.Add(new Separator());

        var vm = ViewModel;
        entries.Add(CommandEntry(Strings.SaveAs, "IconSaveAs", SaveAsGesture,
            vm?.IsDocumentOpen == true, () => _ = SaveAsAsync()));
        entries.Add(CommandEntry(Strings.SecurityToolbar, "IconLock", null,
            vm?.IsDocumentOpen == true, () => _ = ChangeSecurityAsync()));
        entries.Add(CommandEntry(Strings.Print, "IconPrint", PrintGesture,
            vm?.PrintCommand.CanExecute(null) == true, () => vm?.PrintCommand.Execute(null)));
        entries.Add(CommandEntry(Strings.Shrink, "IconShrink", null,
            vm?.CanShrink == true, () => _ = ShrinkForEmailAsync()));
        entries.Add(new Separator());
        entries.Add(CommandEntry(Strings.Options, "IconOptions", OptionsGesture, true, ShowOptions));
        return entries;
    }

    /// <summary>
    /// The zoom control's menu (#144): Actual size and the two fits, then the preset
    /// levels with the current one ticked. Built fresh for each menu that shows it.
    /// </summary>
    private IEnumerable<object> ZoomMenuEntries()
    {
        var vm = ViewModel;
        var open = vm?.IsDocumentOpen == true;
        yield return CommandEntry(Strings.ActualSize, "IconActualSize", ActualSizeGesture, open,
            () => vm?.ZoomResetCommand.Execute(null));
        yield return CommandEntry(Strings.FitWidth, "IconFitWidth", null, open,
            () => vm?.FitWidthCommand.Execute(null));
        yield return CommandEntry(Strings.FitPage, "IconFitPage", null, open,
            () => vm?.FitPageCommand.Execute(null));
        yield return new Separator();
        foreach (var preset in MainViewModel.ZoomPresets)
        {
            var entry = new MenuItem
            {
                Header = Strings.ZoomPercent((int)Math.Round(preset * 100)),
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = vm is not null && Math.Abs(vm.Zoom - preset) < 0.005,
                IsEnabled = open,
            };
            entry.Click += (_, _) => vm?.SetZoomCommand.Execute(preset);
            yield return entry;
        }
    }

    /// <summary>Shows the Options flyout (mark style, flatten) from the More button.</summary>
    internal void ShowOptions()
    {
        if (Resources["OptionsFlyout"] is not Flyout flyout)
            return;
        if (flyout.Content is Control content)
            content.DataContext = DataContext;
        // After any menu has closed, or its light-dismiss takes the flyout with it.
        Dispatcher.UIThread.Post(() => flyout.ShowAt(MoreButton));
    }

    /// <summary>
    /// Opens the More menu and keeps it open, for the `more` screenshot state: a
    /// capture run's window is not the active one, and a flyout light-dismisses the
    /// moment activation is elsewhere.
    /// </summary>
    internal void ShowMoreMenuForScreenshot()
    {
        var flyout = OpenToolbarMenu(MoreButton, PlacementMode.BottomEdgeAlignedRight, MoreMenuEntries());
        flyout.Closing += (_, e) => e.Cancel = true;
    }

    private MenuItem CommandEntry(string header, string? iconKey, KeyGesture? gesture, bool enabled, Action invoke)
    {
        var entry = new MenuItem { Header = header, InputGesture = gesture, IsEnabled = enabled };
        if (iconKey is not null && this.TryFindResource(iconKey, out var geometry) && geometry is global::Avalonia.Media.Geometry data)
            entry.Icon = MenuIcon(entry, data);
        entry.Click += (_, _) => invoke();
        return entry;
    }

    private static ShapePath MenuIcon(MenuItem entry, global::Avalonia.Media.Geometry data)
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
        return icon;
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

            // The zoom level becomes a submenu of the zoom menu's own entries.
            case Button button when button == ZoomMenuButton:
            {
                var submenu = new MenuItem { Header = Strings.ZoomMenuName };
                foreach (var entry in ZoomMenuEntries())
                    submenu.Items.Add(entry);
                return submenu;
            }

            // ToggleButton is a Button, so Add text and Cover land here too.
            case Button button:
            {
                var entry = new MenuItem
                {
                    Header = LabelOf(button)?.Text ?? AutomationProperties.GetName(button),
                    InputGesture = _toolbarGestures.GetValueOrDefault(button),
                };
                if (button.Content is Panel panel && panel.Children.OfType<ShapePath>().FirstOrDefault() is { Data: { } data })
                    entry.Icon = MenuIcon(entry, data);
                else if (button.Content is ShapePath { Data: { } bare })
                    entry.Icon = MenuIcon(entry, bare);

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

    /// <summary>
    /// Whether keyboard focus has gone to one of the text pickers (or the list one of
    /// them has open), which an open editor must survive rather than commit on.
    /// </summary>
    private bool IsInTextPicker(object? focused) =>
        focused is ILogical logical
        && logical.GetSelfAndLogicalAncestors().Any(a => ReferenceEquals(a, FontBox) || ReferenceEquals(a, SizeBox));
}
