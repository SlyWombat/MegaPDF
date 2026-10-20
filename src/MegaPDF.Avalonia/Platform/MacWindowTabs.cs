using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace MegaPDF.Avalonia.Platform;

/// <summary>
/// Native macOS window tabs (#711): one window per document, merged into tabs by AppKit
/// the way Preview and TextEdit merge theirs, with the system's own drag-out,
/// Window ▸ Move Tab to New Window, Merge All Windows and Show All Tabs.
///
/// Avalonia 11.2 has no API for any of it, but it does hand out the <c>NSWindow</c>
/// behind every <see cref="Window"/> (<see cref="global::Avalonia.Platform.IMacOSTopLevelPlatformHandle"/>),
/// and tabbing is three properties and one method on that window. Everything else —
/// the tab bar, the drag, the menu items and their French — is AppKit's.
///
/// Hand-written interop in the style of <see cref="MacApplication"/> and
/// <see cref="MacPrinter"/>: one <c>objc_msgSend</c> declaration per distinct
/// signature, because the call's ABI follows its argument and return types and
/// reusing one declaration across shapes is silent corruption on arm64.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacWindowTabs
{
    private const string Objc = "/usr/lib/libobjc.dylib";

    /// <summary>What every document window shares, so AppKit merges them and nothing else.</summary>
    internal const string TabbingIdentifier = "com.megapdf.document";

    /// <summary><c>NSWindowTabbingModeAutomatic</c>: join a tab group or not as the system's
    /// "Prefer tabs when opening documents" setting says — File ▸ New Window's mode.</summary>
    internal const long TabbingModeAutomatic = 0;

    /// <summary><c>NSWindowTabbingModePreferred</c>: always join the front window's tab group,
    /// whatever that setting says — a document's mode, because a document opened from
    /// Finder, Open With or File ▸ Open becomes a tab of the front window (#711).</summary>
    internal const long TabbingModePreferred = 1;

    /// <summary><c>NSWindowAbove</c>, for <c>addTabbedWindow:ordered:</c>.</summary>
    private const long OrderedAbove = 1;

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_Ptr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_Long(IntPtr receiver, IntPtr selector, long arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern long MsgSend_RetLong(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern byte MsgSend_RetBool(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_Void_Long(IntPtr receiver, IntPtr selector, long arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_Void_Ptr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_Void_Ptr_Long(IntPtr receiver, IntPtr selector, IntPtr arg, long arg2);

    [DllImport(Objc)]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Objc)]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Objc)]
    private static extern IntPtr sel_getName(IntPtr selector);

    private static IntPtr Sel(string name) => sel_registerName(name);

    /// <summary>The <c>NSWindow</c> behind an Avalonia window, or zero when there is none (headless, or not macOS).</summary>
    internal static IntPtr NativeWindow(Window window) =>
        window.TryGetPlatformHandle() is global::Avalonia.Platform.IMacOSTopLevelPlatformHandle mac ? mac.NSWindow : IntPtr.Zero;

    private static IntPtr NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return MsgSend_Ptr(objc_getClass("NSString"), Sel("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private static string? ManagedString(IntPtr nsString) =>
        nsString == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(MsgSend(nsString, Sel("UTF8String")));

    /// <summary>
    /// Makes a document window one AppKit will merge with the others. Called before the
    /// window is first shown: AppKit decides whether a window joins a tab group as it
    /// orders it in, from the mode and identifier it has at that moment. False when there
    /// is no native window to set it on.
    /// </summary>
    internal static bool Enable(Window window, bool preferred)
    {
        var ns = NativeWindow(window);
        if (ns == IntPtr.Zero)
            return false;
        MsgSend_Void_Ptr(ns, Sel("setTabbingIdentifier:"), NSString(TabbingIdentifier));
        MsgSend_Void_Long(ns, Sel("setTabbingMode:"), preferred ? TabbingModePreferred : TabbingModeAutomatic);
        return true;
    }

    /// <summary>
    /// What the window's tab says (<c>NSWindowTab.title</c>). AppKit uses the window title
    /// unless told otherwise, which made every tab read "name — MegaPDF"; Preview's read
    /// the document's name alone.
    /// </summary>
    internal static void SetTabTitle(Window window, string title)
    {
        var ns = NativeWindow(window);
        if (ns == IntPtr.Zero)
            return;
        var tab = MsgSend(ns, Sel("tab"));
        if (tab != IntPtr.Zero)
            MsgSend_Void_Ptr(tab, Sel("setTitle:"), NSString(title));
    }

    /// <summary>The tab's title as AppKit has it, for the real-Mac check.</summary>
    internal static string? TabTitle(Window window)
    {
        var ns = NativeWindow(window);
        var tab = ns == IntPtr.Zero ? IntPtr.Zero : MsgSend(ns, Sel("tab"));
        return tab == IntPtr.Zero ? null : ManagedString(MsgSend(tab, Sel("title")));
    }

    /// <summary>
    /// Puts <paramref name="window"/> into <paramref name="front"/>'s tab group, as the tab
    /// after it — what opening a document from the front window does. The new tab is not
    /// selected by this; the caller activates it.
    /// </summary>
    internal static bool AddTab(Window front, Window window)
    {
        var host = NativeWindow(front);
        var guest = NativeWindow(window);
        if (host == IntPtr.Zero || guest == IntPtr.Zero || host == guest)
            return false;
        MsgSend_Void_Ptr_Long(host, Sel("addTabbedWindow:ordered:"), guest, OrderedAbove);
        return true;
    }

    /// <summary>The <c>NSWindow</c>s in <paramref name="window"/>'s tab group, itself included; empty when it has none.</summary>
    internal static IReadOnlyList<IntPtr> TabbedWindows(Window window)
    {
        var ns = NativeWindow(window);
        if (ns == IntPtr.Zero)
            return [];
        var array = MsgSend(ns, Sel("tabbedWindows"));
        if (array == IntPtr.Zero)
            return [];
        var count = MsgSend_RetLong(array, Sel("count"));
        var result = new List<IntPtr>((int)count);
        for (long i = 0; i < count; i++)
            result.Add(MsgSend_Long(array, Sel("objectAtIndex:"), i));
        return result;
    }

    /// <summary>Whether the two windows are tabs of one window.</summary>
    internal static bool AreTabbedTogether(Window a, Window b)
    {
        var nb = NativeWindow(b);
        return nb != IntPtr.Zero && TabbedWindows(a).Contains(nb);
    }

    /// <summary>The tab group's selected window, or zero.</summary>
    internal static IntPtr SelectedTab(Window window)
    {
        var ns = NativeWindow(window);
        if (ns == IntPtr.Zero)
            return IntPtr.Zero;
        var group = MsgSend(ns, Sel("tabGroup"));
        return group == IntPtr.Zero ? IntPtr.Zero : MsgSend(group, Sel("selectedWindow"));
    }

    /// <summary>AppKit's own Window-menu actions, sent to the window as the menu would send them.</summary>
    internal static void Perform(Window window, string selector)
    {
        var ns = NativeWindow(window);
        if (ns != IntPtr.Zero)
            MsgSend_Void_Ptr(ns, Sel(selector), IntPtr.Zero);
    }

    internal static bool IsKey(Window window)
    {
        var ns = NativeWindow(window);
        return ns != IntPtr.Zero && MsgSend_RetBool(ns, Sel("isKeyWindow")) != 0;
    }

    internal static bool IsNativeVisible(Window window)
    {
        var ns = NativeWindow(window);
        return ns != IntPtr.Zero && MsgSend_RetBool(ns, Sel("isVisible")) != 0;
    }

    internal static long WindowNumber(Window window)
    {
        var ns = NativeWindow(window);
        return ns == IntPtr.Zero ? 0 : MsgSend_RetLong(ns, Sel("windowNumber"));
    }

    private static IntPtr SharedApplication()
    {
        var cls = objc_getClass("NSApplication");
        return cls == IntPtr.Zero ? IntPtr.Zero : MsgSend(cls, Sel("sharedApplication"));
    }

    /// <summary>Whether MegaPDF is the active application — the one whose menu bar is on screen.</summary>
    internal static bool IsAppActive()
    {
        var app = SharedApplication();
        return app != IntPtr.Zero && MsgSend_RetBool(app, Sel("isActive")) != 0;
    }

    /// <summary>One line per item of the application's main menu bar and its submenus: title and action.</summary>
    internal static IReadOnlyList<string> DescribeMainMenu()
    {
        var lines = new List<string>();
        var app = SharedApplication();
        if (app == IntPtr.Zero)
            return lines;
        var windowsMenu = MsgSend(app, Sel("windowsMenu"));
        var main = MsgSend(app, Sel("mainMenu"));
        // Whether the Window menu AppKit was given is one in the menu bar on screen, not the
        // same-titled menu of a window that is no longer key: each window has its own bar.
        var inBar = false;
        if (main != IntPtr.Zero && windowsMenu != IntPtr.Zero)
            for (long i = 0, n = MsgSend_RetLong(main, Sel("numberOfItems")); i < n; i++)
                inBar |= MsgSend(MsgSend_Long(main, Sel("itemAtIndex:"), i), Sel("submenu")) == windowsMenu;
        lines.Add($"NSApp.windowsMenu = {(windowsMenu == IntPtr.Zero ? "nil" : ManagedString(MsgSend(windowsMenu, Sel("title"))))}"
                  + (inBar ? "" : " (not in the menu bar on screen)"));
        Walk(main, "");
        return lines;

        void Walk(IntPtr menu, string indent)
        {
            if (menu == IntPtr.Zero)
                return;
            var count = MsgSend_RetLong(menu, Sel("numberOfItems"));
            for (long i = 0; i < count; i++)
            {
                var item = MsgSend_Long(menu, Sel("itemAtIndex:"), i);
                if (MsgSend_RetBool(item, Sel("isSeparatorItem")) != 0)
                    continue;
                var action = MsgSend(item, Sel("action"));
                var actionName = action == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(sel_getName(action));
                var hidden = MsgSend_RetBool(item, Sel("isHidden")) != 0 ? " (hidden)" : "";
                lines.Add($"{indent}{ManagedString(MsgSend(item, Sel("title")))} [{actionName}]{hidden}");
                if (indent.Length < 4)
                    Walk(MsgSend(item, Sel("submenu")), indent + "  ");
            }
        }
    }

    /// <summary>
    /// Tells AppKit which of the installed menu bar's submenus is the Window menu
    /// (<c>NSApp.windowsMenu</c>), found by its title. Without it AppKit has nowhere to put
    /// Move Tab to New Window and Merge All Windows — measured on the Mac mini: the two
    /// items, and the window list, simply never appear — because Avalonia builds the menu
    /// bar in code and never names one. Each window has its own menu bar (they are built
    /// per window), so this is said again whenever a window becomes key.
    /// </summary>
    internal static bool DesignateWindowsMenu(string title)
    {
        var app = SharedApplication();
        var main = app == IntPtr.Zero ? IntPtr.Zero : MsgSend(app, Sel("mainMenu"));
        if (main == IntPtr.Zero)
            return false;
        var count = MsgSend_RetLong(main, Sel("numberOfItems"));
        for (long i = 0; i < count; i++)
        {
            var item = MsgSend_Long(main, Sel("itemAtIndex:"), i);
            if (ManagedString(MsgSend(item, Sel("title"))) != title)
                continue;
            var submenu = MsgSend(item, Sel("submenu"));
            if (submenu == IntPtr.Zero)
                return false;
            if (MsgSend(app, Sel("windowsMenu")) != submenu)
                MsgSend_Void_Ptr(app, Sel("setWindowsMenu:"), submenu);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clicks the main menu bar's item titled <paramref name="title"/> as AppKit would —
    /// through the menu the system currently has installed, which is the question
    /// "is the menu bar the front window's?" turns on. False when no item has that title.
    /// </summary>
    internal static bool PerformMainMenuItem(string title)
    {
        var app = SharedApplication();
        var main = app == IntPtr.Zero ? IntPtr.Zero : MsgSend(app, Sel("mainMenu"));
        return main != IntPtr.Zero && Find(main);

        bool Find(IntPtr menu)
        {
            var count = MsgSend_RetLong(menu, Sel("numberOfItems"));
            for (long i = 0; i < count; i++)
            {
                var item = MsgSend_Long(menu, Sel("itemAtIndex:"), i);
                if (ManagedString(MsgSend(item, Sel("title"))) == title)
                {
                    MsgSend_Void_Long(menu, Sel("performActionForItemAtIndex:"), i);
                    return true;
                }
                var sub = MsgSend(item, Sel("submenu"));
                if (sub != IntPtr.Zero && Find(sub))
                    return true;
            }
            return false;
        }
    }
}
