using System.Text.Json;
using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

public class SignatureLibraryTests : IDisposable
{
    private static readonly byte[] FakePng = [0x89, 0x50, 0x4E, 0x47];

    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-sig-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Add_StoresPngAndEntry()
    {
        var library = new SignatureLibrary(_dir);

        var entry = library.Add("Dave", FakePng);

        Assert.Single(library.All);
        Assert.Equal("Dave", entry.Name);
        Assert.Equal(FakePng, File.ReadAllBytes(entry.PngPath));
    }

    [Fact]
    public void Library_PersistsAcrossInstances()
    {
        new SignatureLibrary(_dir).Add("Dave", FakePng);

        var reloaded = new SignatureLibrary(_dir);

        Assert.Single(reloaded.All);
        Assert.Equal("Dave", reloaded.All[0].Name);
    }

    [Fact]
    public void Rename_UpdatesEntry()
    {
        var library = new SignatureLibrary(_dir);
        var entry = library.Add("Dave", FakePng);

        library.Rename(entry.Id, "Dave — initials");

        Assert.Equal("Dave — initials", library.All[0].Name);
    }

    [Fact]
    public void Remove_DeletesEntryAndImage()
    {
        var library = new SignatureLibrary(_dir);
        var entry = library.Add("Dave", FakePng);

        library.Remove(entry.Id);

        Assert.Empty(library.All);
        Assert.False(File.Exists(entry.PngPath));
    }

    [Fact]
    public void Add_BeyondSoftLimit_Throws()
    {
        var library = new SignatureLibrary(_dir);
        for (var i = 0; i < SignatureLibrary.SoftLimit; i++)
            library.Add($"Signature {i}", FakePng);

        Assert.Throws<InvalidOperationException>(() => library.Add("One too many", FakePng));
    }

    [Fact]
    public void Add_BlankName_Throws()
    {
        var library = new SignatureLibrary(_dir);
        Assert.Throws<ArgumentException>(() => library.Add("   ", FakePng));
    }

    [Fact]
    public void Load_SkipsEntriesWithMissingImageFiles()
    {
        var library = new SignatureLibrary(_dir);
        var entry = library.Add("Dave", FakePng);
        File.Delete(entry.PngPath);

        var reloaded = new SignatureLibrary(_dir);

        Assert.Empty(reloaded.All);
        Assert.Equal([entry], reloaded.Missing);
    }

    // --- Reload: the library changed on disk under a running app (#402) ---

    [Fact]
    public void Reload_MovesAnEntryWhoseImageWasDeletedToMissing()
    {
        var library = new SignatureLibrary(_dir);
        var kept = library.Add("Kept", FakePng);
        var gone = library.Add("Gone", FakePng);
        File.Delete(gone.PngPath);

        library.Reload();

        Assert.Equal([kept], library.All);
        Assert.Equal([gone], library.Missing);
    }

    [Fact]
    public void Reload_PicksUpAnIndexReplacedOnDisk()
    {
        var library = new SignatureLibrary(_dir);
        library.Add("Before", FakePng);
        // Another writer (a sync tool, a restore) puts a different library in place.
        var pngPath = Path.Combine(_dir, "after.png");
        File.WriteAllBytes(pngPath, FakePng);
        var replacement = new SignatureEntry(Guid.NewGuid(), "After", pngPath, DateTime.UtcNow);
        File.WriteAllText(Path.Combine(_dir, "index.json"), JsonSerializer.Serialize(new[] { replacement }));

        library.Reload();

        Assert.Equal([replacement], library.All);
        Assert.Empty(library.Missing);
    }

    [Fact]
    public void Reload_KeepsWhatItHadWhenTheIndexCannotBeRead()
    {
        var library = new SignatureLibrary(_dir);
        var entry = library.Add("Dave", FakePng);
        File.WriteAllText(Path.Combine(_dir, "index.json"), "[{\"Id\": \"half-writ");

        library.Reload();

        Assert.Equal([entry], library.All);
    }

    [Fact]
    public void Reload_WithNoIndexIsAnEmptyLibrary()
    {
        var library = new SignatureLibrary(_dir);
        library.Add("Dave", FakePng);
        File.Delete(Path.Combine(_dir, "index.json"));

        library.Reload();

        Assert.Empty(library.All);
        Assert.Empty(library.Missing);
    }

    [Fact]
    public void Load_ToleratesAnIndexThatIsNotJson()
    {
        File.WriteAllText(Path.Combine(_dir, "index.json"), "not json at all");

        var library = new SignatureLibrary(_dir);

        Assert.Empty(library.All);
        Assert.Empty(library.Missing);
    }

    [Fact]
    public void Load_IgnoresAnEntryWithoutAPath()
    {
        File.WriteAllText(Path.Combine(_dir, "index.json"), "[{\"Id\": \"a11f0000-0000-4000-8000-000000000001\", \"Name\": \"No path\"}, null]");

        var library = new SignatureLibrary(_dir);

        Assert.Empty(library.All);
        Assert.Empty(library.Missing);
    }

    [Fact]
    public void Remove_ForgetsAMissingEntry()
    {
        var library = new SignatureLibrary(_dir);
        var gone = library.Add("Gone", FakePng);
        File.Delete(gone.PngPath);
        library.Reload();

        library.Remove(gone.Id);

        Assert.Empty(library.Missing);
        Assert.Empty(new SignatureLibrary(_dir).Missing); // written out of the index, not just forgotten in memory
    }

    [Fact]
    public void Add_RecreatesTheFolderWhenItWasRemovedUnderneath()
    {
        var library = new SignatureLibrary(_dir);
        Directory.Delete(_dir, recursive: true);

        var entry = library.Add("Dave", FakePng);

        Assert.True(File.Exists(entry.PngPath));
        Assert.Single(new SignatureLibrary(_dir).All);
    }
}
