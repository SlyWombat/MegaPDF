using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using MegaPDF.Avalonia.ViewModels;
using MegaPDF.Avalonia.Views;
using MegaPDF.Core.Engine;
using SkiaSharp;

namespace MegaPDF.Avalonia.Platform;

/// <summary>
/// <c>--window-tabs-check &lt;out-dir&gt; a.pdf b.pdf c.pdf</c> (#711): native macOS window tabs,
/// in a real process on a real Mac, through the person's own route — the same files handed
/// over the way Finder hands them, the same menu bar AppKit installs, the same Window-menu
/// commands a person would choose.
///
/// The headless self-test cannot see any of this: on the headless platform there is no
/// <c>NSWindow</c>, so no tab group, no AppKit menu items and nothing drawn by Metal. This is
/// the check that ran the spike on the Mac mini, kept so the evidence can be had again.
///
/// Screen recording is blocked by TCC over SSH, which is where the Mac mini is driven from, so
/// "is it still drawing" is answered by this process capturing its own windows
/// (<c>CGWindowListCreateImage</c> on a window it owns needs no permission) and counting the
/// colours in what came back: a window that stopped drawing comes back one flat colour, or not
/// at all. The captures are written to the output directory for a person to look at.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacWindowTabsCheck
{
    private const string Objc = "/usr/lib/libobjc.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X, Y;
    }

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGWindowListCreateImage(CGRect bounds, uint listOption, uint windowId, uint imageOption);

    [DllImport(CoreGraphics)] private static extern nuint CGImageGetWidth(IntPtr image);
    [DllImport(CoreGraphics)] private static extern nuint CGImageGetHeight(IntPtr image);
    [DllImport(CoreGraphics)] private static extern nuint CGImageGetBytesPerRow(IntPtr image);
    [DllImport(CoreGraphics)] private static extern IntPtr CGImageGetDataProvider(IntPtr image);
    [DllImport(CoreGraphics)] private static extern IntPtr CGDataProviderCopyData(IntPtr provider);
    [DllImport(CoreGraphics)] private static extern void CGImageRelease(IntPtr image);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern byte MsgSend_PopUp(IntPtr receiver, IntPtr selector, IntPtr item, CGPoint at, IntPtr view);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_PerformOnMain(IntPtr receiver, IntPtr selector, IntPtr aSelector, IntPtr arg, byte wait);

    [DllImport(Objc)]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Objc)]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    private static readonly List<string> Report = [];
    private static int _failures;

    private static void Log(string line)
    {
        Console.WriteLine(line);
        Report.Add(line);
    }

    private static void Check(string what, bool ok)
    {
        Log($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok)
            _failures++;
    }

    private static string Name(MainWindow w) => Path.GetFileName(w.Active?.DocumentPath ?? "(empty)");

    private static void Describe(string label, IEnumerable<MainWindow> windows)
    {
        Log($"{label} (MegaPDF active: {MacWindowTabs.IsAppActive()}):");
        foreach (var w in windows)
            Log($"  {Name(w)}: tabs={MacWindowTabs.TabbedWindows(w).Count} "
                + $"selected={MacWindowTabs.SelectedTab(w) == MacWindowTabs.NativeWindow(w)} key={MacWindowTabs.IsKey(w)} "
                + $"active={w.IsActive} tab-title=\"{MacWindowTabs.TabTitle(w)}\" client={w.ClientSize.Width:F0}x{w.ClientSize.Height:F0}");
    }

    /// <summary>Captures one of this app's own windows; the distinct colours in a coarse sample, or -1.</summary>
    private static int Capture(Window window, string outDir, string name)
    {
        var image = CGWindowListCreateImage(new CGRect { X = double.PositiveInfinity, Y = double.PositiveInfinity },
                                            8 /* IncludingWindow */, (uint)MacWindowTabs.WindowNumber(window),
                                            1 /* BoundsIgnoreFraming */);
        if (image == IntPtr.Zero)
        {
            Log($"  capture {name}: nothing came back");
            return -1;
        }
        var width = (int)CGImageGetWidth(image);
        var height = (int)CGImageGetHeight(image);
        var stride = (int)CGImageGetBytesPerRow(image);
        var data = CGDataProviderCopyData(CGImageGetDataProvider(image));
        var bytes = new byte[CFDataGetLength(data)];
        Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length);
        CFRelease(data);
        CGImageRelease(image);

        var colours = new HashSet<int>();
        for (var y = 0; y < height; y += 7)
            for (var x = 0; x < width; x += 7)
            {
                var o = y * stride + x * 4;
                colours.Add(bytes[o] | bytes[o + 1] << 8 | bytes[o + 2] << 16);
            }
        using (var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(bytes, y * stride, bitmap.GetPixels() + y * bitmap.RowBytes, width * 4);
            using var file = File.Create(Path.Combine(outDir, name + ".png"));
            bitmap.Encode(file, SKEncodedImageFormat.Png, 100);
        }
        Log($"  capture {name}: {width}x{height}, {colours.Count} colours");
        return colours.Count;
    }

    /// <summary>
    /// Asks AppKit to bring the menu bar's submenu titled <paramref name="title"/> up to date,
    /// which is when it adds its own items (the tab commands, Show All Tabs) — measured: they
    /// are not in the menu until it is about to show. Asked by popping the menu up, which is
    /// refused without a session in front, but not before AppKit has populated it. A
    /// background thread cancels the tracking in case it ever does show.
    /// </summary>
    private static void PopulateMenu(string title)
    {

        var app = MsgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        var main = MsgSend(app, sel_registerName("mainMenu"));
        var item = MsgSend_Title(main, title);
        if (item == IntPtr.Zero)
            return;
        var submenu = MsgSend(item, sel_registerName("submenu"));
        var cancel = new Thread(() =>
        {
            Thread.Sleep(1000);
            MsgSend_PerformOnMain(submenu, sel_registerName("performSelectorOnMainThread:withObject:waitUntilDone:"),
                                  sel_registerName("cancelTracking"), IntPtr.Zero, 0);
        }) { IsBackground = true };
        cancel.Start();
        MsgSend_PopUp(submenu, sel_registerName("popUpMenuPositioningItem:atLocation:inView:"),
                      IntPtr.Zero, new CGPoint { X = 300, Y = 900 }, IntPtr.Zero);
    }

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_Ptr(IntPtr receiver, IntPtr selector, IntPtr arg);

    private static IntPtr MsgSend_Title(IntPtr menu, string title)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(title);
        try
        {
            var nsTitle = MsgSend_Ptr(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8);
            return MsgSend_Ptr(menu, sel_registerName("itemWithTitle:"), nsTitle);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private static MainWindow WindowOf(string pdf) =>
        MainWindow.OpenWindows.First(w => w.Active?.DocumentPath is { } p && Core.Recovery.LaunchedDocument.SameFile(p, pdf));

    internal static async Task<int> RunAsync(MainWindow first, string outDir, IReadOnlyList<string> pdfs)
    {
        Directory.CreateDirectory(outDir);
        try
        {
            Log($"window-tabs-check: {string.Join(", ", pdfs.Select(Path.GetFileName))}");
            if (pdfs.Count < 3)
            {
                Check("three PDFs were given", false);
                return Finish(outDir);
            }


            // (a) Three documents handed over the person's way: each its own window, one tab group.
            App.RouteExternalPaths(first, pdfs);
            var opened = await WaitFor(() => MainWindow.OpenWindows.Count(w => w.Active is { IsDocumentOpen: true }) == 3,
                                       TimeSpan.FromSeconds(15));
            await Task.Delay(1500);
            var windows = MainWindow.OpenWindows.ToList();
            Describe("after opening three", windows);
            Check("three documents opened", opened);
            Check("  one window each (the Mac's shape)", windows.Count == 3 && windows.All(w => w.Shell is { Documents.Count: 1 }));
            var activations = 0;
            foreach (var w in windows)
                w.Activated += (_, _) => activations++;
            // No activation storm (#711): idle, the windows stay put. Before BringToFront
            // coalesced its requests, a cold launch's duplicate open-file events left AppKit
            // cycling through the tabs about 140 times a second.
            activations = 0;
            await Task.Delay(2000);
            Check($"idle for two seconds, no window is activated ({activations} activation(s))", activations == 0);

            var a = WindowOf(pdfs[0]);
            var b = WindowOf(pdfs[1]);
            var c = WindowOf(pdfs[2]);
            Check("  merged by AppKit into one window of three tabs",
                  MacWindowTabs.TabbedWindows(a).Count == 3 && MacWindowTabs.AreTabbedTogether(a, b) && MacWindowTabs.AreTabbedTogether(a, c));
            Check("  the last one handed over is the selected tab, and active", MacWindowTabs.SelectedTab(a) == MacWindowTabs.NativeWindow(c) && c.IsActive);
            Check("  no in-window tab strip", windows.All(w => !w.DocumentTabStrip.IsVisible));
            Check("  each tab is titled with its document's name alone",
                  windows.All(w => MacWindowTabs.TabTitle(w) == w.Active?.TabTitle));
            Check("  the front tab draws", Capture(c, outDir, "1-three-tabs") > 20);

            // (c) The menu bar: AppKit's own tab commands, in the front window's Window menu.
            //
            // Read the way AppKit fills it for the menu bar. A pop-up that really shows is a
            // context menu, and AppKit leaves the window-tab commands out of those (measured:
            // the tiling items come, the tab items do not). With MegaPDF not the active app —
            // how a binary started over SSH comes up — the pop-up is refused, but only after
            // AppKit has filled the menu for the menu bar, which is what is read here. With
            // MegaPDF active, this reading is not possible, and the check says so rather than
            // fail on a property of the probe.
            var readable = !MacWindowTabs.IsAppActive();
            Log($"MegaPDF is the active app: {!readable}");
            PopulateMenu(Strings.MenuWindow);
            PopulateMenu(Strings.MenuView);
            await Task.Delay(300);
            var menu = MacWindowTabs.DescribeMainMenu();
            File.WriteAllLines(Path.Combine(outDir, "menu.txt"), menu);
            if (!readable)
                Log("  [SKIP] AppKit's tab commands in the menu bar: MegaPDF is the active app, so the menu cannot be read as the menu bar shows it");
            else
            {
            Check($"NSApp.windowsMenu is the Window menu of the menu bar on screen ({menu[0]})",
                  menu[0] == $"NSApp.windowsMenu = {Strings.MenuWindow}");
            foreach (var action in new[] { "moveTabToNewWindow:", "mergeAllWindows:", "selectNextTab:", "selectPreviousTab:", "toggleTabOverview:" })
                Check($"  AppKit's [{action}] item is in the menu bar", menu.Any(l => l.Contains($"[{action}]", StringComparison.Ordinal)));
            Check("  and MegaPDF's own Show Next Tab is not, beside it",
                  menu.Count(l => l.Contains(Strings.ShowNextTab, StringComparison.Ordinal)) == 1);
            }

            var zooms = (a.Active!.Zoom, b.Active!.Zoom, c.Active!.Zoom);
            MacWindowTabs.PerformMainMenuItem(Strings.ZoomIn);
            await Task.Delay(300);
            Check($"View ▸ Zoom In acts on the front tab only (A {a.Active.Zoom:F2}, B {b.Active.Zoom:F2}, C {c.Active.Zoom:F2})",
                  c.Active.Zoom > zooms.Item3 && a.Active.Zoom == zooms.Item1 && b.Active.Zoom == zooms.Item2);
            a.ShowDocument(a.Active!);
            await Task.Delay(500);
            Describe("after choosing A's tab", MainWindow.OpenWindows);
            MacWindowTabs.PerformMainMenuItem(Strings.ZoomIn);
            await Task.Delay(300);
            Check($"  and on the other tab once it is chosen (A {a.Active.Zoom:F2}, B {b.Active.Zoom:F2}, C {c.Active.Zoom:F2})",
                  MacWindowTabs.SelectedTab(a) == MacWindowTabs.NativeWindow(a) && a.Active.Zoom > zooms.Item1);
            if (!MacWindowTabs.IsAppActive())
            {
                PopulateMenu(Strings.MenuWindow);
                await Task.Delay(300);
                Check("  whose own Window menu has Move Tab to New Window too",
                      MacWindowTabs.DescribeMainMenu().Any(l => l.Contains("[moveTabToNewWindow:]", StringComparison.Ordinal)));
            }

            // A document already open is brought forward, not opened again.
            first = MainWindow.MostRecentlyActive!;
            first.OpenFromSystem(pdfs[1]);
            await Task.Delay(800);
            Describe("after opening B again", MainWindow.OpenWindows);
            Check("opening a document already open selects its tab, and opens nothing",
                  MainWindow.OpenWindows.Count == 3 && MacWindowTabs.SelectedTab(a) == MacWindowTabs.NativeWindow(b) && b.IsActive);

            // (b) An edit, then the tab moved out the way the Window menu (or a drag) moves it.
            b.Active.HandlePageClick(0, new PdfPoint(78, 186));
            await WaitFor(() => b.Active.IsDirty, TimeSpan.FromSeconds(3));
            var moved = b.Active;
            Check("B has an unsaved edit and an undo step", moved.IsDirty && moved.CanUndo);
            MacWindowTabs.Perform(b, "moveTabToNewWindow:");
            await Task.Delay(1000);
            Describe("after Move Tab to New Window on B", MainWindow.OpenWindows);
            Check("B is a window of its own", !MacWindowTabs.AreTabbedTogether(a, b) && MacWindowTabs.TabbedWindows(a).Count == 2);
            Check("  with the same document, still unsaved and undoable", ReferenceEquals(b.Active, moved) && moved.IsDirty && moved.CanUndo);
            Check("  and it draws", Capture(b, outDir, "2-moved-out") > 20);
            var left = MacWindowTabs.SelectedTab(a) == MacWindowTabs.NativeWindow(a) ? a : c;
            Check("the window it left draws", Capture(left, outDir, "2-left-behind") > 20);
            Check("  ⌘W reads Close on the lone window and Close Tab on the tabbed one",
                  !b.IsInNativeTabGroup && a.IsInNativeTabGroup);

            MacWindowTabs.Perform(b, "mergeAllWindows:");
            await Task.Delay(1000);
            Describe("after Merge All Windows", MainWindow.OpenWindows);
            Check("Merge All Windows makes them three tabs again", MacWindowTabs.TabbedWindows(a).Count == 3 && MacWindowTabs.AreTabbedTogether(a, b));
            foreach (var w in new[] { a, b, c })
            {
                w.ShowDocument(w.Active!);
                await Task.Delay(500);
                Check($"  {Name(w)} draws when its tab is chosen", Capture(w, outDir, $"3-merged-{Name(w)}") > 20);
            }
            moved.UndoCommand.Execute(null);
            await Task.Delay(300);
            Check("undo after the round trip undoes B's edit", !moved.CanUndo);

            // Close Window (⇧⌘W) asks every tab first; a Cancel keeps every one.
            moved.RedoCommand.Execute(null);
            await Task.Delay(300);
            foreach (var w in MainWindow.OpenWindows)
                w.AnswerUnsavedChangesForTest = () => UnsavedChangesWindow.Decision.Cancel;
            a.ShowDocument(a.Active!);
            await Task.Delay(300);
            a.CloseWindow();
            await Task.Delay(800);
            Check("Close Window with an unsaved tab, answered Cancel, closes none of them",
                  MainWindow.OpenWindows.Count == 3 && b.UnsavedChangesAsked == 1);

            // ⌘W on a clean tab closes that tab alone.
            c.ShowDocument(c.Active!);
            await Task.Delay(300);
            await c.CloseActiveTabOrWindowAsync();
            await Task.Delay(800);
            Check("⌘W on a clean tab closes that tab and leaves the other two tabbed",
                  MainWindow.OpenWindows.Count == 2 && MacWindowTabs.AreTabbedTogether(a, b));
            var front = MacWindowTabs.SelectedTab(a) == MacWindowTabs.NativeWindow(a) ? a : b;
            Check("  and the tab chosen after it draws", Capture(front, outDir, "4-after-close") > 20);
        }
        catch (Exception ex)
        {
            Log($"::error::window-tabs-check: {ex}");
            _failures++;
        }
        foreach (var w in MainWindow.OpenWindows.ToList())
            w.SkipCloseConfirmation();
        return Finish(outDir);
    }

    private static int Finish(string outDir)
    {
        Log(_failures == 0 ? "window-tabs-check: PASS" : $"::error::window-tabs-check: {_failures} check(s) failed");
        File.WriteAllLines(Path.Combine(outDir, "report.txt"), Report);
        return _failures == 0 ? 0 : 1;
    }
}
