using System.Text.Json;

namespace MegaPDF.Core.Services;

public sealed record SignatureEntry(Guid Id, string Name, string PngPath, DateTime CreatedUtc);

/// <summary>
/// The user's signature library (SDD §3.3): local-only PNGs with alpha plus a JSON index,
/// stored per-user. Soft limit of 20 entries.
/// <para>
/// This is the reference the iOS and Android ports follow (#333), on the three points they
/// had drifted on: the soft limit above, dropping index entries whose image has gone missing
/// (see <see cref="SignatureLibrary.Load"/>), and the delete order in
/// <see cref="SignatureLibrary.Remove"/>.
/// </para>
/// </summary>
public interface ISignatureLibrary
{
    IReadOnlyList<SignatureEntry> All { get; }

    /// <summary>
    /// Index entries whose image file was not there at the last load or <see cref="Reload"/>
    /// (#402). They are not in <see cref="All"/> — nothing can show or place them — but a
    /// flyout can still list them by name and say the image is gone, rather than silently
    /// dropping a signature the user remembers adding. <see cref="Remove"/> forgets one.
    /// </summary>
    IReadOnlyList<SignatureEntry> Missing { get; }

    /// <summary>
    /// Re-reads the index from disk, so a library changed underneath a running app — a sync
    /// tool, a restore, a file deleted by hand — is what the next flyout shows (#402). An
    /// index that cannot be read (half-written, or not JSON) keeps what was already loaded;
    /// an index that is not there is an empty library.
    /// </summary>
    void Reload();

    SignatureEntry Add(string name, ReadOnlyMemory<byte> pngBytes);
    void Rename(Guid id, string newName);
    void Remove(Guid id);
}

public sealed class SignatureLibrary : ISignatureLibrary
{
    public const int SoftLimit = 20;

    private readonly string _directory;
    private readonly string _indexPath;
    private List<SignatureEntry> _entries;
    private List<SignatureEntry> _missing;

    /// <param name="directory">
    /// Storage directory; defaults to <c>Signatures</c> in the user-data folder
    /// (<see cref="UserDataPaths"/>, SDD §3.3). Injectable for tests.
    /// </param>
    public SignatureLibrary(string? directory = null)
    {
        _directory = directory ?? UserDataPaths.InAppFolder("Signatures");
        Directory.CreateDirectory(_directory);
        _indexPath = Path.Combine(_directory, "index.json");
        // A first load that cannot be read starts empty: there is nothing to keep, and an
        // app that will not start over its own index file is the worse outcome.
        (_entries, _missing) = Load() ?? ([], []);
    }

    public IReadOnlyList<SignatureEntry> All => _entries;

    public IReadOnlyList<SignatureEntry> Missing => _missing;

    public void Reload()
    {
        if (Load() is var (entries, missing))
            (_entries, _missing) = (entries, missing);
    }

    public SignatureEntry Add(string name, ReadOnlyMemory<byte> pngBytes)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Signature name is required.", nameof(name));
        if (_entries.Count >= SoftLimit)
            throw new InvalidOperationException($"The signature library is limited to {SoftLimit} signatures.");

        var id = Guid.NewGuid();
        var pngPath = Path.Combine(_directory, $"{id:N}.png");
        Directory.CreateDirectory(_directory); // the folder itself may have gone with the images
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

    /// <summary>
    /// Takes the entry out of the index and then deletes the PNG — the order all three
    /// platforms use (#333). A failure between the two leaves an orphan image, which nothing
    /// shows; the other order would leave an index entry naming a file that is gone, which
    /// <see cref="Load"/> can only drop rather than repair.
    /// <para>
    /// A <see cref="Missing"/> entry is forgotten the same way: written out of the index, with
    /// no image left to delete.
    /// </para>
    /// </summary>
    public void Remove(Guid id)
    {
        if (_missing.Find(e => e.Id == id) is { } gone)
        {
            _missing.Remove(gone);
            SaveIndex();
            return;
        }

        var entry = _entries.Find(e => e.Id == id)
            ?? throw new KeyNotFoundException($"No signature with id {id}.");

        _entries.Remove(entry);
        SaveIndex();
        if (File.Exists(entry.PngPath))
            File.Delete(entry.PngPath);
    }

    /// <summary>
    /// The index as it is on disk, split into entries whose image is there and entries
    /// whose image has gone missing — the latter are kept apart rather than surfacing as
    /// broken thumbnails. Null when the index exists but cannot be read, so the caller
    /// decides what that means; an absent index is an empty library.
    /// </summary>
    private (List<SignatureEntry> Entries, List<SignatureEntry> Missing)? Load()
    {
        if (!File.Exists(_indexPath))
            return ([], []);

        List<SignatureEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<SignatureEntry>>(File.ReadAllText(_indexPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Half-written by whatever replaced it, or not the index at all (#402).
            return null;
        }

        // An entry with no path at all (a hand-edited or foreign index) is neither present
        // nor missing: there is nothing to name.
        var readable = (entries ?? []).Where(e => e is { PngPath.Length: > 0 }).ToList();
        return (readable.Where(e => File.Exists(e.PngPath)).ToList(),
                readable.Where(e => !File.Exists(e.PngPath)).ToList());
    }

    /// <summary>
    /// Writes <see cref="All"/> only: an entry whose image is gone leaves the index at the
    /// next save, as it always has.
    /// </summary>
    private void SaveIndex()
    {
        Directory.CreateDirectory(_directory);
        AtomicFileWriter.Write(_indexPath, s => JsonSerializer.Serialize(s, _entries, IndexJsonOptions));
    }

    private static readonly JsonSerializerOptions IndexJsonOptions = new() { WriteIndented = true };
}
