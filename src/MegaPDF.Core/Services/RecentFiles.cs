using System.Text.Json;

namespace MegaPDF.Core.Services;

/// <summary>A recent document plus its last view state (SDD §3.4: restore scroll position).</summary>
public sealed record RecentEntry(string Path, double ScrollOffset = 0, int ZoomPercent = 100);

/// <summary>
/// Most-recently-used document list (SDD §2.2 empty state). Stored per-user as JSON;
/// entries whose files have disappeared are pruned on load.
/// </summary>
public sealed class RecentFiles
{
    public const int Capacity = 10;

    private readonly string _path;
    private List<RecentEntry> _entries;

    /// <param name="path">Defaults to %LOCALAPPDATA%\MegaPDF\recent.json; injectable for tests.</param>
    public RecentFiles(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MegaPDF", "recent.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _entries = Load();
    }

    public IReadOnlyList<string> All => _entries.Select(e => e.Path).ToList();

    public void Add(string documentPath)
    {
        var full = Path.GetFullPath(documentPath);
        var existing = FindEntry(full);
        _entries.RemoveAll(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase));
        // Re-opening keeps the remembered view state.
        _entries.Insert(0, existing ?? new RecentEntry(full));
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
            return entries.Where(e => File.Exists(e.Path)).ToList();
        }
        catch (JsonException)
        {
            // Migrate the 1.0 format (a plain list of paths).
            try
            {
                var paths = JsonSerializer.Deserialize<List<string>>(json) ?? [];
                return paths.Where(File.Exists).Select(p => new RecentEntry(p)).ToList();
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
