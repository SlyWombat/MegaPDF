using MegaPDF.Core.Recovery;
using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// These set XDG_DATA_HOME and HOME, which are the process's, so they run one at a time.
/// Nothing else in the suite reads the user-data root — every service that has one takes
/// its path in the constructor, and the tests for them pass one — so serialising these
/// against each other is enough.
/// </summary>
[CollectionDefinition("user-data-root")]
public sealed class UserDataRootCollection;

/// <summary>
/// #195: <c>Environment.GetFolderPath(LocalApplicationData)</c> returns the empty string
/// when the folder it resolves to is not there. Four services built their paths straight
/// off it, so the path became relative and the first CreateDirectory met the apphost — a
/// file named <c>MegaPDF</c> beside the executable — and the app died before its window.
/// </summary>
[Collection("user-data-root")]
public class UserDataPathsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-userdata-").FullName;
    private readonly string? _xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    private readonly string? _home = Environment.GetEnvironmentVariable("HOME");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", _xdg);
        Environment.SetEnvironmentVariable("HOME", _home);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* left in temp */ }
    }

    [Fact]
    public void Root_IsAnAbsolutePathThatExists()
    {
        var root = UserDataPaths.Root();

        Assert.True(Path.IsPathFullyQualified(root), $"the user-data root must be absolute: '{root}'");
        Assert.True(Directory.Exists(root), $"the user-data root must exist: '{root}'");
    }

    [Fact]
    public void InAppFolder_IsUnderTheRoot_AndUnderTheAppsOwnFolder()
    {
        var path = UserDataPaths.InAppFolder("Recovery", "session.jsonl");

        Assert.Equal(Path.Combine(UserDataPaths.Root(), "MegaPDF", "Recovery", "session.jsonl"), path);
        Assert.True(Path.IsPathFullyQualified(path));
    }

    [Fact]
    public void Root_WhenTheDataFolderIsNotThere_CreatesIt()
    {
        // The #195 case itself: an account that has never run a desktop application, so
        // ~/.local/share does not exist and GetFolderPath answers with "".
        if (!TheDataFolderCanBeMoved)
            return;
        var data = Path.Combine(_dir, "never-used", "share");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
        Assert.False(Directory.Exists(data));
        Assert.Equal("", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        var root = UserDataPaths.Root();

        Assert.Equal(data, root);
        Assert.True(Directory.Exists(data));
    }

    [Fact]
    public void Root_WithNoHomeAndNoXdgDataHome_NeverAnswersWithARelativePath()
    {
        // The empty string must never become a path. Combined with a folder name it used to
        // give a relative one, which resolves against the working directory — beside the
        // app, where a file called MegaPDF already is.
        //
        // Whether there is anywhere at all with neither variable set is the machine's
        // business, not this class's: .NET falls back to the account's passwd entry for a
        // home directory when HOME is unset, and an account in a container may have no
        // entry to fall back to. Both answers are right. A relative path is the one that
        // is not.
        if (!TheDataFolderCanBeMoved)
            return;

        // HOME is the process's and the rest of the suite is running in it — fontconfig and
        // Skia both read it as they start up — so it is unset for one call and no longer.
        string? root = null;
        UserDataUnavailableException? nowhere = null;
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", null);
        Environment.SetEnvironmentVariable("HOME", null);
        try
        {
            root = UserDataPaths.Root();
        }
        catch (UserDataUnavailableException ex)
        {
            nowhere = ex;
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", _home);
        }

        if (nowhere is not null)
        {
            Assert.Contains("XDG_DATA_HOME", nowhere.Message);
            Assert.Contains("HOME", nowhere.Message);
            return;
        }
        Assert.True(Path.IsPathFullyQualified(root!), $"the user-data root must be absolute: '{root}'");
        Assert.True(Directory.Exists(root), $"the user-data root must exist: '{root}'");
    }

    [Fact]
    public void Root_WhenTheDataFolderCannotBeCreated_SaysWhichFolderAndWhy()
    {
        if (!TheDataFolderCanBeMoved)
            return;
        var locked = Directory.CreateDirectory(Path.Combine(_dir, "locked")).FullName;
        var data = Path.Combine(locked, "share");
        if (!NowRefusesNewFiles(locked))
            return;   // running as a user the mode does not apply to
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);

        try
        {
            var ex = Assert.Throws<UserDataUnavailableException>(() => UserDataPaths.Root());

            Assert.Contains(data, ex.Message);
            Assert.NotNull(ex.InnerException);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("recents")]
    [InlineData("signatures")]
    [InlineData("journal")]
    public void AServiceWithNoPathOfItsOwn_LandsUnderTheDataFolder_NotBesideTheExecutable(string service)
    {
        // What actually crashed: MainViewModel builds all four with no path. Each used to
        // resolve "" into a relative path and the first CreateDirectory hit the apphost.
        if (!TheDataFolderCanBeMoved)
            return;
        var data = Path.Combine(_dir, "never-used", "share");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
        var app = Path.Combine(data, "MegaPDF");

        switch (service)
        {
            case "settings": _ = new AppSettings(); break;
            case "recents": _ = new RecentFiles(); break;
            case "signatures": _ = new SignatureLibrary(); break;
            default: new RecoveryJournal().Dispose(); break;
        }

        Assert.True(Directory.Exists(app), $"{service} did not create {app}");
        Assert.False(Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "MegaPDF")),
                     "a MegaPDF folder beside the executable is the #195 crash");
    }

    /// <summary>
    /// Whether this platform lets a test put the user-data folder somewhere that is not
    /// there — which is how the state behind #195 is reached at all.
    ///
    /// Only Linux does. There the folder is <c>$XDG_DATA_HOME</c> or <c>~/.local/share</c>,
    /// and an account that has never run a desktop application really can be without it —
    /// which is where the crash was found. On Windows it is <c>%LOCALAPPDATA%</c> and on
    /// macOS <c>~/Library/Application Support</c>: always there, and moved by no environment
    /// variable, so the same test would either assert nothing or write into the account's
    /// own data folder. The two tests that say the answer is absolute and exists do run
    /// everywhere, and they are the ones that would catch a regression there.
    /// </summary>
    private static bool TheDataFolderCanBeMoved => OperatingSystem.IsLinux();

    /// <summary>
    /// Makes <paramref name="directory"/> refuse new files and says whether it now does.
    /// False for a user the mode does not apply to, such as root in a container.
    /// </summary>
    private static bool NowRefusesNewFiles(string directory)
    {
        if (OperatingSystem.IsWindows())
            return false;
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var probe = Path.Combine(directory, "probe");
        try
        {
            using (File.Create(probe)) { }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return true;
        }
        File.Delete(probe);
        return false;
    }
}
