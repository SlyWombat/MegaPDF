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
/// </summary>
public sealed class RecoveryJournal : IDisposable
{
    private readonly string _directory;
    private string? _journalPath;
    private StreamWriter? _writer;

    /// <param name="directory">Defaults to %LOCALAPPDATA%\MegaPDF\Recovery; injectable for tests.</param>
    public RecoveryJournal(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MegaPDF", "Recovery");
        Directory.CreateDirectory(_directory);
    }

    public void BeginSession(string documentPath)
    {
        EndSession();
        _journalPath = Path.Combine(_directory, $"{HashPath(documentPath)}.journal");
        _writer = new StreamWriter(new FileStream(_journalPath, FileMode.Create, FileAccess.Write, FileShare.Read));
        _writer.WriteLine(JsonSerializer.Serialize(new Header(documentPath)));
        _writer.Flush();
    }

    public void Record(JournalEntry entry)
    {
        if (_writer is null)
            return;
        _writer.WriteLine(JsonSerializer.Serialize(entry));
        _writer.Flush();
    }

    /// <summary>The document was saved — recorded edits are now durable, so restart the log.</summary>
    public void MarkSaved(string documentPath) => BeginSession(documentPath);

    /// <summary>Clean close (or the user discarded changes): nothing to recover.</summary>
    public void EndSession()
    {
        _writer?.Dispose();
        _writer = null;
        if (_journalPath is not null && File.Exists(_journalPath))
            File.Delete(_journalPath);
        _journalPath = null;
    }

    public IReadOnlyList<RecoverableSession> FindRecoverableSessions()
    {
        var sessions = new List<RecoverableSession>();
        foreach (var file in Directory.GetFiles(_directory, "*.journal"))
        {
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
                // Locked by a live session — not ours to recover.
            }
        }
        return sessions.OrderByDescending(s => s.LastWriteUtc).ToList();
    }

    /// <summary>Reads a journal even while a writer holds it open (writers use FileShare.Read).</summary>
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

    public static void Discard(string journalPath)
    {
        if (File.Exists(journalPath))
            File.Delete(journalPath);
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }

    private static string HashPath(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..24];

    private sealed record Header(string DocumentPath);
}
