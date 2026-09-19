namespace MegaPDF.Core.Services;

/// <summary>How this copy of the Linux app was installed, which decides where its updates come from.</summary>
public enum LinuxInstallKind
{
    /// <summary>Not Linux, or a build no package produced (a `dotnet run`). The About window says nothing.</summary>
    None,

    /// <summary>The .deb, from the MegaPDF APT repository: updates arrive with the system's.</summary>
    AptRepository,

    /// <summary>The .deb, installed from a downloaded file with no repository behind it: no updates arrive.</summary>
    DebFile,

    /// <summary>The Snap Store package, which snapd keeps up to date.</summary>
    Snap,

    /// <summary>The Flatpak, which Flatpak keeps up to date from whatever remote it came from.</summary>
    Flatpak,

    /// <summary>The release tarball, unpacked and installed by hand: nothing updates it.</summary>
    Tarball,
}

/// <summary>
/// Which Linux package this is (#158), so the About window can say where updates come
/// from.
///
/// MegaPDF opens no network connection of its own, on any platform, and says so in its
/// privacy policy and its listings. So it has no update check: on Linux the package
/// manager that installed it is the update mechanism, and for the two installs that have
/// none (a downloaded .deb and the tarball) the About window says so and points at the
/// download page, which the person's browser opens, not MegaPDF.
///
/// Snap and Flatpak announce themselves in the environment. The .deb and the tarball
/// cannot be told apart by where they sit — the tarball's install.sh may put the same
/// tree anywhere — so each build writes <see cref="MarkerFileName"/> beside the binary:
/// `tarball` from tools/build-linux-app.sh, rewritten to `deb` by tools/linux/build-deb.sh.
/// A tree with no marker is a developer's build.
/// </summary>
public static class LinuxInstall
{
    public const string MarkerFileName = "INSTALL-KIND";

    /// <summary>The deb822 source file the website tells people to install (website/megapdf/apt/megapdf.sources).</summary>
    public const string AptSourcesPath = "/etc/apt/sources.list.d/megapdf.sources";

    /// <summary>The page with every way to install MegaPDF on Linux.</summary>
    public const string DownloadPage = "https://electricrv.ca/megapdf/linux/";

    /// <summary>The decision, with the environment and the file system passed in so it can be tested anywhere.</summary>
    /// <param name="environment">Reads an environment variable; null or empty when unset.</param>
    /// <param name="fileExists">Whether an absolute path exists.</param>
    /// <param name="marker">The contents of the marker file beside the binary, or null if there is none.</param>
    public static LinuxInstallKind Detect(Func<string, string?> environment, Func<string, bool> fileExists, string? marker)
    {
        // snapd sets both for every app it runs. SNAP alone is also what a snapcraft
        // build environment sets, which is not a user's install.
        if (!string.IsNullOrEmpty(environment("SNAP")) && !string.IsNullOrEmpty(environment("SNAP_NAME")))
            return LinuxInstallKind.Snap;

        // FLATPAK_ID is set for every app flatpak runs; /.flatpak-info is the sandbox's own
        // record of itself, present even if the environment was scrubbed.
        if (!string.IsNullOrEmpty(environment("FLATPAK_ID")) || fileExists("/.flatpak-info"))
            return LinuxInstallKind.Flatpak;

        return marker?.Trim() switch
        {
            "deb" => fileExists(AptSourcesPath) ? LinuxInstallKind.AptRepository : LinuxInstallKind.DebFile,
            "tarball" => LinuxInstallKind.Tarball,
            _ => LinuxInstallKind.None,
        };
    }

    /// <summary>This process's install, read from its environment and the marker beside <paramref name="appDirectory"/>.</summary>
    public static LinuxInstallKind Current(string appDirectory)
    {
        if (!OperatingSystem.IsLinux())
            return LinuxInstallKind.None;
        string? marker = null;
        try
        {
            var path = Path.Combine(appDirectory, MarkerFileName);
            if (File.Exists(path))
                marker = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable is the same as absent: the About window just says nothing.
        }
        return Detect(Environment.GetEnvironmentVariable, File.Exists, marker);
    }

    /// <summary>Whether the About window should offer the download page: only where nothing else brings updates.</summary>
    public static bool NeedsDownloadPage(LinuxInstallKind kind) =>
        kind is LinuxInstallKind.DebFile or LinuxInstallKind.Tarball;
}
