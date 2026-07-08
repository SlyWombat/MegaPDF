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
    }
}
