using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #45 — a text box must keep its identity when tier-2 font substitution rebuilds
/// the underlying object.
///
/// SDD §6.2 contract 4 makes the `id` param the handle every reversible mobile
/// edit addresses. A substitution that re-added the mark but dropped the params
/// turned a box created on a phone into one that phone would then refuse to
/// select, reporting it as written by an older version — when in fact a desktop
/// edit had silently downgraded it.
/// </summary>
public class TextBoxSubstitutionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-subst-").FullName;
    private readonly PdfiumEngine _engine = new();

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Write(byte[] bytes)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void SubstitutingTheFontOfATextBox_KeepsItsIdAndFace()
    {
        const string id = "text:fixture-subset";
        var path = Write(SamplePdf.BuildSubsetTextBox(id, StandardTextBoxFonts.Serif, "abc"));
        string savedPath;

        using (var document = _engine.Open(path))
        {
            using (var page = document.GetPage(0))
            {
                var box = page.GetTextBoxes().Single();
                Assert.Equal(id, box.TextBoxId);
                Assert.Equal(StandardTextBoxFonts.Serif, box.TextBoxFont);

                // "xyz" needs glyphs the subset never drew, so tier 2 fires and the
                // object is rebuilt in a standard font — the path that used to drop
                // the params.
                var outcome = page.SetTextRunText(box, "xyz");
                Assert.Equal(TextEditOutcome.EditedWithSubstitutedFont, outcome);
            }

            savedPath = Path.Combine(_dir, "substituted.pdf");
            using var file = File.Create(savedPath);
            document.Save(file);
        }

        using var reopened = _engine.Open(savedPath);
        using var reopenedPage = reopened.GetPage(0);
        var survivor = reopenedPage.GetTextBoxes()
            .SingleOrDefault(b => b.Text.Contains("xyz", StringComparison.Ordinal));

        Assert.NotNull(survivor);
        // The identity, which is the whole point: without it both phones refuse to
        // select the box and blame an older version.
        Assert.Equal(id, survivor.TextBoxId);
        // And the face, which would otherwise silently revert to Helvetica (#43).
        Assert.Equal(StandardTextBoxFonts.Serif, survivor.TextBoxFont);
    }

    [Fact]
    public void SubstitutingOrdinaryBodyText_DoesNotMakeItATextBox()
    {
        // The guard cuts both ways: rebuilding a run that was never a MegaPDF box
        // must not tag it as one, or ordinary text becomes draggable.
        var path = Write(SamplePdf.BuildWithSubsetFont("abc"));
        using var document = _engine.Open(path);
        using var page = document.GetPage(0);

        var run = page.GetTextRuns().Single(r => r.Text.Contains("abc", StringComparison.Ordinal));
        page.SetTextRunText(run, "xyz");

        Assert.Empty(page.GetTextBoxes());
    }
}
