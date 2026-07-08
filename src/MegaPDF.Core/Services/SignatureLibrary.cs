using System.Text.Json;

namespace MegaPDF.Core.Services;

public sealed record SignatureEntry(Guid Id, string Name, string PngPath, DateTime CreatedUtc);

/// <summary>
/// The user's signature library (SDD §3.3): local-only PNGs with alpha plus a JSON index,
/// stored per-user. Soft limit of 20 entries.
/// </summary>
public interface ISignatureLibrary
{
    IReadOnlyList<SignatureEntry> All { get; }
    SignatureEntry Add(string name, ReadOnlyMemory<byte> pngBytes);
    void Rename(Guid id, string newName);
    void Remove(Guid id);
}

public sealed class SignatureLibrary : ISignatureLibrary
{
    public const int SoftLimit = 20;

    private readonly string _directory;
    private readonly string _indexPath;
    private readonly List<SignatureEntry> _entries;

    /// <param name="directory">
    /// Storage directory; defaults to %LOCALAPPDATA%\MegaPDF\Signatures (SDD §3.3).
    /// Injectable for tests.
    /// </param>
    public SignatureLibrary(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MegaPDF", "Signatures");
        Directory.CreateDirectory(_directory);
        _indexPath = Path.Combine(_directory, "index.json");
        _entries = Load();
    }

    public IReadOnlyList<SignatureEntry> All => _entries;

    public SignatureEntry Add(string name, ReadOnlyMemory<byte> pngBytes)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Signature name is required.", nameof(name));
        if (_entries.Count >= SoftLimit)
            throw new InvalidOperationException($"The signature library is limited to {SoftLimit} signatures.");

        var id = Guid.NewGuid();
        var pngPath = Path.Combine(_directory, $"{id:N}.png");
        AtomicFileWriter.Write(pngPath, s => s.Write(pngBytes.Span));

        var entry = new SignatureEntry(id, name.Trim(), pngPath, DateTime.UtcNow);
        _entries.Add(entry);
        SaveIndex();
        return entry;
    }

    public void Rename(Guid id, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Signature name is required.", nameof(newName));

        var index = _entries.FindIndex(e => e.Id == id);
        if (index < 0)
            throw new KeyNotFoundException($"No signature with id {id}.");

        _entries[index] = _entries[index] with { Name = newName.Trim() };
        SaveIndex();
    }

    public void Remove(Guid id)
    {
        var entry = _entries.Find(e => e.Id == id)
            ?? throw new KeyNotFoundException($"No signature with id {id}.");

        _entries.Remove(entry);
        SaveIndex();
        if (File.Exists(entry.PngPath))
            File.Delete(entry.PngPath);
    }

    private List<SignatureEntry> Load()
    {
        if (!File.Exists(_indexPath))
            return [];
        var entries = JsonSerializer.Deserialize<List<SignatureEntry>>(File.ReadAllText(_indexPath)) ?? [];
        // Drop index entries whose image file has gone missing rather than surfacing broken thumbnails.
        return entries.Where(e => File.Exists(e.PngPath)).ToList();
    }

    private void SaveIndex() =>
        AtomicFileWriter.Write(_indexPath, s => JsonSerializer.Serialize(s, _entries, IndexJsonOptions));

    private static readonly JsonSerializerOptions IndexJsonOptions = new() { WriteIndented = true };
}
