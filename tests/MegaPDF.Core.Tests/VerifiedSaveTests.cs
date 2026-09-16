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
        // Its own staging folder: other test classes save in parallel through the shared temp
        // folder, so a count there raced them (seen as a one-off failure while a battery ran).
        var staging = Directory.CreateDirectory(Path.Combine(_dir, "staging")).FullName;
        var source = WriteSample();
        using var document = _engine.Open(source);
        VerifiedSave.StagingDirectoryForTests = staging;
        try
        {
            VerifiedSave.ToPath(_engine, document, Path.Combine(_dir, "tidy.pdf"));
        }
        finally
        {
            VerifiedSave.StagingDirectoryForTests = null;
        }

        Assert.Empty(Directory.GetFiles(staging));
        Assert.True(File.Exists(Path.Combine(_dir, "tidy.pdf")));
    }
}
