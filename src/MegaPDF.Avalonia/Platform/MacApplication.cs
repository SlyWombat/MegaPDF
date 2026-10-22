using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Media;

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
/// uses for the print panel. And one question put to AppKit that Avalonia does not
/// answer: the colour selected text uses (<see cref="SelectedTextBackgroundColor"/>, #710).
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

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_Ptr_Ptr(IntPtr receiver, IntPtr selector, IntPtr arg);

    // A CGFloat is a double on every 64-bit Mac. A double comes back in a floating-point
    // register on arm64 and on x86-64 alike, through plain objc_msgSend (only a long double
    // needs the _fpret entry point), so the one declaration serves both builds.
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern double MsgSend_Double(IntPtr receiver, IntPtr selector);

    // An NSInteger argument is a 64-bit integer in a general register on arm64 and x86-64 alike.
    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_Ptr_Long(IntPtr receiver, IntPtr selector, long arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void MsgSend_Void_Ptr_Ptr(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    [DllImport(Objc)]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(Objc)]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);

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

    /// <summary>
    /// The colour AppKit gives selected text (<c>NSColor.selectedTextBackgroundColor</c>), in sRGB,
    /// opaque (#710). It follows System Settings ▸ Appearance ▸ Highlight colour, which is the
    /// Accent colour only while Highlight is left at "Accent colour": Preview's selection uses it,
    /// so the app's selection must too. It is a dynamic colour, so it is resolved for the
    /// appearance in force when this is called. Null if AppKit cannot say.
    /// </summary>
    internal static Color? SelectedTextBackgroundColor()
    {
        try
        {
            var nsColor = objc_getClass("NSColor");
            var nsColorSpace = objc_getClass("NSColorSpace");
            if (nsColor == IntPtr.Zero || nsColorSpace == IntPtr.Zero)
                return null;
            var selected = MsgSend(nsColor, sel_registerName("selectedTextBackgroundColor"));
            var srgbSpace = MsgSend(nsColorSpace, sel_registerName("sRGBColorSpace"));
            if (selected == IntPtr.Zero || srgbSpace == IntPtr.Zero)
                return null;
            // Some catalogue colours cannot be converted; the answer is then nil, not a wrong colour.
            var srgb = MsgSend_Ptr_Ptr(selected, sel_registerName("colorUsingColorSpace:"), srgbSpace);
            if (srgb == IntPtr.Zero)
                return null;
            byte Channel(string component) =>
                (byte)Math.Round(Math.Clamp(MsgSend_Double(srgb, sel_registerName(component)), 0, 1) * 255);
            return Color.FromRgb(Channel("redComponent"), Channel("greenComponent"), Channel("blueComponent"));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    // void NSAccessibilityPostNotificationWithUserInfo(id element, NSAccessibilityNotificationName
    // notification, NSDictionary *userInfo): three pointers, nothing returned, so there is no
    // CGFloat and no struct return for arm64 and x86-64 to disagree about.
    [DllImport(AppKit)]
    private static extern void NSAccessibilityPostNotificationWithUserInfo(IntPtr element, IntPtr notification, IntPtr userInfo);

    /// <summary>NSAccessibilityPriorityHigh (<c>NSAccessibilityPriorityLevel</c>: Low 10, Medium 50, High 90).</summary>
    private const long AccessibilityPriorityHigh = 90;

    /// <summary>
    /// Reads an <c>NSString *const</c> that AppKit exports, by name. dlsym gives the address of the
    /// pointer, so the NSString is what lives there. The constants' text is AppKit's to say, not ours.
    /// </summary>
    private static IntPtr AppKitString(IntPtr appKit, string symbol) =>
        NativeLibrary.TryGetExport(appKit, symbol, out var address) ? Marshal.ReadIntPtr(address) : IntPtr.Zero;

    private static IntPtr NSStringFrom(string text)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            return MsgSend_Ptr_Ptr(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    /// <summary>
    /// Has VoiceOver say <paramref name="text"/> (#778): posts
    /// <c>NSAccessibilityAnnouncementRequestedNotification</c> with the text and a high priority
    /// from the main window, which is how an AppKit app speaks without moving focus.
    /// Avalonia's <c>AutomationProperties.LiveSetting</c> reaches Windows UI Automation and nothing
    /// on the Mac (its native layer posts no live-region or announcement notification), so a live
    /// region alone was silent here: "Selected: ...", "Copied" and reading mode's announcement were
    /// in the accessibility tree and never spoken.
    /// High priority is deliberate: a later announcement, such as "Copied" straight after
    /// "Selected: ...", interrupts the earlier one rather than queueing behind it.
    /// Returns false, and says nothing, when there is no text or AppKit cannot do it.
    /// </summary>
    internal static bool PostAnnouncement(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        var pool = IntPtr.Zero;
        try
        {
            var appKit = NativeLibrary.Load(AppKit);
            var notification = AppKitString(appKit, "NSAccessibilityAnnouncementRequestedNotification");
            var textKey = AppKitString(appKit, "NSAccessibilityAnnouncementKey");
            var priorityKey = AppKitString(appKit, "NSAccessibilityPriorityKey");
            if (notification == IntPtr.Zero || textKey == IntPtr.Zero || priorityKey == IntPtr.Zero)
                return false;
            var app = SharedApplication();
            if (app == IntPtr.Zero)
                return false;

            pool = objc_autoreleasePoolPush();
            // The main window if there is one, else the key window, else the application itself.
            var element = MsgSend(app, sel_registerName("mainWindow"));
            if (element == IntPtr.Zero)
                element = MsgSend(app, sel_registerName("keyWindow"));
            if (element == IntPtr.Zero)
                element = app;

            var userInfo = MsgSend(objc_getClass("NSMutableDictionary"), sel_registerName("dictionary"));
            var setObject = sel_registerName("setObject:forKey:");
            MsgSend_Void_Ptr_Ptr(userInfo, setObject, NSStringFrom(text), textKey);
            MsgSend_Void_Ptr_Ptr(
                userInfo, setObject,
                MsgSend_Ptr_Long(objc_getClass("NSNumber"), sel_registerName("numberWithInteger:"), AccessibilityPriorityHigh),
                priorityKey);
            NSAccessibilityPostNotificationWithUserInfo(element, notification, userInfo);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (pool != IntPtr.Zero)
                objc_autoreleasePoolPop(pool);
        }
    }
}
