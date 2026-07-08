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
}
