using System.Text;
using MegaPDF.Core.Editing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// Applying the marks, through the engine the desktop apps call (#173, #330).
///
/// The #330 coverage map (tests/matrix/coverage.toml, `redact-apply`) found this hole: the C
/// core's apply is proved by <c>core_tests.cpp::test_redaction_fixtures</c> and on a device by
/// Android's <c>RedactionTest</c>, and <see cref="RedactionMarkTests"/> covers everything up to
/// the moment of truth — but no test in this suite called
/// <see cref="IPdfDocument.ApplyRedactions"/> at all. So the one path Windows and both Avalonia
/// desktops actually take to keep the promise was the one path `ci.yml` did not check.
///
/// The assertions follow <c>tools/leakcheck</c>'s discipline rather than the engine's own word:
/// the test does not ask whether the redaction worked, it hunts for the removed string the way
/// another application would — in the text PDFium extracts, and in the saved bytes as ASCII, as
/// UTF-16 and as PDF hex digits. A redaction that reported success while leaving the word in a
/// stream would pass an engine-trusting test and fail this one.
/// </summary>
public class RedactionApplyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("megapdf-redactapply-tests-").FullName;
    private readonly PdfiumEngine _engine = new();

    /// <summary>Distinctive enough that a hit in the bytes cannot be a coincidence.</summary>
    private const string Canary = "ZZCANARYZZ";

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Write(byte[] bytes, string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private IPdfDocument OpenWithCanary() =>
        _engine.Open(Write(SamplePdf.Build(Canary), $"sample-{Guid.NewGuid():N}.pdf"));

    /// <summary>The drawn line's own box, so a drag over it marks the text it covers.</summary>
    private static PdfRect OverTheLine(IPdfDocument doc)
    {
        using var page = doc.GetPage(0);
        return page.GetTextRuns().First().Bounds;
    }

    private string Save(IPdfDocument doc)
    {
        var path = Path.Combine(_dir, $"saved-{Guid.NewGuid():N}.pdf");
        using var stream = File.Create(path);
        doc.Save(stream);
        return path;
    }

    private static string TextOf(IPdfDocument doc, int pageIndex = 0)
    {
        using var page = doc.GetPage(pageIndex);
        return string.Concat(page.GetTextRuns().Select(run => run.Text));
    }

    /// <summary>
    /// Every encoding of <paramref name="word"/> a PDF can hide it in that a plain byte search
    /// for the ASCII would miss: a UTF-16 string, and the hex-string form that is what
    /// <c>&lt;5A5A...&gt;</c> is. Mirrors <c>tools/leakcheck/outside.py</c>'s search set, minus
    /// the parts that need qpdf — this is an xunit test, not the battery.
    /// </summary>
    private static IEnumerable<(string How, byte[] Needle)> Encodings(string word)
    {
        yield return ("ASCII", Encoding.ASCII.GetBytes(word));
        yield return ("UTF-16BE", Encoding.BigEndianUnicode.GetBytes(word));
        yield return ("UTF-16LE", Encoding.Unicode.GetBytes(word));
        yield return ("PDF hex", Encoding.ASCII.GetBytes(Convert.ToHexString(Encoding.ASCII.GetBytes(word))));
        yield return ("PDF hex, lower case",
                      Encoding.ASCII.GetBytes(Convert.ToHexString(Encoding.ASCII.GetBytes(word)).ToLowerInvariant()));
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
            return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
                j++;
            if (j == needle.Length)
                return true;
        }
        return false;
    }

    [Fact]
    public void Apply_RemovesTheMarkedWord_FromTheExtractedTextAndFromTheBytes()
    {
        string saved;
        using (var doc = OpenWithCanary())
        {
            // Before: the word is there to be found, or the test proves nothing.
            Assert.Contains(Canary, TextOf(doc));
            Assert.NotNull(MarkForRedactionOperation.Place(doc, 0, OverTheLine(doc)));

            var report = doc.ApplyRedactions();

            Assert.True(report.Applied,
                "the redaction refused: " + string.Join("; ", report.Refusals.Select(r => $"{r.Reason}: {r.Message}")));
            // The report's own account of what went, which is what the summary shown after
            // saving reads from: one area, on one page, carrying at least the canary.
            Assert.Equal(1, report.Counts.Areas);
            Assert.Equal(1, report.Counts.Pages);
            Assert.True(report.Counts.Characters >= Canary.Length,
                        $"reported {report.Counts.Characters} characters removed, expected at least {Canary.Length}");
            Assert.DoesNotContain(Canary, TextOf(doc));

            saved = Save(doc);
        }

        // Reopened, because what matters is the file another application gets.
        using (var reopened = _engine.Open(saved))
            Assert.DoesNotContain(Canary, TextOf(reopened));

        var bytes = File.ReadAllBytes(saved);
        foreach (var (how, needle) in Encodings(Canary))
            Assert.False(Contains(bytes, needle), $"the saved file still carries the word as {how}");
    }

    [Fact]
    public void Apply_WithAMarkOverEmptySpace_RemovesNothing_AndLeavesTheTextAlone()
    {
        // The refusal's quieter cousin, and the shape that made #567 worth finding: a path
        // that reports zero can mean "nothing was there" or "nothing looked". Marking space
        // with nothing in it must remove nothing *and* leave what is beside it untouched.
        using var doc = OpenWithCanary();
        Assert.NotNull(MarkForRedactionOperation.Place(doc, 0, new PdfRect(400, 400, 80, 60)));

        var report = doc.ApplyRedactions();

        Assert.True(report.Applied,
            "the redaction refused: " + string.Join("; ", report.Refusals.Select(r => $"{r.Reason}: {r.Message}")));
        Assert.Equal(0, report.Counts.Characters);
        Assert.Equal(0, report.Counts.TextRuns);
        Assert.Contains(Canary, TextOf(doc));
    }
}
