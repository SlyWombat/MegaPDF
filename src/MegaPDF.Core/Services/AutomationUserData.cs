using System.Diagnostics;
using System.Security.Cryptography;

namespace MegaPDF.Core.Services;

/// <summary>
/// An automation launch's own user data (#717): a self-test, a screenshot state, a capture
/// run or an engine check never reads or writes the real user's settings, recent documents,
/// signatures or recovery journal.
///
/// Until this existed every one of them did. On GPD-DAVE one evening of agent runs left 26
/// test journals in the real Recovery folder — which the next ordinary launch would have
/// offered to recover — rewrote settings.json, and added a recent-documents row for every
/// state, even <c>empty</c>; a release pass emptied the recents list outright.
///
/// <see cref="Begin()"/> is the first thing an automation launch does, before any service is
/// built. Unless <see cref="UserDataPaths.OverrideVariable"/> already names a folder, it makes
/// a fresh one under the temporary folder and points the variable at it, so this process and
/// every process it starts keep their data there; <see cref="Finish"/> deletes it. A folder
/// the variable named before launch belongs to whoever named it and is never deleted.
///
/// It is also the guard. <see cref="Begin()"/> fingerprints the real folder — every file's
/// SHA-256 — and <see cref="Finish"/> fingerprints it again, and the launch fails if the
/// folder in use is the real one or the real one changed at all.
/// </summary>
public sealed class AutomationUserData
{
    /// <summary>The name every folder this makes starts with, under the temporary folder.</summary>
    public const string FolderPrefix = "megapdf-userdata-";

    private const string OwnerFile = "owner.pid";

    private static AutomationUserData? _current;
    private static readonly object Gate = new();

    private readonly Fingerprint _before;
    private int _finished;

    private AutomationUserData(string folder, bool ownsFolder, string? realFolder, Fingerprint before)
    {
        Folder = folder;
        OwnsFolder = ownsFolder;
        RealFolder = realFolder;
        _before = before;
    }

    /// <summary>This process's isolation, once <see cref="Begin()"/> has run; null in a person's launch.</summary>
    public static AutomationUserData? Current => _current;

    /// <summary>True in an automation launch: nothing this process does may leave a trace in the person's session.</summary>
    public static bool IsActive => _current is not null;

    /// <summary>The folder this launch keeps its user data in.</summary>
    public string Folder { get; }

    /// <summary>True when <see cref="Begin()"/> made <see cref="Folder"/> and <see cref="Finish"/> deletes it.</summary>
    public bool OwnsFolder { get; }

    /// <summary>The real user's folder, which must be the same at the end as at the start; null if there is none.</summary>
    public string? RealFolder { get; }

    /// <summary>
    /// Isolates this process's user data. Idempotent: a second call returns the first one's.
    /// </summary>
    public static AutomationUserData Begin() => Begin(UserDataPaths.DefaultAppFolder(), Path.GetTempPath());

    /// <summary>
    /// <see cref="Begin()"/> with the real folder and the temporary folder given, for tests,
    /// which must never fingerprint — let alone risk — an account's real data.
    /// </summary>
    public static AutomationUserData Begin(string? realFolder, string tempRoot)
    {
        lock (Gate)
        {
            if (_current is { } existing)
                return existing;

            var before = Fingerprint.Take(realFolder);
            string folder;
            var owns = false;
            if (UserDataPaths.IsOverridden)
            {
                folder = UserDataPaths.AppFolder();
            }
            else
            {
                SweepLeftovers(tempRoot);
                folder = Path.Combine(tempRoot, FolderPrefix + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, OwnerFile), Environment.ProcessId.ToString());
                // The variable, not a field: a child this launch starts (the Avalonia self-test
                // relaunches itself for --open-check) inherits it and shares the folder, and
                // sees it as named from outside, so only this launch deletes it.
                Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, folder);
                owns = true;
            }

            var isolation = new AutomationUserData(folder, owns, realFolder, before);
            _current = isolation;
            // A launch that ends some other way than Finish still takes its folder with it,
            // as far as it can; whatever an open file keeps behind, the next launch sweeps.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => isolation.DeleteOwnFolder();
            return isolation;
        }
    }

    /// <summary>
    /// Everything wrong with this launch's handling of the real user's data, or nothing: the
    /// folder in use must not be the real one, and the real one must be byte-identical to
    /// what <see cref="Begin()"/> found.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        string inUse;
        try
        {
            inUse = Path.GetFullPath(UserDataPaths.AppFolder());
        }
        catch (UserDataUnavailableException ex)
        {
            problems.Add($"the data folder in use could not be found: {ex.Message}");
            inUse = "";
        }
        if (inUse.Length > 0)
        {
            if (RealFolder is not null && (IsSameOrUnder(inUse, RealFolder) || IsSameOrUnder(RealFolder, inUse)))
                problems.Add($"the data folder in use is the real one: {inUse}");
            else if (!SamePath(inUse, Folder))
                problems.Add($"the data folder in use moved during the run, to {inUse}");
        }
        problems.AddRange(_before.DifferencesFrom(Fingerprint.Take(RealFolder)));
        return problems;
    }

    /// <summary>
    /// Checks the guard, says what it found on <paramref name="log"/>, deletes the folder this
    /// launch made, and returns the exit code to use: <paramref name="exitCode"/>, or 1 when
    /// it was 0 and the guard failed. <paramref name="quietWhenClean"/> says nothing at all when
    /// the guard passes, for a diagnostic whose output a script reads whole
    /// (<c>--install-kind</c> is compared against one exact line).
    /// </summary>
    public int Finish(int exitCode, TextWriter log, bool quietWhenClean = false)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
            return exitCode;

        var problems = Problems();
        if (quietWhenClean && problems.Count == 0)
        {
            DeleteOwnFolder();
            return exitCode;
        }
        var folderProblems = problems.Where(p => p.StartsWith("the data folder", StringComparison.Ordinal)).ToList();
        var changed = problems.Except(folderProblems).ToList();
        log.WriteLine("user data (#717):");
        log.WriteLine($"  [{(folderProblems.Count == 0 ? "PASS" : "FAIL")}] kept in {Folder}"
                      + (OwnsFolder ? " (made for this run)" : $" (named by {UserDataPaths.OverrideVariable})")
                      + $", not the real folder {RealFolder ?? "(none on this account)"}");
        log.WriteLine($"  [{(changed.Count == 0 ? "PASS" : "FAIL")}] the real folder is byte-identical to its state at launch "
                      + (_before.Exists ? $"({_before.Count} entries)" : "(it was not there, and still is not)"));
        foreach (var problem in problems)
            log.WriteLine($"    {problem}");
        if (changed.Count > 0)
            log.WriteLine("    (a person's own MegaPDF running at the same time would also do this; the run cannot tell which)");

        DeleteOwnFolder();
        return problems.Count == 0 || exitCode != 0 ? exitCode : 1;
    }

    private void DeleteOwnFolder()
    {
        if (!OwnsFolder)
            return;
        // The journals first: on Windows an open file cannot be deleted, and a state that
        // exits with a document open still holds its journal.
        Recovery.RecoveryJournal.ReleaseAllUnder(Folder);
        DeleteLeavingOwnerLast(Folder);
    }

    /// <summary>
    /// Deletes a folder this class made, the owner file last: if anything in it cannot go,
    /// the owner file stays, and the next launch's <see cref="SweepLeftovers"/> still knows
    /// the folder for one of ours and finishes the job.
    /// </summary>
    private static void DeleteLeavingOwnerLast(string folder)
    {
        if (!Directory.Exists(folder))
            return;
        var owner = Path.Combine(folder, OwnerFile);
        var left = false;
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (string.Equals(entry.FullName, owner, StringComparison.Ordinal))
                continue;
            try
            {
                if (entry is DirectoryInfo directory)
                    directory.Delete(recursive: true);
                else
                    entry.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left = true;   // still open somewhere; swept once its owner has gone
            }
        }
        if (left)
            return;
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Deletes the folders earlier launches left: any whose owner has exited, or that is more
    /// than a day old. A live run's folder is never touched.
    /// </summary>
    public static void SweepLeftovers(string tempRoot)
    {
        List<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(tempRoot, FolderPrefix + "*").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var folder in folders)
        {
            try
            {
                var stale = Directory.GetCreationTimeUtc(folder) < DateTime.UtcNow.AddDays(-1);
                if (!stale)
                {
                    var owner = Path.Combine(folder, OwnerFile);
                    if (!File.Exists(owner) || !int.TryParse(File.ReadAllText(owner).Trim(), out var pid))
                        continue;   // not one of ours, or still being made
                    stale = !IsRunning(pid);
                }
                if (stale)
                    DeleteLeavingOwnerLast(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use, or gone already.
            }
        }
    }

    private static bool IsRunning(int pid)
    {
        if (pid == Environment.ProcessId)
            return true;
        try
        {
            using var owner = Process.GetProcessById(pid);
            return !owner.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static string Normal(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SamePath(string a, string b) => string.Equals(Normal(a), Normal(b), PathComparison);

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    internal static bool IsSameOrUnder(string path, string folder)
    {
        var p = Normal(path);
        var f = Normal(folder);
        return string.Equals(p, f, PathComparison)
               || p.StartsWith(f + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>Every entry in a folder and the SHA-256 of every file, compared by relative path.</summary>
    internal sealed class Fingerprint
    {
        private readonly Dictionary<string, string> _entries;

        private Fingerprint(bool exists, Dictionary<string, string> entries)
        {
            Exists = exists;
            _entries = entries;
        }

        public bool Exists { get; }

        public int Count => _entries.Count;

        public static Fingerprint Take(string? folder)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            if (folder is null || !Directory.Exists(folder))
                return new Fingerprint(false, entries);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = true,
            };
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                // The Linux single-instance socket's fallback home: a socket, not data, and a
                // person's own running copy owns it.
                if (entry.Name.EndsWith(".sock", StringComparison.Ordinal))
                    continue;
                var relative = Path.GetRelativePath(folder, entry.FullName);
                entries[relative] = entry is DirectoryInfo ? "folder" : Hash(entry.FullName);
            }
            return new Fingerprint(true, entries);
        }

        private static string Hash(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "unreadable";
            }
        }

        /// <summary>What changed between this fingerprint and <paramref name="after"/>, one line each: names only, never contents.</summary>
        public IEnumerable<string> DifferencesFrom(Fingerprint after)
        {
            if (Exists != after.Exists)
                yield return Exists ? "the real folder was deleted" : "the real folder was created";
            foreach (var (path, hash) in _entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (!after._entries.TryGetValue(path, out var now))
                    yield return $"removed from the real folder: {path}";
                else if (now != hash)
                    yield return $"changed in the real folder: {path}";
            }
            foreach (var path in after._entries.Keys.Where(p => !_entries.ContainsKey(p)).OrderBy(p => p, StringComparer.Ordinal))
                yield return $"added to the real folder: {path}";
        }
    }

    /// <summary>Forgets this process's isolation, for tests only: the variable is the caller's to restore.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
            _current = null;
    }
}
