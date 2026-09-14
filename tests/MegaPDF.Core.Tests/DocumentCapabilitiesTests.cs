using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #131: ADR-004's permission table as the desktops apply it — which tools each open of
/// a document may use, from the bits alone and from the committed fixtures.
/// </summary>
public sealed class DocumentCapabilitiesTests : IDisposable
{
    private readonly PdfiumEngine _engine = new();

    public void Dispose() => _engine.Dispose();

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "security", name);

    private static DocumentCapabilities Restricted(PdfPermissions permissions) =>
        DocumentCapabilities.From(new PdfSecurity(true, 6, permissions, HasFullAccess: false));

    [Fact]
    public void Modify_GatesContentEditingAndShrink_AndNothingElse()
    {
        var caps = Restricted(PdfPermissions.Modify);
        Assert.True(caps.CanEditContent);
        Assert.True(caps.CanShrink);
        Assert.False(caps.CanSign);
        Assert.False(caps.CanFillForms);
        Assert.False(caps.CanPrint);
        Assert.False(caps.CanChangeSecurity);
        Assert.True(caps.IsRestricted);
    }

    [Fact]
    public void Annotate_GatesSigning_AndAlsoAllowsForms()
    {
        var caps = Restricted(PdfPermissions.Annotate);
        Assert.True(caps.CanSign);
        Assert.True(caps.CanFillForms);
        Assert.False(caps.CanEditContent);
        Assert.False(caps.CanShrink);
        Assert.False(caps.CanPrint);
    }

    [Fact]
    public void FillForms_GatesFormsOnly()
    {
        var caps = Restricted(PdfPermissions.FillForms);
        Assert.True(caps.CanFillForms);
        Assert.False(caps.CanSign);
        Assert.False(caps.CanEditContent);
    }

    [Fact]
    public void Print_GatesPrintingOnly()
    {
        var caps = Restricted(PdfPermissions.Print);
        Assert.True(caps.CanPrint);
        Assert.False(caps.CanEditContent);
        Assert.False(caps.CanSign);
        Assert.False(caps.CanFillForms);
    }

    [Fact]
    public void EveryBitWithoutTheOwner_AllowsEveryToolButChangingSecurity()
    {
        var caps = Restricted(PdfPermissions.All);
        Assert.True(caps.CanEditContent && caps.CanSign && caps.CanFillForms && caps.CanPrint && caps.CanShrink);
        Assert.False(caps.CanChangeSecurity);
        Assert.True(caps.IsRestricted);
    }

    [Fact]
    public void Unprotected_AllowsEverything_AndIsNotRestricted()
    {
        var everything = new DocumentCapabilities(true, true, true, true, true, true, false);
        Assert.Equal(everything, DocumentCapabilities.Unprotected);
        Assert.Equal(everything, DocumentCapabilities.From(PdfSecurity.Unprotected));
    }

    [Fact]
    public void OwnerOnlyFixture_OpensRestrictedWithNothingAllowed()
    {
        using var doc = _engine.Open(Fixture("owner-only.pdf"));
        var caps = DocumentCapabilities.From(doc.Security);
        Assert.Equal(new DocumentCapabilities(false, false, false, false, false, false, IsRestricted: true), caps);
    }

    [Fact]
    public void OwnerOnlyFixture_WithItsOwnerPassword_AllowsEverything()
    {
        using var doc = _engine.Open(Fixture("owner-only.pdf"), "o-restricted");
        var caps = DocumentCapabilities.From(doc.Security);
        Assert.Equal(DocumentCapabilities.Unprotected, caps);
    }

    [Fact]
    public void UnprotectedDocument_AllowsEverything()
    {
        var dir = Directory.CreateTempSubdirectory("megapdf-capabilities-").FullName;
        try
        {
            var path = Path.Combine(dir, "plain.pdf");
            File.WriteAllBytes(path, SamplePdf.Build());
            using var doc = _engine.Open(path);
            Assert.Equal(DocumentCapabilities.Unprotected, DocumentCapabilities.From(doc.Security));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(PageHitKind.FormCheckbox, false, false, true)]
    [InlineData(PageHitKind.FormTextField, false, false, true)]
    [InlineData(PageHitKind.DrawnCheckbox, false, true, false)]
    [InlineData(PageHitKind.StampAnnotation, false, true, false)]
    [InlineData(PageHitKind.TextRun, true, false, false)]
    [InlineData(PageHitKind.TextBox, true, false, false)]
    [InlineData(PageHitKind.Whiteout, true, false, false)]
    public void PageRegions_NeedTheirPermission(PageHitKind kind, bool needsModify, bool needsAnnotate, bool needsForms)
    {
        var nothing = Restricted(PdfPermissions.None);
        Assert.False(nothing.Allows(kind));
        Assert.True(nothing.Allows(PageHitKind.None));

        Assert.Equal(needsModify, Restricted(PdfPermissions.Modify).Allows(kind));
        // Annotate also allows forms, so a form region opens for either bit.
        Assert.Equal(needsAnnotate || needsForms, Restricted(PdfPermissions.Annotate).Allows(kind));
        Assert.Equal(needsForms, Restricted(PdfPermissions.FillForms).Allows(kind));
        Assert.True(DocumentCapabilities.Unprotected.Allows(kind));
    }

    [Fact]
    public void Operations_NeedTheirPermission()
    {
        // The gate reads only the operation's type; nothing here is applied.
        var rect = new PdfRect(10, 10, 20, 20);
        IPageEditOperation toggle = new CheckboxToggleOperation(null!, 0, null!);
        IPageEditOperation mark = new AddMarkOperation(null!, 0, rect);
        IPageEditOperation unsign = new RemoveSignatureOperation(null!, 0, "sig:1", rect);
        IPageEditOperation cover = new AddWhiteoutOperation(null!, 0, rect);

        var forms = Restricted(PdfPermissions.FillForms);
        Assert.True(forms.Allows(toggle));
        Assert.False(forms.Allows(mark));
        Assert.False(forms.Allows(cover));

        var annotate = Restricted(PdfPermissions.Annotate);
        Assert.True(annotate.Allows(toggle));
        Assert.True(annotate.Allows(mark));
        Assert.True(annotate.Allows(unsign));
        Assert.False(annotate.Allows(cover));

        var modify = Restricted(PdfPermissions.Modify);
        Assert.True(modify.Allows(cover));
        Assert.False(modify.Allows(mark));
        Assert.False(modify.Allows(toggle));
    }
}
