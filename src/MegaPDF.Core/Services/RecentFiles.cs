using System.Text.Json.Serialization;
using System.Text.Json;

namespace MegaPDF.Core.Services;

/// <summary>A recent document plus its last view state (SDD §3.4: restore scroll position).</summary>
/// <param name="Bookmark">
/// Opaque, platform-supplied handle for re-opening this file on a platform where a
/// stored path is not sufficient. On macOS under the App Sandbox an absolute path
/// from a previous launch cannot be opened at all — the grant is to the file the
/// user picked, in that session — so the host stores a security-scoped bookmark here
/// (Avalonia's IStorageProvider.SaveBookmarkAsync) and re-opens through it.
///
/// Null on Windows, which never sets it and never reads it: a path is enough there,
/// and this round-trips through the JSON untouched.
/// </param>
public sealed record RecentEntry(string Path, double ScrollOffset = 0, int ZoomPercent = 100, string? Bookmark = null)
{
    /// <summary>
    /// What a recents row should say: the file name, not the path (#162). A
    /// sandboxed container path is long enough that trimming it with an ellipsis
    /// eats the file name — the only part anyone reads — so the path belongs in a
    /// tooltip instead, which is what the Windows template already does.
    ///
    /// Computed, and kept out of the JSON so recent.json stays the record it was.
    /// </summary>
    [JsonIgnore]
    public string DisplayName => System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;
}

/// <summary>
/// Most-recently-used document list (SDD §2.2 empty state). Stored per-user as JSON;
/// entries whose files have disappeared are pruned on load.
/// </summary>
public sealed class RecentFiles
{
    public const int Capacity = 10;

    private readonly string _path;
    private List<RecentEntry> _entries;

    private readonly bool _pruneMissing;

    /// <param name="path">Defaults to <c>recent.json</c> in the user-data folder
    /// (<see cref="UserDataPaths"/>); injectable for tests.</param>
    /// <param name="pruneMissing">
    /// Drop entries whose files have gone when the list loads. Windows passes false and
    /// shows them as unavailable instead, with a way to remove them (#165): a file that
    /// silently vanishes from Recent looks like the app lost it.
    /// </param>
    public RecentFiles(string? path = null, bool pruneMissing = true)
    {
        _pruneMissing = pruneMissing;
        _path = path ?? UserDataPaths.InAppFolder("recent.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _entries = Load();
    }

    public IReadOnlyList<string> All => _entries.Select(e => e.Path).ToList();

    /// <summary>Full entries, newest first — what a recents list needs to reopen them.</summary>
    public IReadOnlyList<RecentEntry> Entries => _entries;

    public void Add(string documentPath) => Add(documentPath, bookmark: null);

    /// <param name="bookmark">
    /// Platform handle for re-opening the file where a path alone will not do — a
    /// macOS security-scoped bookmark under the App Sandbox. Null on Windows, and a
    /// null here never clears a bookmark already recorded for the same file.
    /// </param>
    public void Add(string documentPath, string? bookmark)
    {
        var full = Path.GetFullPath(documentPath);
        var existing = FindEntry(full);
        _entries.RemoveAll(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase));
        // Re-opening keeps the remembered view state, and its bookmark if this call
        // did not bring a fresher one.
        var entry = existing ?? new RecentEntry(full);
        if (bookmark is not null)
            entry = entry with { Bookmark = bookmark };
        _entries.Insert(0, entry);
        if (_entries.Count > Capacity)
            _entries.RemoveRange(Capacity, _entries.Count - Capacity);
        Save();
    }

    /// <summary>Remembers where the user left a document (scroll + zoom).</summary>
    public void UpdateViewState(string documentPath, double scrollOffset, int zoomPercent)
    {
        var full = Path.GetFullPath(documentPath);
        var index = _entries.FindIndex(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;
        _entries[index] = _entries[index] with { ScrollOffset = scrollOffset, ZoomPercent = zoomPercent };
        Save();
    }

    /// <summary>Takes a document off the list, as "Remove from Recent" does.</summary>
    public void Remove(string documentPath)
    {
        var full = Path.GetFullPath(documentPath);
        if (_entries.RemoveAll(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase)) > 0)
            Save();
    }

    public RecentEntry? FindEntry(string documentPath)
    {
        var full = Path.GetFullPath(documentPath);
        return _entries.FirstOrDefault(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase));
    }

    private List<RecentEntry> Load()
    {
        if (!File.Exists(_path))
            return [];
        var json = File.ReadAllText(_path);
        try
        {
            var entries = JsonSerializer.Deserialize<List<RecentEntry>>(json) ?? [];
            return entries.Where(e => !_pruneMissing || File.Exists(e.Path)).ToList();
        }
        catch (JsonException)
        {
            // Migrate the 1.0 format (a plain list of paths).
            try
            {
                var paths = JsonSerializer.Deserialize<List<string>>(json) ?? [];
                return paths.Where(p => !_pruneMissing || File.Exists(p)).Select(p => new RecentEntry(p)).ToList();
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    private void Save() =>
        AtomicFileWriter.Write(_path, s => JsonSerializer.Serialize(s, _entries));
}
