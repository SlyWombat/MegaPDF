using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MegaPDF.Avalonia.Platform;

/// <summary>
/// The language the person chose in System Settings › General › Language &amp;
/// Region, read through CoreFoundation (#91).
///
/// .NET on macOS derives <see cref="CultureInfo.CurrentUICulture"/> from the POSIX
/// locale environment (LANG and friends), which is not what a bundled .app is
/// launched with from the Dock or Finder — it is typically unset, so the app
/// would come up in English on a French Mac. <c>CFLocaleCopyPreferredLanguages</c>
/// is what AppKit itself consults, and its first element is the language the
/// rest of the desktop is running in.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacLanguage
{
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFLocaleCopyPreferredLanguages();

    [DllImport(CoreFoundation)]
    private static extern long CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr str, byte[] buffer, long bufferSize, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    private const uint KCFStringEncodingUTF8 = 0x08000100;

    /// <summary>
    /// The first preferred language as a BCP 47 tag ("fr-CA", "en-US"), or null if
    /// it could not be read. Never throws: the caller falls back to .NET's own
    /// default, and a missing preference must not stop the app launching.
    /// </summary>
    internal static string? PreferredLanguageTag()
    {
        if (!OperatingSystem.IsMacOS())
            return null;

        var languages = IntPtr.Zero;
        try
        {
            languages = CFLocaleCopyPreferredLanguages();
            if (languages == IntPtr.Zero || CFArrayGetCount(languages) == 0)
                return null;

            var first = CFArrayGetValueAtIndex(languages, 0);
            if (first == IntPtr.Zero)
                return null;

            // Language tags are short ASCII; 64 bytes is generous for
            // "zh-Hant-HK" and anything else CoreFoundation hands back.
            var buffer = new byte[64];
            if (!CFStringGetCString(first, buffer, buffer.Length, KCFStringEncodingUTF8))
                return null;

            var length = Array.IndexOf(buffer, (byte)0);
            var tag = System.Text.Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
            return string.IsNullOrWhiteSpace(tag) ? null : tag;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (languages != IntPtr.Zero)
                CFRelease(languages);
        }
    }
}
