using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #476, #481: <see cref="Engine.IPdfDocument.IsSigned"/>/<c>IsSignedCertification</c>, the
/// desktop apps' one binding of <c>megapdf_document_flags()</c>'s
/// <c>MEGAPDF_DOC_SIGNED</c>/<c>MEGAPDF_DOC_SIGNED_CERTIFICATION</c> bits
/// (core/tests/core_tests.cpp's <c>test_signature_detection</c> already covers the bits
/// themselves at the C level; this is the same fact read through <see cref="PdfiumEngine"/>,
/// which is what both desktop view models actually call).
///
/// <see cref="SamplePdf.BuildSignedApproval"/>/<see cref="SamplePdf.BuildSignedCertified"/>
/// mirror <c>tools/gen_signature_fixtures.py</c>'s <c>signed-approval.pdf</c>/
/// <c>signed-certified.pdf</c> byte for byte in shape — a second, independent
/// implementation of the same fixtures, so agreement between the two is itself evidence
/// the detection rule reads PDFium's own answer rather than something incidental to one
/// generator's byte layout.
/// </summary>
public sealed class SignatureDetectionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-sig-detect-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void ApprovalSignature_SetsIsSigned_ButNotCertification()
    {
        using var doc = _engine.Open(Write("signed-approval.pdf", SamplePdf.BuildSignedApproval()));
        Assert.True(doc.IsSigned);
        Assert.False(doc.IsSignedCertification);

        // #481's whole point: the document still opens, reports a plausible page count,
        // and saving is unaffected at the engine level — the warning is a UI-level
        // concern, not a refusal here.
        Assert.Equal(1, doc.PageCount);
        using var ms = new MemoryStream();
        doc.Save(ms);
        Assert.True(ms.Length > 0);
    }

    [Fact]
    public void CertificationSignature_SetsBothIsSignedAndCertification()
    {
        using var doc = _engine.Open(Write("signed-certified.pdf", SamplePdf.BuildSignedCertified()));
        Assert.True(doc.IsSigned);
        Assert.True(doc.IsSignedCertification);
    }

    [Fact]
    public void OrdinaryDocument_DoesNotReportSigned()
    {
        using var doc = _engine.Open(Write("plain.pdf", SamplePdf.Build()));
        Assert.False(doc.IsSigned);
        Assert.False(doc.IsSignedCertification);
    }

    [Fact]
    public void HybridXfaDocument_DoesNotReportSigned()
    {
        // A form document with real AcroForm content, but no /Sig, must not be confused
        // with a signed one (the same "not everything with an AcroForm is special" shape
        // #456/#457 already checks for dynamic XFA).
        using var doc = _engine.Open(Write("hybrid-xfa.pdf", SamplePdf.BuildHybridXfa()));
        Assert.False(doc.IsSigned);
        Assert.False(doc.IsSignedCertification);
    }
}
