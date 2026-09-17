using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MegaPDF.Core.Services;

namespace MegaPDF.Avalonia.Platform;

/// <summary>
/// What Finder calls a file or a folder (#165).
///
/// The recents list shows where each document lives, and on a Mac that means the
/// names Finder uses: "Téléchargements" rather than "Downloads" on a French
/// system, the account's own name rather than /Users/…, "iCloud Drive" rather than
/// com~apple~CloudDocs, and a file name without ".pdf" when the person has Finder
/// set to hide extensions.
///
/// All of that is one AppKit call — <c>displayNameAtPath:</c> — rather than a table
/// of folder names we would have to keep in step with macOS and translate
/// ourselves. Same hand-written interop as <see cref="MacPrinter"/> and
/// <see cref="MacApplication"/>.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacFileNames
{
    private const string Objc = "/usr/lib/libobjc.dylib";

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_Ptr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend_Utf8(IntPtr receiver, IntPtr selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MsgSend_Bool_PtrPtr(IntPtr receiver, IntPtr selector,
        IntPtr first, IntPtr second);

    [DllImport(Objc)]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Objc)]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    /// <summary>
    /// Wraps a C# string as an NSString. Marshalling the string directly into a
    /// selector that wants an NSString* passes a char*, which is not an object —
    /// the same trap MacPrinter documents.
    /// </summary>
    private static IntPtr NSString(string value)
    {
        var cls = objc_getClass("NSString");
        return cls == IntPtr.Zero
            ? IntPtr.Zero
            : MsgSend_Utf8(cls, sel_registerName("stringWithUTF8String:"), value);
    }

    private static string? FromNSString(IntPtr value)
    {
        if (value == IntPtr.Zero)
            return null;
        var utf8 = MsgSend(value, sel_registerName("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    /// <summary>
    /// What Finder calls the item at <paramref name="path"/>, or null when AppKit
    /// will not say — off macOS, or for a path this app cannot reach at all. Never
    /// throws: a name we cannot read falls back to the last path component, which is
    /// what the caller was showing before.
    /// </summary>
    internal static string? DisplayName(string path)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(path))
            return null;
        try
        {
            var cls = objc_getClass("NSFileManager");
            if (cls == IntPtr.Zero)
                return null;
            var manager = MsgSend(cls, sel_registerName("defaultManager"));
            var nsPath = NSString(path);
            if (manager == IntPtr.Zero || nsPath == IntPtr.Zero)
                return null;
            var name = FromNSString(MsgSend_Ptr(manager, sel_registerName("displayNameAtPath:"), nsPath));
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reveals a file in Finder, selected in its folder — the recents list's context
    /// menu (#165). False when Finder would not show it, which the caller says out
    /// loud rather than leaving a menu item that appears to do nothing.
    /// </summary>
    internal static bool RevealInFinder(string path)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(path))
            return false;
        try
        {
            var cls = objc_getClass("NSWorkspace");
            if (cls == IntPtr.Zero)
                return false;
            var workspace = MsgSend(cls, sel_registerName("sharedWorkspace"));
            var file = NSString(path);
            var root = NSString(Path.GetDirectoryName(path) ?? "");
            if (workspace == IntPtr.Zero || file == IntPtr.Zero || root == IntPtr.Zero)
                return false;
            return MsgSend_Bool_PtrPtr(workspace,
                sel_registerName("selectFile:inFileViewerRootedAtPath:"), file, root);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// The folders a recents line may start from, each under the name Finder gives
    /// it: the home folder, the standard folders inside it, iCloud Drive, and any
    /// mounted volume. Longest match wins, so a file in ~/Documents/Clients reads
    /// "Documents › Clients" and never "/Users/…".
    ///
    /// Built fresh per load of the list rather than cached: volumes come and go, and
    /// the list is ten rows once, on an empty window.
    /// </summary>
    internal static IReadOnlyList<NamedFolder> Places()
    {
        var places = new List<NamedFolder>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return places;

        void Add(string path)
        {
            if (!Directory.Exists(path))
                return;
            var name = DisplayName(path) ?? Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(name))
                places.Add(new NamedFolder(path, name));
        }

        // iCloud Drive first in the list for readability; the match is by length, so
        // order carries no meaning.
        Add(Path.Combine(home, "Library", "Mobile Documents", "com~apple~CloudDocs"));
        foreach (var folder in new[] { "Desktop", "Documents", "Downloads", "Movies", "Music", "Pictures", "Public" })
            Add(Path.Combine(home, folder));
        Add(home);

        try
        {
            foreach (var volume in Directory.EnumerateDirectories("/Volumes"))
                Add(volume);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No /Volumes, or not readable. Those paths simply keep their raw names.
        }
        return places;
    }
}
