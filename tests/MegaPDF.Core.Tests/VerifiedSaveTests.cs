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
        var before = Directory.GetFiles(Path.GetTempPath(), "megapdf-verify-*.pdf").Length;

        var source = WriteSample();
        using var document = _engine.Open(source);
        VerifiedSave.ToPath(_engine, document, Path.Combine(_dir, "tidy.pdf"));

        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "megapdf-verify-*.pdf").Length);
    }
}
