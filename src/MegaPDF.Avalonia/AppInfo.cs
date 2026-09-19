using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Platform;
using MegaPDF.Core.Services;

namespace MegaPDF.Avalonia;

/// <summary>
/// What the About window says about this build, and where its third-party
/// notices come from (#176).
///
/// The Mac shipped with Avalonia's own "About Avalonia" in the first slot of the
/// first menu, no version, no copyright and — the part a licence actually
/// requires — no notices file anywhere in the bundle. The facts the window needs
/// live here rather than in the view so the self-test can assert them without a
/// window, and so the notices are loaded the same way wherever the app runs.
/// </summary>
internal static partial class AppInfo
{
    internal const string ProjectUrl = "https://github.com/SlyWombat/MegaPDF";

    internal const string NoticesFileName = "THIRD-PARTY-NOTICES.txt";

    /// <summary>
    /// The copy inside the assembly (src/MegaPDF.Avalonia/Assets), which is the
    /// same generated file the bundle carries. It is what a `dotnet run` on any
    /// platform shows, and the fallback if a bundle is ever built without one.
    /// </summary>
    private static readonly Uri EmbeddedNotices = new($"avares://MegaPDF/Assets/{NoticesFileName}");

    /// <summary>
    /// MegaPDF.app/Contents/Resources/THIRD-PARTY-NOTICES.txt, or null when the app
    /// is not running from a bundle. Contents/MacOS is where the executable sits.
    /// </summary>
    internal static string? BundledNoticesPath
    {
        get
        {
            var macOs = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.Equals(macOs.Name, "MacOS", StringComparison.Ordinal) || macOs.Parent is not { } contents)
                return null;
            var path = Path.Combine(contents.FullName, "Resources", NoticesFileName);
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// "Version 1.7.0", or "Version 1.7.0 (42)" once the two differ. A build number
    /// that only repeats the version is noise, which is why iOS's About drops it too.
    /// </summary>
    internal static string VersionLabel
    {
        get
        {
            var (version, build) = BundleVersions() ?? (AssemblyVersion(), null);
            return build is null || build == version
                ? Strings.VersionOnly(version)
                : Strings.VersionWithBuild(version, build);
        }
    }

    /// <summary>
    /// Where this Linux copy's updates come from, for the About window (#158), or null
    /// where there is nothing to say: the Mac (the App Store updates it) and a
    /// developer's build.
    /// </summary>
    internal static string? UpdatesLine(LinuxInstallKind kind) => kind switch
    {
        LinuxInstallKind.AptRepository => Strings.UpdatesFromApt,
        LinuxInstallKind.Snap => Strings.UpdatesFromSnap,
        LinuxInstallKind.Flatpak => Strings.UpdatesFromFlatpak,
        LinuxInstallKind.DebFile => Strings.UpdatesFromDebFile,
        LinuxInstallKind.Tarball => Strings.UpdatesFromTarball,
        _ => null,
    };

    private static string AssemblyVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // The SDK appends "+<commit sha>" to the informational version; the About
        // window wants the version, not the build metadata.
        if (informational is { Length: > 0 })
            return informational.Split('+')[0];
        return assembly.GetName().Version?.ToString(3) ?? "—";
    }

    /// <summary>
    /// The versions macOS itself shows for this app, read from the bundle's
    /// Info.plist. Deliberately not a plist parser: two keys of a file this build
    /// writes itself (tools/build-macos-app.sh) do not warrant one.
    /// </summary>
    private static (string Version, string? Build)? BundleVersions()
    {
        var macOs = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (!string.Equals(macOs.Name, "MacOS", StringComparison.Ordinal) || macOs.Parent is not { } contents)
            return null;
        var plist = Path.Combine(contents.FullName, "Info.plist");
        if (!File.Exists(plist))
            return null;
        try
        {
            var xml = File.ReadAllText(plist);
            var version = PlistString(xml, "CFBundleShortVersionString");
            return version is null ? null : (version, PlistString(xml, "CFBundleVersion"));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? PlistString(string xml, string key)
    {
        var match = Regex.Match(
            xml,
            $"<key>{Regex.Escape(key)}</key>\\s*<string>([^<]*)</string>",
            RegexOptions.None,
            TimeSpan.FromSeconds(1));
        return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The notices text: the bundle's copy first, because that is the one the
    /// licences require to travel with the binary and the one a person could
    /// inspect on disk — so what the window shows is what shipped. Reads off the
    /// UI thread; the file is around a third of a megabyte.
    /// </summary>
    internal static async Task<string> LoadNoticesAsync() =>
        await Task.Run(LoadNotices).ConfigureAwait(true);

    internal static string LoadNotices()
    {
        try
        {
            if (BundledNoticesPath is { } path)
                return File.ReadAllText(path);
            using var stream = AssetLoader.Open(EmbeddedNotices);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or FileNotFoundException)
        {
            return Strings.NoticesMissing;
        }
    }

    /// <summary>The embedded copy alone, for the self-test's comparison against the bundle's.</summary>
    internal static string EmbeddedNoticesText()
    {
        using var stream = AssetLoader.Open(EmbeddedNotices);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The notices split on blank lines, so the window can virtualise them. A third
    /// of a megabyte in one TextBlock is a single text layout the size of the file,
    /// measured on the UI thread; Android's notices screen splits for the same reason
    /// (splitNoticeParagraphs in AboutDialog.kt).
    /// </summary>
    internal static IReadOnlyList<string> NoticeParagraphs(string text) =>
        ParagraphBreak().Split(text)
            .Select(p => p.TrimEnd())
            .Where(p => p.Trim().Length > 0)
            .ToList();

    [GeneratedRegex(@"\r?\n(?:[ \t]*\r?\n)+")]
    private static partial Regex ParagraphBreak();
}
