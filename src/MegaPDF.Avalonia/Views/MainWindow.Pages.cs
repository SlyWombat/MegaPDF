using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MegaPDF.Avalonia.ViewModels;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// The Pages sidebar (#174) — where this app lets you see pages as pages, and rotate, delete,
/// reorder, insert and extract them.
///
/// <b>Why a sidebar and not a page grid.</b> Both of these desktops already have an answer to
/// "show me the pages", and it is the same answer: Preview's <i>View ▸ Thumbnails</i> (⌥⌘2) and
/// Evince's and Okular's side pane (F9). A strip beside the document, always in the same place,
/// which you turn on and leave on. Acrobat's <i>Organize Pages</i> — a full-window grid you
/// enter, do one thing in, and leave — is what this deliberately is not: it takes the document
/// away at the moment you most want to see what you are doing to it. Here, rotating page 3
/// turns page 3 in front of you, and the page you are reading stays on screen.
///
/// <b>Keys, per platform, not per modifier.</b> The same reasoning #505 wrote down for reading
/// mode: these desktops differ in the key, not only in the modifier.
/// <list type="bullet">
/// <item>Thumbnails: ⌥⌘2 on macOS (Preview's own), F9 on Linux (Evince, Okular, and the
///   side-pane key every GTK and KDE document viewer answers).</item>
/// <item>Rotate: ⌘L / ⌘R on macOS (Preview's own pair), Ctrl+Left / Ctrl+Right on Linux
///   (Evince's and Okular's).</item>
/// <item>Reorder from the keyboard: Alt+↑ / Alt+↓, the "move this row" modifier on both.</item>
/// <item>Delete: ⌫ or Delete, but only while the strip has the focus — a bare Delete bound on
///   the window would eat the key out of every text field in the app.</item>
/// </list>
///
/// Every command reads from the strip's selection and does nothing the menu item would not, so
/// the keyboard, the menu bar, the context menu and the drag all go through the same view-model
/// command. Dragging is the extra, not the route.
/// </summary>
public partial class MainWindow
{
    /// <summary>The row a drag started on; null when no drag is in progress.</summary>
    private PageViewModel? _draggingPage;

    /// <summary>Where the pointer went down in the strip, to tell a drag from a click.</summary>
    private Point? _dragOrigin;

    /// <summary>The data format the strip's own drag carries: a page index, within this window.</summary>
    private const string PageDragFormat = "megapdf/page-index";

    private void WirePages()
    {
        // The strip's selection is the view model's: every page command acts on it, and the
        // self-test sets it directly. Pushed rather than bound because Avalonia's multiple
        // selection lives on the control, not in a bindable list of indices.
        PageThumbnails.SelectionChanged += (_, _) =>
        {
            if (Active is not { } vm || _syncingPageSelection)
                return;
            vm.SelectedPageIndices = PageThumbnails.Selection.SelectedIndexes.ToArray();
            if (vm.SelectedPageIndices is [var only])
                vm.CurrentPage = only + 1;
        };

        // Thumbnails are drawn for the rows the strip has realised, and only those — the same
        // virtualization hook the page list uses to decide what to rasterise.
        PageThumbnails.ContainerPrepared += (_, e) =>
        {
            if (e.Container.DataContext is PageViewModel page)
                page.EnsureThumbnail();
        };

        PageThumbnails.KeyDown += OnPageStripKeyDown;

        PageThumbnails.AddHandler(PointerPressedEvent, OnPageStripPointerPressed, RoutingStrategies.Tunnel);
        PageThumbnails.AddHandler(PointerMovedEvent, OnPageStripPointerMoved, RoutingStrategies.Tunnel);
        PageThumbnails.AddHandler(PointerReleasedEvent, OnPageStripPointerReleased, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(PageThumbnails, true);
        PageThumbnails.AddHandler(DragDrop.DragOverEvent, OnPageStripDragOver);
        PageThumbnails.AddHandler(DragDrop.DropEvent, OnPageStripDrop);

        PageThumbnails.ContextFlyout = BuildPageContextMenu();
    }

    /// <summary>True while the view is writing the list's selection from the view model, so the
    /// resulting SelectionChanged does not bounce back as a change the person made.</summary>
    private bool _syncingPageSelection;

    /// <summary>Whether this window's Pages sidebar is showing. A tab state, read from the tab.</summary>
    internal bool IsPageStripOpen => Active?.IsPageStripOpen == true;

    /// <summary>
    /// Shows or hides the strip. Not bound to <c>IsVisible</c>: reading mode also owns this
    /// host's visibility, and a binding firing inside the mode would put the strip back on
    /// screen — the same trap #505 fixed for the tab and busy strips.
    /// </summary>
    internal void ApplyPageStripVisibility()
    {
        var wanted = !IsReadingMode && IsPageStripOpen;
        if (PageStripHost.IsVisible == wanted)
            return;
        PageStripHost.IsVisible = wanted;
        if (!wanted)
            return;
        // Draw what the strip can show at once rather than waiting for a scroll: the rows are
        // realised on the next layout pass, so this is posted behind it.
        Dispatcher.UIThread.Post(() =>
        {
            if (Active is not { } vm)
                return;
            var rows = (int)Math.Ceiling(PageStripHost.Bounds.Height / (PageViewModel.ThumbnailBoxWidth + 24)) + 1;
            vm.PrepareThumbnails(Math.Max(0, vm.CurrentPage - 2), Math.Max(4, rows));
            SyncPageStripSelection();
        });
    }

    /// <summary>Writes the view model's selection into the list — after a page operation renumbered it.</summary>
    internal void SyncPageStripSelection()
    {
        if (Active is not { } vm)
            return;
        _syncingPageSelection = true;
        try
        {
            var wanted = vm.SelectedPageIndices;
            if (PageThumbnails.Selection.SelectedIndexes.OrderBy(i => i).SequenceEqual(wanted))
                return;
            PageThumbnails.Selection.Clear();
            foreach (var index in wanted)
                PageThumbnails.Selection.Select(index);
        }
        finally
        {
            _syncingPageSelection = false;
        }
    }

    /// <summary>
    /// Every entry in the strip's context menu, with the question that decides whether it is
    /// enabled. Refreshed as the menu opens rather than bound: the commands belong to the active
    /// tab, and the window outlives its tabs.
    /// </summary>
    private readonly List<(MenuItem Item, Func<bool> Enabled)> _pageMenuItems = [];

    private MenuFlyout BuildPageContextMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(PageCommandItem(Strings.RotatePageLeft, () => Active?.RotatePagesLeftCommand));
        menu.Items.Add(PageCommandItem(Strings.RotatePageRight, () => Active?.RotatePagesRightCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(PageCommandItem(Strings.MovePageUpItem, () => Active?.MovePageUpCommand));
        menu.Items.Add(PageCommandItem(Strings.MovePageDownItem, () => Active?.MovePageDownCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(PageCommandItem(Strings.DeletePageItem, () => Active?.DeletePagesCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(PageCommandItem(Strings.InsertBlankPageItem, () => Active?.InsertBlankPageCommand));
        menu.Items.Add(PageActionItem(Strings.InsertPagesFromFileItem,
            () => Active?.CanAssemblePages == true, InsertPagesFromFileAsync));
        menu.Items.Add(PageActionItem(Strings.ExtractPagesItem,
            () => Active?.CanExtractPages == true, ExtractSelectedPagesAsync));
        menu.Opening += (_, _) =>
        {
            foreach (var (item, enabled) in _pageMenuItems)
                item.IsEnabled = enabled();
        };
        return menu;
    }

    /// <summary>An entry over one of the active tab's commands.</summary>
    private MenuItem PageCommandItem(string header, Func<System.Windows.Input.ICommand?> command)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            var target = command();
            if (target?.CanExecute(null) == true)
                target.Execute(null);
        };
        _pageMenuItems.Add((item, () => command()?.CanExecute(null) == true));
        return item;
    }

    /// <summary>An entry over something the window does, because it needs a file picker.</summary>
    private MenuItem PageActionItem(string header, Func<bool> enabled, Func<Task> invoke)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await GuardedAsync(invoke);
        _pageMenuItems.Add((item, enabled));
        return item;
    }

    private void OnPageStripKeyDown(object? sender, KeyEventArgs e)
    {
        if (Active is not { } vm)
            return;
        switch (e.Key)
        {
            // Bound on the strip, never on the window: a bare Delete on the window would take
            // the key away from every text box in the app.
            case Key.Delete or Key.Back when vm.DeletePagesCommand.CanExecute(null):
                vm.DeletePagesCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // --- Dragging a page to reorder it ---------------------------------------

    private void OnPageStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _draggingPage = RowUnder(e.Source as Control);
        _dragOrigin = _draggingPage is null ? null : e.GetPosition(PageThumbnails);
    }

    private async void OnPageStripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingPage is not { } page || _dragOrigin is not { } origin
            || !e.GetCurrentPoint(PageThumbnails).Properties.IsLeftButtonPressed)
            return;
        var moved = e.GetPosition(PageThumbnails) - origin;
        // Far enough that it is a drag and not a shaky click. Avalonia has no system drag
        // threshold to ask for, so this is the same 6 DIP the selection chrome uses.
        if (Math.Abs(moved.X) < 6 && Math.Abs(moved.Y) < 6)
            return;
        var index = page.Index;
        _draggingPage = null;
        _dragOrigin = null;
        var data = new DataObject();
        data.Set(PageDragFormat, index);
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        catch (InvalidOperationException)
        {
            // No drag session available (a headless platform): the keyboard route still works.
        }
    }

    private void OnPageStripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _draggingPage = null;
        _dragOrigin = null;
    }

    private void OnPageStripDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(PageDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPageStripDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Active is not { } vm || e.Data.Get(PageDragFormat) is not int from)
            return;
        if (RowUnder(e.Source as Control) is { } target)
            vm.MovePage(from, target.Index);
        else
            vm.MovePage(from, vm.Pages.Count - 1);   // dropped past the last thumbnail: the end
    }

    /// <summary>The page whose row a control belongs to, or null when it is not in a row.</summary>
    private static PageViewModel? RowUnder(Control? source) =>
        source?.GetSelfAndVisualAncestors()
              .OfType<ListBoxItem>()
              .FirstOrDefault()?.DataContext as PageViewModel;

    // --- Combine and split, through the platform's file pickers ---------------

    /// <summary>
    /// "Insert Pages from File…" — the combine feature. Every page of the chosen PDF goes in
    /// after the page you are on. A refusal is reported in the status line by the view model,
    /// in the person's words: a form whose fields the page copy cannot carry is refused whole,
    /// and nothing is changed.
    /// </summary>
    internal async Task InsertPagesFromFileAsync()
    {
        if (Active is not { IsIdle: true, CanAssemblePages: true } vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.ChoosePagesToInsert,
            AllowMultiple = false,
            FileTypeFilter = [PdfFileType],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { Length: > 0 } path)
            return;

        await vm.ImportPagesAsync(path);
    }

    /// <summary>
    /// "Save Selected Pages As…" — the split feature. The document itself is untouched, so
    /// this is not a save of it: no dirty flag is cleared and the tab keeps its own file.
    /// </summary>
    internal async Task ExtractSelectedPagesAsync()
    {
        if (Active is not { IsIdle: true, CanExtractPages: true } vm)
            return;
        var pages = vm.TargetPages;
        if (pages.Count == 0)
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.SaveSelectedPagesTitle,
            SuggestedFileName = DocumentViewModel.SuggestExtractedFileName(vm.DocumentName ?? "", pages),
            DefaultExtension = "pdf",
            FileTypeChoices = [PdfFileType],
            ShowOverwritePrompt = true,
        });
        if (file is null)
            return;

        // By path where there is one — the engine's own write is staged, verified and atomic,
        // which is stronger than anything this could do through a stream. Through the granted
        // stream otherwise (the sandbox), which is what the save path does for the same reason.
        if (file.TryGetLocalPath() is { Length: > 0 } local)
            await vm.ExtractPagesToPathAsync(local, pages);
        else
            await vm.ExtractPagesThroughAsync(async () => await file.OpenWriteAsync(), file.Name, pages);
    }
}
