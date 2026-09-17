using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MegaPDF.Avalonia.Platform;

/// <summary>
/// The three commands the macOS application menu carries that are AppKit's rather
/// than ours: Hide, Hide Others and Show All (#191).
///
/// Avalonia builds those items itself, with English titles compiled into the
/// framework, so a French run showed "About MegaPDF" above "Hide MegaPDF / Hide
/// Others / Show All / Quit" — the first menu, half translated. Its own titles
/// cannot be changed, and the interface that performs them,
/// <c>IAvnApplicationCommands</c>, is internal to Avalonia. The way out is to stop
/// Avalonia adding the items (<c>MacOSPlatformOptions.DisableDefaultApplicationMenuItems</c>)
/// and add our own, which then have to reach NSApplication for themselves.
///
/// That is all this is: three selectors on the shared application, in the same
/// hand-written interop style as <see cref="MacPrinter"/>, which the app already
/// uses for the print panel.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacApplication
{
    private const string Objc = "/usr/lib/libobjc.dylib";

    // One declaration per distinct signature, as in MacPrinter: objc_msgSend's ABI
    // varies by return and argument type, and reusing a declaration across shapes
    // is silent corruption on arm64 rather than an exception.
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_Void_Ptr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Objc)]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Objc)]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    /// <summary>[NSApplication sharedApplication], or zero if AppKit is not up yet.</summary>
    private static IntPtr SharedApplication()
    {
        var cls = objc_getClass("NSApplication");
        return cls == IntPtr.Zero ? IntPtr.Zero : MsgSend(cls, sel_registerName("sharedApplication"));
    }

    /// <summary>
    /// Sends a no-argument-but-sender action to NSApp. All three of these take a
    /// sender, and nil is what a menu item that is not a control passes.
    /// </summary>
    private static void Perform(string selector)
    {
        var app = SharedApplication();
        if (app == IntPtr.Zero)
            return;
        MsgSend_Void_Ptr(app, sel_registerName(selector), IntPtr.Zero);
    }

    /// <summary>Hides MegaPDF — the app menu's Hide, ⌘H.</summary>
    internal static void Hide() => Perform("hide:");

    /// <summary>Hides every other app — Hide Others, ⌥⌘H.</summary>
    internal static void HideOthers() => Perform("hideOtherApplications:");

    /// <summary>Brings everything hidden back — Show All.</summary>
    internal static void ShowAll() => Perform("unhideAllApplications:");
}
