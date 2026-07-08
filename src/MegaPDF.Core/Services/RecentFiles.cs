using System.Text.Json;

namespace MegaPDF.Core.Services;

/// <summary>
/// Most-recently-used document list (SDD §2.2 empty state). Stored per-user as JSON;
/// entries whose files have disappeared are pruned on load.
/// </summary>
public sealed class RecentFiles
{
    public const int Capacity = 10;

    private readonly string _path;
    private List<string> _paths;

    /// <param name="path">Defaults to %LOCALAPPDATA%\MegaPDF\recent.json; injectable for tests.</param>
    public RecentFiles(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MegaPDF", "recent.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _paths = Load();
    }

    public IReadOnlyList<string> All => _paths;

    public void Add(string documentPath)
    {
        var full = Path.GetFullPath(documentPath);
        _paths.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        _paths.Insert(0, full);
        if (_paths.Count > Capacity)
            _paths.RemoveRange(Capacity, _paths.Count - Capacity);
        Save();
    }

    private List<string> Load()
    {
        if (!File.Exists(_path))
            return [];
        try
        {
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? [];
            return paths.Where(File.Exists).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Save() =>
        AtomicFileWriter.Write(_path, s => JsonSerializer.Serialize(s, _paths));
}
