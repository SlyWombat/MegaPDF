using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

public class PasswordTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-password-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteEncryptedPdf(string password)
    {
        var path = Path.Combine(_dir, $"locked-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, SamplePdf.BuildEncrypted(password));
        return path;
    }

    [Fact]
    public void Open_WithoutPassword_ThrowsPasswordError()
    {
        var path = WriteEncryptedPdf("hunter2");
        var ex = Assert.Throws<PdfLoadException>(() => _engine.Open(path));
        Assert.True(ex.IsPasswordError);
    }

    [Fact]
    public void Open_WithWrongPassword_ThrowsPasswordError()
    {
        var path = WriteEncryptedPdf("hunter2");
        var ex = Assert.Throws<PdfLoadException>(() => _engine.Open(path, "wrong"));
        Assert.True(ex.IsPasswordError);
    }

    [Fact]
    public void Open_WithCorrectPassword_ReadsAndRendersContent()
    {
        var path = WriteEncryptedPdf("hunter2");
        using var doc = _engine.Open(path, "hunter2");
        Assert.Equal(1, doc.PageCount);
        using var page = doc.GetPage(0);
        Assert.Equal("Hello MegaPDF", Assert.Single(page.GetTextRuns()).Text);
    }

    [Fact]
    public void UnprotectedFile_IgnoresSuppliedPassword()
    {
        var path = Path.Combine(_dir, "open.pdf");
        File.WriteAllBytes(path, SamplePdf.Build());
        using var doc = _engine.Open(path, "irrelevant");
        Assert.Equal(1, doc.PageCount);
    }
}
