using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace MegaPDF.App;

/// <summary>
/// The window's half of the page tools (#174): the toolbar's Pages control, what its menu says
/// and when each item is live, and the keys.
///
/// The commands themselves are the active tab's, bound straight onto the menu items in the XAML,
/// so nothing here dispatches a page operation — only the pane toggle and the accelerators, both
/// of which are the window's business because the toolbar and the accelerator grid are.
///
/// Where the affordance lives, and why it is here rather than anywhere else: this app has no menu
/// bar, so the Avalonia leg's View / Edit / Tools menus have no counterpart to copy. What it has
/// is a CommandBar, and the one thing on that bar which already carries a group of related
/// commands behind a single label is the zoom control's DropDownButton. Pages takes the same
/// shape, three groups along, and the pane's toggle is the first item inside it — so "show me the
/// pages" and "turn this page" are one place to look rather than two.
///
/// The keys are this platform's rather than the other desktop's with a modifier swapped, which is
/// the rule #505 wrote down for reading mode:
///
/// * <b>F4</b> shows and hides the pane. Acrobat and Acrobat Reader have shown and hidden the
///   navigation pane on F4 on Windows for twenty years, and between them they are what a Windows
///   user has already learnt about PDFs. Deliberately not the Avalonia leg's F9, which is what
///   every GTK and KDE viewer uses for its side pane and means nothing here.
/// * <b>Ctrl+R</b> turns the page right, <b>Ctrl+Shift+R</b> left. Ctrl+R is rotate in Windows
///   Photos, which is the rotate on this platform; the other desktop's Ctrl+Left/Ctrl+Right is
///   Evince's and Okular's.
/// * <b>Delete</b>, and <b>Ctrl+Shift+Left</b>/<b>Right</b> to reorder, are bound on the pane and
///   not here — on the window Delete would be taken from every text box (DocumentView.Pages.cs).
/// </summary>
public sealed partial class MainWindow
{
    private void InitializePagesToolbar()
    {
        ToolTipService.SetToolTip(PagesMenuButton, Strings.ToolbarPagesTip);
        AutomationProperties.SetName(PagesMenuButton, Strings.ToolbarPages);
        SetLabels(PagesPaneButton, Strings.ToolbarPages, Strings.ToolbarPagesTip);
        PagesPaneItem.Text = Strings.PageThumbnailsMenuItem;
        RotateRightItem.Text = Strings.RotatePageRight;
        RotateLeftItem.Text = Strings.RotatePageLeft;
        DeletePagesItem.Text = Strings.DeletePageItem;
        MovePageEarlierItem.Text = Strings.MovePageEarlierItem;
        MovePageLaterItem.Text = Strings.MovePageLaterItem;
        InsertBlankPageItem.Text = Strings.InsertBlankPageItem;
        InsertPagesFromFileItem.Text = Strings.InsertPagesFromFileItem;
        ExtractPagesItem.Text = Strings.ExtractPagesItem;
    }

    /// <summary>
    /// The pane's tick, set when the flyout opens rather than bound: the item is only ever looked
    /// at while the menu is up, and this is the tab's own state, which a tab switch changes under
    /// a one-time binding. Everything else in the menu is a command and gates itself.
    /// </summary>
    private void OnPagesMenuOpening(object sender, object e)
    {
        PagesPaneItem.IsEnabled = Shell.Active?.IsDocumentOpen ?? false;
        PagesPaneItem.IsChecked = Shell.Active?.IsPagesPaneOpen ?? false;
    }

    private void OnTogglePagesPaneClicked(object sender, RoutedEventArgs e) => TogglePagesPane();

    /// <summary>
    /// Shows or hides the active tab's pane. Per tab, not per window: two documents open side by
    /// side in one window are two documents, and only one of them may be the one being reordered.
    /// (Reading mode is the window's, for the opposite reason — the chrome it hides is the
    /// window's — which is why that one is mirrored onto every tab and this one is not.)
    /// </summary>
    internal void TogglePagesPane()
    {
        if (Shell.Active is not { IsDocumentOpen: true } active)
            return;
        active.IsPagesPaneOpen = !active.IsPagesPaneOpen;
        Announce(active.IsPagesPaneOpen ? Strings.PagesPaneOn : Strings.PagesPaneOff);
    }

    /// <summary>
    /// F4 and the two rotate chords. Added to the same accelerator grid every other shortcut
    /// hangs off (<c>RootGrid</c>), so they keep working when the Pages control has overflowed
    /// into "…" — which is exactly why the shortcuts are not on the menu items themselves.
    /// </summary>
    private void InitializePageAccelerators()
    {
        Accelerator(VirtualKey.F4, VirtualKeyModifiers.None, () =>
        {
            if (Shell.Active is not { IsDocumentOpen: true })
                return false;
            TogglePagesPane();
            return true;
        });
        Accelerator(VirtualKey.R, VirtualKeyModifiers.Control,
                    () => RunPageCommand(Shell.Active?.RotatePagesRightCommand));
        Accelerator(VirtualKey.R, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
                    () => RunPageCommand(Shell.Active?.RotatePagesLeftCommand));

        void Accelerator(VirtualKey key, VirtualKeyModifiers modifiers, Func<bool> invoke)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) => args.Handled = invoke();
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }
    }

    private static bool RunPageCommand(System.Windows.Input.ICommand? command)
    {
        if (command is null || !command.CanExecute(null))
            return false;
        command.Execute(null);
        return true;
    }

    // --- For the self-test ----------------------------------------------------

    /// <summary>
    /// Every place the page tools can be reached from, by name — the counterpart of
    /// <c>ReadingModeEntryPointsForTest</c> (#541). An affordance that quietly stops being on the
    /// toolbar is the failure this exists to catch.
    /// </summary>
    internal IReadOnlyList<string> PageToolEntryPointsForTest()
    {
        var found = new List<string>();
        if (Toolbar.PrimaryCommands.Contains(PagesMenuItem))
            found.Add("Toolbar/PagesMenuButton");
        if (PagesMenu.Items.Contains(PagesPaneItem) && PagesPaneItem.Text == Strings.PageThumbnailsMenuItem)
            found.Add("PagesMenu/PagesPaneItem");
        if (Toolbar.SecondaryCommands.Contains(PagesPaneButton) && PagesPaneButton.Label == Strings.ToolbarPages)
            found.Add("More/PagesPaneButton");
        return found;
    }

    /// <summary>The Pages menu's items in order, by automation id, for the self-test.</summary>
    internal IReadOnlyList<string> PagesMenuItemsForTest() =>
        PagesMenu.Items
            .OfType<MenuFlyoutItemBase>()
            .Select(AutomationProperties.GetAutomationId)
            .Where(id => id is { Length: > 0 })
            .ToList();

    /// <summary>The accelerators on the window's grid, as "Modifiers+Key", for the self-test.</summary>
    internal IReadOnlyList<string> PageAcceleratorsForTest() =>
        RootGrid.KeyboardAccelerators.Select(a => $"{a.Modifiers}+{a.Key}").ToList();

    /// <summary>Runs what opening the Pages flyout runs, so its tick can be asserted.</summary>
    internal void OpenPagesMenuForTest() => OnPagesMenuOpening(PagesMenu, EventArgs.Empty);

    /// <summary>Whether the Pages menu's pane item is ticked, after <see cref="OpenPagesMenuForTest"/>.</summary>
    internal bool PagesPaneItemIsCheckedForTest => PagesPaneItem.IsChecked;
}
