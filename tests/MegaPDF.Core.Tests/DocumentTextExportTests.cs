using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #386 stage 1: <see cref="IPdfDocument.WriteText"/>/<see cref="IPdfDocument.WriteMarkdown"/>,
/// the managed wrapper over megapdf_write_text() (#142, #355, #357), against the same contract-9
/// fixtures and golden files core/tests/core_tests.cpp's own write_text/write_markdown golden
/// tests use (core/CMakeLists.txt points MEGAPDF_REPO_FIXTURES at this project's own Fixtures
/// directory, so the native and managed tests read literally the same .pdf files).
///
/// The native goldens were written with <c>megapdf_write_options{}</c> — every field zero, i.e.
/// megapdf-cli's own defaults (page_break = MEGAPDF_PAGE_BREAK_FORM_FEED). This binding's public
/// default (<see cref="DocumentWriteOptions.Default"/>) instead defaults to
/// <see cref="DocumentPageBreak.None"/> for the GUI Save As use case (see that type's own doc
/// comment for why), so the golden comparisons below pass <see cref="NativeDefaultOptions"/>
/// explicitly — proving the binding reaches the same native call the CLI does, and that the GUI
/// default is a deliberate, overridable choice rather than a hardcoded departure from the
/// contract's fixtures.
/// </summary>
public class DocumentTextExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-text-export-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>megapdf_write_options{} (native, all-zero) as this binding's options type — what
    /// megapdf-cli's own default `extract` run passes, and what the golden files were written with.</summary>
    private static readonly DocumentWriteOptions NativeDefaultOptions = new() { PageBreak = DocumentPageBreak.FormFeed };

    private static readonly string[] GoldenCases =
    [
        "columns", "furniture", "lists", "headings", "tabular-headings", "xobject-text", "scan", "mixed",
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MegaPDF.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "structure", name + ".pdf");

    private static string GoldenPath(string name, string extension) =>
        Path.Combine(RepoRoot(), "core", "tests", "expected", "structure", name + "." + extension);

    public static IEnumerable<object[]> Cases() => GoldenCases.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(Cases))]
    public void WriteText_MatchesTheCliGoldenExactly(string name)
    {
        var engine = new PdfiumEngine();
        using var doc = engine.Open(FixturePath(name));

        using var target = new MemoryStream();
        var pagesWithText = doc.WriteText(target, options: NativeDefaultOptions);

        var expected = File.ReadAllBytes(GoldenPath(name, "txt"));
        Assert.Equal(expected, target.ToArray());
        Assert.True(pagesWithText >= 0);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void WriteMarkdown_MatchesTheCliGoldenExactly(string name)
    {
        var engine = new PdfiumEngine();
        using var doc = engine.Open(FixturePath(name));

        using var target = new MemoryStream();
        var pagesWithText = doc.WriteMarkdown(target, options: NativeDefaultOptions);

        var expected = File.ReadAllBytes(GoldenPath(name, "md"));
        Assert.Equal(expected, target.ToArray());
        Assert.True(pagesWithText >= 0);
    }

    /// <summary>
    /// The Markdown writer renders MEGAPDF_PAGE_BREAK_FORM_FEED and _NONE identically (neither a
    /// form feed nor "--- page N ---" belongs in CommonMark; megapdf_write_text.cpp's own
    /// AppendPageSeparatorMarkdown comment says so). So the GUI default
    /// (<see cref="DocumentWriteOptions.Default"/>, page break None) must produce Markdown
    /// byte-identical to the CLI's own form-feed default over a genuinely multi-page fixture —
    /// this is the one golden case where the GUI default needs no override at all.
    /// </summary>
    [Fact]
    public void WriteMarkdown_DefaultOptions_StillMatchTheGoldenOnAMultiPageFixture()
    {
        var engine = new PdfiumEngine();
        using var doc = engine.Open(FixturePath("furniture"));
        Assert.True(doc.PageCount > 1, "the page-break behaviour this test checks needs more than one page");

        using var target = new MemoryStream();
        doc.WriteMarkdown(target, options: DocumentWriteOptions.Default);

        var expected = File.ReadAllBytes(GoldenPath("furniture", "md"));
        Assert.Equal(expected, target.ToArray());
    }

    /// <summary>
    /// Unlike Markdown, the plain-text writer's form feed and "no separator" cases render
    /// differently (AppendPageSeparatorText: "\f\n" vs "\n"). The GUI default replaces the CLI's
    /// form feed with a blank line — the type's own doc comment explains why a form feed reads as
    /// nothing at all outside a terminal — so the default output is the golden with every
    /// "\f\n" collapsed to "\n", not the golden itself.
    /// </summary>
    [Fact]
    public void WriteText_DefaultOptions_ReplacesFormFeedsWithBlankLines()
    {
        var engine = new PdfiumEngine();
        using var doc = engine.Open(FixturePath("furniture"));
        Assert.True(doc.PageCount > 1, "the page-break behaviour this test checks needs more than one page");

        using var target = new MemoryStream();
        doc.WriteText(target, options: DocumentWriteOptions.Default);

        var golden = File.ReadAllText(GoldenPath("furniture", "txt"));
        Assert.Contains('\f', golden);
        var expectedWithBlankLines = golden.Replace("\f\n", "\n");

        var actual = System.Text.Encoding.UTF8.GetString(target.ToArray());
        Assert.DoesNotContain('\f', actual);
        Assert.Equal(expectedWithBlankLines, actual);
    }

    [Fact]
    public void WriteText_OnASinglePageRange_CoversOnlyThatPage()
    {
        var engine = new PdfiumEngine();
        using var doc = engine.Open(FixturePath("furniture"));
        Assert.True(doc.PageCount > 1);

        using var whole = new MemoryStream();
        doc.WriteText(whole, options: NativeDefaultOptions);

        using var firstPageOnly = new MemoryStream();
        doc.WriteText(firstPageOnly, firstPage: 0, pageCount: 1, options: NativeDefaultOptions);

        var wholeText = System.Text.Encoding.UTF8.GetString(whole.ToArray());
        var firstPageText = System.Text.Encoding.UTF8.GetString(firstPageOnly.ToArray());

        // The whole document has content from every page; a one-page range only page 1's.
        Assert.Contains("page 1", wholeText);
        Assert.Contains("page 2", wholeText);
        Assert.Contains("page 3", wholeText);
        Assert.Contains("page 1", firstPageText);
        Assert.DoesNotContain("page 2", firstPageText);
        Assert.DoesNotContain("page 3", firstPageText);
        Assert.NotEqual(wholeText, firstPageText);
    }

    /// <summary>
    /// None of the golden fixtures above carry a form field (their .blocks goldens have no FIELD
    /// entries), so this uses the same synthetic AcroForm PDF <see cref="AcroFormTests"/> does
    /// (an empty text field "FullName", an unchecked checkbox "Agree") — unfilled, on purpose:
    /// MEGAPDF_WRITE_FIELDS_FILLED (the GUI default) reports nothing for either, so this is also
    /// the case that tells Filled and All apart.
    /// </summary>
    [Fact]
    public void WriteText_FieldsOption_ControlsWhetherUnfilledFieldsAppear()
    {
        var path = Path.Combine(_dir, "form.pdf");
        File.WriteAllBytes(path, SamplePdf.BuildWithForm());
        var engine = new PdfiumEngine();
        using var doc = engine.Open(path);

        using var allFields = new MemoryStream();
        doc.WriteText(allFields, options: NativeDefaultOptions with { Fields = DocumentWriteFields.All });
        var withAll = System.Text.Encoding.UTF8.GetString(allFields.ToArray());

        using var filledOnly = new MemoryStream();
        doc.WriteText(filledOnly, options: NativeDefaultOptions with { Fields = DocumentWriteFields.Filled });
        var withFilledOnly = System.Text.Encoding.UTF8.GetString(filledOnly.ToArray());

        using var noFields = new MemoryStream();
        doc.WriteText(noFields, options: NativeDefaultOptions with { Fields = DocumentWriteFields.None });
        var withNone = System.Text.Encoding.UTF8.GetString(noFields.ToArray());

        Assert.Contains("FullName", withAll);
        Assert.Contains("Agree", withAll);

        // FILLED (the default, both here and for the GUI): an empty text field and an unchecked
        // box are not "filled", so neither shows up — same as asking for none at all.
        Assert.DoesNotContain("FullName", withFilledOnly);
        Assert.DoesNotContain("Agree", withFilledOnly);
        Assert.DoesNotContain("FullName", withNone);
        Assert.DoesNotContain("Agree", withNone);
        Assert.Equal(withNone, withFilledOnly);
    }

    [Fact]
    public void WriteMarkdown_OutOfRangePageThrows()
    {
        var engine = new PdfiumEngine();
        using var doc = engine.Open(FixturePath("headings"));

        using var target = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.WriteMarkdown(target, firstPage: doc.PageCount + 1));
    }

    /// <summary>
    /// The handle lifecycle this binds (megapdf_structure_load/_free) is entirely internal to
    /// megapdf_write_text(); this repeats a write many times over the same document to give an
    /// external leak checker (LeakSanitizer/valgrind under `dotnet test`, or a memory profiler)
    /// many chances to see a leak that a single call would not expose. It is not itself a leak
    /// detector — the CI job that runs this suite under a checked allocator is what makes it one.
    /// </summary>
    [Fact]
    public void WriteMarkdown_RepeatedCalls_DoNotAccumulateOutputOrObviouslyLeak()
    {
        var engine = new PdfiumEngine();
        using var doc = engine.Open(FixturePath("mixed"));

        byte[]? first = null;
        for (var i = 0; i < 50; i++)
        {
            using var target = new MemoryStream();
            doc.WriteMarkdown(target, options: NativeDefaultOptions);
            var bytes = target.ToArray();
            first ??= bytes;
            Assert.Equal(first, bytes);
        }
    }
}
