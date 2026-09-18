using MegaPDF.Core.Recovery;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #145: a launch with a file after a crash offers recovery first, then opens the file —
/// unless the restore already opened that same document with its recovered edits.
/// </summary>
public class LaunchedDocumentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-launch-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Pdf(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "%PDF-1.7");
        return path;
    }

    [Fact]
    public void NothingOpen_TheLaunchedFileIsOpened()
    {
        // No crashed session, the offer was declined, or a restore failed to open anything.
        Assert.True(LaunchedDocument.NeedsOpening(Pdf("lease.pdf"), openDocumentPath: null));
    }

    [Fact]
    public void RestoredTheSameDocument_ItIsNotOpenedAgain()
    {
        // Opening it again would ask to save the edits just recovered, or drop them.
        var lease = Pdf("lease.pdf");
        Assert.False(LaunchedDocument.NeedsOpening(lease, openDocumentPath: lease));
    }

    [Fact]
    public void RestoredAnotherDocument_TheLaunchedFileIsStillOpened()
    {
        // The open itself asks about the recovered document's unsaved changes (D5).
        Assert.True(LaunchedDocument.NeedsOpening(Pdf("lease.pdf"), openDocumentPath: Pdf("invoice.pdf")));
    }

    [Fact]
    public void SameFile_SeesThroughRelativeAndDottedPaths()
    {
        var lease = Pdf("lease.pdf");
        var dotted = Path.Combine(_dir, "sub", "..", "lease.pdf");
        Assert.True(LaunchedDocument.SameFile(lease, dotted));

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _dir;
            Assert.False(LaunchedDocument.NeedsOpening("lease.pdf", openDocumentPath: lease));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void SameFile_IgnoresCaseWhereTheFileSystemDoes()
    {
        var lease = Pdf("lease.pdf");
        var shouted = Path.Combine(_dir, "LEASE.PDF");
        // Windows and macOS resolve both spellings to one file; Linux keeps them apart.
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
                     LaunchedDocument.SameFile(lease, shouted));
    }
}
