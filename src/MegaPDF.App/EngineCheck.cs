using System.Runtime.InteropServices;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;

namespace MegaPDF.App;

/// <summary>
/// <c>MegaPDF.exe --engine-check &lt;report.txt&gt; --term &lt;word&gt; &lt;document.pdf&gt;</c>:
/// runs the engine through the shipped binary with no window, writes what it found to
/// the report and exits 0 or 1 (#288).
/// </summary>
/// <remarks>
/// The Store's ARM64 package shipped from 1.7 to 2.0 with x64 native DLLs beside an
/// ARM64 process, so on an ARM64 PC no document could open, and nothing noticed. An
/// ARM64 PC is not something we have; a GitHub windows-11-arm runner is, but it can't
/// take the app's screenshots (RenderTargetBitmap hangs without a compositor, #84).
/// This takes the same engine path the viewer does, from inside the same executable
/// and beside the same DLLs, without needing a window: open, render, search, add a
/// text box, redact the term, save, and read the saved file back.
/// </remarks>
internal static class EngineCheck
{
    public static int Run(string pdf, string term, string report)
    {
        var lines = new List<string>();
        var failed = false;
        void Check(bool ok, string what)
        {
            lines.Add($"{(ok ? "ok  " : "FAIL")} {what}");
            failed |= !ok;
        }

        try
        {
            lines.Add($"process {RuntimeInformation.ProcessArchitecture}, OS {RuntimeInformation.OSArchitecture}");
            foreach (var name in new[] { "MegaPDF.exe", "pdfium.dll", "megapdf_core.dll" })
                lines.Add($"{name}: {Machine(Path.Combine(AppContext.BaseDirectory, name))}");

            const string added = "Added on this machine";
            var saved = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "engine-check-saved.pdf");
            using var engine = new PdfiumEngine();

            using (var doc = engine.Open(pdf))
            {
                Check(doc.PageCount > 0, $"open: {doc.PageCount} page(s)");
                using var page = doc.GetPage(0);

                var width = 800;
                var height = (int)Math.Round(width * page.Height / page.Width);
                var rendered = page.Render(width, height);
                var ink = 0;
                for (var i = 0; i < rendered.Bgra.Length; i += 4)
                    if (rendered.Bgra[i] < 128 && rendered.Bgra[i + 1] < 128 && rendered.Bgra[i + 2] < 128)
                        ink++;
                Check(ink > 100, $"render {rendered.PixelWidth}x{rendered.PixelHeight}: {ink} dark pixels");

                var matches = page.FindText(term);
                Check(matches.Count > 0, $"search \"{term}\": {matches.Count} match(es)");

                // Well clear of the term: a redaction takes whatever is inside its area,
                // a text box included.
                page.AppendTextBox(added, 14, new PdfPoint(72, page.Height / 2));
                Check(page.GetTextBoxes().Count == 1 && page.FindText(added).Count == 1, "add a text box");

                if (matches.Count > 0)
                {
                    var marked = page.MarkTextForRedaction(matches[0].Rects[0]);
                    Check(marked.Count > 0, $"mark \"{term}\" for redaction: {marked.Count} mark(s)");
                    var redaction = doc.ApplyRedactions();
                    Check(redaction.Applied && redaction.Refusals.Count == 0,
                          $"apply the redaction: applied={redaction.Applied}, refusals={redaction.Refusals.Count}");
                }

                using var stream = File.Create(saved);
                doc.Save(stream);
            }
            Check(new FileInfo(saved).Length > 0, $"save: {new FileInfo(saved).Length} bytes");

            using (var reopened = engine.Open(saved))
            using (var page = reopened.GetPage(0))
            {
                Check(page.FindText(term).Count == 0, $"the saved file no longer contains \"{term}\"");
                var text = string.Join(" | ", page.GetTextRuns().Select(r => r.Text));
                Check(page.FindText(added).Count == 1, $"the saved file carries the added text: {text}");
            }
            // Beyond what PDFium says about its own output: the term's bytes are gone
            // from the file itself.
            var bytes = File.ReadAllText(saved, System.Text.Encoding.Latin1);
            Check(!bytes.Contains(term, StringComparison.Ordinal), $"\"{term}\" is not in the saved file's raw bytes");
        }
        catch (Exception e)
        {
            Check(false, $"{e.GetType().Name}: {e.Message}");
        }

        lines.Add(failed ? "ENGINE CHECK FAILED" : "ENGINE CHECK PASSED");
        File.WriteAllLines(report, lines);
        return failed ? 1 : 0;
    }

    private static string Machine(string path)
    {
        if (!File.Exists(path))
            return "missing";
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file);
        file.Position = 0x3C;
        file.Position = reader.ReadInt32() + 4;
        return reader.ReadUInt16() switch
        {
            0x8664 => "x64",
            0xAA64 => "arm64",
            0x014C => "x86",
            var other => $"0x{other:X4}",
        };
    }
}
