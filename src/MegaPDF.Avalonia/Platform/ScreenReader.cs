using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MegaPDF.Avalonia.Platform;

/// <summary>
/// Whether a screen reader is running (#505).
///
/// Reading mode's floating pill fades after a couple of idle seconds, and that is
/// exactly the wrong behaviour for somebody driving the app by voice: "waggle the
/// mouse to get it back" is not an instruction VoiceOver or Orca can follow. So the
/// fade is suppressed while a reader is on, which means the app has to be able to ask.
///
/// There is no one answer on this stack. Avalonia's <c>AutomationPeer</c> has no
/// <c>ListenerExists()</c> — the WinUI call the plan names — and its accessibility
/// bridges differ per platform, so this asks each desktop the way that desktop is
/// asked:
///
/// * **macOS** — <c>NSWorkspace.sharedWorkspace.isVoiceOverEnabled</c>, the sanctioned
///   API, through the ObjC runtime (the same interop shape <see cref="MacLanguage"/>
///   uses for CoreFoundation). It answers correctly inside the App Sandbox, which
///   reading another application's preferences would not.
/// * **Linux** — GNOME's <c>org.gnome.desktop.a11y.applications screen-reader-enabled</c>,
///   which is the key Orca itself sets, read through <c>gsettings</c>; then the
///   accessibility environment a desktop sets for toolkits that predate it.
/// * **Anywhere, including CI and the rigs** — <c>MEGAPDF_SCREEN_READER=1</c>, which
///   is also how the self-test drives the "never fades with a reader on" check without
///   needing one installed.
///
/// Never throws and never blocks for long: the answer is cached for a few seconds, and
/// any failure is "no reader", because a pill that fades is a much smaller fault than
/// a window that will not come up.
/// </summary>
internal static class ScreenReader
{
    /// <summary>How long an answer is reused before the probe runs again.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(3);

    private static bool _cached;
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>
    /// Forces the answer, for the self-test. Null is the real probe. Set and cleared
    /// around a check rather than left on, so the rest of a run is unaffected.
    /// </summary>
    internal static Func<bool>? OverrideForTest;

    internal static bool IsRunning()
    {
        if (OverrideForTest is { } forced)
            return forced();

        var now = DateTime.UtcNow;
        if (now - _cachedAt < CacheFor)
            return _cached;

        _cached = Probe();
        _cachedAt = now;
        return _cached;
    }

    /// <summary>Drops the cached answer. For the self-test, between scenarios.</summary>
    internal static void ForgetForTest() => _cachedAt = DateTime.MinValue;

    private static bool Probe()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("MEGAPDF_SCREEN_READER") is { Length: > 0 } forced)
                return forced is "1" or "true" or "TRUE" or "yes";

            if (OperatingSystem.IsMacOS())
                return VoiceOverIsOn();

            if (OperatingSystem.IsLinux())
                return OrcaIsOn();
        }
        catch (Exception)
        {
            // Any failure means "assume not": see the class doc.
        }
        return false;
    }

    // --- macOS ---------------------------------------------------------------

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr GetSelector([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendForPointer(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendForBool(IntPtr receiver, IntPtr selector);

    /// <summary>
    /// <c>[[NSWorkspace sharedWorkspace] isVoiceOverEnabled]</c>. NSWorkspace is AppKit's,
    /// which an Avalonia app has loaded; a null class or a nil workspace is "no reader"
    /// rather than a crash, since this runs on a timer.
    /// </summary>
    private static bool VoiceOverIsOn()
    {
        var workspaceClass = GetClass("NSWorkspace");
        if (workspaceClass == IntPtr.Zero)
            return false;
        var shared = SendForPointer(workspaceClass, GetSelector("sharedWorkspace"));
        if (shared == IntPtr.Zero)
            return false;
        return SendForBool(shared, GetSelector("isVoiceOverEnabled"));
    }

    // --- Linux ---------------------------------------------------------------

    /// <summary>
    /// GNOME's own switch first — it is what Orca sets and what every GNOME-based
    /// desktop reads — then the accessibility environment variables a session exports
    /// for toolkits, which is the only signal on desktops with no gsettings schema.
    /// </summary>
    private static bool OrcaIsOn()
    {
        if (ReadGSetting("org.gnome.desktop.a11y.applications", "screen-reader-enabled") is { } value)
            return value.Trim() is "true";

        foreach (var name in new[] { "ACCESSIBILITY_ENABLED", "GNOME_ACCESSIBILITY", "QT_ACCESSIBILITY" })
        {
            if (Environment.GetEnvironmentVariable(name) is "1" or "true")
                return true;
        }
        return false;
    }

    /// <summary>
    /// One gsettings read, or null when there is no gsettings, no schema or no answer
    /// within a second. Bounded because this is on the pill's idle path: a desktop that
    /// hangs must not hang the window with it.
    /// </summary>
    private static string? ReadGSetting(string schema, string key)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gsettings")
            {
                ArgumentList = { "get", schema, key },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1000))
                return null;
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
