using Avalonia.Controls;
using Avalonia.Input;
using MegaPDF.Avalonia.ViewModels;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// The menu bar (#144). On the Mac every toolbar command is in a menu as well, with
/// its shortcut, as Apple's guidelines ask: the toolbar is the quick way to a command,
/// the menu bar is where people look for it and learn its key. It is also what keeps
/// a command reachable once the toolbar has moved it into More.
///
/// Built with NativeMenu, which Avalonia exports to the macOS menu bar and ignores
/// elsewhere. On the Mac a menu key equivalent answers before the window's key
/// bindings, so a shortcut runs once whichever of the two holds it; the window's
/// bindings stay because Windows and Linux have no native menu to carry them.
///
/// Each item is registered under the name of the toolbar control (or More entry) it
/// mirrors, which is what <see cref="MissingFromMenuBar"/> checks against.
/// </summary>
public partial class MainWindow
{
    /// <summary>Menu bar items by the toolbar command they mirror.</summary>
    private readonly Dictionary<string, NativeMenuItem> _menuBarItems = [];

    private readonly List<(NativeMenuItem Item, Func<bool> Enabled)> _menuBarEnabled = [];
    private readonly List<(NativeMenuItem Item, Func<bool> Checked)> _menuBarChecked = [];

    /// <summary>
    /// Items with a submenu, each with a plain stand-in under the same id that takes its
    /// place in the menu while it is disabled; see RefreshMenuBar.
    /// </summary>
    private readonly List<(string Id, NativeMenuItem Item, NativeMenuItem StandIn, Func<bool> Enabled)> _menuBarSubmenus = [];

    /// <summary>The commands that live in More rather than on the row.</summary>
    internal static readonly string[] MoreMenuCommands = ["SaveAs", "Password", "Print", "Shrink", "Options"];

    /// <summary>The zoom control's menu entries, which the menu bar carries too.</summary>
    internal static readonly string[] ZoomMenuCommands = ["ActualSize", "FitWidth", "FitPage", "ZoomPresets"];

    private static KeyGesture Shortcut(Key key, KeyModifiers extra = KeyModifiers.None) => new(key, CommandModifier | extra);

    private static KeyGesture OpenGesture => Shortcut(Key.O);
    private static KeyGesture SaveGesture => Shortcut(Key.S);
    private static KeyGesture SaveAsGesture => Shortcut(Key.S, KeyModifiers.Shift);
    private static KeyGesture PrintGesture => Shortcut(Key.P);
    private static KeyGesture UndoGesture => Shortcut(Key.Z);

    /// <summary>Redo is Shift+Cmd+Z on macOS and Ctrl+Y on Windows — different conventions, not just modifiers.</summary>
    private static KeyGesture RedoGesture => OperatingSystem.IsMacOS() ? Shortcut(Key.Z, KeyModifiers.Shift) : Shortcut(Key.Y);

    private static KeyGesture FindGesture => Shortcut(Key.F);
    private static KeyGesture ZoomInGesture => Shortcut(Key.OemPlus);
    private static KeyGesture ZoomOutGesture => Shortcut(Key.OemMinus);
    private static KeyGesture ActualSizeGesture => Shortcut(Key.D0);

    /// <summary>Cmd+, is where every Mac app keeps its settings.</summary>
    private static KeyGesture OptionsGesture => Shortcut(Key.OemComma);

    /// <summary>File ▸ Close. ⌘W did nothing at all before (#176).</summary>
    private static KeyGesture CloseGesture => Shortcut(Key.W);

    /// <summary>Window ▸ Minimize, the shortcut every Mac window answers.</summary>
    private static KeyGesture MinimizeGesture => Shortcut(Key.M);

    /// <summary>
    /// The window a menu command should act on. NSApp's menu bar is the whole
    /// application's, so ⌘W chosen while About or the notices are in front must
    /// close that window and not the document behind it.
    /// </summary>
    private Window ActiveWindow()
    {
        var windows = (global::Avalonia.Application.Current?.ApplicationLifetime
            as global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Windows;
        return windows?.FirstOrDefault(w => w.IsActive) ?? this;
    }

    /// <summary>
    /// Called once the view model has arrived (OnDataContextChanged): the font and size
    /// submenus list its choices.
    /// </summary>
    private void BuildMenuBar()
    {
        if (_menuBarItems.Count > 0 || ViewModel is null)
            return;

        var file = new NativeMenu();
        file.Items.Add(Command("OpenButton", Strings.OpenAPdfEllipsis, OpenGesture, () => true,
            () => _ = OpenDocumentAsync()));
        // Closing already asked the right questions from the red button and ⌘Q; only
        // the menu and keyboard routes to it were missing (#176). Close() runs the
        // same OnClosing path, so unsaved changes are still put to the person first.
        file.Items.Add(Command("Close", Strings.Close, CloseGesture, () => true,
            () => ActiveWindow().Close()));
        file.Items.Add(new NativeMenuItemSeparator());
        file.Items.Add(Command("SaveButton", Strings.Save, SaveGesture,
            () => ViewModel?.SaveCommand.CanExecute(null) == true, () => ViewModel?.SaveCommand.Execute(null)));
        file.Items.Add(Command("SaveAs", Strings.SaveAs, SaveAsGesture,
            () => ViewModel?.IsDocumentOpen == true, () => _ = SaveAsAsync()));
        file.Items.Add(new NativeMenuItemSeparator());
        file.Items.Add(Command("Password", Strings.SecurityToolbar, null,
            () => ViewModel?.IsDocumentOpen == true, () => _ = ChangeSecurityAsync()));
        file.Items.Add(Command("Shrink", Strings.SaveSmallerCopyForEmail, null,
            () => ViewModel?.CanShrink == true, () => _ = ShrinkForEmailAsync()));
        file.Items.Add(new NativeMenuItemSeparator());
        file.Items.Add(Command("Print", Strings.Print, PrintGesture,
            () => ViewModel?.PrintCommand.CanExecute(null) == true, () => ViewModel?.PrintCommand.Execute(null)));

        var edit = new NativeMenu();
        // With the in-place editor focused, Undo and Redo mean the typing, not the document.
        edit.Items.Add(Command("UndoButton", Strings.Undo, UndoGesture, () => true, () =>
        {
            if (FocusManager?.GetFocusedElement() is TextBox box)
                box.Undo();
            else
                ViewModel?.UndoCommand.Execute(null);
        }));
        edit.Items.Add(Command("RedoButton", Strings.Redo, RedoGesture, () => true, () =>
        {
            if (FocusManager?.GetFocusedElement() is TextBox box)
                box.Redo();
            else
                ViewModel?.RedoCommand.Execute(null);
        }));
        edit.Items.Add(new NativeMenuItemSeparator());
        edit.Items.Add(Command("Find", Strings.FindInDocument, FindGesture,
            () => ViewModel?.IsDocumentOpen == true, OpenFind));

        var tools = new NativeMenu();
        tools.Items.Add(Command("SignButton", Strings.Sign, null, () => ViewModel?.CanSign == true, ShowSignFlyout));
        tools.Items.Add(Toggle("AddTextButton", Strings.AddText,
            () => ViewModel?.CanAddText == true, () => ViewModel?.IsAddingText == true,
            () => ViewModel?.ToggleAddTextCommand.Execute(null)));
        tools.Items.Add(Toggle("WhiteoutButton", Strings.Cover,
            () => ViewModel?.CanEditContent == true, () => ViewModel?.IsWhiteoutMode == true,
            () => ViewModel?.ToggleWhiteoutCommand.Execute(null)));
        // Redact next to Cover, because the pair is the point (#173): one covers, the
        // other removes.
        tools.Items.Add(Toggle("RedactButton", Strings.ToolbarRedact,
            () => ViewModel?.CanEditContent == true, () => ViewModel?.IsRedactMode == true,
            () => ViewModel?.ToggleRedactCommand.Execute(null)));
        tools.Items.Add(new NativeMenuItemSeparator());
        tools.Items.Add(Submenu("FontBox", Strings.TextFontName, TextPickerEnabled,
            ViewModel?.TextFontChoices.Cast<object>().ToList() ?? [],
            choice => ((FontChoice)choice).Label,
            choice => ViewModel?.TextFont == ((FontChoice)choice).PostScriptName,
            choice => { if (ViewModel is { } vm) vm.SelectedTextFont = (FontChoice)choice; }));
        tools.Items.Add(Submenu("SizeBox", Strings.TextSizeName, TextPickerEnabled,
            SizeChoices,
            choice => ((double)choice).ToString(System.Globalization.CultureInfo.CurrentCulture),
            choice => ViewModel is { } vm && Math.Abs(vm.TextSize - (double)choice) < 0.01,
            choice => { if (ViewModel is { } vm) vm.TextSize = (double)choice; }));
        tools.Items.Add(new NativeMenuItemSeparator());
        tools.Items.Add(Command("Options", Strings.Options, OptionsGesture, () => true, ShowOptions));

        var view = new NativeMenu();
        view.Items.Add(Command("ZoomInButton", Strings.ZoomIn, ZoomInGesture,
            () => ViewModel?.IsDocumentOpen == true, () => ViewModel?.ZoomInCommand.Execute(null)));
        view.Items.Add(Command("ZoomOutButton", Strings.ZoomOut, ZoomOutGesture,
            () => ViewModel?.IsDocumentOpen == true, () => ViewModel?.ZoomOutCommand.Execute(null)));
        view.Items.Add(new NativeMenuItemSeparator());
        view.Items.Add(Command("ActualSize", Strings.ActualSize, ActualSizeGesture,
            () => ViewModel?.IsDocumentOpen == true, () => ViewModel?.ZoomResetCommand.Execute(null)));
        view.Items.Add(Command("FitWidth", Strings.FitWidth, null,
            () => ViewModel?.IsDocumentOpen == true, () => ViewModel?.FitWidthCommand.Execute(null)));
        view.Items.Add(Command("FitPage", Strings.FitPage, null,
            () => ViewModel?.IsDocumentOpen == true, () => ViewModel?.FitPageCommand.Execute(null)));
        var presets = Submenu("ZoomMenuButton", Strings.ZoomMenuName, () => ViewModel?.IsDocumentOpen == true,
            MainViewModel.ZoomPresets.Cast<object>().ToList(),
            choice => Strings.ZoomPercent((int)Math.Round((double)choice * 100)),
            choice => ViewModel is { } vm && Math.Abs(vm.Zoom - (double)choice) < 0.005,
            choice => ViewModel?.SetZoomCommand.Execute((double)choice));
        _menuBarItems["ZoomPresets"] = presets;
        view.Items.Add(presets);

        // Window and Help: the two menus every Mac app has and this one did not (#176).
        // Both act on whichever window is in front, because the menu bar is the
        // application's rather than this window's.
        var window = new NativeMenu();
        window.Items.Add(Command("Minimize", Strings.Minimize, MinimizeGesture, () => true,
            () => ActiveWindow().WindowState = WindowState.Minimized));
        window.Items.Add(Command("Zoom", Strings.Zoom, null, () => true, () =>
        {
            var target = ActiveWindow();
            target.WindowState = target.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }));

        var help = new NativeMenu();
        help.Items.Add(Command("Notices", Strings.ThirdPartyNoticesEllipsis, null, () => true,
            () => App.ShowNotices()));

        var bar = new NativeMenu();
        bar.Items.Add(new NativeMenuItem(Strings.MenuFile) { Menu = file });
        bar.Items.Add(new NativeMenuItem(Strings.MenuEdit) { Menu = edit });
        bar.Items.Add(new NativeMenuItem(Strings.MenuView) { Menu = view });
        bar.Items.Add(new NativeMenuItem(Strings.MenuTools) { Menu = tools });
        bar.Items.Add(new NativeMenuItem(Strings.MenuWindow) { Menu = window });
        bar.Items.Add(new NativeMenuItem(Strings.MenuHelp) { Menu = help });

        foreach (var menu in new[] { file, edit, view, tools, window, help })
        {
            menu.NeedsUpdate += (_, _) => RefreshMenuBar();
            menu.Opening += (_, _) => RefreshMenuBar();
        }

        NativeMenu.SetMenu(this, bar);
        RefreshMenuBar();
    }

    private IReadOnlyList<object> SizeChoices => ViewModel?.TextSizes.Cast<object>().ToList() ?? [];

    private bool TextPickerEnabled() => ViewModel is { IsTextStyleContext: true, CanAddText: true };

    /// <summary>Brings every item's enabled and checked state up to date. Cheap; called on any view model change.</summary>
    private void RefreshMenuBar()
    {
        foreach (var (item, enabled) in _menuBarEnabled)
            item.IsEnabled = enabled();
        foreach (var (item, isChecked) in _menuBarChecked)
            item.IsChecked = isChecked();

        // On the Mac an item with a submenu is enabled whatever IsEnabled says: Avalonia's
        // native menu item validates YES for any item that has one. So Tools > Text font
        // and Text size stayed enabled outside Add text (#144). Out of context the item is
        // swapped for a plain stand-in with the same title, which greys out like any
        // other, and swapped back when the context returns. Swapped in the menu's item
        // list: the native menu follows changes to the list, but not a submenu taken off
        // an item that stays in it.
        foreach (var (id, item, standIn, enabled) in _menuBarSubmenus)
        {
            var (wanted, other) = enabled() ? (item, standIn) : (standIn, item);
            if (other.Parent is not NativeMenu parent)
                continue;
            var index = parent.Items.IndexOf(other);
            if (index < 0)
                continue;
            parent.Items[index] = wanted;
            // Every id the swapped item answered to, not only the one it was built
            // under: the zoom submenu is registered twice, as ZoomMenuButton and as
            // ZoomPresets, and leaving the alias pointing at the item just taken out
            // of the menu made MissingFromMenuBar report ZoomPresets missing on any
            // capture with no document open.
            foreach (var alias in _menuBarItems
                         .Where(entry => ReferenceEquals(entry.Value, other))
                         .Select(entry => entry.Key)
                         .ToList())
            {
                _menuBarItems[alias] = wanted;
            }
            _menuBarItems[id] = wanted;
        }
    }

    private NativeMenuItem Command(string id, string header, KeyGesture? gesture, Func<bool> enabled, Action invoke)
    {
        var item = new NativeMenuItem(header) { Gesture = gesture };
        item.Click += (_, _) =>
        {
            if (enabled())
                invoke();
        };
        _menuBarEnabled.Add((item, enabled));
        _menuBarItems[id] = item;
        return item;
    }

    private NativeMenuItem Toggle(string id, string header, Func<bool> enabled, Func<bool> isChecked, Action invoke)
    {
        var item = Command(id, header, null, enabled, invoke);
        item.ToggleType = NativeMenuItemToggleType.CheckBox;
        _menuBarChecked.Add((item, isChecked));
        return item;
    }

    /// <summary>A submenu of radio choices; the choice list is read when the menu is built.</summary>
    private NativeMenuItem Submenu(string id, string header, Func<bool> enabled, IEnumerable<object> choices,
                                   Func<object, string> label, Func<object, bool> isChosen, Action<object> choose)
    {
        var menu = new NativeMenu();
        foreach (var choice in choices)
        {
            var option = new NativeMenuItem(label(choice)) { ToggleType = NativeMenuItemToggleType.Radio };
            option.Click += (_, _) =>
            {
                if (enabled())
                    choose(choice);
            };
            _menuBarEnabled.Add((option, enabled));
            _menuBarChecked.Add((option, () => isChosen(choice)));
            menu.Items.Add(option);
        }
        menu.NeedsUpdate += (_, _) => RefreshMenuBar();
        menu.Opening += (_, _) => RefreshMenuBar();

        var item = new NativeMenuItem(header) { Menu = menu };
        _menuBarEnabled.Add((item, enabled));
        _menuBarSubmenus.Add((id, item, new NativeMenuItem(header) { IsEnabled = false }, enabled));
        _menuBarItems[id] = item;
        return item;
    }

    /// <summary>The signature library, from the menu bar: under Sign when it is on the row, else under More.</summary>
    private void ShowSignFlyout()
    {
        if (SignButton.Flyout is not { } flyout)
            return;
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            flyout.ShowAt(SignButton.IsVisible ? SignButton : MoreButton));
    }

    /// <summary>
    /// The menu bar audit (#144): every command on the toolbar row, in More and in the
    /// zoom menu that has no item in the menu bar. Empty is the pass.
    /// </summary>
    internal IReadOnlyList<string> MissingFromMenuBar()
    {
        var inBar = new HashSet<NativeMenuItem>();
        void Walk(NativeMenu? menu)
        {
            foreach (var item in menu?.Items.OfType<NativeMenuItem>() ?? [])
            {
                inBar.Add(item);
                Walk(item.Menu);
            }
        }
        Walk(NativeMenu.GetMenu(this));

        var required = ToolbarChildren
            .Where(c => c is not Rectangle)
            .Select(c => c.Name!)
            .Concat(MoreMenuCommands)
            .Concat(ZoomMenuCommands);
        return required
            .Where(id => !_menuBarItems.TryGetValue(id, out var item) || !inBar.Contains(item))
            .ToList();
    }

    /// <summary>The menu bar item that mirrors a toolbar command, for the self-test.</summary>
    internal NativeMenuItem? MenuBarItem(string id) => _menuBarItems.GetValueOrDefault(id);

    /// <summary>For --screenshot runs: the audit, one line, and how many commands it covered.</summary>
    internal string DescribeMenuBar()
    {
        var missing = MissingFromMenuBar();
        var covered = ToolbarChildren.Count(c => c is not Rectangle) + MoreMenuCommands.Length + ZoomMenuCommands.Length;
        return missing.Count == 0
            ? $"menu bar: PASS, all {covered} toolbar, More and zoom-menu commands are in the menu bar"
            : $"::error::menu bar: missing {string.Join(", ", missing)}";
    }
}
