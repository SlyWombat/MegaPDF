using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #456, #457: <see cref="Engine.IPdfDocument.IsDynamicXfa"/>, the desktop apps' one
/// binding of <c>megapdf_document_flags()</c>'s <c>MEGAPDF_DOC_DYNAMIC_XFA</c> bit
/// (core/tests/core_tests.cpp's <c>test_dynamic_xfa</c> already covers the bit itself at
/// the C level; this is the same fact read through <see cref="PdfiumEngine"/>, which is
/// what both desktop view models actually call).
///
/// <see cref="SamplePdf.BuildDynamicXfa"/>/<see cref="SamplePdf.BuildHybridXfa"/> mirror
/// <c>tools/gen_xfa_fixtures.py</c>'s <c>dynamic-xfa.pdf</c>/<c>hybrid-xfa.pdf</c> byte
/// for byte in shape (AcroForm `/XFA`, Catalog `/NeedsRendering` only on the dynamic one,
/// the placeholder text verbatim) — a second, independent implementation of the same
/// fixtures, so agreement between the two is itself evidence the detection rule reads
/// PDFium's own answer rather than something incidental to one generator's byte layout.
/// </summary>
public sealed class DynamicXfaTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-xfa-").FullName;
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
    public void DynamicXfaDocument_ReportsIsDynamicXfa_AndEverythingElseStillWorks()
    {
        using var doc = _engine.Open(Write("dynamic-xfa.pdf", SamplePdf.BuildDynamicXfa()));
        Assert.True(doc.IsDynamicXfa);

        // #457's whole point: the document still opens, reports a plausible page count,
        // and its page still loads and renders — view, print, save, share and page tools
        // are all unaffected by this bit. Only filling in the (nonexistent) real fields
        // is unavailable, which is a UI-level concern, not an engine one.
        Assert.Equal(1, doc.PageCount);
        using var page = doc.GetPage(0);
        Assert.True(page.Width > 0 && page.Height > 0);
        Assert.Empty(page.GetFormFields()); // the real fields live in the XFA template, not here
        using var ms = new MemoryStream();
        doc.Save(ms); // saving a dynamic-XFA document is unaffected
        Assert.True(ms.Length > 0);
    }

    [Fact]
    public void HybridXfaDocument_DoesNotReportIsDynamicXfa()
    {
        using var doc = _engine.Open(Write("hybrid-xfa.pdf", SamplePdf.BuildHybridXfa()));
        Assert.False(doc.IsDynamicXfa);
        using var page = doc.GetPage(0);
        Assert.Contains("Statement of Remuneration", string.Concat(page.GetTextRuns().Select(r => r.Text)));
    }

    [Fact]
    public void OrdinaryDocument_DoesNotReportIsDynamicXfa()
    {
        using var doc = _engine.Open(Write("plain.pdf", SamplePdf.Build()));
        Assert.False(doc.IsDynamicXfa);
    }
}
