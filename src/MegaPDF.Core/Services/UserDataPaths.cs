namespace MegaPDF.Core.Services;

/// <summary>
/// Thrown when there is nowhere to keep this user's own data — no folder the app can
/// find or create for its settings, signatures, recent documents and recovery journal
/// (#195). An <see cref="IOException"/>, because every caller that already handles a
/// failure to write user data handles one of those.
/// </summary>
public sealed class UserDataUnavailableException(string message, Exception? innerException = null)
    : IOException(message, innerException);

/// <summary>
/// Where this user's own MegaPDF data lives: settings, the signature library, recent
/// documents and the recovery journal (SDD §4.4).
///
/// It exists because <c>Environment.GetFolderPath(LocalApplicationData)</c> returns the
/// **empty string** when the directory it resolves to is not there — documented
/// behaviour for the default <see cref="Environment.SpecialFolderOption.None"/>, not a
/// bug. Every service used to build its own path straight off that call, so
/// <c>Path.Combine("", "MegaPDF", …)</c> gave a *relative* path and the first
/// <c>Directory.CreateDirectory</c> resolved it against the working directory. Beside
/// the app that directory holds a file called <c>MegaPDF</c> — the apphost — so the app
/// died before its window ever appeared with "The file '…/MegaPDF' already exists".
///
/// Windows and macOS never saw it: <c>%LOCALAPPDATA%</c> and
/// <c>~/Library/Application Support</c> are always there. On Linux it resolves to
/// <c>$XDG_DATA_HOME</c> or <c>~/.local/share</c>, which is not guaranteed on an account
/// that has never run a desktop application — a fresh container, a new user, a server
/// login. The same code was one missing directory away from the same crash anywhere.
///
/// The empty string never becomes a path here. What it becomes is a directory, or an
/// exception that says what is wrong.
/// </summary>
public static class UserDataPaths
{
    /// <summary>The app's own folder inside the user-data root, on every platform.</summary>
    public const string AppFolderName = "MegaPDF";

    /// <summary>
    /// <paramref name="parts"/> joined onto the app's folder (<see cref="AppFolder"/>: the
    /// one in the user-data root, or the one <see cref="OverrideVariable"/> names). The root,
    /// or the named folder, is created if it is not there. The parts themselves are not created — a caller that
    /// wants a directory still creates it, and a caller that wants a file still writes it.
    /// </summary>
    /// <exception cref="UserDataUnavailableException">
    /// There is no folder to keep user data in, or it could not be created.
    /// </exception>
    public static string InAppFolder(params string[] parts) =>
        Path.Combine([AppFolder(), .. parts]);

    /// <summary>
    /// The environment variable that moves the app's whole folder somewhere else (#717): set
    /// to an absolute path, that path *is* the folder settings, recents, signatures and the
    /// recovery journal are kept in, on every platform, for this process and every process it
    /// starts. Unset or empty, the folder is the ordinary one in the user-data root.
    ///
    /// Every automation launch sets it for itself (<see cref="AutomationUserData"/>), so a
    /// self-test or a capture run never reads or writes the real user's data. A person or a
    /// script can set it too, to seed a run with data of their own.
    /// </summary>
    public const string OverrideVariable = "MEGAPDF_USER_DATA";

    /// <summary>
    /// The app's folder: <see cref="OverrideVariable"/>'s, when it is set, and otherwise
    /// <c>MegaPDF</c> in the user-data root. An overriding folder is created if it is not
    /// there; the ordinary one is left to its first writer, as it always was.
    /// </summary>
    /// <exception cref="UserDataUnavailableException">
    /// There is no folder to keep user data in, or it could not be created, or the override
    /// is not an absolute path.
    /// </exception>
    public static string AppFolder()
    {
        if (OverridingFolder() is { } overriding)
            return Ensure(overriding);
        return Path.Combine(Root(), AppFolderName);
    }

    /// <summary>True when <see cref="OverrideVariable"/> moves the app's folder for this process.</summary>
    public static bool IsOverridden => OverridingFolder() is not null;

    private static string? OverridingFolder()
    {
        var value = Environment.GetEnvironmentVariable(OverrideVariable);
        if (string.IsNullOrWhiteSpace(value))
            return null;
        // A relative path here would land beside the working directory — the very thing this
        // class exists to prevent — and quietly falling back to the real folder would defeat
        // the reason it was set. Neither: say so.
        if (!Path.IsPathFullyQualified(value))
            throw new UserDataUnavailableException(
                $"{OverrideVariable} must name an absolute folder, not '{value}'.");
        return Path.TrimEndingDirectorySeparator(value);
    }

    /// <summary>
    /// Where the app's folder would be with no override, worked out without creating anything,
    /// or null when there is nowhere it could be. The automation guard compares against this:
    /// it is the real user's data, which an automation launch must leave exactly as it found it.
    /// </summary>
    public static string? DefaultAppFolder()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                             Environment.SpecialFolderOption.DoNotVerify);
        if (root.Length == 0 && !OperatingSystem.IsWindows())
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var home = Environment.GetEnvironmentVariable("HOME");
            root = xdg is { Length: > 0 } && Path.IsPathFullyQualified(xdg) ? xdg
                 : home is { Length: > 0 } && Path.IsPathFullyQualified(home) ? Path.Combine(home, ".local", "share")
                 : "";
        }
        return root.Length > 0 && Path.IsPathFullyQualified(root) ? Path.Combine(root, AppFolderName) : null;
    }

    /// <summary>
    /// The user-data root, created if it is not there: <c>%LOCALAPPDATA%</c> on Windows,
    /// <c>~/Library/Application Support</c> on macOS, <c>$XDG_DATA_HOME</c> or
    /// <c>~/.local/share</c> on Linux.
    /// </summary>
    /// <exception cref="UserDataUnavailableException">
    /// There is no folder to keep user data in, or it could not be created.
    /// </exception>
    public static string Root()
    {
        // 1. The ordinary answer, and the only one that costs nothing: a root that is
        //    already there. This is what every platform returns except the one case
        //    this class is about.
        var existing = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (existing.Length > 0)
            return existing;

        // 2. Ask the platform for the same folder and to make it. .NET knows where each
        //    platform's is — including that Linux's is $XDG_DATA_HOME or ~/.local/share,
        //    and that the Mac's is ~/Library/Application Support and not either of those.
        //    So this is the fallback, rather than the XDG rule written out here: it is
        //    right on a Mac whose Application Support folder has somehow gone, and the
        //    answer on Linux is the XDG one anyway.
        var created = "";
        Exception? couldNotCreate = null;
        try
        {
            created = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                                Environment.SpecialFolderOption.Create);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept, not thrown: the rule written out below reaches the same folder and fails
            // saying which one it was, which is the more useful of the two messages.
            couldNotCreate = ex;
        }
        if (created.Length > 0)
            return Ensure(created);

        // 3. Still nothing, which off Windows means .NET had neither $XDG_DATA_HOME nor
        //    $HOME to work from. Write the XDG rule out, in case only one of them is
        //    missing or $XDG_DATA_HOME is relative, which the spec says to ignore and
        //    .NET does.
        if (!OperatingSystem.IsWindows())
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (xdg is { Length: > 0 } && Path.IsPathFullyQualified(xdg))
                return Ensure(xdg);

            var home = Environment.GetEnvironmentVariable("HOME");
            if (home is { Length: > 0 } && Path.IsPathFullyQualified(home))
                return Ensure(Path.Combine(home, ".local", "share"));
        }

        // 4. Nowhere at all. Say so: the one thing that must never happen here is a
        //    relative path, which is a directory made beside the executable.
        throw Unavailable(couldNotCreate);
    }

    /// <summary>Makes sure the root is there, and is a root — never a path to resolve against the working directory.</summary>
    private static string Ensure(string root)
    {
        if (!Path.IsPathFullyQualified(root))
            throw Unavailable(new ArgumentException($"the folder is not an absolute path: {root}"));
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UserDataUnavailableException(
                $"MegaPDF could not create the folder it keeps your settings in: {root}", ex);
        }
        return root;
    }

    private static UserDataUnavailableException Unavailable(Exception? inner) =>
        new(OperatingSystem.IsWindows()
                ? "MegaPDF has nowhere to keep your settings: the local application data folder " +
                  "could not be found or created."
                : "MegaPDF has nowhere to keep your settings: neither XDG_DATA_HOME nor HOME names " +
                  "a folder it can use. Set one of them to a folder it can write to.",
            inner);
}
