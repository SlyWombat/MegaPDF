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
    public void MultiplePathsOpen_NothingMatches_TheLaunchedFileIsOpened()
    {
        // #348 phase 1: several crashed sessions can restore into several tabs at once launch;
        // the launched file must still open when it matches none of them.
        var lease = Pdf("lease.pdf");
        var open = new[] { Pdf("invoice.pdf"), Pdf("statement.pdf") };
        Assert.True(LaunchedDocument.NeedsOpening(lease, open));
    }

    [Fact]
    public void MultiplePathsOpen_TheLaunchedFileMatchesOneTab_ItIsNotOpenedAgain()
    {
        var lease = Pdf("lease.pdf");
        var open = new[] { Pdf("invoice.pdf"), lease, Pdf("statement.pdf") };
        Assert.False(LaunchedDocument.NeedsOpening(lease, open));
    }

    [Fact]
    public void NoTabsOpen_TheLaunchedFileIsOpened()
    {
        Assert.True(LaunchedDocument.NeedsOpening(Pdf("lease.pdf"), Array.Empty<string>()));
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
            // As the process sees its own directory: on macOS the temp folder is behind a
            // symlink (/var -> /private/var), and the working directory comes back resolved.
            var open = Path.Combine(Environment.CurrentDirectory, "lease.pdf");
            Assert.False(LaunchedDocument.NeedsOpening("lease.pdf", openDocumentPath: open));
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

    // --- #348: a window can hold several open tabs at once, where the old code had
    // exactly one open document. ShellViewModel.IsOpen(path) — the router's guard
    // against opening the launched file into a second tab when it is already open
    // in one — is "NeedsOpening against every open tab's path, negated"; these two
    // cases are that check, done the way the router does it. ---

    [Fact]
    public void SeveralTabsOpen_NoneOfThemTheLaunchedFile_ItStillNeedsOpening()
    {
        var lease = Pdf("lease.pdf");
        var openInTabs = new[] { Pdf("invoice.pdf"), Pdf("contract.pdf") };

        Assert.All(openInTabs, open => Assert.True(LaunchedDocument.NeedsOpening(lease, open)));
        // What Router.IsOpen(path) computes: true only once every tab says "not this one".
        Assert.DoesNotContain(openInTabs, open => !LaunchedDocument.NeedsOpening(lease, open));
    }

    [Fact]
    public void SeveralTabsOpen_OneOfThemIsTheLaunchedFile_ItIsAlreadyOpen()
    {
        var lease = Pdf("lease.pdf");
        var openInTabs = new[] { Pdf("invoice.pdf"), lease, Pdf("contract.pdf") };

        // One tab already has it: NeedsOpening is false against that one tab —
        // exactly what makes Router.IsOpen(path) true and the router activate that
        // tab instead of opening a duplicate.
        Assert.Contains(openInTabs, open => !LaunchedDocument.NeedsOpening(lease, open));
    }
}
