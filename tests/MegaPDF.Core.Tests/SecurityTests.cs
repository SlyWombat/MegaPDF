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

    /// <summary>
    /// The credentials the #241 fixtures were written with
    /// (tools/gen_security_fixtures.sh). Test data.
    /// </summary>
    public static TheoryData<string, string?, string> RemoveFixtures() => new()
    {
        { "remove-rc4-40.pdf", "u-remove-40", "o-remove-40" },
        { "remove-rc4-128.pdf", "u-remove-128", "o-remove-128" },
        { "remove-aes-128.pdf", "u-remove-a128", "o-remove-a128" },
        { "remove-aes-256.pdf", "u-remove-a256", "o-remove-a256" },
        { "remove-owner-only.pdf", null, "o-remove-owner" },
        { "remove-user-owner.pdf", "u-remove-both", "o-remove-both" },
        { "remove-objstm-aes256.pdf", "u-remove-x256", "o-remove-x256" },
        { "remove-objstm-rc4-128.pdf", "u-remove-x128", "o-remove-x128" },
    };

    /// <summary>
    /// #241: through the same verified save the desktops use, removing protection must
    /// leave a plain file — no /Encrypt and nothing of the security handler anywhere in
    /// it, not even as an object nothing points at — and change nothing a reader sees.
    /// </summary>
    [Theory]
    [MemberData(nameof(RemoveFixtures))]
    public void RemovingProtection_WritesAPlainFile_AndChangesNothing(string file, string? user, string owner)
    {
        string[] Text(IPdfDocument d)
        {
            using var page = d.GetPage(0);
            return [.. page.GetTextRuns().Select(r => r.Text)];
        }
        string[] Fields(IPdfDocument d)
        {
            using var page = d.GetPage(0);
            return [.. page.GetFormFields().Select(f => $"{f.Kind}:{f.Name}={f.Value}:{f.IsChecked}")];
        }
        string[] Stamps(IPdfDocument d)
        {
            using var page = d.GetPage(0);
            return [.. page.GetStamps().Select(s => s.Id)];
        }

        var stripped = Path.Combine(_dir, $"stripped-{file}");
        string[] text, fields, stamps;
        int pages;
        using (var asOwner = _engine.Open(Fixture(file), owner))
        {
            Assert.True(asOwner.Security.IsEncrypted);
            Assert.True(asOwner.Security.HasFullAccess);
            (pages, text, fields, stamps) = (asOwner.PageCount, Text(asOwner), Fields(asOwner), Stamps(asOwner));
            VerifiedSave.ToPathWithoutSecurity(_engine, asOwner, stripped);
        }

        // The file itself, with the whitespace taken out so a dictionary written over
        // several lines is still found.
        var flat = new string([.. File.ReadAllText(stripped, System.Text.Encoding.Latin1)
            .Where(c => c is not (' ' or '\r' or '\n' or '\t'))]);
        Assert.DoesNotContain("/Encrypt", flat);
        Assert.DoesNotContain("/Filter/Standard", flat);
        Assert.DoesNotContain("/StdCF", flat);
        Assert.DoesNotContain("/Perms", flat);

        using var plain = _engine.Open(stripped);
        Assert.Equal(PdfSecurity.Unprotected, plain.Security);
        Assert.Equal(pages, plain.PageCount);
        Assert.Equal(text, Text(plain));
        Assert.Equal(fields, Fields(plain));
        Assert.Equal(stamps, Stamps(plain));
        // The fixture really carries all three, or the three comparisons prove nothing.
        Assert.NotEmpty(text);
        Assert.Equal(2, fields.Length);
        Assert.Equal(2, stamps.Length);

        // An open that is not the owner's may not do this.
        if (user is not null || file.Contains("owner-only"))
        {
            using var lesser = _engine.Open(Fixture(file), user);
            if (!lesser.Security.HasFullAccess)
                Assert.Throws<DocumentRestrictedException>(() =>
                    VerifiedSave.ToPathWithoutSecurity(_engine, lesser, Path.Combine(_dir, "refused.pdf")));
        }
    }

    /// <summary>
    /// #246: the copy's trailer must name an /Encrypt object that is in the copy.
    /// unused-tail.pdf's highest object number is one nothing refers to, which is where
    /// the writer used to number the dictionary differently from the trailer naming it —
    /// and then every reader but MegaPDF called the file unencrypted while its streams
    /// were enciphered.
    /// </summary>
    [Fact]
    public void NewSecurity_NamesAnEncryptObjectThatIsInTheFile()
    {
        var source = Path.Combine(_dir, "unused-tail.pdf");
        File.WriteAllBytes(source, SamplePdf.BuildWithUnusedTailObject());
        var locked = Path.Combine(_dir, "tail-locked.pdf");
        using (var doc = _engine.Open(source))
        {
            Assert.Equal(PdfSecurity.Unprotected, doc.Security);
            VerifiedSave.ToPathWithSecurity(_engine, doc, locked, "tail-user", "tail-owner", PdfPermissions.Print);
        }

        var bytes = File.ReadAllText(locked, System.Text.Encoding.Latin1);
        var reference = System.Text.RegularExpressions.Regex.Matches(bytes, @"/Encrypt\s+(\d+)\s+0\s+R")
            .Select(m => m.Groups[1].Value).LastOrDefault();
        Assert.NotNull(reference);
        Assert.Matches($@"[\r\n]{reference} 0 obj", bytes);

        Assert.True(Assert.Throws<PdfLoadException>(() => _engine.Open(locked)).IsPasswordError);
        using var asOwner = _engine.Open(locked, "tail-owner");
        Assert.Equal(6, asOwner.Security.Revision);
        Assert.True(asOwner.Security.HasFullAccess);
        using var page = asOwner.GetPage(0);
        Assert.Contains("nobody refers to", string.Concat(page.GetTextRuns().Select(r => r.Text)));
    }
}
