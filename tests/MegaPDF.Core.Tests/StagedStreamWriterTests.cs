using System.Text;
using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// The macOS App Sandbox save path (ADR-002). The interesting cases are the two
/// ways this differs from <see cref="AtomicFileWriter"/>: it writes through a
/// stream the host already holds, and it must not leave the tail of a longer
/// previous document behind.
/// </summary>
/// <summary>
/// Both classes create and delete temp files matching a shared glob, and
/// VerifiedSave calls StagedStreamWriter internally — so run in parallel they
/// race on each other's counts. This is what an xUnit collection is for: it
/// serialises them without weakening either assertion.
/// </summary>
[CollectionDefinition("temp-staging")]
public sealed class TempStagingCollection;

[Collection("temp-staging")]
public class StagedStreamWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Write_FillsAnEmptyDestination()
    {
        var path = Path.Combine(_dir, "new.pdf");
        using (var destination = File.Create(path))
            StagedStreamWriter.Write(destination, s => s.Write(Encoding.UTF8.GetBytes("content")));

        Assert.Equal("content", File.ReadAllText(path));
    }

    [Fact]
    public void Write_ShorterContent_TruncatesTheTailOfTheOldDocument()
    {
        // The failure this guards against is a real one: writing in place over a
        // longer file leaves trailing bytes, and a PDF with garbage after %%EOF is
        // exactly the kind of thing that opens here and fails in Acrobat.
        var path = Path.Combine(_dir, "shrinking.pdf");
        File.WriteAllText(path, "a much longer previous document");

        using (var destination = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            StagedStreamWriter.Write(destination, s => s.Write(Encoding.UTF8.GetBytes("short")));

        Assert.Equal("short", File.ReadAllText(path));
    }

    [Fact]
    public void Write_WhenContentCallbackThrows_NeverTouchesTheDestination()
    {
        // The whole point of staging: serialising is where a crash or a full disk is
        // likely, and the user's file must not have been opened for truncation yet.
        var path = Path.Combine(_dir, "victim.pdf");
        File.WriteAllText(path, "original");

        using (var destination = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            Assert.Throws<InvalidOperationException>(() =>
                StagedStreamWriter.Write(destination, s =>
                {
                    s.Write(Encoding.UTF8.GetBytes("partial garbage"));
                    throw new InvalidOperationException("simulated failure mid-write");
                }));
        }

        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public void Write_LeavesNoStagingFilesBehind()
    {
        var before = Directory.GetFiles(Path.GetTempPath(), "megapdf-save-*.tmp").Length;

        using (var destination = new MemoryStream())
            StagedStreamWriter.Write(destination, s => s.Write(Encoding.UTF8.GetBytes("x")));

        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "megapdf-save-*.tmp").Length);
    }
}
