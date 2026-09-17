using System.Runtime.Versioning;

namespace MegaPDF.Avalonia.Platform;

/// <summary>
/// Where MegaPDF puts its own temporary files on Linux (#193, #158).
///
/// Saving stages a whole second copy of the document in
/// <see cref="Path.GetTempPath"/> and verifies it there before the destination is
/// touched — that is what makes a failed save leave the original alone (#145 D2).
/// On Windows <c>%TEMP%</c> and on macOS <c>$TMPDIR</c> are both on disk, so the
/// copy costs disk and nothing else.
///
/// On Linux <c>Path.GetTempPath()</c> is <c>$TMPDIR</c> or <c>/tmp</c>, and on
/// Fedora — one of the two distributions #158 targets — <c>/tmp</c> is a tmpfs
/// sized at half of RAM. Saving a 2.5 GB document there is a 2.5 GB allocation of
/// *memory*, which is exactly the cost #147 and #148 took out of opening one; on a
/// small machine it fails outright, with 67 GB free where the person was saving to
/// and a message that says only "Writing the PDF failed".
///
/// So the app points its own temporary directory at the XDG cache directory,
/// which freedesktop defines for exactly this — large regenerable data — and which
/// is on the same filesystem as the person's documents on both target
/// distributions.
///
/// **The save this was written for no longer comes here.** #193 landed the real
/// fix in MegaPDF.Core's VerifiedSave, shared with Windows, macOS, iOS and Android:
/// a save with a destination path stages beside that destination, which is on the
/// right filesystem by definition and does not depend on where the home directory
/// is. On Linux that is every Save and Save As, which reach a real path and go
/// through VerifiedSave.ToPath.
///
/// This stays for everything else the app writes to a temporary file — including
/// VerifiedSave's own fallback, for a destination folder that will not take the
/// staged copy, and ToStagedFile, which has no destination folder to be beside.
/// None of them should land on a tmpfs sized at half of RAM either.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxPaths
{
    /// <summary>
    /// Prepares the per-user directories the app writes to: the XDG data
    /// directory, which must exist before .NET is asked where it is, and TMPDIR,
    /// pointed at <c>$XDG_CACHE_HOME/MegaPDF/tmp</c>. Call before anything reads a
    /// setting or writes a temporary file. Never throws: a directory that cannot
    /// be made is not a reason to refuse to start.
    /// </summary>
    internal static void PrepareUserDirectories()
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            EnsureDataDirectory();
        }
        catch (Exception)
        {
            // Nothing here is worth refusing to start over; the app will report
            // whatever it cannot write when it tries.
        }

        try
        {
            // An explicit TMPDIR is someone saying where they want it — a script, a
            // sandbox, a machine with one big scratch disk. Honoured rather than
            // overridden; the default is the case this is about.
            if (Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 })
                return;

            var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrEmpty(cache))
            {
                var home = Environment.GetEnvironmentVariable("HOME");
                if (string.IsNullOrEmpty(home))
                    return;
                cache = Path.Combine(home, ".cache");
            }

            var directory = Path.Combine(cache, "MegaPDF", "tmp");
            Directory.CreateDirectory(directory);

            // /tmp is cleared for us between boots; a cache directory is not, so a
            // file left behind by a crash mid-save would sit there for ever. Anything
            // older than a day cannot belong to a running save.
            SweepStaleFiles(directory);

            Environment.SetEnvironmentVariable("TMPDIR", directory);
        }
        catch (Exception)
        {
            // Unwritable home, read-only cache, a race with another copy of the app:
            // none of them is worth refusing to start over. .NET's own default stands.
        }
    }

    /// <summary>
    /// Makes sure the XDG data directory exists before .NET is asked for it.
    ///
    /// <c>Environment.GetFolderPath(LocalApplicationData)</c> returns the **empty
    /// string** when the directory it resolves to is not there — that is its
    /// documented behaviour for <c>SpecialFolderOption.None</c>. Every service that
    /// keeps user data (settings, the signature library, recent documents, the
    /// recovery journal) then builds a *relative* path from it, and the first one to
    /// call Directory.CreateDirectory hits a file of that name in the working
    /// directory instead. The app dies before its window with an unhandled
    /// IOException: "The file '.../MegaPDF' already exists" — naming the apphost.
    ///
    /// Windows and macOS never see it: %LOCALAPPDATA% and ~/Library/Application
    /// Support are always there. On Linux ~/.local/share is not guaranteed on an
    /// account that has never run a desktop application, which is exactly what a
    /// fresh container, a server login or a new user is. The general fix belongs in
    /// MegaPDF.Core, where the empty string should never become a relative path;
    /// creating the directory first is what stops it happening here (#195).
    /// </summary>
    private static void EnsureDataDirectory()
    {
        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(data))
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home))
                return;
            data = Path.Combine(home, ".local", "share");
        }
        Directory.CreateDirectory(data);
    }

    private static void SweepStaleFiles(string directory)
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch (IOException)
            {
                // In use by another copy of the app, or gone already.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
