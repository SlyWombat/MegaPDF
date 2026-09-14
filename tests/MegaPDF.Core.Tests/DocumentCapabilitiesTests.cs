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
    public void Modify_GatesContentEditingShrinkAndTextBoxes_AndNotFilling()
    {
        var caps = Restricted(PdfPermissions.Modify);
        Assert.True(caps.CanEditContent);
        Assert.True(caps.CanShrink);
        Assert.True(caps.CanAddText);
        Assert.False(caps.CanSign);
        Assert.False(caps.CanFillForms);
        Assert.False(caps.CanPrint);
        Assert.False(caps.CanChangeSecurity);
        Assert.True(caps.IsRestricted);
    }

    [Fact]
    public void FillForms_AllowsFillingInEverything_ButNotChangingTheDocument()
    {
        // A form that allows filling lets people fill in everything it offers.
        var caps = Restricted(PdfPermissions.FillForms);
        Assert.True(caps.CanFillForms);
        Assert.True(caps.CanSign);
        Assert.True(caps.CanAddText);
        Assert.False(caps.CanEditContent);
        Assert.False(caps.CanShrink);
        Assert.False(caps.CanPrint);
        Assert.False(caps.CanChangeSecurity);
        Assert.True(caps.IsRestricted);
    }

    [Fact]
    public void Annotate_AllowsTheSameAsFillForms()
    {
        // Annotate implies form filling (ISO 32000).
        Assert.Equal(Restricted(PdfPermissions.FillForms), Restricted(PdfPermissions.Annotate));
    }

    [Fact]
    public void Print_GatesPrintingOnly()
    {
        var caps = Restricted(PdfPermissions.Print);
        Assert.True(caps.CanPrint);
        Assert.False(caps.CanEditContent);
        Assert.False(caps.CanSign);
        Assert.False(caps.CanFillForms);
        Assert.False(caps.CanAddText);
    }

    [Fact]
    public void EveryBitWithoutTheOwner_AllowsEveryToolButChangingSecurity()
    {
        var caps = Restricted(PdfPermissions.All);
        Assert.True(caps.CanEditContent && caps.CanSign && caps.CanFillForms && caps.CanAddText && caps.CanPrint && caps.CanShrink);
        Assert.False(caps.CanChangeSecurity);
        Assert.True(caps.IsRestricted);
    }

    [Fact]
    public void Unprotected_AllowsEverything_AndIsNotRestricted()
    {
        var everything = new DocumentCapabilities(true, true, true, true, true, true, true, false);
        Assert.Equal(everything, DocumentCapabilities.Unprotected);
        Assert.Equal(everything, DocumentCapabilities.From(PdfSecurity.Unprotected));
    }

    [Fact]
    public void OwnerOnlyFixture_OpensRestrictedWithNothingAllowed()
    {
        using var doc = _engine.Open(Fixture("owner-only.pdf"));
        var caps = DocumentCapabilities.From(doc.Security);
        Assert.Equal(new DocumentCapabilities(false, false, false, false, false, false, false, IsRestricted: true), caps);
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
    [InlineData(PageHitKind.FormCheckbox, false, true)]
    [InlineData(PageHitKind.FormTextField, false, true)]
    [InlineData(PageHitKind.DrawnCheckbox, false, true)]
    [InlineData(PageHitKind.StampAnnotation, false, true)]
    [InlineData(PageHitKind.TextRun, true, false)]
    [InlineData(PageHitKind.TextBox, true, true)]
    [InlineData(PageHitKind.Whiteout, true, false)]
    public void PageRegions_NeedTheirPermission(PageHitKind kind, bool opensForModify, bool opensForFilling)
    {
        var nothing = Restricted(PdfPermissions.None);
        Assert.False(nothing.Allows(kind));
        Assert.True(nothing.Allows(PageHitKind.None));

        Assert.Equal(opensForModify, Restricted(PdfPermissions.Modify).Allows(kind));
        // Annotate implies form filling, so both bits open the same regions.
        Assert.Equal(opensForFilling, Restricted(PdfPermissions.FillForms).Allows(kind));
        Assert.Equal(opensForFilling, Restricted(PdfPermissions.Annotate).Allows(kind));
        Assert.True(DocumentCapabilities.Unprotected.Allows(kind));
    }

    [Fact]
    public void TextBoxRegion_NeedsCanAddText_AndNothingElse()
    {
        var onlyTextBoxes = new DocumentCapabilities(false, false, false, CanAddText: true, false, false, false, true);
        Assert.True(onlyTextBoxes.Allows(PageHitKind.TextBox));
        Assert.False(onlyTextBoxes.Allows(PageHitKind.TextRun));
        Assert.False(onlyTextBoxes.Allows(PageHitKind.Whiteout));

        var allButTextBoxes = DocumentCapabilities.Unprotected with { CanAddText = false };
        Assert.False(allButTextBoxes.Allows(PageHitKind.TextBox));
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
        IPageEditOperation addText = new AddTextBoxOperation(null!, 0, "Hi", 12, new PdfPoint(10, 10));
        IPageEditOperation retype = new LineEditOperation(null!, 0, null!, "Hello");

        var forms = Restricted(PdfPermissions.FillForms);
        Assert.True(forms.Allows(toggle));
        Assert.True(forms.Allows(mark));
        Assert.True(forms.Allows(unsign));
        Assert.True(forms.Allows(addText));
        Assert.False(forms.Allows(cover));
        Assert.False(forms.Allows(retype));

        var annotate = Restricted(PdfPermissions.Annotate);
        Assert.True(annotate.Allows(toggle));
        Assert.True(annotate.Allows(mark));
        Assert.True(annotate.Allows(unsign));
        Assert.True(annotate.Allows(addText));
        Assert.False(annotate.Allows(cover));
        Assert.False(annotate.Allows(retype));

        var modify = Restricted(PdfPermissions.Modify);
        Assert.True(modify.Allows(cover));
        Assert.True(modify.Allows(retype));
        Assert.True(modify.Allows(addText));
        Assert.False(modify.Allows(mark));
        Assert.False(modify.Allows(unsign));
        Assert.False(modify.Allows(toggle));
    }

    [Fact]
    public void TextBoxOperations_NeedCanAddText()
    {
        var run = new PdfTextRun(3, "Hi", new PdfRect(10, 10, 20, 12), "Helvetica", 12, TextBoxId: "text:1");
        IPageEditOperation[] textBoxOperations =
        [
            new AddTextBoxOperation(null!, 0, "Hi", 12, new PdfPoint(10, 10)),
            new MoveTextBoxOperation(null!, 0, 3, run.Bounds, new PdfRect(30, 30, 20, 12)),
            new RestyleTextBoxOperation(null!, 0, 3, run, "Hello", "Helvetica", 14),
            new RemoveTextBoxOperation(null!, 0, 3, run),
        ];

        var onlyTextBoxes = new DocumentCapabilities(false, false, false, CanAddText: true, false, false, false, true);
        var allButTextBoxes = DocumentCapabilities.Unprotected with { CanAddText = false };
        foreach (var operation in textBoxOperations)
        {
            Assert.True(onlyTextBoxes.Allows(operation), operation.GetType().Name);
            Assert.False(allButTextBoxes.Allows(operation), operation.GetType().Name);
        }

        // Everything else that changes the page still needs modify.
        IPageEditOperation cover = new AddWhiteoutOperation(null!, 0, run.Bounds);
        Assert.False(onlyTextBoxes.Allows(cover));
    }
}
