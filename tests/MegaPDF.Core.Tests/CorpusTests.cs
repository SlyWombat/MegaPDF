using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// Smoke-tests every PDF in tests/corpus (git-ignored; SDD §5.6 private corpus):
/// open → render first page → save → reopen. Passes trivially when the folder is
/// empty so public contributors aren't blocked.
/// </summary>
public class CorpusTests
{
    private static string? FindCorpusDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "corpus");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void EveryCorpusDocument_OpensRendersAndRoundTrips()
    {
        var corpus = FindCorpusDirectory();
        if (corpus is null)
            return;
        var files = Directory.GetFiles(corpus, "*.pdf");
        if (files.Length == 0)
            return;

        var engine = new PdfiumEngine();
        var scratch = Directory.CreateTempSubdirectory("megapdf-corpus-");
        try
        {
            foreach (var file in files)
            {
                try
                {
                    using var doc = engine.Open(file);
                    Assert.True(doc.PageCount > 0, $"{Path.GetFileName(file)}: no pages");

                    using (var page = doc.GetPage(0))
                    {
                        var rendered = page.Render((int)page.Width, (int)page.Height);
                        Assert.Equal(rendered.PixelWidth * rendered.PixelHeight * 4, rendered.Bgra.Length);
                    }

                    var savedPath = Path.Combine(scratch.FullName, Path.GetFileName(file));
                    using (var stream = File.Create(savedPath))
                        doc.Save(stream);
                    using var reopened = engine.Open(savedPath);
                    Assert.Equal(doc.PageCount, reopened.PageCount);
                }
                catch (PdfLoadException ex) when (ex.IsPasswordError)
                {
                    // Protected corpus files are fine — they exercise the error path.
                }
                catch (Exception ex)
                {
                    Assert.Fail($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }
}
