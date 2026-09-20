using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #56: a save must not replace the original with bytes no reader will accept.
/// Atomicity and validity are different guarantees, and only one of them was
/// covered before.
/// </summary>
[Collection("temp-staging")]
public class VerifiedSaveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-verified-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string WriteSample()
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.Build());
        return path;
    }

    [Fact]
    public void ToPath_ProtectedDocument_SavesAndStaysProtectedWithTheSameUnlock()
    {
        // #132: the verification reopened the staged copy without the document's
        // password. The copy is still encrypted, so every protected save failed.
        const string unlock = "hunter2";
        var source = Path.Combine(_dir, "protected.pdf");
        File.WriteAllBytes(source, SamplePdf.BuildEncrypted(unlock));
        var destination = Path.Combine(_dir, "protected-out.pdf");
        using var document = _engine.Open(source, unlock);

        VerifiedSave.ToPath(_engine, document, destination);

        var locked = Assert.Throws<PdfLoadException>(() => _engine.Open(destination));
        Assert.True(locked.IsPasswordError);
        using var reopened = _engine.Open(destination, unlock);
        Assert.Equal(document.PageCount, reopened.PageCount);
    }

    [Fact]
    public void ToPath_WritesADocumentThatReopens()
    {
        var source = WriteSample();
        var destination = Path.Combine(_dir, "out.pdf");
        using var document = _engine.Open(source);

        VerifiedSave.ToPath(_engine, document, destination);

        using var reopened = _engine.Open(destination);
        Assert.Equal(document.PageCount, reopened.PageCount);
    }

    [Fact]
    public void ToStream_WritesADocumentThatReopens()
    {
        var source = WriteSample();
        var destination = Path.Combine(_dir, "out-stream.pdf");
        using var document = _engine.Open(source);

        using (var file = File.Create(destination))
            VerifiedSave.ToStream(_engine, document, file);

        using var reopened = _engine.Open(destination);
        Assert.Equal(document.PageCount, reopened.PageCount);
    }

    [Fact]
    public void ToPath_OverTheFileTheDocumentReads_ReplacesItAndTheDocumentReadsOn()
    {
        // #147: the document is read from its file on demand. The desktop save replaces that
        // file (File.Replace), which leaves the open document on the bytes it was opened on.
        var source = WriteSample();
        using var document = _engine.Open(source);

        VerifiedSave.ToPath(_engine, document, source);
        Assert.False(document.ReadsFile(source), "the file was replaced, not written in place");
        VerifiedSave.ToPath(_engine, document, source);

        using var reopened = _engine.Open(source);
        Assert.Equal(document.PageCount, reopened.PageCount);
    }

    [Fact]
    public void ToStagedFile_WriteOverInPlace_AfterReadFromCopy_KeepsTheDocumentWhole()
    {
        // #147: the macOS sandbox can only write the opened file in place. The save is staged in a
        // file (not built in memory), the document moves onto a copy of its file, and the staged
        // save is written over the original, twice.
        var source = WriteSample();
        using var document = _engine.Open(source);
        Assert.True(document.ReadsFile(source));
        Assert.False(document.ReadsFile(WriteSample()), "identical bytes elsewhere are another file");

        for (var round = 0; round < 2; round++)
        {
            string stagedPath;
            using (var staged = VerifiedSave.ToStagedFile(_engine, document))
            {
                stagedPath = staged.Path;
                Assert.True(File.Exists(stagedPath));
                Assert.True(staged.Length > 0);
                if (document.ReadsFile(source))
                    document.ReadFromCopy();
                Assert.False(document.ReadsFile(source));
                using var destination = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                destination.Write(new byte[destination.Length + 4096]);   // what was there was longer: WriteOver cuts it off
                staged.WriteOver(destination);
            }
            Assert.False(File.Exists(stagedPath), "disposing the staged copy deletes it");

            using var page = document.GetPage(document.PageCount - 1);
            using var reopened = _engine.Open(source);
            Assert.Equal(document.PageCount, reopened.PageCount);
        }
    }

    [Fact]
    public void ToPath_WhenTheOutputIsUnreadable_LeavesTheOriginalIntact()
    {
        // The whole point: a save that cannot be read back must not have replaced
        // anything. A disposed document serialises nothing usable, which is the
        // cheapest way to produce that state deliberately.
        var source = WriteSample();
        var destination = Path.Combine(_dir, "precious.pdf");
        File.WriteAllText(destination, "the original, which must survive");

        var document = _engine.Open(source);
        document.Dispose();

        Assert.ThrowsAny<Exception>(() => VerifiedSave.ToPath(_engine, document, destination));
        Assert.Equal("the original, which must survive", File.ReadAllText(destination));
    }

    [Fact]
    public void Staging_LeavesNoTemporaryFilesBehind()
    {
        // Its own folder to save into, and its own stand-in for the temp folder: other test
        // classes save in parallel through the shared temp folder, so a count there raced
        // them (seen as a one-off failure while a battery ran).
        var staging = Directory.CreateDirectory(Path.Combine(_dir, "staging")).FullName;
        var into = Directory.CreateDirectory(Path.Combine(_dir, "tidy")).FullName;
        var source = WriteSample();
        using var document = _engine.Open(source);
        VerifiedSave.StagingDirectoryForTests = staging;
        try
        {
            VerifiedSave.ToPath(_engine, document, Path.Combine(into, "tidy.pdf"));
        }
        finally
        {
            VerifiedSave.StagingDirectoryForTests = null;
        }

        // The staged copy and AtomicFileWriter's own sibling both live in the destination's
        // folder now (#193), and both are cleaned up: nothing there but the document.
        Assert.Equal(Path.Combine(into, "tidy.pdf"), Assert.Single(Directory.GetFiles(into)));
        Assert.Empty(Directory.GetFiles(staging));
    }

    [Fact]
    public void ToPath_StagesInTheDestinationsOwnFolder()
    {
        // #193: the staged copy is a whole second copy of the document, and in the system
        // temp folder it lands on a filesystem nobody chose — on Fedora /tmp is a tmpfs sized
        // at half of RAM, so saving a large document was an allocation of memory the size of
        // the document and failed with 67 GB free where it was being saved to. The
        // destination's folder is on the same filesystem as the destination by definition,
        // which is also what keeps AtomicFileWriter's swap a rename.
        var staging = Directory.CreateDirectory(Path.Combine(_dir, "staging")).FullName;
        var into = Directory.CreateDirectory(Path.Combine(_dir, "into")).FullName;
        var source = WriteSample();
        using var document = _engine.Open(source);

        var (beside, inTemp) = FilesWhileVerifying(into, staging, onStage =>
            VerifiedSave.ToPath(_engine, document, Path.Combine(into, "out.pdf"), onStage));

        var staged = Assert.Single(beside);
        Assert.True(staged.Length > 0, "the staged copy is the document, written out");
        Assert.Empty(inTemp);
        using var reopened = _engine.Open(Path.Combine(into, "out.pdf"));
        Assert.Equal(document.PageCount, reopened.PageCount);
    }

    [Fact]
    public void ToStream_StagesInTheTempFolder_NotBesideTheDestination()
    {
        // The macOS sandbox path (#147): the app is granted the file the person picked, not
        // the folder it is in, so it may not create a sibling of it at all. #193 must not
        // touch this one.
        var staging = Directory.CreateDirectory(Path.Combine(_dir, "staging")).FullName;
        var into = Directory.CreateDirectory(Path.Combine(_dir, "into")).FullName;
        var destination = Path.Combine(into, "out.pdf");
        var source = WriteSample();
        using var document = _engine.Open(source);

        var (beside, inTemp) = FilesWhileVerifying(into, staging, onStage =>
        {
            using var file = File.Create(destination);
            VerifiedSave.ToStream(_engine, document, file, onStage);
        });

        Assert.Single(inTemp);
        Assert.Equal(destination, Assert.Single(beside).Path);
        using var reopened = _engine.Open(destination);
        Assert.Equal(document.PageCount, reopened.PageCount);
    }

    [Fact]
    public void ToStagedFile_StagesInTheTempFolder_NotBesideAnything()
    {
        // The other sandbox path: the host is handed a staged file and writes it over a
        // stream it already holds. There is no destination folder to be beside (#193).
        var staging = Directory.CreateDirectory(Path.Combine(_dir, "staging")).FullName;
        var source = WriteSample();
        using var document = _engine.Open(source);

        VerifiedSave.StagingDirectoryForTests = staging;
        try
        {
            using var staged = VerifiedSave.ToStagedFile(_engine, document);
            Assert.Equal(staging, Path.GetDirectoryName(staged.Path));
            Assert.True(staged.Length > 0);
        }
        finally
        {
            VerifiedSave.StagingDirectoryForTests = null;
        }
    }

    [Fact]
    public void ToPath_WhenTheDestinationsFolderIsGone_StillStagesAndVerifies()
    {
        // Staging beside the destination must not become a new way for a save to fail: a
        // folder that is not there — a stale path, an unplugged disk, a share that has gone —
        // falls back to the temp folder, and the save fails where it always did, on the
        // write, having got as far as proving the bytes were readable.
        var staging = Directory.CreateDirectory(Path.Combine(_dir, "staging")).FullName;
        var gone = Path.Combine(_dir, "no-such-folder");
        var source = WriteSample();
        using var document = _engine.Open(source);

        var stages = new List<VerifiedSave.SaveStage>();
        string[] inTempWhileVerifying = [];
        VerifiedSave.StagingDirectoryForTests = staging;
        try
        {
            Assert.ThrowsAny<Exception>(() => VerifiedSave.ToPath(_engine, document, Path.Combine(gone, "out.pdf"), stage =>
            {
                stages.Add(stage);
                if (stage == VerifiedSave.SaveStage.Verifying)
                    inTempWhileVerifying = Directory.GetFiles(staging);
            }));
        }
        finally
        {
            VerifiedSave.StagingDirectoryForTests = null;
        }

        Assert.Contains(VerifiedSave.SaveStage.Verifying, stages);
        Assert.Single(inTempWhileVerifying);
        Assert.Empty(Directory.GetFiles(staging));
        Assert.False(Directory.Exists(gone));
    }

    /// <summary>
    /// #334: the staged copy is beside the destination and it is the file that is renamed into
    /// place, so a save to a path holds one copy of the document on disk rather than two. The
    /// staging file is the only temp file, it is the one that becomes the destination, and
    /// <see cref="AtomicFileWriter"/>'s own temp file is never made.
    /// </summary>
    [Fact]
    public void ToPath_SwapsTheStagedCopy_MakingNoSecondTempFile()
    {
        var into = Directory.CreateDirectory(Path.Combine(_dir, "into")).FullName;
        var destination = Path.Combine(into, "out.pdf");
        var source = WriteSample();
        using var document = _engine.Open(source);

        string[] beside = [];
        var made = new List<string>();
        AtomicFileWriter.TempFileCreatedForTests = made.Add;
        try
        {
            VerifiedSave.ToPath(_engine, document, destination, stage =>
            {
                if (stage == VerifiedSave.SaveStage.Verifying)
                    beside = Directory.GetFiles(into);
            });
        }
        finally
        {
            AtomicFileWriter.TempFileCreatedForTests = null;
        }

        var staged = Assert.Single(beside);
        Assert.Empty(made);
        Assert.Contains("megapdf-verify", Path.GetFileName(staged));
        // Renamed into place, not copied and left: nothing of the staged file is still there.
        Assert.False(File.Exists(staged));
        Assert.True(File.Exists(destination));
        using var reopened = _engine.Open(destination);
        Assert.Equal(document.PageCount, reopened.PageCount);
    }

    [Fact]
    public void ToPath_WhenTheDestinationsFolderRefusesNewFiles_FallsBackToTheTempFolder()    {
        // The Linux shape of the same thing: a folder that is read-only to us. Windows denies
        // through an ACL rather than a mode, and a user who overrides the mode (root in a
        // container) is not denied at all, so this proves the folder really does refuse
        // before it asks anything of the save.
        var staging = Directory.CreateDirectory(Path.Combine(_dir, "staging")).FullName;
        var into = Directory.CreateDirectory(Path.Combine(_dir, "read-only")).FullName;
        if (!NowRefusesNewFiles(into))
            return;

        var source = WriteSample();
        using var document = _engine.Open(source);
        var stages = new List<VerifiedSave.SaveStage>();
        string[] inTempWhileVerifying = [];
        VerifiedSave.StagingDirectoryForTests = staging;
        try
        {
            Assert.ThrowsAny<Exception>(() => VerifiedSave.ToPath(_engine, document, Path.Combine(into, "out.pdf"), stage =>
            {
                stages.Add(stage);
                if (stage == VerifiedSave.SaveStage.Verifying)
                    inTempWhileVerifying = Directory.GetFiles(staging);
            }));
        }
        finally
        {
            VerifiedSave.StagingDirectoryForTests = null;
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(into, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Contains(VerifiedSave.SaveStage.Verifying, stages);
        Assert.Single(inTempWhileVerifying);
        Assert.Empty(Directory.GetFiles(staging));
    }

    /// <summary>
    /// What is in the destination's folder and in the stand-in temp folder at the moment the
    /// staged copy has been written and is about to be read back — the only moment at which
    /// where a save stages is observable from outside it, since the staged copy is gone by
    /// the time the save returns. Sizes are read there too, for the same reason.
    /// </summary>
    private static (StagedFile[] Beside, StagedFile[] InTemp) FilesWhileVerifying(
        string into, string staging, Action<Action<VerifiedSave.SaveStage>> save)
    {
        StagedFile[] beside = [], inTemp = [];
        VerifiedSave.StagingDirectoryForTests = staging;
        try
        {
            save(stage =>
            {
                if (stage != VerifiedSave.SaveStage.Verifying)
                    return;
                beside = Snapshot(into);
                inTemp = Snapshot(staging);
            });
        }
        finally
        {
            VerifiedSave.StagingDirectoryForTests = null;
        }
        return (beside, inTemp);

        static StagedFile[] Snapshot(string directory) =>
            [.. Directory.GetFiles(directory).Select(f => new StagedFile(f, new FileInfo(f).Length))];
    }

    private readonly record struct StagedFile(string Path, long Length);

    /// <summary>
    /// Makes <paramref name="directory"/> refuse new files and says whether it now does.
    /// False on Windows, where the mode is not the mechanism, and for a user the mode does
    /// not apply to.
    /// </summary>
    private static bool NowRefusesNewFiles(string directory)
    {
        if (OperatingSystem.IsWindows())
            return false;
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var probe = Path.Combine(directory, "probe");
        try
        {
            using (File.Create(probe)) { }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return true;
        }
        File.Delete(probe);
        return false;
    }
}
