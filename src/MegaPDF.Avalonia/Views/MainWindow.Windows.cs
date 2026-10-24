using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MegaPDF.Avalonia.ViewModels;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// A document's tab, moved into a window of its own (#711), and the windows that result.
///
/// The two desktops this app covers do it differently, on purpose — the platform-native rule:
///
/// <list type="bullet">
/// <item><b>The Mac</b> follows the Mac (Dave, 2026-10-20). One window per document
/// (<see cref="ShellViewModel.OneDocumentPerWindow"/>), merged into tabs by AppKit as
/// Preview's and TextEdit's are — when the person's System Settings "Prefer tabs when opening
/// documents" says so, and whenever they merge them by hand — with the system's own tab bar, drag-out, Window ▸ Move Tab
/// to New Window, Merge All Windows and Show All Tabs — in the system's own words, in every
/// language. The in-window tab strip is hidden. All this file adds is the plumbing:
/// <see cref="Platform.MacWindowTabs"/>, and opening a second document into a new window
/// rather than a second tab of this one.</item>
/// <item><b>Linux</b> keeps #348's in-window strip, and gains Move Tab to New Window on each
/// tab's context menu and a drag off the strip that does the same. The document — its unsaved
/// edits, undo history, zoom, scroll position and any open find — is the same
/// <see cref="DocumentViewModel"/> object afterwards; only the window around it is new.</item>
/// </list>
///
/// With more than one window, an external open (Finder, Open With, the Linux single-instance
/// socket, a drop, File ▸ Open, a recent) needs a rule. A document already open anywhere
/// activates its window and tab rather than opening twice; anything else goes to the most
/// recently active window — which, on the Mac, means a new window beside it, made a tab of it
/// by AppKit when the person prefers tabs.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Whether this process puts documents in native macOS window tabs. The Mac app, and only
    /// the Mac app; the same binary on Linux keeps the in-window strip.
    /// </summary>
    internal static bool UsesNativeWindowTabs => OperatingSystem.IsMacOS();

    // --- Every document window, most recently active first ---

    private static readonly List<MainWindow> WindowsByRecency = [];

    /// <summary>Every open document window, the most recently active first.</summary>
    internal static IReadOnlyList<MainWindow> OpenWindows => WindowsByRecency;

    /// <summary>
    /// The window an external open with no better home goes to (#711): the one used last.
    /// Not "the active one" — when Finder or a file manager hands a file over, it is that
    /// app that is active, and every window here reports inactive.
    /// </summary>
    internal static MainWindow? MostRecentlyActive => WindowsByRecency.FirstOrDefault();

    private void MarkMostRecentlyActive()
    {
        WindowsByRecency.Remove(this);
        WindowsByRecency.Insert(0, this);
    }

    private void OnWindowActivated()
    {
        MarkMostRecentlyActive();
        // Each window has its own menu bar, installed by Avalonia as the window becomes
        // key; AppKit must be told again which of its menus is the Window menu, or the tab
        // commands live only in whichever menu bar was named last. Posted, so it runs
        // after Avalonia has put this window's menu bar in place.
        if (_nativeTabs && OperatingSystem.IsMacOS())
            Dispatcher.UIThread.Post(() => Platform.MacWindowTabs.DesignateWindowsMenu(Strings.MenuWindow));
    }

    /// <summary>
    /// The windows sharing this one's settings, recents and signatures — the same running
    /// app. The self-test builds a fresh, isolated shell per check, and a check must never
    /// find a document open in a window some earlier check left behind.
    /// </summary>
    private IEnumerable<MainWindow> SiblingWindows() =>
        WindowsByRecency.Where(w => w.Shell is { } other && Shell is { } mine && ReferenceEquals(other.Settings, mine.Settings));

    /// <summary>
    /// The window and tab already open on <paramref name="path"/>, in this window or any of
    /// its siblings — counting a tab whose open is still in flight (<see cref="ShellViewModel.FindTab"/>).
    /// </summary>
    internal (MainWindow Window, DocumentViewModel Document)? FindOpenDocument(string path)
    {
        if (Shell?.FindTab(path) is { } here)
            return (this, here);
        foreach (var window in SiblingWindows())
            if (!ReferenceEquals(window, this) && window.Shell?.FindTab(path) is { } there)
                return (window, there);
        return null;
    }

    /// <summary>Brings a document already open to the front: its tab selected, its window active.</summary>
    internal void ShowDocument(DocumentViewModel document)
    {
        Shell?.ActivateTab(document);
        BringToFront();
    }

    /// <summary>The window the next coalesced activation will bring forward; see <see cref="BringToFront"/>.</summary>
    private static MainWindow? _frontRequest;

    /// <summary>
    /// Activates this window — on the Mac's native tabs, once per burst. Measured on the Mac
    /// mini: activating two or three tabbed windows back to back (what a multi-file open from
    /// Finder does, and what a cold launch's duplicate open-file events do, #398) left AppKit
    /// selecting each tab in turn, ~140 activations a second, indefinitely, with the menu bar
    /// and keyboard following it round. One activation of the window asked for last never did.
    /// So requests in the same turn of the dispatcher collapse into one, of the last window
    /// asked for. Everywhere else, and on the headless platform, it activates at once.
    /// </summary>
    internal void BringToFront()
    {
        MarkMostRecentlyActive();
        if (!_nativeTabs || !OperatingSystem.IsMacOS())
        {
            Activate();
            return;
        }
        var scheduled = _frontRequest is not null;
        _frontRequest = this;
        if (scheduled)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            var window = _frontRequest;
            _frontRequest = null;
            if (window is { IsVisible: true })
                window.Activate();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Set on a window made to hold one particular document (the Mac's next document, or
    /// Linux's moved tab): if that document fails to open there, the window goes too, so a
    /// failed open never leaves an empty window behind — the "no Untitled tab" rule of #348,
    /// one level up.
    /// </summary>
    private bool _madeForDocument;

    /// <summary>Whether this window's NSWindow has been given a tabbing identifier.</summary>
    private bool _nativeTabs;

    /// <summary>
    /// Makes this window one AppKit can merge with the others (#711). Before the window is
    /// first shown: AppKit decides as it orders a window in. Whether it does is the person's
    /// choice, not MegaPDF's — System Settings ▸ Desktop &amp; Dock ▸ "Prefer tabs when opening
    /// documents" (Always, In Full Screen, Never), which Preview and TextEdit follow and so
    /// does this. Either way the windows can be merged later with Window ▸ Merge All Windows
    /// or by dragging one onto another's tab bar. A no-op off the Mac, on the headless
    /// platform, and for a shell of the in-window shape.
    /// </summary>
    internal void EnableNativeTabs()
    {
        if (!UsesNativeWindowTabs || !OperatingSystem.IsMacOS() || Shell?.OneDocumentPerWindow != true)
            return;
        _nativeTabs = Platform.MacWindowTabs.Enable(this);
    }

    /// <summary>Whether AppKit has this window in a tab group with another — what ⌘W's title says.</summary>
    internal bool IsInNativeTabGroup =>
        _nativeTabs && OperatingSystem.IsMacOS() && Platform.MacWindowTabs.TabbedWindows(this).Count > 1;

    /// <summary>This window and every other MegaPDF window AppKit shows as a tab beside it.</summary>
    internal IReadOnlyList<MainWindow> NativeTabGroup()
    {
        if (!IsInNativeTabGroup || !OperatingSystem.IsMacOS())
            return [this];
        var natives = Platform.MacWindowTabs.TabbedWindows(this);
        return SiblingWindows().Where(w => natives.Contains(Platform.MacWindowTabs.NativeWindow(w))).ToList();
    }

    /// <summary>Puts the active document's name on this window's native tab, without " — MegaPDF".</summary>
    private void UpdateNativeTabTitle()
    {
        if (_nativeTabs && OperatingSystem.IsMacOS())
            Platform.MacWindowTabs.SetTabTitle(this, Active?.TabTitle ?? Title ?? "MegaPDF");
    }

    /// <summary>
    /// Where a document about to be opened from this window goes: here, while this window has
    /// room for it, or — the Mac, with a document already in this window — a new window of its
    /// own, which AppKit makes a tab of the front window or not as the person's "Prefer tabs"
    /// setting says. Never forced into a tab against that setting. The new window is shown,
    /// and asked to come forward, when this returns.
    /// </summary>
    internal MainWindow HostForNewDocument()
    {
        if (Shell is not { } shell || shell.HasRoomForDocument)
            return this;
        var window = new MainWindow
        {
            DataContext = shell.CreateSibling(),
            // A window made for one document is never the place for a crash-recovery offer;
            // that is asked once per launch, by the first window (#348 §5.3).
            SkipRecoveryOffer = true,
            _madeForDocument = true,
        };
        window.EnableNativeTabs();
        // Listed before it is shown, not when it opens: a second hand-over of the same file
        // can arrive in between (#398), and must find the document already on its way here.
        window.MarkMostRecentlyActive();
        window.Show();
        window.BringToFront();
        return window;
    }

    /// <summary>A window made for one document whose open came to nothing closes again.</summary>
    private void CloseIfLeftEmpty()
    {
        if (_madeForDocument && Shell is { Documents.Count: 0 })
            Close();
    }

    // --- Linux: Move Tab to New Window ---

    /// <summary>
    /// Moves a tab into a window of its own (#711): the context menu's Move Tab to New Window,
    /// and where a drag off the strip ends. The document is not reopened — the same
    /// <see cref="DocumentViewModel"/>, with its unsaved edits, undo history, zoom, find and
    /// recovery journal, is detached from this window's shell and added to the new one's, and
    /// its file handle (what Save writes through) and scroll position travel with it. The
    /// window left behind closes if that was its last tab.
    ///
    /// Not for the Mac's shape, whose windows already are one document each and are moved by
    /// AppKit. Returns the new window, or null when there was nothing to move.
    /// </summary>
    internal MainWindow? MoveTabToNewWindow(DocumentViewModel document, PixelPoint? at = null)
    {
        if (Shell is not { OneDocumentPerWindow: false } shell || !shell.Documents.Contains(document))
            return null;

        var hadFile = _openedFiles.TryGetValue(document, out var file);
        // Detaching makes another tab (or none) active, and that switch is what saves the
        // outgoing tab's scroll position (OnActiveDocumentChanged) and cancels any gesture
        // half done on it — the same rules a tab switch has always followed.
        shell.DetachTab(document);
        var hadOffset = _scrollOffsets.TryGetValue(document, out var offset);
        ForgetTab(document);

        var window = new MainWindow
        {
            DataContext = shell.CreateSibling(),
            SkipRecoveryOffer = true,
            Width = Width,
            Height = Height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = at ?? new PixelPoint(Position.X + 40, Position.Y + 40),
        };
        if (hadFile)
            window._openedFiles[document] = file;
        if (hadOffset)
            window._scrollOffsets[document] = offset;
        window.Shell!.AddTab(document);
        window.Show();
        window.Activate();

        if (shell.Documents.Count == 0)
            Close();
        return window;
    }

    /// <summary>The ✕-free part of a tab: the menu its context request opens. One per tab container, built once.</summary>
    private ContextMenu BuildTabMenu(Control container)
    {
        var move = new MenuItem { Header = Strings.MoveTabToNewWindow };
        move.Click += (_, _) =>
        {
            if (container.DataContext is DocumentViewModel document)
                MoveTabToNewWindow(document);
        };
        var menu = new ContextMenu();
        menu.Items.Add(move);
        menu.Opening += (_, _) => move.IsEnabled = Shell?.CanMoveTabToNewWindow == true;
        return menu;
    }

    /// <summary>For the self-test: the tab container's own context menu, as the Menu key or Shift+F10 would open it.</summary>
    internal ContextMenu? TabContextMenuForTest(int index) =>
        (DocumentTabStrip.ContainerFromIndex(index) as Control)?.ContextMenu;

    private void WireTabStrip()
    {
        DocumentTabStrip.ContainerPrepared += (_, e) => e.Container.ContextMenu ??= BuildTabMenu(e.Container);

        // Shift+F10 (#773). Avalonia opens a focused control's ContextMenu on key-up of the
        // keys in PlatformHotkeyConfiguration.OpenContextMenu, which is the Menu key alone,
        // so Shift+F10 did nothing here (measured on GNOME) although the Menu key and a
        // right-click work. Shift+F10 is the Windows/Linux (GNOME, KDE) convention and is
        // wired on Linux only; the Mac's convention is Ctrl+click or the Menu key, and its
        // tabs are AppKit's own, not this strip. Tunnelling, so it is seen before anything
        // else treats F10 as its own; only a focused tab answers, not the rest of the window.
        if (OperatingSystem.IsLinux())
            DocumentTabStrip.AddHandler(KeyDownEvent, OnTabStripKeyDown, RoutingStrategies.Tunnel);

        // Bubbling, and handled events too: the strip's own items handle the press (it is
        // what selects them), and this must see it regardless. Not tunnelling: Avalonia moves
        // focus on a press to whatever holds the pointer capture when the tunnel reaches the
        // item, so capturing on the way down sent focus to the strip instead of the tab — and
        // an in-place text editor open on the page never lost focus, so never committed.
        DocumentTabStrip.AddHandler(PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        DocumentTabStrip.AddHandler(PointerMovedEvent, OnTabPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        DocumentTabStrip.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        DocumentTabStrip.AddHandler(PointerCaptureLostEvent, (_, e) =>
        {
            if (ReferenceEquals(e.Source, DocumentTabStrip))
                EndTabDrag();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnTabStripKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F10 || e.KeyModifiers != KeyModifiers.Shift)
            return;
        // The focused element is the tab (or something inside it, such as its close button).
        var tab = (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual)
            ?.FindAncestorOfType<TabStripItem>(includeSelf: true);
        if (tab?.ContextMenu is null)
            return;
        // The request Avalonia itself raises for the Menu key, so the menu opens exactly as
        // that does, anchored under the tab. ContextMenu.Open(tab) would put it at the
        // pointer (measured on GNOME), which is wherever the mouse was left.
        var request = new ContextRequestedEventArgs();
        tab.RaiseEvent(request);
        e.Handled = request.Handled;
    }

    // --- Linux: dragging a tab off the strip ---
    //
    // Avalonia 11.2's TabStrip has no drag of its own, so this is a small one: press on a
    // tab, carry the pointer clear of the strip, let go, and the tab becomes a window where
    // it was let go. The pointer is captured on the press so the release still arrives when
    // it happens outside the window, which is the common case. Back over the strip before
    // letting go, and nothing happens. Reordering within the strip is not part of it.

    /// <summary>How far clear of the strip, in DIPs, a drag has to be carried before letting go moves the tab.</summary>
    internal const double TabTearOutDistance = 40;

    private DocumentViewModel? _tabDragDocument;
    private IPointer? _tabDragPointer;
    private bool _tabDragClear;

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        EndTabDrag();
        if (Shell is not { OneDocumentPerWindow: false } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var source = e.Source as Visual;
        // The ✕ closes; a drag that starts on it is not a drag of the tab.
        if (source?.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (source?.FindAncestorOfType<TabStripItem>(includeSelf: true) is not { DataContext: DocumentViewModel document })
            return;
        // Captured before the drag is recorded: taking the capture from the item the press
        // landed on (Avalonia captures implicitly on press) raises PointerCaptureLost on the
        // way, and that must not end a drag that has not started yet.
        e.Pointer.Capture(DocumentTabStrip);
        _tabDragDocument = document;
        _tabDragPointer = e.Pointer;
    }

    private void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_tabDragDocument is null || !ReferenceEquals(e.Pointer, _tabDragPointer))
            return;
        var clear = IsClearOfTabStrip(e.GetPosition(DocumentTabStrip));
        if (clear != _tabDragClear)
        {
            _tabDragClear = clear;
            Cursor = clear ? new Cursor(StandardCursorType.DragMove) : null;
        }
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_tabDragDocument is not { } document || !ReferenceEquals(e.Pointer, _tabDragPointer))
            return;
        var clear = IsClearOfTabStrip(e.GetPosition(DocumentTabStrip));
        var screen = this.PointToScreen(e.GetPosition(this));
        EndTabDrag();
        e.Pointer.Capture(null);
        if (!clear)
            return;
        e.Handled = true;
        // The new window's top-left a little up and left of where the tab was let go, so the
        // pointer lands on its toolbar rather than on its corner.
        var scaling = RenderScaling > 0 ? RenderScaling : 1;
        MoveTabToNewWindow(document, new PixelPoint(screen.X - (int)(120 * scaling), screen.Y - (int)(16 * scaling)));
    }

    private void EndTabDrag()
    {
        _tabDragDocument = null;
        _tabDragPointer = null;
        if (_tabDragClear)
            Cursor = null;
        _tabDragClear = false;
    }

    private bool IsClearOfTabStrip(Point inStrip) =>
        inStrip.Y < -TabTearOutDistance || inStrip.Y > DocumentTabStrip.Bounds.Height + TabTearOutDistance
        || inStrip.X < -TabTearOutDistance || inStrip.X > DocumentTabStrip.Bounds.Width + TabTearOutDistance;

    // --- Closing a whole Mac window of tabs ---

    /// <summary>
    /// ⇧⌘W on the Mac's shape: every tab AppKit shows in this window, each asked about its
    /// unsaved changes before any of them closes — the same "ask everyone, then close" rule
    /// quitting follows (<see cref="App.ConfirmAllWindowsForQuitAsync"/>), so a Cancel on the
    /// third leaves the first two open.
    /// </summary>
    private static async Task CloseTabGroupAsync(IReadOnlyList<MainWindow> group)
    {
        if (!await App.ConfirmAllWindowsForQuitAsync(group))
            return;
        foreach (var window in group)
            window.Close();
    }
}
