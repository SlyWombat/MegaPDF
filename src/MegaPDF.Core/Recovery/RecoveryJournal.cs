using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MegaPDF.Core.Recovery;

/// <summary>A crashed session that can be restored: the journal file, its document, and how many edits it holds.</summary>
public sealed record RecoverableSession(string JournalPath, string DocumentPath, int EntryCount, DateTime LastWriteUtc);

/// <summary>
/// Crash-recovery journal (SDD §3.4): an append-only log of edit actions per open
/// document, stored under %LOCALAPPDATA%\MegaPDF\Recovery. Each Record call appends
/// one JSON line and flushes, so a crash at any point loses at most the in-flight
/// entry. MarkSaved truncates the log; EndSession removes it. A journal file that
/// still exists with entries at scan time is a crashed session.
///
/// A live journal is held exclusively (#145). Two app instances with the same document
/// open used to share one journal file: the second truncated the first's entries, and
/// the first crashed on close when its delete met the second's handle. Now a journal
/// another instance holds is skipped — the session writes to the next free name beside
/// it — and a scan never offers a journal that is still being written.
/// </summary>
public sealed class RecoveryJournal : IDisposable
{
    /// <summary>How many sibling names a session tries before it gives up journaling.</summary>
    private const int MaxSiblings = 16;

    private readonly string _directory;
    private string? _journalPath;
    private StreamWriter? _writer;
    private bool _contentIsProtected;

    /// <param name="directory">Defaults to %LOCALAPPDATA%\MegaPDF\Recovery; injectable for tests.</param>
    public RecoveryJournal(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MegaPDF", "Recovery");
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Where this session's entries go; null while nothing is journaled.</summary>
    public string? JournalPath => _journalPath;

    /// <summary>
    /// Starts journaling edits to <paramref name="documentPath"/>, truncating any earlier
    /// journal for it that no other instance holds. A document whose content is protected —
    /// opened with a password — is not journaled at all: entries carry document text, which
    /// would otherwise sit on disk unencrypted beside a file its owner encrypted (#135). A
    /// journal an earlier version left for such a document is deleted; a restore has already
    /// read its entries.
    /// </summary>
    public void BeginSession(string documentPath, bool contentIsProtected = false)
    {
        EndSession();
        _contentIsProtected = contentIsProtected;
        var baseName = HashPath(documentPath);
        if (contentIsProtected)
        {
            TryDelete(Path.Combine(_directory, $"{baseName}.journal"));
            return;
        }

        for (var sibling = 1; sibling <= MaxSiblings; sibling++)
        {
            var candidate = Path.Combine(_directory, sibling == 1 ? $"{baseName}.journal" : $"{baseName}-{sibling}.journal");
            FileStream stream;
            try
            {
                // OpenOrCreate and then truncate, not Create: on macOS and Linux Create
                // truncates before the lock is tried, which would wipe another instance's
                // entries even though the open then fails.
                stream = new FileStream(candidate, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            }
            catch (IOException)
            {
                continue; // another instance holds this one
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            stream.SetLength(0);
            _journalPath = candidate;
            _writer = new StreamWriter(stream);
            _writer.WriteLine(JsonSerializer.Serialize(new Header(documentPath)));
            _writer.Flush();
            return;
        }
        // Every name is held: the document is open in that many other instances. Its edits
        // go unjournaled rather than the open failing.
    }

    public void Record(JournalEntry entry)
    {
        if (_writer is null)
            return;
        _writer.WriteLine(JsonSerializer.Serialize(entry));
        _writer.Flush();
    }

    /// <summary>The document was saved — recorded edits are now durable, so restart the log.</summary>
    public void MarkSaved(string documentPath) => BeginSession(documentPath, _contentIsProtected);

    /// <summary>
    /// Clean close (or the user discarded changes): nothing to recover. Never throws: a
    /// journal that cannot be deleted is left behind, and a scan offers it only if it holds
    /// entries.
    /// </summary>
    public void EndSession()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (IOException)
        {
            // The final flush failed; the file is being removed anyway.
        }
        _writer = null;
        if (_journalPath is not null)
            TryDelete(_journalPath);
        _journalPath = null;
    }

    public IReadOnlyList<RecoverableSession> FindRecoverableSessions()
    {
        var sessions = new List<RecoverableSession>();
        foreach (var file in Directory.GetFiles(_directory, "*.journal"))
        {
            // This process's own live journal is not a crashed session either.
            if (string.Equals(file, _journalPath, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var lines = ReadAllLinesShared(file);
                if (lines.Length < 2) // header only — no unsaved edits
                    continue;
                var header = JsonSerializer.Deserialize<Header>(lines[0]);
                if (header is null || !File.Exists(header.DocumentPath))
                    continue;
                sessions.Add(new RecoverableSession(
                    file, header.DocumentPath, lines.Length - 1, File.GetLastWriteTimeUtc(file)));
            }
            catch (JsonException)
            {
                // A torn header means nothing usable — skip it.
            }
            catch (IOException)
            {
                // Held by a live session in another instance — not ours to recover.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return sessions.OrderByDescending(s => s.LastWriteUtc).ToList();
    }

    /// <summary>
    /// Reads a journal nobody is writing. A live writer holds its file exclusively, so this
    /// throws <see cref="IOException"/> for one.
    /// </summary>
    private static string[] ReadAllLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines.ToArray();
    }

    public static IReadOnlyList<JournalEntry> LoadEntries(string journalPath)
    {
        var entries = new List<JournalEntry>();
        foreach (var line in ReadAllLinesShared(journalPath).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<JournalEntry>(line) is { } entry)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
                break; // a torn final line — everything before it is still good
            }
        }
        return entries;
    }

    /// <summary>Deletes a crashed session's journal; one that is in use elsewhere is left alone.</summary>
    public static void Discard(string journalPath) => TryDelete(journalPath);

    /// <summary>Lets go of the file without deleting it: an unconsented exit keeps its edits recoverable (#145).</summary>
    public void Dispose()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (IOException)
        {
        }
        _writer = null;
    }

    /// <summary>
    /// Deletes a journal only while holding its exclusive lock, so a journal another instance
    /// is writing is never deleted (#145). File.Delete was enough on Windows, where the writer's
    /// share mode refuses it; on macOS and Linux the lock is advisory, and an unlink goes ahead
    /// under it, so a discard could remove a live instance's journal and leave its edits
    /// unrecoverable. DeleteOnClose removes the file before the lock is let go, on every platform.
    /// </summary>
    private static void TryDelete(string path)
    {
        try
        {
            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                                            bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            // Gone already, or another instance holds it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string HashPath(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..24];

    private sealed record Header(string DocumentPath);
}
