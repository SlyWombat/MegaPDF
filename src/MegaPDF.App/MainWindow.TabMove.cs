using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MegaPDF.App;

/// <summary>
/// A tab leaving one window for another (#711): dragged off the strip into a window of its own,
/// dragged onto another MegaPDF window's strip, or "Move tab to new window" from its context menu.
/// All three end in <see cref="MoveDocument"/>, so what a drag does is what the menu does, and what
/// the self-test drives. The document does not go through the file system or the engine: the same
/// <see cref="DocumentViewModel"/> is released by one shell and adopted by the other, so unsaved
/// edits, the undo history, zoom, the page focus, the find, the journal and the open PDFium document
/// all go with it. The one thing that cannot go is the page view, a WinUI element, which belongs to
/// the window that made it: the document gets a new one.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Windows made for a drag in progress, until the drop hands them their tabs.</summary>
    private static readonly Dictionary<WindowId, MainWindow> TornOutWindows = [];

    /// <summary>
    /// TabView's tear-out: asked when a tab is dragged off the strip, wanting a window to put it in.
    /// Only offered when it would leave something behind. A window's only tab dragged out would just
    /// be a window moving, so the tab snaps back instead.
    /// </summary>
    private void OnTabTearOutWindowRequested(TabView sender, TabViewTabTearOutWindowRequestedEventArgs args)
    {
        if (Shell.Documents.Count < 2)
            return;
        args.NewWindowId = BeginTearOutWindow().AppWindow.Id;
    }

    /// <summary>
    /// How long a window made for a drag waits for its tab before it is taken to be abandoned.
    /// Measured: TabTearOutRequested follows TabTearOutWindowRequested at once, while the pointer
    /// is still moving, and Escape does not cancel between them, so this is a net for a drag that
    /// ends some way TabView does not report, not something a drag normally meets. A field so the
    /// self-test can shorten it.
    /// </summary>
    internal static TimeSpan AbandonedTearOutWindowAfter = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Makes the window a tear-out will put its tab in, as large as this one (TabView places it
    /// under the pointer). If no tab has landed in it by <see cref="AbandonedTearOutWindowAfter"/>
    /// it closes, so a drag that ends without a drop can never leave an empty window on screen.
    /// </summary>
    internal MainWindow BeginTearOutWindow()
    {
        var window = new MainWindow();
        TornOutWindows[window.AppWindow.Id] = window;
        window.AppWindow.Resize(AppWindow.Size);
        var timer = window.DispatcherQueue.CreateTimer();
        timer.Interval = AbandonedTearOutWindowAfter;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (TornOutWindows.Remove(window.AppWindow.Id) && window.Shell.Documents.Count == 0)
                window.CloseEmptied();
        };
        timer.Start();
        return window;
    }

    /// <summary>Whether a window made for a drag is still waiting for its tab (the self-test).</summary>
    internal static bool IsAwaitingTearOut(MainWindow window) => TornOutWindows.ContainsKey(window.AppWindow.Id);

    /// <summary>The window exists and the tab is over it: move the document across.</summary>
    private void OnTabTearOutRequested(TabView sender, TabViewTabTearOutRequestedEventArgs args)
    {
        // Measured on 1.7.260224002: NewWindowId is 0 here, not the id given to
        // TabTearOutWindowRequested, so the window is the one that event made. Only one drag
        // is ever in flight, and the id is honoured should a later SDK fill it in.
        var id = args.NewWindowId.Value != 0 ? args.NewWindowId : TornOutWindows.Keys.FirstOrDefault();
        if (!TornOutWindows.Remove(id, out var target))
            return;

        foreach (var doc in args.Items.OfType<DocumentViewModel>().ToList())
            MoveDocument(doc, this, target, int.MaxValue);
        target.Activate();
        target.JoinWindowUnderPointerOnRelease(source: this);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    /// <summary>
    /// TabView raises tear-out when the tab leaves the strip, not when it is let go: from then on
    /// the new window follows the pointer, a window drag. So where the tab ends up is known only
    /// at the release. When that is over another MegaPDF window's tab strip, the tab joins that
    /// window and this one, made for the drag, closes; anywhere else it stays here, where it was
    /// dropped. The pointer is looked for rather than a drop event, because this window sits
    /// under the pointer for the whole drag and the strip below never receives the drop
    /// (measured: ExternalTornOutTabsDropping did not fire for a tab torn out of a window with
    /// others in it).
    /// </summary>
    private void JoinWindowUnderPointerOnRelease(MainWindow source)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        var started = DateTime.UtcNow;
        timer.Tick += (_, _) =>
        {
            const int VK_LBUTTON = 0x01;
            var down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            if (down && DateTime.UtcNow - started < TimeSpan.FromMinutes(1))
                return;
            timer.Stop();
            if (Shell.Documents.Count == 0 || StripUnderPointer(excluding: [this]) is not { Window: { } joined, Index: var index })
                return;
            foreach (var doc in Shell.Documents.ToList())
                MoveDocument(doc, this, joined, index++);
            joined.Activate();
        };
        timer.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out global::Windows.Graphics.PointInt32 point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref global::Windows.Graphics.PointInt32 point);

    /// <summary>
    /// The MegaPDF window whose tab strip the pointer is over, and the index in it a tab dropped
    /// here would take (the number of its tabs whose centres are left of the pointer).
    /// </summary>
    private static (MainWindow? Window, int Index) StripUnderPointer(IReadOnlyCollection<MainWindow> excluding)
    {
        if (!GetCursorPos(out var cursor))
            return (null, 0);
        foreach (var window in App.Windows.Reverse())
        {
            if (excluding.Contains(window) || window.Content?.XamlRoot is not { } root)
                continue;
            var origin = new global::Windows.Graphics.PointInt32(0, 0);
            if (!ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(window), ref origin))
                continue;
            var scale = root.RasterizationScale;
            var strip = window.DocumentsTabView.TransformToVisual(null).TransformPoint(new global::Windows.Foundation.Point(0, 0));
            var left = origin.X + strip.X * scale;
            var top = origin.Y + strip.Y * scale;
            // The strip is the tab row at the top of the TabView: 48 DIPs, the TabView's own height.
            if (cursor.X < left || cursor.X > left + window.DocumentsTabView.ActualWidth * scale
                || cursor.Y < top || cursor.Y > top + 48 * scale)
                continue;
            var index = 0;
            for (var i = 0; i < window.Shell.Documents.Count; i++)
            {
                if (window.DocumentsTabView.ContainerFromIndex(i) is not TabViewItem tab)
                    continue;
                var centre = tab.TransformToVisual(null).TransformPoint(new global::Windows.Foundation.Point(tab.ActualWidth / 2, 0));
                if (origin.X + centre.X * scale < cursor.X)
                    index = i + 1;
            }
            return (window, index);
        }
        return (null, 0);
    }

    /// <summary>Another window's tab is over this strip: take it if it is one of ours.</summary>
    private void OnExternalTornOutTabsDropping(TabView sender, TabViewExternalTornOutTabsDroppingEventArgs args) =>
        args.AllowDrop = args.Items.OfType<DocumentViewModel>().Any();

    private void OnExternalTornOutTabsDropped(TabView sender, TabViewExternalTornOutTabsDroppedEventArgs args)
    {
        var index = args.DropIndex;
        foreach (var doc in args.Items.OfType<DocumentViewModel>().ToList())
        {
            if (App.WindowHolding(doc) is { } source && source != this)
                MoveDocument(doc, source, this, index++);
        }
        Activate();
    }

    /// <summary>The tab's context menu: its one command is only offered when there is another tab to stay behind.</summary>
    private void OnTabContextMenuOpening(object sender, object e)
    {
        if (sender is MenuFlyout { Items.Count: > 0 } flyout && flyout.Items[0] is MenuFlyoutItem move)
            move.IsEnabled = Shell.Documents.Count > 1;
    }

    private void OnMoveTabToNewWindowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DocumentViewModel doc })
            _ = MoveTabToNewWindow(doc);
    }

    /// <summary>
    /// Moves <paramref name="doc"/> into a window of its own, a little down and to the right of this
    /// one, or does nothing when it is this window's only tab. Returns the new window.
    /// </summary>
    internal MainWindow? MoveTabToNewWindow(DocumentViewModel doc)
    {
        if (Shell.Documents.Count < 2 || !Shell.Documents.Contains(doc))
            return null;
        var window = new MainWindow();
        window.AppWindow.Resize(AppWindow.Size);
        var origin = AppWindow.Position;
        window.AppWindow.Move(new global::Windows.Graphics.PointInt32(origin.X + 40, origin.Y + 40));
        window.Activate();
        MoveDocument(doc, this, window, 0);
        return window;
    }

    /// <summary>
    /// The one move: <paramref name="doc"/> leaves <paramref name="from"/> and joins
    /// <paramref name="to"/> at <paramref name="index"/>, getting a new page view on the way. The
    /// window it leaves closes when that was its last tab; its other tabs are untouched.
    /// </summary>
    internal static void MoveDocument(DocumentViewModel doc, MainWindow from, MainWindow to, int index)
    {
        if (from == to || !from.Shell.Documents.Contains(doc))
            return;

        var scroll = doc.CurrentScrollOffset;
        var announcement = Strings.TabMoved(doc.OpenDocumentName);
        var find = doc.ReleaseView();
        from.Shell.ReleaseDocument(doc);

        // The new view exists only once the new strip has made it. What only the old view knew
        // (the find bar's text, the scroll position) is put back when it has.
        EventHandler? attached = null;
        attached = (_, _) =>
        {
            if (doc.View is not { } view)
                return;
            doc.ViewAttached -= attached;
            view.RestoreFindBar(find);
            view.RestoreScrollOffset(scroll);
            view.SyncFromDocument();
            doc.RedrawIfScaleChanged();
            // Said on the new view, once it has drawn: the old window may be gone by now, and a
            // screen reader following the tab needs to hear where it went.
            view.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => view.Announce(announcement));
        };
        doc.ViewAttached += attached;
        to.Shell.AdoptDocument(doc, index);

        if (from.Shell.Documents.Count == 0)
            from.CloseEmptied();
    }

    /// <summary>Closes a window its last tab has just left: nothing to ask, nothing to save, nothing to dispose.</summary>
    private void CloseEmptied()
    {
        _allowClose = true;
        Close();
    }
}
