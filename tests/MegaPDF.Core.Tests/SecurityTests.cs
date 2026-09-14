using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #131: the committed encrypted fixtures (tools/gen_security_fixtures.sh) through the
/// desktop engine — what each open may do — and copies saved with new security or none.
/// </summary>
[Collection("temp-staging")]
public sealed class SecurityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-security-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "security", name);

    [Theory]
    [InlineData("rc4-40.pdf", 2, "u-rc4-40", "o-rc4-40")]
    [InlineData("rc4-128.pdf", 3, "u-rc4-128", "o-rc4-128")]
    [InlineData("aes-128.pdf", 4, "u-aes-128", "o-aes-128")]
    [InlineData("aes-256.pdf", 6, "u-aes-256", "o-aes-256")]
    [InlineData("nonascii-aes-256.pdf", 6, "clé-été", "o-nonascii")]
    [InlineData("nonascii-rc4-128.pdf", 3, "clé", "o-nonascii-rc4")]
    [InlineData("metadata-clear.pdf", 6, "u-meta", "o-meta")]
    public void EveryHandler_OpensWithItsPasswords_AndReportsItsSecurity(string file, int revision, string user, string owner)
    {
        Assert.True(Assert.Throws<PdfLoadException>(() => _engine.Open(Fixture(file))).IsPasswordError);

        using (var asUser = _engine.Open(Fixture(file), user))
        {
            Assert.True(asUser.Security.IsEncrypted);
            Assert.Equal(revision, asUser.Security.Revision);
            using var page = asUser.GetPage(0);
            Assert.Contains("MegaPDF", string.Concat(page.GetTextRuns().Select(r => r.Text)));
        }

        using var asOwner = _engine.Open(Fixture(file), owner);
        Assert.True(asOwner.Security.HasFullAccess);
        Assert.Equal(PdfPermissions.All, asOwner.Security.Permissions);
    }

    [Fact]
    public void OwnerOnlyDocument_OpensRestricted_AndOnlyTheOwnerMayChangeItsSecurity()
    {
        var stripped = Path.Combine(_dir, "stripped.pdf");
        using (var plain = _engine.Open(Fixture("owner-only.pdf")))
        {
            var security = plain.Security;
            Assert.True(security.IsEncrypted);
            Assert.False(security.HasFullAccess);
            Assert.False(security.Allows(PdfPermissions.Print));
            Assert.False(security.Allows(PdfPermissions.Modify));
            Assert.False(security.Allows(PdfPermissions.Copy));
            Assert.False(security.Allows(PdfPermissions.FillForms));

            Assert.Throws<DocumentRestrictedException>(() => VerifiedSave.ToPathWithoutSecurity(_engine, plain, stripped));
            Assert.Throws<DocumentRestrictedException>(() =>
                VerifiedSave.ToPathWithSecurity(_engine, plain, stripped, "x", null, PdfPermissions.All));
            Assert.False(File.Exists(stripped));
        }

        using var owner = _engine.Open(Fixture("owner-only.pdf"), "o-restricted");
        VerifiedSave.ToPathWithoutSecurity(_engine, owner, stripped);
        using var reopened = _engine.Open(stripped);
        Assert.Equal(PdfSecurity.Unprotected, reopened.Security);
    }

    [Fact]
    public void NewSecurity_NeedsItsPassword_GrantsOnlyWhatWasAllowed_AndTheOwnerCanChangeIt()
    {
        var source = Path.Combine(_dir, "plain.pdf");
        File.WriteAllBytes(source, SamplePdf.Build());
        var locked = Path.Combine(_dir, "locked.pdf");
        using (var doc = _engine.Open(source))
        {
            Assert.Equal(PdfSecurity.Unprotected, doc.Security);
            VerifiedSave.ToPathWithSecurity(_engine, doc, locked, "new-user", "new-owner", PdfPermissions.Print);
        }

        Assert.True(Assert.Throws<PdfLoadException>(() => _engine.Open(locked)).IsPasswordError);
        using (var asUser = _engine.Open(locked, "new-user"))
        {
            Assert.Equal(new PdfSecurity(true, 6, PdfPermissions.Print, false), asUser.Security);
            using var page = asUser.GetPage(0);
            Assert.Equal("Hello MegaPDF", Assert.Single(page.GetTextRuns()).Text);
        }

        var changed = Path.Combine(_dir, "changed.pdf");
        using (var asOwner = _engine.Open(locked, "new-owner"))
        {
            Assert.True(asOwner.Security.HasFullAccess);
            VerifiedSave.ToPathWithSecurity(_engine, asOwner, changed, "changed", null, PdfPermissions.All);
        }

        Assert.True(Assert.Throws<PdfLoadException>(() => _engine.Open(changed, "new-user")).IsPasswordError);
        using var again = _engine.Open(changed, "changed");
        Assert.True(again.Security.HasFullAccess);
    }
}
