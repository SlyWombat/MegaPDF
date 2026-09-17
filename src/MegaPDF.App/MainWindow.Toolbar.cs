using System.Globalization;
using System.Windows.Input;
using MegaPDF.Core.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MegaPDF.App;

/// <summary>
/// The toolbar (#144): one CommandBar row at a fixed height, whatever the width.
///
/// It sheds detail in three steps as the window narrows, and never wraps:
///
///   1. Labels to the right of the icons, while the labelled row fits. That width
///      is measured from the items themselves in the running language, with Save
///      wearing its unsaved-changes dot and the pickers counted when they show —
///      not a constant, which is what the old breakpoint was until #91.
///   2. Icons only. Every button keeps its tooltip and automation name.
///   3. The CommandBar's dynamic overflow moves commands into "…", lowest
///      DynamicOverflowOrder first (MainWindow.xaml): Redo, zoom, the pickers,
///      Whiteout, Undo, Add text, Signatures, Save. Open is last and never goes at
///      the minimum window width.
///
/// Save As, Password…, Print, Shrink, Find and Settings always live in "…".
///
/// Labels, tooltips and automation names are set here from plain string keys rather
/// than by x:Uid, so they follow --language in the unpackaged build as well as in the
/// package (AppLanguage), and the toolbar can be checked in French from a dev build.
///
/// The keyboard shortcuts are accelerators on RootGrid rather than on the buttons: an
/// accelerator only fires for an element in the live tree, and an overflowed command's
/// button is not in it until the overflow opens.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The smallest window, in effective pixels: Open, Save and "…" still fit (#144).</summary>
    private const double MinimumWindowWidth = 480;
    private const double MinimumWindowHeight = 360;

    /// <summary>Headroom on the measured labelled width, so labels do not flicker at the edge.</summary>
    private const double ToolbarSlack = 8;

    private double? _labelledWidthWithPickers;
    private double? _labelledWidthWithoutPickers;

    /// <summary>The inline editor is open over added text, whose face and size the pickers choose.</summary>
    private bool _styleEditorOpen;

    /// <summary>A picker change is being applied to the selected box; the pickers stay while it re-renders.</summary>
    private bool _restylingSelection;

    /// <summary>The pickers are being set from a box or the last style, not by the person.</summary>
    private bool _syncingPickers;

    private void InitializeToolbar()
    {
        SetLabels(OpenButton, Strings.ToolbarOpen, Strings.ToolbarOpenTip);
        // Save's label is bound (it carries the unsaved dot); its name stays plain.
        ToolTipService.SetToolTip(SaveButton, Strings.ToolbarSaveTip);
        AutomationProperties.SetName(SaveButton, Strings.Save);
        SetLabels(SignaturesToolbarButton, Strings.ToolbarSignatures, Strings.ToolbarSignaturesTip);
        SetLabels(AddTextButton, Strings.ToolbarAddText, Strings.ToolbarAddTextTip);
        SetLabels(WhiteoutButton, Strings.ToolbarWhiteout, Strings.ToolbarWhiteoutTip);
        SetLabels(RedactButton, Strings.ToolbarRedact, Strings.ToolbarRedactTip);
        SetLabels(UndoButton, Strings.ToolbarUndo, Strings.ToolbarUndoTip);
        SetLabels(RedoButton, Strings.ToolbarRedo, Strings.ToolbarRedoTip);
        SetLabels(ZoomOutButton, Strings.ToolbarZoomOut, Strings.ToolbarZoomOutTip);
        SetLabels(ZoomInButton, Strings.ToolbarZoomIn, Strings.ToolbarZoomInTip);
        ToolTipService.SetToolTip(ZoomMenuButton, Strings.ToolbarZoomLevel);
        AutomationProperties.SetName(ZoomMenuButton, Strings.ToolbarZoomLevel);
        ActualSizeItem.Text = Strings.ToolbarActualSize;
        FitWidthItem.Text = Strings.ToolbarFitWidth;
        FitPageItem.Text = Strings.ToolbarFitPage;

        SetLabels(SaveAsButton, Strings.ToolbarSaveAs, Strings.ToolbarSaveAsTip);
        SetLabels(SecurityButton, Strings.ToolbarPassword, Strings.ToolbarPasswordTip, Strings.ToolbarPasswordName);
        SetLabels(PrintButton, Strings.ToolbarPrint, Strings.ToolbarPrintTip);
        SetLabels(ShrinkButton, Strings.ToolbarShrink, Strings.ToolbarShrinkTip, Strings.ToolbarShrinkName);
        SetLabels(FindButton, Strings.ToolbarFind, Strings.ToolbarFindTip);
        SetLabels(SettingsButton, Strings.ToolbarSettings, Strings.ToolbarSettings);

        InitializeTextPickers();
        InitializeAccelerators();

        RootGrid.Loaded += (_, _) =>
        {
            // In raw pixels, so scaled by the display the window is on.
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
                presenter.PreferredMinimumWidth = (int)Math.Ceiling(MinimumWindowWidth * scale);
                presenter.PreferredMinimumHeight = (int)Math.Ceiling(MinimumWindowHeight * scale);
            }
            ApplyToolbarLayout();
        };
    }

    private static void SetLabels(AppBarButton button, string label, string tooltip, string? name = null)
    {
        button.Label = label;
        ToolTipService.SetToolTip(button, tooltip);
        AutomationProperties.SetName(button, name ?? label);
    }

    // --- Width: labelled or icons only ---

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => ApplyToolbarLayout();

    /// <summary>
    /// Labels while the labelled row fits the window, icons only once it does not. The
    /// overflow below that is the CommandBar's own, driven by DynamicOverflowOrder.
    /// </summary>
    private void ApplyToolbarLayout()
    {
        var available = RootGrid.ActualWidth;
        if (available <= 0)
            return;
        var needed = LabelledToolbarWidth(FontPickerItem.Visibility == Visibility.Visible);
        var position = available >= needed
            ? CommandBarDefaultLabelPosition.Right
            : CommandBarDefaultLabelPosition.Collapsed;
        if (Toolbar.DefaultLabelPosition != position)
            Toolbar.DefaultLabelPosition = position;
    }

    /// <summary>
    /// The width the toolbar needs with every label showing, every command on the row
    /// and Save wearing its dot — the widest it ever gets — with or without the
    /// pickers. Measured once for each, since labels only change with the language.
    /// </summary>
    private double LabelledToolbarWidth(bool withPickers)
    {
        if ((withPickers ? _labelledWidthWithPickers : _labelledWidthWithoutPickers) is { } known)
            return known;

        var position = Toolbar.DefaultLabelPosition;
        var pickers = FontPickerItem.Visibility;
        Toolbar.IsDynamicOverflowEnabled = false;
        Toolbar.DefaultLabelPosition = CommandBarDefaultLabelPosition.Right;
        FontPickerItem.Visibility = SizePickerItem.Visibility = withPickers ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Label = Strings.SaveWithDot;

        Toolbar.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Toolbar.DesiredSize.Width + ToolbarSlack;

        SaveButton.Label = ViewModel.SaveButtonLabel;
        FontPickerItem.Visibility = SizePickerItem.Visibility = pickers;
        Toolbar.DefaultLabelPosition = position;
        Toolbar.IsDynamicOverflowEnabled = true;

        if (withPickers)
            _labelledWidthWithPickers = width;
        else
            _labelledWidthWithoutPickers = width;
        return width;
    }

    /// <summary>
    /// For --screenshot diagnostics: the label state, the measured labelled width, the
    /// row's height and what has overflowed, so a width's layout can be read off a run.
    /// </summary>
    internal string DescribeToolbar()
    {
        var pickers = FontPickerItem.Visibility == Visibility.Visible;
        var overflowed = Toolbar.PrimaryCommands
            .Where(c => c is not AppBarSeparator && c.IsInOverflow && ((UIElement)c).Visibility == Visibility.Visible)
            .Select(c => (c as FrameworkElement)?.Name);
        return $"toolbar: labels={(Toolbar.DefaultLabelPosition == CommandBarDefaultLabelPosition.Right ? "right" : "none")}, "
               + $"pickers={(pickers ? "shown" : "absent")}, labelled needs {LabelledToolbarWidth(pickers):F0} effective px, "
               + $"window={RootGrid.ActualWidth:F0} effective px, height={Toolbar.ActualHeight:F0}, "
               + $"in overflow=[{string.Join(", ", overflowed)}]";
    }

    // --- Shortcuts ---

    private void InitializeAccelerators()
    {
        const VirtualKeyModifiers ctrl = VirtualKeyModifiers.Control;
        Accelerator(VirtualKey.O, ctrl, () => Run(ViewModel.OpenCommand));
        Accelerator(VirtualKey.S, ctrl, () => Run(ViewModel.SaveCommand));
        Accelerator(VirtualKey.S, ctrl | VirtualKeyModifiers.Shift, () => Run(ViewModel.SaveAsCommand));
        Accelerator(VirtualKey.P, ctrl, () =>
        {
            // Without the print permission there is nothing to do (#131).
            if (!ViewModel.IsPrintAllowed)
                return false;
            OnPrintClicked(PrintButton, new RoutedEventArgs());
            return true;
        });
        Accelerator(VirtualKey.Z, ctrl, () => Run(ViewModel.UndoCommand));
        Accelerator(VirtualKey.Y, ctrl, () => Run(ViewModel.RedoCommand));
        // Both plus keys and both minus keys: the main row's (OEM 187, 189) and the keypad's.
        foreach (var key in new[] { (VirtualKey)187, VirtualKey.Add })
            Accelerator(key, ctrl, () => Run(ViewModel.ZoomInCommand));
        foreach (var key in new[] { (VirtualKey)189, VirtualKey.Subtract })
            Accelerator(key, ctrl, () => Run(ViewModel.ZoomOutCommand));
        foreach (var key in new[] { VirtualKey.Number0, VirtualKey.NumberPad0 })
        {
            Accelerator(key, ctrl, () =>
            {
                if (!ViewModel.IsDocumentOpen)
                    return false;
                _ = ViewModel.SetZoomPercentAsync(100);
                return true;
            });
        }

        void Accelerator(VirtualKey key, VirtualKeyModifiers modifiers, Func<bool> invoke)
        {
            // Hidden: the tooltips already say the shortcut, in the app's language.
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) => args.Handled = invoke();
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }

        static bool Run(ICommand command)
        {
            if (!command.CanExecute(null))
                return false;
            command.Execute(null);
            return true;
        }
    }

    // --- The panels behind Signatures and Settings ---

    private void OnSignaturesClicked(object sender, RoutedEventArgs e) => ShowSignaturesFlyout();

    /// <summary>Opens the library under its toolbar button, or under the toolbar when the button has overflowed.</summary>
    public void ShowSignaturesFlyout() =>
        ShowFromToolbar(SignaturesFlyout, SignaturesToolbarButton, FlyoutPlacementMode.BottomEdgeAlignedLeft);

    private void OnSettingsClicked(object sender, RoutedEventArgs e) =>
        ShowFromToolbar(SettingsFlyout, null, FlyoutPlacementMode.BottomEdgeAlignedRight);

    private void ShowFromToolbar(FlyoutBase flyout, AppBarButton? button, FlyoutPlacementMode placement)
    {
        FrameworkElement target = button is { IsInOverflow: false, Visibility: Visibility.Visible } ? button : Toolbar;
        Toolbar.IsOpen = false;
        // After the overflow has closed, or its light-dismiss takes the flyout with it.
        DispatcherQueue.TryEnqueue(() => flyout.ShowAt(target, new FlyoutShowOptions { Placement = placement }));
    }

    /// <summary>Opens "…" for the `more` screenshot state.</summary>
    internal void OpenToolbarOverflow() => Toolbar.IsOpen = true;

    /// <summary>
    /// The More button's own tooltip stayed on screen over the menu it had just opened,
    /// where it covered the first entries and their keyboard shortcuts.
    /// (#189). A tooltip belongs to a
    /// button nobody is pointing at any more, so it is switched off while the overflow is
    /// open and back on when it closes.
    /// </summary>
    private void WireOverflowTooltip()
    {
        Toolbar.Opening += (_, _) => SetOverflowTooltip(enabled: false);
        Toolbar.Closed += (_, _) => SetOverflowTooltip(enabled: true);
    }

    private object? _overflowTooltip;

    private void SetOverflowTooltip(bool enabled)
    {
        if (FindOverflowButton(Toolbar) is not { } more)
            return;
        if (enabled)
        {
            if (_overflowTooltip is not null)
                ToolTipService.SetToolTip(more, _overflowTooltip);
            return;
        }
        _overflowTooltip ??= ToolTipService.GetToolTip(more);
        ToolTipService.SetToolTip(more, null);
    }

    private static DependencyObject? FindOverflowButton(DependencyObject node)
    {
        if (node is FrameworkElement { Name: "MoreButton" })
            return node;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (FindOverflowButton(VisualTreeHelper.GetChild(node, i)) is { } found)
                return found;
        }
        return null;
    }

    // --- Zoom menu ---

    /// <summary>Actual size and the fits are in the XAML; the preset levels are rebuilt so the current one is ticked.</summary>
    private void OnZoomMenuOpening(object sender, object e)
    {
        var first = ZoomMenu.Items.IndexOf(ZoomPresetsSeparator) + 1;
        while (ZoomMenu.Items.Count > first)
            ZoomMenu.Items.RemoveAt(first);

        var open = ViewModel.IsDocumentOpen;
        ActualSizeItem.IsEnabled = FitWidthItem.IsEnabled = FitPageItem.IsEnabled = open;
        foreach (var preset in MainViewModel.ZoomPresets)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = Strings.ZoomPercent(preset),
                GroupName = "ZoomPresets",
                IsChecked = ViewModel.ZoomPercent == preset,
                IsEnabled = open,
            };
            item.Click += async (_, _) => await ViewModel.SetZoomPercentAsync(preset);
            ZoomMenu.Items.Add(item);
        }
    }

    private async void OnActualSizeClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.SetZoomPercentAsync(100);

    // --- The contextual font and size pickers ---

    private void InitializeTextPickers()
    {
        foreach (var size in ViewModel.TextSizes)
            SizePicker.Items.Add(new ComboBoxItem { Content = ((int)size).ToString(CultureInfo.CurrentCulture), Tag = size });
        foreach (var face in StandardTextBoxFonts.All)
            FontPicker.Items.Add(new ComboBoxItem { Content = FontLabel(face), Tag = face });
        AutomationProperties.SetName(FontPicker, Strings.TextFontName);
        ToolTipService.SetToolTip(FontPicker, Strings.TextFontName);
        AutomationProperties.SetName(SizePicker, Strings.TextSizeName);
        ToolTipService.SetToolTip(SizePicker, Strings.TextSizeName);
        ShowStyleInPickers(ViewModel.LastTextStyle);
    }

    /// <summary>
    /// While Add text is armed, while an added box is selected or being edited — and
    /// nowhere else: for the document's own text and for form fields the formatting is
    /// inherited, and SDD §3.1 keeps formatting UI away from them.
    /// </summary>
    private bool TextPickersWanted =>
        ViewModel.IsTextBoxMode || _styleEditorOpen || _restylingSelection || _selection is { Run: not null };

    private void UpdateTextPickers()
    {
        var visibility = TextPickersWanted ? Visibility.Visible : Visibility.Collapsed;
        if (FontPickerItem.Visibility == visibility)
            return;
        FontPickerItem.Visibility = SizePickerItem.Visibility = visibility;
        ApplyToolbarLayout();
    }

    private void OnTextBoxModeChanged()
    {
        if (ViewModel.IsTextBoxMode)
            ShowStyleInPickers(ViewModel.LastTextStyle);
        UpdateTextPickers();
    }

    private void ShowStyleInPickers(TextStyleChoice style)
    {
        _syncingPickers = true;
        try
        {
            SizePicker.SelectedIndex = IndexOfTag(SizePicker, tag => tag is double size && Math.Abs(size - style.FontSize) < 0.01);
            FontPicker.SelectedIndex = IndexOfTag(FontPicker, tag => tag is string face && face == style.FontName);
        }
        finally
        {
            _syncingPickers = false;
        }

        static int IndexOfTag(ComboBox picker, Func<object?, bool> matches)
        {
            for (var i = 0; i < picker.Items.Count; i++)
            {
                if (picker.Items[i] is ComboBoxItem item && matches(item.Tag))
                    return i;
            }
            return -1;
        }
    }

    /// <summary>What the pickers hold; a picker showing nothing (a size not in the list) keeps <paramref name="fallback"/>'s.</summary>
    private TextStyleChoice PickedTextStyle(TextStyleChoice fallback) => new(
        (SizePicker.SelectedItem as ComboBoxItem)?.Tag as double? ?? fallback.FontSize,
        (FontPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback.FontName);

    /// <summary>
    /// A picker changed. Over an open editor the choice waits for the commit (and the
    /// editor shows the size); with an added box selected, the box takes it at once as
    /// one undoable edit and stays selected; with Add text armed, the next box gets it.
    /// </summary>
    private async void OnTextPickerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPickers)
            return;

        if (_styleEditorOpen && _activeEditor is { } editor)
        {
            var size = PickedTextStyle(ViewModel.LastTextStyle).FontSize;
            editor.FontSize = Math.Max(size * 96.0 / 72 * ViewModel.ZoomFactor, 10);
            return;
        }

        if (_selection is not { Run: { } run } selection)
            return;
        var current = new TextStyleChoice(run.FontSize, run.TextBoxFont ?? StandardTextBoxFonts.Default);
        var picked = PickedTextStyle(current);
        if (picked.FontName == current.FontName && Math.Abs(picked.FontSize - current.FontSize) < 0.01)
            return;

        _restylingSelection = true;
        try
        {
            Deselect();
            await ViewModel.RestyleTextBoxAsync(selection.Page.Index, run, run.Text, picked.FontName, picked.FontSize);
            // The page re-rendered: select the box again as it now is — or as it still is,
            // after Cancel at the #139 warning, which also puts the pickers back.
            if (selection.Page.Index < ViewModel.Pages.Count
                && ViewModel.FindTextBox(selection.Page.Index, run.TextBoxId, run.ObjectIndex) is { } box
                && FindPageCanvas(selection.Page.Index) is { } canvas)
            {
                SelectStamp(canvas, ViewModel.Pages[selection.Page.Index], $"textbox:{box.ObjectIndex}", box.Bounds,
                            resizable: false, run: box);
            }
        }
        finally
        {
            _restylingSelection = false;
            UpdateTextPickers();
        }
    }

    /// <summary>Back to typing once a picker has been used over an open editor.</summary>
    private void OnTextPickerClosed(object? sender, object e)
    {
        if (_styleEditorOpen)
            _activeEditor?.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// For the `textbox` screenshot state: adds a box and selects it, which brings the
    /// pickers onto the row.
    /// </summary>
    internal async Task<bool> SelectNewTextBoxForScreenshotAsync(string text)
    {
        if (!ViewModel.IsDocumentOpen)
            return false;
        // Where the Mac --story prints the name: under the demo agreement's signature line
        // (tools/gen_test_fixtures.py demo.pdf). On another document it may land on text.
        await ViewModel.AddTextBoxAsync(0, new PdfPoint(72, 405), text);
        await Task.Delay(900);
        if (ViewModel.LastTextBoxOn(0) is not { } box || FindPageCanvas(0) is not { } canvas)
            return false;
        SelectStamp(canvas, ViewModel.Pages[0], $"textbox:{box.ObjectIndex}", box.Bounds, resizable: false, run: box);
        return FontPickerItem.Visibility == Visibility.Visible;
    }
}
