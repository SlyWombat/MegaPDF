using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace MegaPDF.App;

/// <summary>
/// The Pages pane's own view code (#174): what the grid of page tiles does with a selection, a
/// drag, a right-click and a key press. The commands themselves live on the view model, so this
/// file has no page arithmetic in it beyond turning a drop point into an index.
/// </summary>
public sealed partial class DocumentView
{
    /// <summary>The page being dragged, or -1. One page at a time: a drop is one undo step.</summary>
    private int _draggingPage = -1;

    /// <summary>The pane's selection is being written from the view model, not by the person.</summary>
    private bool _syncingTileSelection;

    private void InitializePagesPane()
    {
        AutomationProperties.SetName(PageTiles, Strings.PagesSidebarName);
        PageTiles.ContextFlyout = BuildPageContextMenu();
        // A rotation leaves the tiles it turned without a raster, and the grid will not realise a
        // container it already has — so the pane redraws the tiles it is showing itself.
        ViewModel.ThumbnailsInvalidated += (_, _) => DispatcherQueue.TryEnqueue(RedrawRealisedTiles);
        // The selection is the view model's, not the grid's: a page operation renumbers it (the
        // page you dragged stays selected, a page you deleted stops being), and the grid has to
        // be told. Two-way, with a guard, because each direction can raise the other.
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(DocumentViewModel.SelectedPageIndices))
                return;
            // Straight through on the UI thread, which is where every selection change comes from
            // (the grid itself, or the renumbering after a page operation). Posting it instead
            // would leave the grid a frame behind the view model, and a command that ran in that
            // frame would act on the selection the grid still shows rather than the one the
            // document has.
            if (DispatcherQueue.HasThreadAccess)
                PullTileSelectionFromViewModel();
            else
                DispatcherQueue.TryEnqueue(PullTileSelectionFromViewModel);
        };
    }

    /// <summary>
    /// The right-click menu on a tile, which is where a Windows user looks for what can be done
    /// to the thing under the pointer. Built here rather than in the XAML because it is the same
    /// list of commands the toolbar's Pages menu offers and a <see cref="MenuFlyout"/> cannot be
    /// shared between two places in the tree; both are driven straight off the view model's
    /// commands, so neither has a click handler to drift.
    /// </summary>
    private MenuFlyout BuildPageContextMenu()
    {
        var menu = new MenuFlyout();
        Add(Strings.RotatePageRight, ViewModel.RotatePagesRightCommand, "RotateRightMenuItem");
        Add(Strings.RotatePageLeft, ViewModel.RotatePagesLeftCommand, "RotateLeftMenuItem");
        Add(Strings.DeletePageItem, ViewModel.DeletePagesCommand, "DeletePagesMenuItem");
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(Strings.MovePageEarlierItem, ViewModel.MovePageEarlierCommand, "MovePageEarlierMenuItem");
        Add(Strings.MovePageLaterItem, ViewModel.MovePageLaterCommand, "MovePageLaterMenuItem");
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(Strings.InsertBlankPageItem, ViewModel.InsertBlankPageCommand, "InsertBlankPageMenuItem");
        Add(Strings.InsertPagesFromFileItem, ViewModel.InsertPagesFromFileCommand, "InsertPagesFromFileMenuItem");
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(Strings.ExtractPagesItem, ViewModel.ExtractSelectedPagesCommand, "ExtractPagesMenuItem");
        return menu;

        void Add(string text, System.Windows.Input.ICommand command, string automationId)
        {
            var item = new MenuFlyoutItem { Text = text, Command = command };
            AutomationProperties.SetAutomationId(item, automationId);
            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// Right-clicking a tile selects it first, the way Explorer does: a menu that acts on
    /// something other than the thing you pointed at is a menu that deletes the wrong page. A
    /// tile already inside the selection leaves the selection alone, so a right-click on one of
    /// five selected pages still acts on all five.
    /// </summary>
    private void OnPageTileContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (args.TryGetPosition(PageTiles, out var point)
            && TileIndexAt(point) is { } index
            && index < ViewModel.Thumbnails.Count
            && !PageTiles.SelectedItems.Contains(ViewModel.Thumbnails[index]))
        {
            PageTiles.SelectedItems.Clear();
            PageTiles.SelectedItems.Add(ViewModel.Thumbnails[index]);
        }
        // Left unhandled: the flyout on PageTiles opens itself, at the pointer.
    }

    /// <summary>
    /// Each tile draws its raster when the grid realises it, and not before — and the container
    /// the grid handed it takes the page's spoken name.
    ///
    /// On the container, not in the item template: what a screen reader reads for a grid row is
    /// the <c>GridViewItem</c>'s name, and the template's own root is a panel, which has no name
    /// to give. "3" under a picture of the page is what a sighted person needs and is useless
    /// without it, so the container says "Page 3". The name is set again on every realisation
    /// because containers are recycled onto other pages as the pane scrolls.
    /// </summary>
    private void OnPageTileChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Phase != 0)
            return;
        if (args.ItemContainer is { } container && args.Item is PageThumbnail tile)
        {
            AutomationProperties.SetName(container, tile.AccessibleName);
            AutomationProperties.SetAutomationId(container, "PageTile");
        }
        _ = ViewModel.EnsureTileAsync(args.ItemIndex);
    }

    /// <summary>The tiles the pane has realised, redrawn — after a rotation invalidated theirs.</summary>
    private void RedrawRealisedTiles()
    {
        for (var i = 0; i < ViewModel.Thumbnails.Count; i++)
        {
            if (PageTiles.ContainerFromIndex(i) is not null)
                _ = ViewModel.EnsureTileAsync(i);
        }
    }

    private void OnPageTileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Not while the view model is writing it, and not while the tiles are being moved to match
        // a page operation — a selected tile leaving the collection raises this with an empty
        // selection, which is not the person deselecting anything.
        if (_syncingTileSelection || ViewModel.IsRenumbering)
            return;
        ViewModel.SelectedPageIndices = PageTiles.SelectedItems
            .OfType<PageThumbnail>()
            .Select(t => t.Index)
            .ToArray();
    }

    private void PullTileSelectionFromViewModel()
    {
        var wanted = ViewModel.SelectedPageIndices;
        var showing = PageTiles.SelectedItems.OfType<PageThumbnail>().Select(t => t.Index).Order().ToArray();
        if (showing.SequenceEqual(wanted))
            return;
        _syncingTileSelection = true;
        try
        {
            PageTiles.SelectedItems.Clear();
            foreach (var index in wanted)
            {
                if (index >= 0 && index < ViewModel.Thumbnails.Count)
                    PageTiles.SelectedItems.Add(ViewModel.Thumbnails[index]);
            }
        }
        finally
        {
            _syncingTileSelection = false;
        }
    }

    // --- Dragging a page to a new place ---------------------------------------

    private void OnPageTileDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (!ViewModel.CanAssemblePages || e.Items.Count != 1 || e.Items[0] is not PageThumbnail tile)
        {
            e.Cancel = true;
            return;
        }
        _draggingPage = tile.Index;
        // A drag with nothing in its data package is refused by the drop target before it gets
        // anywhere; the page number is what the pane means, and it is never read back — the
        // index is held here, so a drag out of the app cannot smuggle one in.
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText(tile.PageNumberLabel);
    }

    private void OnPageTileDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        _draggingPage = -1;

    private void OnPageTileDragOver(object sender, DragEventArgs e)
    {
        if (_draggingPage < 0 || !ViewModel.CanAssemblePages)
            return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsGlyphVisible = false;
        e.DragUIOverride.IsCaptionVisible = false;
        e.Handled = true;
    }

    private async void OnPageTileDrop(object sender, DragEventArgs e)
    {
        var from = _draggingPage;
        _draggingPage = -1;
        if (from < 0)
            return;
        e.Handled = true;
        // Where the page would be inserted, in the numbering *before* the move — so dropping a
        // page later in the document lands it one index lower than the gap it was dropped into,
        // because it leaves its own place first.
        var gap = InsertGapAt(e.GetPosition(PageTiles));
        var to = gap > from ? gap - 1 : gap;
        await ViewModel.MovePageAsync(from, Math.Clamp(to, 0, Math.Max(0, ViewModel.Thumbnails.Count - 1)));
    }

    /// <summary>The tile under a point in the grid, or null for the space between or after them.</summary>
    private int? TileIndexAt(Point point)
    {
        for (var i = 0; i < ViewModel.Thumbnails.Count; i++)
        {
            if (PageTiles.ContainerFromIndex(i) is not FrameworkElement container)
                continue;
            var origin = container.TransformToVisual(PageTiles).TransformPoint(new Point(0, 0));
            if (point.X >= origin.X && point.X <= origin.X + container.ActualWidth
                && point.Y >= origin.Y && point.Y <= origin.Y + container.ActualHeight)
                return i;
        }
        return null;
    }

    /// <summary>
    /// Which gap a drop at <paramref name="point"/> means: 0 before the first tile, the page
    /// count after the last. The nearer vertical edge of the tile under the pointer, and the end
    /// of the document for a drop past every tile — the only two answers a reflowing grid needs,
    /// since a row's tiles are consecutive pages.
    /// </summary>
    private int InsertGapAt(Point point)
    {
        for (var i = 0; i < ViewModel.Thumbnails.Count; i++)
        {
            if (PageTiles.ContainerFromIndex(i) is not FrameworkElement container)
                continue;
            var origin = container.TransformToVisual(PageTiles).TransformPoint(new Point(0, 0));
            if (point.Y > origin.Y + container.ActualHeight)
                continue;   // a row above the pointer's
            if (point.X <= origin.X + (container.ActualWidth / 2))
                return i;
            if (point.X <= origin.X + container.ActualWidth)
                return i + 1;
        }
        return ViewModel.Thumbnails.Count;
    }

    // --- Keys, inside the pane only -------------------------------------------

    /// <summary>
    /// Delete, and the two reorder chords, bound on the pane rather than on the window: on the
    /// window Delete would be taken from every text box and every inline editor, which is the
    /// same reason the other desktop binds it to its strip (#174).
    ///
    /// Ctrl+Shift+Left / Ctrl+Shift+Right rather than an arrow pair without modifiers, which the
    /// grid itself needs to move between tiles; and left/right rather than up/down because in a
    /// reflowing grid the tile above a page is a whole row back, while the page before it is
    /// always to its left.
    /// </summary>
    private async void OnPageTileKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // KeyRoutedEventArgs carries no modifier state of its own (only PointerRoutedEventArgs
        // does), so the two keys are read from the thread's own keyboard state — the same way
        // DocumentView.Keyboard.cs reads Shift for its focus walk.
        var control = Down(VirtualKey.Control);
        var shift = Down(VirtualKey.Shift);
        var menu = Down(VirtualKey.Menu);
        var plain = !control && !shift && !menu;
        var ctrlShift = control && shift && !menu;

        static bool Down(VirtualKey key) =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
             & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        switch (e.Key)
        {
            case VirtualKey.Delete when plain:
                if (ViewModel.DeletePagesCommand.CanExecute(null))
                {
                    e.Handled = true;
                    await ViewModel.DeletePagesCommand.ExecuteAsync(null);
                }
                else if (ViewModel.CanAssemblePages && ViewModel.HasPageSelection)
                {
                    // The one case the command is off for a reason a person can act on: the
                    // document's last page. Said as the rule it is rather than as nothing
                    // happening — the engine's own refusal, in the engine's own words.
                    e.Handled = true;
                    ViewModel.ShowLastPageRefusal();
                }
                break;

            case VirtualKey.Left when ctrlShift:
                if (ViewModel.MovePageEarlierCommand.CanExecute(null))
                {
                    e.Handled = true;
                    await ViewModel.MovePageEarlierCommand.ExecuteAsync(null);
                }
                break;

            case VirtualKey.Right when ctrlShift:
                if (ViewModel.MovePageLaterCommand.CanExecute(null))
                {
                    e.Handled = true;
                    await ViewModel.MovePageLaterCommand.ExecuteAsync(null);
                }
                break;
        }
    }

    // --- For the self-test ----------------------------------------------------

    /// <summary>The pane's grid, so the `pages` self-test can assert on the real control.</summary>
    internal GridView PageTilesForTest => PageTiles;

    /// <summary>Whether the pane is on screen — the control's own state, not the flag behind it.</summary>
    internal bool IsPagesPaneShown => PagesPane.Visibility == Visibility.Visible;

    /// <summary>What a screen reader would read for the tile at <paramref name="index"/>, or "".</summary>
    internal string TileAccessibleNameForTest(int index) =>
        PageTiles.ContainerFromIndex(index) is DependencyObject container
            ? AutomationProperties.GetName(container)
            : "";

    /// <summary>A drop of <paramref name="from"/> into the gap <paramref name="gap"/>, without a pointer.</summary>
    internal Task DropPageForTest(int from, int gap) =>
        ViewModel.MovePageAsync(from, gap > from ? gap - 1 : gap);
}
