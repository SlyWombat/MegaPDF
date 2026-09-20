using System.Text;
using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

public class AtomicFileWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Write_CreatesNewFile()
    {
        var path = Path.Combine(_dir, "new.pdf");

        AtomicFileWriter.Write(path, s => s.Write(Encoding.UTF8.GetBytes("content")));

        Assert.Equal("content", File.ReadAllText(path));
    }

    [Fact]
    public void Write_ReplacesExistingFile()
    {
        var path = Path.Combine(_dir, "existing.pdf");
        File.WriteAllText(path, "old");

        AtomicFileWriter.Write(path, s => s.Write(Encoding.UTF8.GetBytes("new")));

        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void Write_WhenContentCallbackThrows_LeavesOriginalIntact_AndNoTempFiles()
    {
        var path = Path.Combine(_dir, "victim.pdf");
        File.WriteAllText(path, "original");

        Assert.Throws<InvalidOperationException>(() =>
            AtomicFileWriter.Write(path, s =>
            {
                s.Write(Encoding.UTF8.GetBytes("partial garbage"));
                throw new InvalidOperationException("simulated failure mid-write");
            }));

        Assert.Equal("original", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.megapdf-tmp"));
    }

    [Fact]
    public void SwapInPlace_RenamesTheFileItIsGiven()
    {
        var temp = Path.Combine(_dir, ".verified.megapdf-tmp");
        File.WriteAllText(temp, "verified content");
        var path = Path.Combine(_dir, "out.pdf");

        AtomicFileWriter.SwapInPlace(path, temp);

        Assert.Equal("verified content", File.ReadAllText(path));
        // Consumed by the rename, not copied: nothing of the caller's file is left behind.
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void SwapInPlace_ReplacesAnExistingFile()
    {
        var path = Path.Combine(_dir, "existing.pdf");
        File.WriteAllText(path, "old");
        var temp = Path.Combine(_dir, ".verified.megapdf-tmp");
        File.WriteAllText(temp, "new");

        AtomicFileWriter.SwapInPlace(path, temp);

        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(temp));
    }

    /// <summary>
    /// The rule the swap rests on (#334): a rename happens within one file system, so a file from
    /// anywhere else is refused rather than quietly copied into place by <see cref="File.Move"/>.
    /// </summary>
    [Fact]
    public void SwapInPlace_RefusesAFileFromAnotherDirectory()
    {
        var elsewhere = Directory.CreateDirectory(Path.Combine(_dir, "elsewhere")).FullName;
        var temp = Path.Combine(elsewhere, "staged.megapdf-tmp");
        File.WriteAllText(temp, "staged");
        var path = Path.Combine(_dir, "out.pdf");

        var error = Assert.Throws<InvalidOperationException>(() => AtomicFileWriter.SwapInPlace(path, temp));

        Assert.Contains("own directory", error.Message);
        // Nothing happened: the destination is untouched and the caller still owns its file.
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(temp));
    }

    [Fact]
    public void SameDirectory_TellsBesideFromElsewhere()
    {
        var path = Path.Combine(_dir, "out.pdf");
        var beside = Path.Combine(_dir, ".staged.megapdf-tmp");
        var elsewhere = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "other")).FullName, "staged");

        Assert.True(AtomicFileWriter.SameDirectory(path, beside));
        Assert.False(AtomicFileWriter.SameDirectory(path, elsewhere));
    }

    /// <summary>
    /// What <c>ToPath_SwapsTheStagedCopy_MakingNoSecondTempFile</c> reads as the absence of (#334):
    /// this is the call it is watching for, so it has to be one that happens.
    /// </summary>
    [Fact]
    public void Write_ReportsTheTempFileItMakes()
    {
        var made = new List<string>();
        AtomicFileWriter.TempFileCreatedForTests = made.Add;
        try
        {
            AtomicFileWriter.Write(Path.Combine(_dir, "out.pdf"), s => s.Write(Encoding.UTF8.GetBytes("content")));
        }
        finally
        {
            AtomicFileWriter.TempFileCreatedForTests = null;
        }

        var temp = Assert.Single(made);
        Assert.Equal(_dir, Path.GetDirectoryName(temp));
        Assert.EndsWith(".megapdf-tmp", temp);
    }
}
