using MegaPDF.Core.Recovery;
using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #717: the desktop self-tests read and wrote the real user's data. One evening of agent runs
/// on GPD-DAVE left 26 test journals in the real Recovery folder, rewrote settings.json and
/// added a recents row per state; a release pass emptied the recents list.
///
/// None of these touch the account's own folder. The "real" folder each one guards is a
/// folder of the test's own, handed to <see cref="AutomationUserData.Begin(string?, string)"/>,
/// so the guard is proved against a stand-in and never pointed at a person's data. They set
/// MEGAPDF_USER_DATA, which is the process's, so they share the user-data-root collection.
/// </summary>
[Collection("user-data-root")]
public class AutomationUserDataTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-717-").FullName;
    private readonly string? _override = Environment.GetEnvironmentVariable(UserDataPaths.OverrideVariable);

    public AutomationUserDataTests()
    {
        AutomationUserData.ResetForTests();
        Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, null);
    }

    public void Dispose()
    {
        AutomationUserData.ResetForTests();
        Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, _override);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* left in temp */ }
    }

    /// <summary>A stand-in for the person's folder, as GPD-DAVE's was on 2026-10-21.</summary>
    private string RealFolder()
    {
        var real = Path.Combine(_dir, "real", "MegaPDF");
        Directory.CreateDirectory(Path.Combine(real, "Recovery"));
        Directory.CreateDirectory(Path.Combine(real, "Signatures"));
        File.WriteAllText(Path.Combine(real, "recent.json"), "[]");
        File.WriteAllText(Path.Combine(real, "settings.json"), "{\"DefaultAppCardShown\": false}");
        File.WriteAllText(Path.Combine(real, "Recovery", "66B80A1CF4AF01AE5B6190D3.journal"), "{}");
        File.WriteAllText(Path.Combine(real, "Signatures", "index.json"), "[]");
        return real;
    }

    private string TempRoot() => Directory.CreateDirectory(Path.Combine(_dir, "tmp")).FullName;

    [Fact]
    public void TheVariable_MovesEveryServiceIntoTheFolderItNames()
    {
        var named = Path.Combine(_dir, "named");
        Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, named);

        Assert.True(UserDataPaths.IsOverridden);
        Assert.Equal(named, UserDataPaths.AppFolder());
        Assert.Equal(Path.Combine(named, "recent.json"), UserDataPaths.InAppFolder("recent.json"));

        var settings = new AppSettings();
        settings.ReopenLastFile = !settings.ReopenLastFile;   // a write
        new RecentFiles().Add(Path.Combine(_dir, "some.pdf"));
        _ = new SignatureLibrary();
        using (var journal = new RecoveryJournal())
            journal.BeginSession(Path.Combine(_dir, "some.pdf"), contentIsProtected: false);

        Assert.True(File.Exists(Path.Combine(named, "settings.json")));
        Assert.True(File.Exists(Path.Combine(named, "recent.json")));
        Assert.True(Directory.Exists(Path.Combine(named, "Signatures")));
        Assert.True(Directory.Exists(Path.Combine(named, "Recovery")));
    }

    [Fact]
    public void TheVariable_RefusesARelativePath_RatherThanFallingBackToTheRealFolder()
    {
        Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, "relative/folder");

        var ex = Assert.Throws<UserDataUnavailableException>(() => UserDataPaths.AppFolder());
        Assert.Contains(UserDataPaths.OverrideVariable, ex.Message);
    }

    [Fact]
    public void DefaultAppFolder_IsAbsolute_EvenWhileTheVariableMovesTheFolderInUse()
    {
        Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, Path.Combine(_dir, "named"));

        var real = UserDataPaths.DefaultAppFolder();

        Assert.NotNull(real);
        Assert.True(Path.IsPathFullyQualified(real!));
        Assert.Equal(UserDataPaths.AppFolderName, Path.GetFileName(real));
        Assert.NotEqual(UserDataPaths.AppFolder(), real);
    }

    [Fact]
    public void Begin_MakesAFreshFolderUnderTheTemporaryFolder_AndEveryServiceUsesIt()
    {
        var real = RealFolder();
        var temp = TempRoot();

        var isolation = AutomationUserData.Begin(real, temp);

        Assert.True(isolation.OwnsFolder);
        Assert.StartsWith(Path.Combine(temp, AutomationUserData.FolderPrefix), isolation.Folder);
        Assert.Equal(isolation.Folder, Environment.GetEnvironmentVariable(UserDataPaths.OverrideVariable));
        Assert.Equal(isolation.Folder, UserDataPaths.AppFolder());
        Assert.True(AutomationUserData.IsActive);
        Assert.Same(isolation, AutomationUserData.Begin(real, temp));

        new RecentFiles().Add(Path.Combine(_dir, "test.pdf"));
        Assert.True(File.Exists(Path.Combine(isolation.Folder, "recent.json")));
    }

    [Fact]
    public void Finish_Passes_WhenTheRunWritesOnlyItsOwnFolder_AndDeletesThatFolder()
    {
        var real = RealFolder();
        var isolation = AutomationUserData.Begin(real, TempRoot());
        var settings = new AppSettings();
        settings.ReopenLastFile = !settings.ReopenLastFile;
        new RecentFiles().Add(Path.Combine(_dir, "test.pdf"));
        // Still open at exit, as a state's document is: on Windows an open file cannot be
        // deleted, and the run's folder used to outlive it with this journal inside.
        var journal = new RecoveryJournal();
        journal.BeginSession(Path.Combine(_dir, "test.pdf"), contentIsProtected: false);
        Assert.NotNull(journal.JournalPath);

        var log = new StringWriter();
        var code = isolation.Finish(0, log);

        Assert.True(code == 0, log.ToString());
        Assert.Contains("[PASS] the real folder is byte-identical to its state at launch (6 entries)", log.ToString());
        Assert.DoesNotContain("FAIL", log.ToString());
        Assert.False(Directory.Exists(isolation.Folder), "the run's own folder outlived it");
    }

    [Fact]
    public void Finish_Fails_WhenTheFolderInUseIsTheRealOne_WhichIsWhatEveryLaunchDidBefore717()
    {
        // Before #717 nothing moved the folder, so the folder in use *was* the real one. Naming
        // the real one through the variable is the same situation, reached on purpose.
        var real = RealFolder();
        Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, real);
        var isolation = AutomationUserData.Begin(real, TempRoot());

        var log = new StringWriter();
        var code = isolation.Finish(0, log);

        Assert.Equal(1, code);
        Assert.Contains("the data folder in use is the real one", log.ToString());
        Assert.True(Directory.Exists(real), "a folder the run did not make must never be deleted");
    }

    [Theory]
    [InlineData("recents")]
    [InlineData("settings")]
    [InlineData("journal")]
    [InlineData("emptied")]
    public void Finish_Fails_WhenTheRealFolderChanged_AndSaysWhichFile(string what)
    {
        var real = RealFolder();
        var isolation = AutomationUserData.Begin(real, TempRoot());

        // What GPD-DAVE measured: a recents row, a rewritten settings.json, a test journal, and
        // a recents list emptied.
        string expected;
        switch (what)
        {
            case "recents":
                new RecentFiles(Path.Combine(real, "recent.json")).Add(Path.Combine(_dir, "test.pdf"));
                expected = "changed in the real folder: recent.json";
                break;
            case "settings":
                File.WriteAllText(Path.Combine(real, "settings.json"), "{\"DefaultAppCardShown\": true}");
                expected = "changed in the real folder: settings.json";
                break;
            case "journal":
                File.WriteAllText(Path.Combine(real, "Recovery", "0123456789ABCDEF01234567.journal"), "{}");
                expected = $"added to the real folder: {Path.Combine("Recovery", "0123456789ABCDEF01234567.journal")}";
                break;
            default:
                File.Delete(Path.Combine(real, "Signatures", "index.json"));
                expected = $"removed from the real folder: {Path.Combine("Signatures", "index.json")}";
                break;
        }

        var log = new StringWriter();
        var code = isolation.Finish(0, log);

        Assert.Equal(1, code);
        Assert.Contains("[FAIL] the real folder is byte-identical", log.ToString());
        Assert.Contains(expected, log.ToString());
    }

    [Fact]
    public void Finish_Fails_WhenARealFolderThatWasNotThereAppears()
    {
        // A CI runner: no real folder at all, and an automation launch must not create one.
        var real = Path.Combine(_dir, "nobody", "MegaPDF");
        var isolation = AutomationUserData.Begin(real, TempRoot());
        Directory.CreateDirectory(real);

        var log = new StringWriter();
        Assert.Equal(1, isolation.Finish(0, log));
        Assert.Contains("the real folder was created", log.ToString());
    }

    [Fact]
    public void Finish_KeepsAFailingExitCode_AndIsQuietWhenCleanIfAsked()
    {
        var isolation = AutomationUserData.Begin(RealFolder(), TempRoot());

        var log = new StringWriter();
        Assert.Equal(3, isolation.Finish(3, log, quietWhenClean: true));
        Assert.Equal("", log.ToString());
    }

    [Fact]
    public void AFolderTheVariableNamedBeforeLaunch_IsUsed_AndNeverDeleted()
    {
        var named = Directory.CreateDirectory(Path.Combine(_dir, "seeded")).FullName;
        File.WriteAllText(Path.Combine(named, "recent.json"), "[\"seeded.pdf\"]");
        Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, named);

        var isolation = AutomationUserData.Begin(RealFolder(), TempRoot());

        Assert.False(isolation.OwnsFolder);
        Assert.Equal(named, isolation.Folder);
        Assert.Equal(0, isolation.Finish(0, new StringWriter()));
        Assert.True(File.Exists(Path.Combine(named, "recent.json")));
    }

    [Fact]
    public void SweepLeftovers_RemovesAFolderWhoseOwnerHasExited_AndKeepsALiveOne()
    {
        var temp = TempRoot();
        var dead = Directory.CreateDirectory(Path.Combine(temp, AutomationUserData.FolderPrefix + "dead")).FullName;
        File.WriteAllText(Path.Combine(dead, "owner.pid"), int.MaxValue.ToString());
        var live = Directory.CreateDirectory(Path.Combine(temp, AutomationUserData.FolderPrefix + "live")).FullName;
        File.WriteAllText(Path.Combine(live, "owner.pid"), Environment.ProcessId.ToString());
        var foreign = Directory.CreateDirectory(Path.Combine(temp, AutomationUserData.FolderPrefix + "foreign")).FullName;

        AutomationUserData.SweepLeftovers(temp);

        Assert.False(Directory.Exists(dead));
        Assert.True(Directory.Exists(live));
        Assert.True(Directory.Exists(foreign), "a folder with no owner file is not ours to delete");
    }
}
