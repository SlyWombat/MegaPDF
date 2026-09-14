using MegaPDF.Core.Editing;
using MegaPDF.Core.Viewing;
using System.Globalization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Engine.Pdfium;
using MegaPDF.Core.Imaging;

namespace MegaPDF.Stress;

/// <summary>
/// Corpus stress harness (#92).
///
/// <c>run</c> enumerates every PDF under <c>--root</c>, then feeds file indices to a
/// pool of <c>worker</c> child processes over stdin. Each worker exercises one file
/// at a time through MegaPDF.Core in the same call pattern as the apps and prints a
/// JSON result line. The parent owns <c>results.jsonl</c>, records a worker that dies
/// (native crash) or stops heartbeating (hang) against the file it was on, and starts
/// a fresh worker in its place. Re-running with the same <c>--out</c> resumes.
///
/// Everything written to <c>--out</c> contains file names and is private.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
            return Usage();
        var opts = Options.Parse(args.Skip(1));
        return args[0] switch
        {
            "run" => Orchestrator.Run(opts),
            "worker" => Worker.Run(opts),
            "find" => Tools.Find(opts),
            "open-bench" => Tools.OpenBench(opts),
            "inspect" => Tools.Inspect(opts),
            "dump-text" => Tools.DumpText(opts),
            "flags-bench" => FlagsBench.Run(opts),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "usage: MegaPDF.Stress run --root <dir> --out <dir> [--workers N] [--scale S]\n" +
            "         [--phases scroll,search,zoom,save,images[,edit]] [--terms Seaman,the]\n" +
            "         [--limit N] [--filter substring] [--hang-seconds 180] [--cap-base 300] [--cap-per-page 2]\n" +
            "       MegaPDF.Stress worker --list <files.txt> --root <dir> [--scale S] [--phases ...] [--terms ...]");
        return 2;
    }
}

internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        string? key = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (key is not null)
                    o._values[key] = "true";
                key = a[2..];
            }
            else if (key is not null)
            {
                o._values[key] = a;
                key = null;
            }
        }
        if (key is not null)
            o._values[key] = "true";
        return o;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public string Require(string key) => Get(key) ?? throw new ArgumentException($"--{key} is required");
    public int Int(string key, int fallback) => int.TryParse(Get(key), out var v) ? v : fallback;
    public double Double(string key, double fallback) => double.TryParse(Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    public string[] List(string key, string fallback) => (Get(key) ?? fallback).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

// ---------------------------------------------------------------------------
// Result model — one JSON line per file.
// ---------------------------------------------------------------------------

internal sealed class FileResult
{
    [JsonPropertyName("i")] public int Index { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("outcome")] public string Outcome { get; set; } = "ok";
    /// <summary>Opened with a password from the private unlock list (#131).</summary>
    [JsonPropertyName("unlocked")] public bool? Unlocked { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("errors")] public Dictionary<string, string>? PhaseErrors { get; set; }
    [JsonPropertyName("exit_code")] public int? ExitCode { get; set; }
    [JsonPropertyName("last_phase")] public string? LastPhase { get; set; }
    [JsonPropertyName("last_page")] public int? LastPage { get; set; }
    [JsonPropertyName("pages")] public int? Pages { get; set; }
    [JsonPropertyName("read_ms")] public double? ReadMs { get; set; }
    [JsonPropertyName("open_ms")] public double? OpenMs { get; set; }
    [JsonPropertyName("sizepass_ms")] public double? SizePassMs { get; set; }
    [JsonPropertyName("page0_pt")] public double[]? Page0Points { get; set; }
    [JsonPropertyName("largest_pt")] public double[]? LargestPoints { get; set; }
    [JsonPropertyName("largest_page")] public int? LargestPage { get; set; }
    [JsonPropertyName("scroll")] public ScrollResult? Scroll { get; set; }
    [JsonPropertyName("search")] public Dictionary<string, SearchResult>? Search { get; set; }
    [JsonPropertyName("zoom")] public List<ZoomResult>? Zoom { get; set; }
    [JsonPropertyName("save")] public SaveResult? Save { get; set; }
    [JsonPropertyName("images")] public ImagesResult? Images { get; set; }
    [JsonPropertyName("edit")] public EditResult? Edit { get; set; }
    [JsonPropertyName("edits")] public EditsResult? Edits { get; set; }
    [JsonPropertyName("mem")] public MemResult? Mem { get; set; }
    [JsonPropertyName("wall_ms")] public double? WallMs { get; set; }
    [JsonPropertyName("worker")] public int? WorkerPid { get; set; }
}

internal sealed class ScrollResult
{
    [JsonPropertyName("scale")] public double Scale { get; set; }
    [JsonPropertyName("total_ms")] public double TotalMs { get; set; }
    [JsonPropertyName("render_ms")] public int[] RenderMs { get; set; } = [];
    [JsonPropertyName("regions_ms")] public int[] RegionsMs { get; set; } = [];
    [JsonPropertyName("max_px")] public long MaxPixels { get; set; }
    [JsonPropertyName("blank_pages")] public List<int> BlankPages { get; set; } = [];
    [JsonPropertyName("blank_with_text")] public List<int> BlankWithText { get; set; } = [];
    [JsonPropertyName("text_chars")] public long TextChars { get; set; }
    [JsonPropertyName("lines")] public int Lines { get; set; }
    [JsonPropertyName("fields")] public int Fields { get; set; }
    [JsonPropertyName("squares")] public int Squares { get; set; }
    [JsonPropertyName("stamps")] public int Stamps { get; set; }
    [JsonPropertyName("first3_ms")] public double First3Ms { get; set; }
}

internal sealed class SearchResult
{
    [JsonPropertyName("ms")] public double Ms { get; set; }
    [JsonPropertyName("hits")] public int Hits { get; set; }
    [JsonPropertyName("pages_with_hits")] public int PagesWithHits { get; set; }
    [JsonPropertyName("first_hit_page")] public int? FirstHitPage { get; set; }
    [JsonPropertyName("first_hit_ms")] public double? FirstHitMs { get; set; }
    [JsonPropertyName("max_page_ms")] public double MaxPageMs { get; set; }
    [JsonPropertyName("max_page")] public int MaxPage { get; set; }
}

internal sealed class ZoomResult
{
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("zoom")] public double Zoom { get; set; }
    [JsonPropertyName("px")] public int[] Pixels { get; set; } = [];
    [JsonPropertyName("ms")] public double? Ms { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

internal sealed class SaveResult
{
    [JsonPropertyName("ms")] public double Ms { get; set; }
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("reopen_ms")] public double? ReopenMs { get; set; }
    [JsonPropertyName("reopen_pages")] public int? ReopenPages { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>The optional edit phase (#112): retype the first line of page 1, save, reopen, read it back.</summary>
/// <summary>
/// The #127 edit battery: realistic edits on sampled lines, each from a fresh copy of the
/// document. Kinds, positions, verdicts, outcomes and measurements only — never text.
/// </summary>
internal sealed class EditsResult
{
    [JsonPropertyName("items")] public List<EditItem> Items { get; set; } = new();
    [JsonPropertyName("error")] public string? Error { get; set; }
}

internal sealed class EditItem
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("page")] public string PagePosition { get; set; } = "";
    [JsonPropertyName("line")] public string LinePosition { get; set; } = "";
    [JsonPropertyName("runs")] public int Runs { get; set; }
    [JsonPropertyName("editable")] public bool? Editable { get; set; }
    // in_place | substituted | deleted | layout | no_font | not_extractable | refused | skipped | error
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("read_back")] public bool? ReadBack { get; set; }
    /// <summary>Deletes only, counts: "text before→after, runs before→after, overlapping after".</summary>
    [JsonPropertyName("delete_counts")] public string? DeleteCounts { get; set; }
    /// <summary>Every edit, counts: runs overlapping the line's area "before->after" reopen (#136).</summary>
    [JsonPropertyName("line_overlap")] public string? LineOverlap { get; set; }
    [JsonPropertyName("untouched")] public int Untouched { get; set; }
    [JsonPropertyName("untouched_missing")] public int UntouchedMissing { get; set; }
    [JsonPropertyName("worst_shift_pt")] public double? WorstShiftPt { get; set; }
    [JsonPropertyName("ms")] public double Ms { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

internal sealed class EditResult
{
    [JsonPropertyName("ms")] public double Ms { get; set; }
    [JsonPropertyName("outcome")] public string? Outcome { get; set; }
    [JsonPropertyName("skipped")] public string? Skipped { get; set; }
    [JsonPropertyName("verified")] public bool Verified { get; set; }
    // Where the retyped text was found — booleans only, never document text.
    [JsonPropertyName("in_memory")] public bool InMemory { get; set; }
    [JsonPropertyName("reopened_same_index")] public bool ReopenedSameIndex { get; set; }
    [JsonPropertyName("reopened_anywhere")] public bool ReopenedAnywhere { get; set; }
    [JsonPropertyName("runs_before")] public int RunsBefore { get; set; }
    [JsonPropertyName("runs_after")] public int RunsAfter { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

internal sealed class ImagesResult
{
    [JsonPropertyName("list_ms")] public double ListMs { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("stored_bytes")] public long StoredBytes { get; set; }
    [JsonPropertyName("max_px")] public long MaxPixels { get; set; }
    [JsonPropertyName("eligible")] public int Eligible { get; set; }
    [JsonPropertyName("decode_ms")] public double DecodeMs { get; set; }
    [JsonPropertyName("decode_max_ms")] public double DecodeMaxMs { get; set; }
    [JsonPropertyName("decode_errors")] public int DecodeErrors { get; set; }
}

internal sealed class MemResult
{
    [JsonPropertyName("ws_before")] public long WsBefore { get; set; }
    [JsonPropertyName("ws_after")] public long WsAfter { get; set; }
    [JsonPropertyName("ws_after_gc")] public long WsAfterGc { get; set; }
    [JsonPropertyName("ws_peak")] public long? WsPeak { get; set; }
    [JsonPropertyName("gc_heap")] public long GcHeap { get; set; }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

// ---------------------------------------------------------------------------
// Worker — one file at a time, driven over stdin.
// ---------------------------------------------------------------------------

internal static class Worker
{
    private const double PointsToPixels = 96.0 / 72.0;
    private const double MaxZoom = 3.0;   // MainViewModel.MaxZoom = 300
    private const double MinZoom = 0.5;   // MainViewModel.MinZoom = 50

    public static int Run(Options opts)
    {
        var listPath = opts.Require("list");
        var root = opts.Require("root");
        var scale = opts.Double("scale", 1.0);
        var phases = new HashSet<string>(opts.List("phases", "scroll,search,zoom,save,images"), StringComparer.OrdinalIgnoreCase);
        var terms = opts.List("terms", "Seaman,the");
        var files = File.ReadAllLines(listPath);

        var stdout = Console.Out;
        var engine = new PdfiumEngine();
        var tmpDir = Directory.CreateTempSubdirectory("megapdf-stress-").FullName;
        var pid = Environment.ProcessId;

        try
        {
            string? line;
            while ((line = Console.In.ReadLine()) is not null)
            {
                if (!int.TryParse(line.Trim(), out var index) || index < 0 || index >= files.Length)
                    continue;
                var result = new FileResult { Index = index, Path = files[index], WorkerPid = pid };
                var wall = Stopwatch.StartNew();
                try
                {
                    ProcessFile(engine, Path.Combine(root, files[index]), result, scale, phases, terms, tmpDir, stdout);
                }
                catch (Exception ex)
                {
                    result.Outcome = "error";
                    result.Error = Describe(ex);
                }
                result.WallMs = wall.Elapsed.TotalMilliseconds;
                stdout.WriteLine("RESULT " + JsonSerializer.Serialize(result, Json.Options));
                stdout.WriteLine($"DONE {index}");
                stdout.Flush();
            }
            return 0;
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>
    /// Passwords for protected corpus files (#131), so they are exercised rather than only
    /// counted: the file MEGAPDF_STRESS_UNLOCK_LIST names, one line per document, its path
    /// relative to <c>--root</c>, a tab, then its password. Private like the corpus itself;
    /// nothing from it is logged or reported. Workers inherit the variable from <c>run</c>.
    /// </summary>
    private static readonly Lazy<Dictionary<string, string>> UnlockList = new(() =>
    {
        var list = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Environment.GetEnvironmentVariable("MEGAPDF_STRESS_UNLOCK_LIST");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return list;
        foreach (var line in File.ReadAllLines(path))
        {
            var tab = line.IndexOf('\t');
            if (tab > 0)
                list[line[..tab].Replace('\\', '/')] = line[(tab + 1)..];
        }
        return list;
    });

    private static void Heartbeat(TextWriter w, int index, string phase, int n)
    {
        w.WriteLine($"HB {index} {phase} {n}");
        w.Flush();
    }

    private static void ProcessFile(PdfiumEngine engine, string fullPath, FileResult r, double scale,
                                    HashSet<string> phases, string[] terms, string tmpDir, TextWriter hb)
    {
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var mem = new MemResult { WsBefore = proc.WorkingSet64 };
        r.Mem = mem;
        var i = r.Index;

        // 1. Read from wherever the corpus lives (share or local disk), then park a
        //    copy on local temp so open_ms measures parsing, not the network.
        Heartbeat(hb, i, "read", 0);
        var sw = Stopwatch.StartNew();
        var bytes = File.ReadAllBytes(fullPath);
        r.ReadMs = sw.Elapsed.TotalMilliseconds;
        r.Bytes = bytes.LongLength;
        var local = Path.Combine(tmpDir, "doc.pdf");
        File.WriteAllBytes(local, bytes);
        bytes = [];

        // 2. Open — what MainViewModel.OpenDocumentAsync does first.
        Heartbeat(hb, i, "open", 0);
        IPdfDocument doc;
        sw.Restart();
        try
        {
            doc = engine.Open(local);
        }
        catch (PdfLoadException ex) when (ex.IsPasswordError &&
                                          UnlockList.Value.TryGetValue(r.Path.Replace('\\', '/'), out var unlock))
        {
            try
            {
                doc = engine.Open(local, unlock);
                r.Unlocked = true;
            }
            catch (PdfLoadException retry)
            {
                r.OpenMs = sw.Elapsed.TotalMilliseconds;
                r.Outcome = "encrypted";
                r.Error = Describe(retry);
                return;
            }
        }
        catch (PdfLoadException ex)
        {
            r.OpenMs = sw.Elapsed.TotalMilliseconds;
            // A protected document is its own reason, never a failure (#131); an unsupported
            // security handler (FPDF_ERR_SECURITY) is one the apps must explain.
            r.Outcome = ex.IsPasswordError ? "encrypted"
                : ex.ErrorCode == 6 ? "unsupported-security"
                : ex.IsFormatError ? "format"
                : ex.IsFileError ? "file"
                : $"load-{ex.ErrorCode}";
            r.Error = Describe(ex);
            return;
        }
        r.OpenMs = sw.Elapsed.TotalMilliseconds;

        using (doc)
        {
            // 3. Size-only pass over every page (open-time geometry, SDD §4.2).
            Heartbeat(hb, i, "sizepass", 0);
            sw.Restart();
            var count = doc.PageCount;
            r.Pages = count;
            hb.WriteLine($"PAGES {i} {count}");
            hb.Flush();
            var sizes = new (double W, double H)[count];
            for (var p = 0; p < count; p++)
            {
                using var page = doc.GetPage(p);
                sizes[p] = (page.Width, page.Height);
            }
            r.SizePassMs = sw.Elapsed.TotalMilliseconds;
            if (count > 0)
            {
                r.Page0Points = [Math.Round(sizes[0].W, 1), Math.Round(sizes[0].H, 1)];
                var largest = 0;
                for (var p = 1; p < count; p++)
                    if (sizes[p].W * sizes[p].H > sizes[largest].W * sizes[largest].H)
                        largest = p;
                r.LargestPage = largest;
                r.LargestPoints = [Math.Round(sizes[largest].W, 1), Math.Round(sizes[largest].H, 1)];
            }

            var errors = new Dictionary<string, string>();

            // 4. Scroll to the end: every page rendered at the viewport scale plus the
            //    interaction-region pass the apps run on the same task (BuildRegions).
            if (phases.Contains("scroll"))
            {
                try { r.Scroll = ScrollToEnd(doc, count, scale, i, hb); }
                catch (Exception ex) { errors["scroll"] = Describe(ex); }
            }

            // 5. Whole-document search, page by page, exactly like SearchAsync/Search.
            if (phases.Contains("search"))
            {
                r.Search = new Dictionary<string, SearchResult>();
                foreach (var term in terms)
                {
                    try { r.Search[term] = SearchAll(doc, count, term, i, hb); }
                    catch (Exception ex) { errors["search:" + term] = Describe(ex); }
                }
            }

            // 6. Zoom extremes on the first page and the largest page.
            if (phases.Contains("zoom") && count > 0)
            {
                r.Zoom = [];
                var targets = new List<int> { 0 };
                if (r.LargestPage is int lp && lp != 0)
                    targets.Add(lp);
                foreach (var p in targets)
                {
                    Heartbeat(hb, i, "zoom", p);
                    r.Zoom.Add(RenderAtZoom(doc, p, MaxZoom, scale));
                    r.Zoom.Add(RenderAtZoom(doc, p, MinZoom, scale));
                }
            }

            // 7. Save (incremental path) and reopen the result.
            if (phases.Contains("save"))
            {
                Heartbeat(hb, i, "save", 0);
                r.Save = SaveAndReopen(engine, doc, Path.Combine(tmpDir, "saved.pdf"));
            }

            // 8. Shrink-for-email: list images and decode the ones the shrinker would re-encode.
            if (phases.Contains("images"))
            {
                try { r.Images = DecodeShrinkCandidates(doc, i, hb); }
                catch (Exception ex) { errors["images"] = Describe(ex); }
            }

            // 9a. Optional (#127): a battery of realistic edits on lines sampled from the
            //     first, middle and last pages, each on a fresh copy, through the app's own
            //     line operations. Runs before the single retype, which changes `doc`.
            if (phases.Contains("edits") && count > 0)
            {
                Heartbeat(hb, i, "edits", 0);
                r.Edits = EditBattery(engine, doc, count, tmpDir);
            }

            // 9. Optional (#112): retype the first line of page 1 through the tiered
            //    body-text edit, save, reopen and read the new text back. Last, because
            //    it changes the document.
            if (phases.Contains("edit") && count > 0)
            {
                Heartbeat(hb, i, "edit", 0);
                r.Edit = RetypeFirstLine(engine, doc, Path.Combine(tmpDir, "edited.pdf"));
            }

            if (errors.Count > 0)
            {
                r.PhaseErrors = errors;
                r.Outcome = "partial";
            }

            proc.Refresh();
            mem.WsAfter = proc.WorkingSet64;
            try { mem.WsPeak = proc.PeakWorkingSet64; } catch { /* not on every OS */ }
        }

        try { File.Delete(local); } catch { /* best effort */ }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        proc.Refresh();
        mem.WsAfterGc = proc.WorkingSet64;
        mem.GcHeap = GC.GetTotalMemory(false);
    }

    private static ScrollResult ScrollToEnd(IPdfDocument doc, int count, double scale, int index, TextWriter hb)
    {
        var res = new ScrollResult { Scale = scale, RenderMs = new int[count], RegionsMs = new int[count] };
        var total = Stopwatch.StartNew();
        var sw = new Stopwatch();
        for (var p = 0; p < count; p++)
        {
            Heartbeat(hb, index, "scroll", p);
            using var page = doc.GetPage(p);
            var pw = Math.Max(1, (int)(page.Width * PointsToPixels * scale));
            var ph = Math.Max(1, (int)(page.Height * PointsToPixels * scale));
            sw.Restart();
            var rendered = page.Render(pw, ph);
            res.RenderMs[p] = (int)sw.ElapsedMilliseconds;
            res.MaxPixels = Math.Max(res.MaxPixels, (long)pw * ph);

            sw.Restart();
            var stamps = page.GetStamps().Count;
            _ = page.GetTextBoxes().Count;
            var fields = page.GetFormFields().Count;
            var lines = page.GetTextLines();
            _ = page.GetWhiteouts().Count;
            var squares = page.DetectCheckboxSquares().Count;
            res.RegionsMs[p] = (int)sw.ElapsedMilliseconds;

            res.Stamps += stamps;
            res.Fields += fields;
            res.Lines += lines.Count;
            res.Squares += squares;
            long chars = 0;
            foreach (var l in lines)
                chars += l.Text.Length;
            res.TextChars += chars;

            if (IsBlank(rendered.Bgra))
            {
                res.BlankPages.Add(p);
                if (chars > 0)
                    res.BlankWithText.Add(p);
            }
            if (p == Math.Min(2, count - 1))
                res.First3Ms = total.Elapsed.TotalMilliseconds;
        }
        res.TotalMs = total.Elapsed.TotalMilliseconds;
        return res;
    }

    private static bool IsBlank(byte[] bgra)
    {
        var px = MemoryMarshal.Cast<byte, uint>(bgra);
        foreach (var v in px)
            if (v != 0xFFFFFFFFu)
                return false;
        return true;
    }

    private static SearchResult SearchAll(IPdfDocument doc, int count, string term, int index, TextWriter hb)
    {
        var res = new SearchResult();
        var total = Stopwatch.StartNew();
        var sw = new Stopwatch();
        for (var p = 0; p < count; p++)
        {
            if (p % 10 == 0)
                Heartbeat(hb, index, "search", p);
            sw.Restart();
            using var page = doc.GetPage(p);
            var matches = page.FindText(term);
            var ms = sw.Elapsed.TotalMilliseconds;
            if (ms > res.MaxPageMs)
            {
                res.MaxPageMs = ms;
                res.MaxPage = p;
            }
            if (matches.Count > 0)
            {
                res.PagesWithHits++;
                if (res.FirstHitPage is null)
                {
                    res.FirstHitPage = p;
                    res.FirstHitMs = total.Elapsed.TotalMilliseconds;
                }
                res.Hits += matches.Count;
            }
        }
        res.Ms = total.Elapsed.TotalMilliseconds;
        return res;
    }

    private static ZoomResult RenderAtZoom(IPdfDocument doc, int p, double zoom, double scale)
    {
        var z = new ZoomResult { Page = p, Zoom = zoom };
        try
        {
            using var page = doc.GetPage(p);
            // The apps never ask for the ideal raster past the render clamp (#93/#111):
            // they fit it first and scale the bitmap up, and since #111 the engine
            // refuses an unclamped request outright rather than trying PDFium's luck.
            var (pw, ph) = RenderLimits.Fit(page.Width * PointsToPixels * scale * zoom, page.Height * PointsToPixels * scale * zoom);
            z.Pixels = [pw, ph];
            var sw = Stopwatch.StartNew();
            _ = page.Render(pw, ph);
            z.Ms = sw.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            z.Error = Describe(ex);
        }
        return z;
    }

    private const string RetypedText = "MegaPDF corpus edit 2026";

    // A run's text carries the separator PDFium generates before the next object on
    // the line (the text-run contract, #106), so the retyped run reads back as the
    // text plus at most trailing whitespace. Anything else — dropped, wrong or
    // spread-apart characters — is a failed edit (#116).
    private static bool IsRetyped(string text) => text.TrimEnd() == RetypedText;

    // #127: what the battery types. Text is generated from the kind, not taken from the
    // document, except "same" and "shorter", which reuse the line's own words in memory
    // and never record them.
    private static readonly string[] EditKinds = { "same", "longer", "shorter", "digits", "accented", "cjk", "delete" };

    private static string Normalized(string text) => text.TrimEnd();

    private static string Squeezed(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>The line with its last word taken out, or empty when it has only one.</summary>
    private static string WithoutLastWord(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length < 2 ? "" : string.Join(' ', words[..^1]);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static EditsResult EditBattery(PdfiumEngine engine, IPdfDocument doc, int pageCount, string tmpDir)
    {
        var result = new EditsResult();
        var pristine = Path.Combine(tmpDir, "edits-pristine.pdf");
        var edited = Path.Combine(tmpDir, "edits-edited.pdf");
        try
        {
            using (var stream = File.Create(pristine))
                doc.Save(stream);

            var pages = new[] { ("first", 0), ("middle", pageCount / 2), ("last", pageCount - 1) }
                .DistinctBy(p => p.Item2).ToList();
            var kind = 0;
            foreach (var (pagePosition, pageIndex) in pages)
            {
                int lineCount;
                using (var probe = engine.Open(pristine))
                using (var page = probe.GetPage(pageIndex))
                    lineCount = page.GetTextLines().Count(l => !string.IsNullOrWhiteSpace(l.Text));
                if (lineCount == 0)
                    continue;
                foreach (var (linePosition, lineIndex) in new[] { ("first", 0), ("middle", lineCount / 2), ("last", lineCount - 1) }.DistinctBy(l => l.Item2))
                {
                    result.Items.Add(OneEdit(engine, pristine, edited, pageIndex, lineIndex, EditKinds[kind % EditKinds.Length], pagePosition, linePosition));
                    kind++;
                }

                // Deleting a word (#127): one extra edit per page on its first line, outside
                // the rotation, so every other edit keeps the kind it had in earlier runs and
                // results stay comparable between builds.
                result.Items.Add(OneEdit(engine, pristine, edited, pageIndex, 0, "delete_word", pagePosition, "first"));
            }
        }
        catch (Exception ex)
        {
            result.Error = Describe(ex);
        }
        finally
        {
            try { File.Delete(pristine); } catch { /* best effort */ }
            try { File.Delete(edited); } catch { /* best effort */ }
        }
        return result;
    }

    private static EditItem OneEdit(PdfiumEngine engine, string pristine, string edited, int pageIndex, int lineIndex,
                                    string kind, string pagePosition, string linePosition)
    {
        var item = new EditItem { Kind = kind, PagePosition = pagePosition, LinePosition = linePosition };
        var sw = Stopwatch.StartNew();
        try
        {
            using var fresh = engine.Open(pristine);
            PdfTextLine line;
            IReadOnlyList<PdfTextRun> before;
            using (var page = fresh.GetPage(pageIndex))
            {
                // Lines are chosen on another open of the same file, and PDFium can group a
                // page's text into lines differently from one open to the next, so the line
                // may not be there any more.
                var lines = page.GetTextLines().Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
                if (lineIndex >= lines.Count)
                {
                    item.Result = "skipped";
                    item.Error = "line grouping differed between opens";
                    item.Ms = sw.Elapsed.TotalMilliseconds;
                    return item;
                }
                line = lines[lineIndex];
                before = page.GetTextRuns();
                item.Editable = line.Runs.All(r => r.TextBoxId is not null || page.IsTextEditable(r.ObjectIndex));
            }
            item.Runs = line.Runs.Count;

            var first = line.Runs[0].Text.Trim();
            var newText = kind switch
            {
                "same" => line.Text,
                "longer" => line.Text.TrimEnd() + " (revised 2026)",
                "shorter" => first.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { Length: > 0 } word ? word : "Edited",
                "digits" => "$1,234.56 due 2026-09-13",
                "accented" => "Reçu : déjà payé l’été",
                "cjk" => "請求書 2026",
                "delete_word" => WithoutLastWord(line.Text),
                _ => "",
            };
            if (kind == "delete_word" && newText.Length == 0)
            {
                item.Result = "skipped";
                item.Error = "the line has a single word";
                item.Ms = sw.Elapsed.TotalMilliseconds;
                return item;
            }

            try
            {
                if (kind == "delete")
                {
                    new DeleteLineOperation(fresh, pageIndex, line).Apply();
                    item.Result = "deleted";
                }
                else
                {
                    var op = new LineEditOperation(fresh, pageIndex, line, newText);
                    op.Apply();
                    item.Result = op.LastOutcome == TextEditOutcome.EditedWithSubstitutedFont ? "substituted" : "in_place";
                }
            }
            catch (TextEditException ex)
            {
                item.Result = ex.Reason switch
                {
                    TextEditFailure.LayoutWouldChange => "layout",
                    TextEditFailure.NoUsableFont => "no_font",
                    TextEditFailure.NotExtractable => "not_extractable",
                    _ => "refused",
                };
                item.Ms = sw.Elapsed.TotalMilliseconds;
                return item;
            }

            using (var stream = File.Create(edited))
                fresh.Save(stream);
            using var reopened = engine.Open(edited);
            using var reopenedPage = reopened.GetPage(pageIndex);
            var after = reopenedPage.GetTextRuns();

            var lineObjects = line.Runs.Select(r => r.ObjectIndex).ToHashSet();

            // Runs where the line was, before and after (#136): a line drawn twice keeps
            // its hidden copy through an edit or a delete, and the copy surfaces here.
            var lineLeft = line.Runs.Min(r => r.Bounds.X);
            var lineTop = line.Runs.Min(r => r.Bounds.Y);
            var lineRight = line.Runs.Max(r => r.Bounds.X + r.Bounds.Width);
            var lineBottom = line.Runs.Max(r => r.Bounds.Y + r.Bounds.Height);
            bool WhereTheLineWas(PdfTextRun r) => !string.IsNullOrWhiteSpace(r.Text)
                                                  && r.Bounds.X < lineRight && r.Bounds.X + r.Bounds.Width > lineLeft
                                                  && r.Bounds.Y < lineBottom && r.Bounds.Y + r.Bounds.Height > lineTop;
            item.LineOverlap = $"{before.Count(WhereTheLineWas)}->{after.Count(WhereTheLineWas)}";

            if (kind == "delete")
            {
                // Counted in the page's whole text, not run by run: PDFium can regroup a
                // long line's runs on reopen, so neither the run count nor a run-for-run
                // match has to drop when the line has really gone.
                var lineText = Squeezed(line.Text);
                var textBefore = lineText.Length == 0 ? 0 : Occurrences(Squeezed(string.Concat(before.Select(r => r.Text))), lineText);
                var textAfter = lineText.Length == 0 ? 0 : Occurrences(Squeezed(string.Concat(after.Select(r => r.Text))), lineText);
                item.ReadBack = lineText.Length == 0 ? after.Count < before.Count : textAfter < textBefore;

                // A second drawn copy of the line (fake bold, a shadow) survives a delete of
                // the copy the line grouping chose: count what still sits where the line was.
                var left = line.Runs.Min(r => r.Bounds.X);
                var top = line.Runs.Min(r => r.Bounds.Y);
                var right = line.Runs.Max(r => r.Bounds.X + r.Bounds.Width);
                var bottom = line.Runs.Max(r => r.Bounds.Y + r.Bounds.Height);
                var overlapping = after.Count(r => !string.IsNullOrWhiteSpace(r.Text)
                                                   && r.Bounds.X < right && r.Bounds.X + r.Bounds.Width > left
                                                   && r.Bounds.Y < bottom && r.Bounds.Y + r.Bounds.Height > top);
                item.DeleteCounts = $"text {textBefore}->{textAfter}, runs {before.Count}->{after.Count}, line runs {line.Runs.Count}, overlapping after {overlapping}";
            }
            else
            {
                item.ReadBack = after.Any(r => Normalized(r.Text) == Normalized(newText));
            }

            // Every other run on the page: matched by text to the nearest run after reopening.
            var used = new bool[after.Count];
            double worst = 0;
            foreach (var run in before)
            {
                if (lineObjects.Contains(run.ObjectIndex) || string.IsNullOrWhiteSpace(run.Text))
                    continue;
                item.Untouched++;
                var text = Normalized(run.Text);
                int best = -1;
                double bestShift = double.MaxValue;
                for (int j = 0; j < after.Count; j++)
                {
                    if (used[j] || Normalized(after[j].Text) != text)
                        continue;
                    var shift = Math.Max(Math.Abs(after[j].Bounds.X - run.Bounds.X), Math.Abs(after[j].Bounds.Y - run.Bounds.Y));
                    if (shift < bestShift) { bestShift = shift; best = j; }
                }
                if (best < 0) { item.UntouchedMissing++; continue; }
                used[best] = true;
                worst = Math.Max(worst, bestShift);
            }
            item.WorstShiftPt = item.Untouched > 0 ? Math.Round(worst, 3) : null;
        }
        catch (Exception ex)
        {
            item.Result = "error";
            item.Error = Describe(ex);
        }
        item.Ms = sw.Elapsed.TotalMilliseconds;
        return item;
    }

    private static EditResult RetypeFirstLine(PdfiumEngine engine, IPdfDocument doc, string savedPath)
    {
        var e = new EditResult();
        try
        {
            int objectIndex;
            var sw = Stopwatch.StartNew();
            using (var page = doc.GetPage(0))
            {
                var line = page.GetTextLines().FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.Text));
                if (line is null)
                {
                    // Scans and pictures: tier 3, nothing to retype.
                    e.Skipped = "no text";
                    return e;
                }
                var run = line.Runs[0];
                objectIndex = run.ObjectIndex;
                e.Outcome = page.SetTextRunText(run, RetypedText) == TextEditOutcome.EditedInPlace ? "in_place" : "substituted";
                var inMemory = page.GetTextRuns();
                e.RunsBefore = inMemory.Count;
                e.InMemory = inMemory.Any(r => IsRetyped(r.Text));
            }
            using (var stream = File.Create(savedPath))
                doc.Save(stream);
            e.Ms = sw.Elapsed.TotalMilliseconds;
            using var reopened = engine.Open(savedPath);
            using var reopenedPage = reopened.GetPage(0);
            var after = reopenedPage.GetTextRuns();
            e.RunsAfter = after.Count;
            e.ReopenedSameIndex = after.Any(r => r.ObjectIndex == objectIndex && IsRetyped(r.Text));
            e.ReopenedAnywhere = after.Any(r => IsRetyped(r.Text));
            // The object index is not stable across a save: PDFium re-parses the
            // rewritten content stream on reopen. What has to survive is the text.
            e.Verified = e.ReopenedAnywhere;
            if (!e.InMemory)
                e.Error = "the retyped text did not read back in memory";
            else if (!e.Verified)
                e.Error = "the retyped text did not read back after save and reopen";
        }
        catch (TextEditException ex) when (ex.Reason == TextEditFailure.LayoutWouldChange)
        {
            // #118: the engine declined because PDFium would disturb the page. That is
            // the engine protecting the document, not a failed edit.
            e.Skipped = "layout";
        }
        catch (TextEditException ex)
        {
            e.Error = "text edit refused: " + ex.Reason;
        }
        catch (Exception ex)
        {
            e.Error = Describe(ex);
        }
        finally
        {
            try { File.Delete(savedPath); } catch { /* best effort */ }
        }
        return e;
    }

    private static SaveResult SaveAndReopen(PdfiumEngine engine, IPdfDocument doc, string savedPath)
    {
        var s = new SaveResult();
        try
        {
            var sw = Stopwatch.StartNew();
            using (var stream = File.Create(savedPath))
                doc.Save(stream);
            s.Ms = sw.Elapsed.TotalMilliseconds;
            s.Bytes = new FileInfo(savedPath).Length;
            sw.Restart();
            using (var reopened = engine.Open(savedPath))
            {
                s.ReopenPages = reopened.PageCount;
            }
            s.ReopenMs = sw.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            s.Error = Describe(ex);
        }
        finally
        {
            try { File.Delete(savedPath); } catch { /* best effort */ }
        }
        return s;
    }

    /// <summary>Mirrors ImageShrinker.Shrink's selection rules and its decode step, without the JPEG encode.</summary>
    private static ImagesResult DecodeShrinkCandidates(IPdfDocument doc, int index, TextWriter hb)
    {
        var res = new ImagesResult();
        Heartbeat(hb, index, "images", 0);
        var sw = Stopwatch.StartNew();
        var images = doc.GetImages();
        res.ListMs = sw.Elapsed.TotalMilliseconds;
        res.Count = images.Count;
        var n = 0;
        foreach (var image in images)
        {
            res.StoredBytes += image.StoredByteLength;
            res.MaxPixels = Math.Max(res.MaxPixels, (long)image.PixelWidth * image.PixelHeight);

            var targetWidth = (int)Math.Round(image.DisplayWidthPoints / 72 * ImageShrinker.TargetDpi);
            var targetHeight = (int)Math.Round(image.DisplayHeightPoints / 72 * ImageShrinker.TargetDpi);
            var oversized = image.PixelWidth > targetWidth * 1.2;
            if ((!oversized && image.StoredByteLength < 100_000) || image.StoredByteLength < 8_000)
                continue;
            if (image.PixelWidth < ImageShrinker.MinTargetPixels || image.PixelHeight < ImageShrinker.MinTargetPixels)
                continue;
            targetWidth = Math.Clamp(targetWidth, ImageShrinker.MinTargetPixels, image.PixelWidth);
            targetHeight = Math.Clamp(targetHeight, ImageShrinker.MinTargetPixels, image.PixelHeight);

            res.Eligible++;
            if (++n % 5 == 0)
                Heartbeat(hb, index, "images", n);
            sw.Restart();
            try
            {
                _ = doc.RenderImageAt(image, targetWidth, targetHeight);
            }
            catch
            {
                res.DecodeErrors++;
            }
            var ms = sw.Elapsed.TotalMilliseconds;
            res.DecodeMs += ms;
            res.DecodeMaxMs = Math.Max(res.DecodeMaxMs, ms);
        }
        return res;
    }
}

// ---------------------------------------------------------------------------
// Tools — one-file diagnostics used to triage what the run flags.
// ---------------------------------------------------------------------------

internal static class Tools
{
    private const double PointsToPixels = 96.0 / 72.0;

    /// <summary>Per-page hit counts for a term — to localise a cross-platform hit-count difference.</summary>
    public static int Find(Options opts)
    {
        var file = opts.Require("file");
        var term = opts.Require("term");
        var engine = new PdfiumEngine();
        using var doc = engine.Open(file);
        var total = 0;
        for (var p = 0; p < doc.PageCount; p++)
        {
            using var page = doc.GetPage(p);
            var matches = page.FindText(term);
            if (matches.Count == 0)
                continue;
            total += matches.Count;
            var rects = string.Join(" ", matches.Select(m => $"[{string.Join("|", m.Rects.Select(r => $"{r.X:F0},{r.Y:F0},{r.Width:F0}x{r.Height:F0}"))}]"));
            Console.WriteLine($"page {p + 1}: {matches.Count} {rects}");
        }
        Console.WriteLine($"total {total}");
        return 0;
    }

    /// <summary>Opens the same file repeatedly, from where it is and from a fresh local temp copy.</summary>
    public static int OpenBench(Options opts)
    {
        var file = opts.Require("file");
        var n = opts.Int("n", 10);
        var engine = new PdfiumEngine();

        static double Median(List<double> xs) { xs.Sort(); return xs[xs.Count / 2]; }

        var inPlace = new List<double>();
        for (var i = 0; i < n; i++)
        {
            var sw = Stopwatch.StartNew();
            using var doc = engine.Open(file);
            _ = doc.PageCount;
            inPlace.Add(sw.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"open in place      : first {inPlace[0]:F2} ms, median {Median(inPlace.ToList()):F2} ms");

        var fresh = new List<double>();
        var bytes = File.ReadAllBytes(file);
        var tmpDir = Directory.CreateTempSubdirectory("megapdf-openbench-").FullName;
        try
        {
            for (var i = 0; i < n; i++)
            {
                var local = Path.Combine(tmpDir, $"doc{i}.pdf");
                File.WriteAllBytes(local, bytes);
                var sw = Stopwatch.StartNew();
                using var doc = engine.Open(local);
                _ = doc.PageCount;
                fresh.Add(sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"open fresh temp copy: first {fresh[0]:F2} ms, median {Median(fresh.ToList()):F2} ms");

            var reread = new List<double>();
            var local2 = Path.Combine(tmpDir, "same.pdf");
            File.WriteAllBytes(local2, bytes);
            for (var i = 0; i < n; i++)
            {
                var sw = Stopwatch.StartNew();
                using var doc = engine.Open(local2);
                _ = doc.PageCount;
                reread.Add(sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"open same temp copy : first {reread[0]:F2} ms, median {Median(reread.ToList()):F2} ms");

            var readOnly = new List<double>();
            for (var i = 0; i < n; i++)
            {
                var sw = Stopwatch.StartNew();
                _ = File.ReadAllBytes(local2);
                readOnly.Add(sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"File.ReadAllBytes   : first {readOnly[0]:F2} ms, median {Median(readOnly.ToList()):F2} ms ({bytes.Length:N0} bytes)");
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
        return 0;
    }

    /// <summary>
    /// Text runs and visual lines of every page of every file in <c>--files</c>, in the
    /// line format <c>core/tests/core_tests.cpp</c> compares against (#106): the desktop
    /// engine's answer, captured before the contract moved into the shared core, is the
    /// checked-in expectation the core must reproduce. Coordinates are crop space
    /// (bottom-left origin, points); strings are hex-encoded UTF-16 code units so the
    /// format needs no quoting.
    /// </summary>
    public static int DumpText(Options opts)
    {
        // "-" for an empty string keeps every record the same number of whitespace-separated fields.
        static string Hex(string text) => text.Length == 0 ? "-" : string.Concat(text.Select(c => ((int)c).ToString("x4")));
        static string Num(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
        var engine = new PdfiumEngine();
        foreach (var file in opts.List("files", "").Where(f => f.Length > 0))
        {
            using var doc = engine.Open(file);
            for (var pageIndex = 0; pageIndex < doc.PageCount; pageIndex++)
            {
                using var page = doc.GetPage(pageIndex);
                var runs = page.GetTextRuns();
                var boxes = page.GetTextBoxes().ToDictionary(b => b.ObjectIndex);
                var lines = page.GetTextLines();
                Console.WriteLine($"page {Path.GetFileName(file)} {pageIndex} {Num(page.Width)} {Num(page.Height)} {runs.Count} {lines.Count}");
                string Crop(PdfRect r) => $"{Num(r.X)} {Num(page.Height - r.Bottom)} {Num(r.Right)} {Num(page.Height - r.Y)}";
                for (var i = 0; i < runs.Count; i++)
                {
                    var r = runs[i];
                    boxes.TryGetValue(r.ObjectIndex, out var box);
                    Console.WriteLine($"run {i} {r.ObjectIndex} {Crop(r.Bounds)} {Num(r.FontSize)} {Hex(r.FontName)} {Hex(box?.TextBoxId ?? "")} {Hex(box?.TextBoxFont ?? "")} {Hex(r.Text)}");
                }
                // GetTextLines re-reads the runs, so match by object index, which is unique per run.
                var index = new Dictionary<int, int>();
                for (var i = 0; i < runs.Count; i++) index[runs[i].ObjectIndex] = i;
                for (var j = 0; j < lines.Count; j++)
                {
                    var l = lines[j];
                    Console.WriteLine($"line {j} {Crop(l.Bounds)} {string.Join(",", l.Runs.Select(r => index[r.ObjectIndex]))}");
                }
            }
        }
        return 0;
    }

    /// <summary>How one page's render time scales with pixel count, plus what is on it.</summary>
    public static int Inspect(Options opts)
    {
        var file = opts.Require("file");
        var pageIndex = opts.Int("page", 1) - 1;
        var engine = new PdfiumEngine();
        using var doc = engine.Open(file);
        using var page = doc.GetPage(pageIndex);
        Console.WriteLine($"pages {doc.PageCount}; page {pageIndex + 1}: {page.Width:F1} x {page.Height:F1} pt");
        var sw = Stopwatch.StartNew();
        var runs = page.GetTextRuns();
        Console.WriteLine($"text runs {runs.Count} ({runs.Sum(r => r.Text.Length)} chars) in {sw.Elapsed.TotalMilliseconds:F0} ms");
        sw.Restart();
        var lines = page.GetTextLines();
        Console.WriteLine($"text lines {lines.Count} in {sw.Elapsed.TotalMilliseconds:F0} ms");
        sw.Restart();
        var fields = page.GetFormFields();
        Console.WriteLine($"form fields {fields.Count} in {sw.Elapsed.TotalMilliseconds:F0} ms");
        sw.Restart();
        var squares = page.DetectCheckboxSquares();
        Console.WriteLine($"checkbox squares {squares.Count} in {sw.Elapsed.TotalMilliseconds:F0} ms");
        sw.Restart();
        var images = doc.GetImages().Where(i => i.PageIndex == pageIndex).ToList();
        Console.WriteLine($"images {images.Count} (largest {images.Select(i => (long)i.PixelWidth * i.PixelHeight).DefaultIfEmpty(0).Max() / 1e6:F1} MP, " +
                          $"{images.Sum(i => i.StoredByteLength) / 1e6:F1} MB stored) in {sw.Elapsed.TotalMilliseconds:F0} ms");
        foreach (var scale in new[] { 0.25, 0.5, 1.0, 1.5, 2.0, 3.0 })
        {
            var pw = Math.Max(1, (int)(page.Width * PointsToPixels * scale));
            var ph = Math.Max(1, (int)(page.Height * PointsToPixels * scale));
            sw.Restart();
            try
            {
                _ = page.Render(pw, ph);
                Console.WriteLine($"render x{scale:F2}: {pw}x{ph} ({pw * (long)ph / 1e6:F1} MP) in {sw.Elapsed.TotalMilliseconds:F0} ms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"render x{scale:F2}: {pw}x{ph} FAILED {ex.GetType().Name}: {ex.Message}");
            }
        }
        // Second render at the same size, same page handle: what a cached page would cost.
        {
            var pw = Math.Max(1, (int)(page.Width * PointsToPixels * 1.5));
            var ph = Math.Max(1, (int)(page.Height * PointsToPixels * 1.5));
            sw.Restart();
            _ = page.Render(pw, ph);
            Console.WriteLine($"render x1.50 again on the same page handle: {sw.Elapsed.TotalMilliseconds:F0} ms");
        }
        return 0;
    }
}

// ---------------------------------------------------------------------------
// FlagsBench — how PDFium's render flags change one page's render time (#96).
// Talks to pdfium directly (the same binary MegaPDF.Core loads) because the
// engine's P/Invoke layer is internal and always renders with ANNOT | LCD_TEXT.
// ---------------------------------------------------------------------------

internal static class FlagsBench
{
    private const string Dll = "pdfium";
    [DllImport(Dll)] private static extern void FPDF_InitLibrary();
    [DllImport(Dll, CharSet = CharSet.Ansi)] private static extern IntPtr FPDF_LoadDocument(string path, string? password);
    [DllImport(Dll)] private static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
    [DllImport(Dll)] private static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Dll)] private static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Dll)] private static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport(Dll)] private static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Dll)] private static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Dll)] private static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport(Dll)] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Dll)] private static extern void FPDF_CloseDocument(IntPtr document);

    public static int Run(Options opts)
    {
        var file = opts.Require("file");
        var pageIndex = opts.Int("page", 1) - 1;
        var scale = opts.Double("scale", 1.5);
        FPDF_InitLibrary();
        var doc = FPDF_LoadDocument(file, null);
        if (doc == IntPtr.Zero) { Console.Error.WriteLine("could not open"); return 1; }
        var page = FPDF_LoadPage(doc, pageIndex);
        if (page == IntPtr.Zero) { Console.Error.WriteLine("could not load the page"); return 1; }
        var w = (int)(FPDF_GetPageWidthF(page) * 96 / 72 * scale);
        var h = (int)(FPDF_GetPageHeightF(page) * 96 / 72 * scale);
        Console.WriteLine($"{w}x{h} px");
        var variants = new (string Name, int Flags)[]
        {
            ("none", 0),
            ("ANNOT|LCD_TEXT (what the app uses)", 0x01 | 0x02),
            ("ANNOT only", 0x01),
            ("LCD_TEXT only", 0x02),
            ("ANNOT|NO_SMOOTHTEXT", 0x01 | 0x1000),
            ("ANNOT|NO_SMOOTHIMAGE", 0x01 | 0x2000),
            ("ANNOT|NO_SMOOTHPATH", 0x01 | 0x4000),
            ("ANNOT|RENDER_LIMITEDIMAGECACHE", 0x01 | 0x200),
            ("ANNOT|RENDER_FORCEHALFTONE", 0x01 | 0x400),
            ("ANNOT|NO_NATIVETEXT", 0x01 | 0x04),
        };
        foreach (var (name, flags) in variants)
        {
            var times = new List<double>();
            for (var i = 0; i < 3; i++)
            {
                var bmp = FPDFBitmap_Create(w, h, 1);
                FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
                var sw = Stopwatch.StartNew();
                FPDF_RenderPageBitmap(bmp, page, 0, 0, w, h, 0, flags);
                times.Add(sw.Elapsed.TotalMilliseconds);
                FPDFBitmap_Destroy(bmp);
            }
            times.Sort();
            Console.WriteLine($"{name,-40} median {times[1],8:F0} ms  (min {times[0]:F0})");
        }
        FPDF_ClosePage(page);
        FPDF_CloseDocument(doc);
        return 0;
    }
}

// ---------------------------------------------------------------------------
// Orchestrator — worker pool, crash/hang isolation, resume, progress.
// ---------------------------------------------------------------------------

internal static class Orchestrator
{
    public static int Run(Options opts)
    {
        var root = Path.GetFullPath(opts.Require("root"));
        var outDir = Path.GetFullPath(opts.Require("out"));
        Directory.CreateDirectory(outDir);
        var workers = Math.Max(1, opts.Int("workers", 4));
        var scale = opts.Double("scale", 1.0);
        var phases = string.Join(",", opts.List("phases", "scroll,search,zoom,save,images"));
        var terms = string.Join(",", opts.List("terms", "Seaman,the"));
        var hangSeconds = opts.Int("hang-seconds", 180);
        var capBase = opts.Int("cap-base", 300);
        var capPerPage = opts.Double("cap-per-page", 2);
        var limit = opts.Int("limit", int.MaxValue);
        var filter = opts.Get("filter");

        var listPath = Path.Combine(outDir, "files.txt");
        string[] files;
        if (File.Exists(listPath))
        {
            files = File.ReadAllLines(listPath);
            Log(outDir, $"resuming: {files.Length} files listed in {listPath}");
        }
        else
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                AttributesToSkip = FileAttributes.Device,
            };
            files = Directory.EnumerateFiles(root, "*.pdf", options)
                .Select(f => Path.GetRelativePath(root, f))
                .Where(f => filter is null || f.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .Take(limit)
                .ToArray();
            File.WriteAllLines(listPath, files);
            Log(outDir, $"enumerated {files.Length} PDFs under {root}");
        }

        var resultsPath = Path.Combine(outDir, "results.jsonl");
        var done = new HashSet<int>();
        if (File.Exists(resultsPath))
        {
            foreach (var line in File.ReadLines(resultsPath))
            {
                try
                {
                    using var d = JsonDocument.Parse(line);
                    if (d.RootElement.TryGetProperty("i", out var ip))
                        done.Add(ip.GetInt32());
                }
                catch { /* truncated tail line from a killed run */ }
            }
        }

        var queue = new ConcurrentQueue<int>(Enumerable.Range(0, files.Length).Where(i => !done.Contains(i)));
        var total = files.Length;
        var alreadyDone = done.Count;
        Log(outDir, $"{queue.Count} to do, {alreadyDone} already done; workers={workers} scale={scale} phases={phases} terms={terms}");

        File.WriteAllText(Path.Combine(outDir, "run.json"), JsonSerializer.Serialize(new
        {
            root, started = DateTimeOffset.Now, workers, scale, phases, terms, hangSeconds, capBase, capPerPage,
            os = RuntimeInformation.OSDescription, arch = RuntimeInformation.OSArchitecture.ToString(),
            machine = Environment.MachineName, cpus = Environment.ProcessorCount,
            pdfium = PdfiumPin(),
        }, new JsonSerializerOptions { WriteIndented = true }));

        var results = new StreamWriter(new FileStream(resultsPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        var resultsLock = new object();
        var stats = new Stats { Done = alreadyDone, Total = total };
        var stopFile = Path.Combine(outDir, "STOP");
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var tasks = new List<Task>();
        for (var w = 0; w < workers; w++)
        {
            var slot = w;
            tasks.Add(Task.Run(() => WorkerLoop(slot, queue, files, root, outDir, listPath, scale, phases, terms,
                hangSeconds, capBase, capPerPage, results, resultsLock, stats, stopFile, cts.Token)));
        }

        var started = Stopwatch.StartNew();
        var lastReport = 0L;
        while (!Task.WaitAll(tasks.ToArray(), 5000))
        {
            if (File.Exists(stopFile) && !cts.IsCancellationRequested)
            {
                Log(outDir, "STOP file seen — stopping workers");
                cts.Cancel();
            }
            if (started.ElapsedMilliseconds - lastReport >= 30000)
            {
                lastReport = started.ElapsedMilliseconds;
                Progress(outDir, stats, started.Elapsed, alreadyDone);
            }
        }
        Progress(outDir, stats, started.Elapsed, alreadyDone);
        results.Dispose();
        Log(outDir, $"finished: done={stats.Done}/{stats.Total} ok={stats.Ok} nonok={stats.NonOk} crashes={stats.Crashes} hangs={stats.Hangs} in {started.Elapsed:hh\\:mm\\:ss}");
        return 0;
    }

    /// <summary>The pinned PDFium build (libs/pdfium/win-x64/VERSION), found by walking up from the binary.</summary>
    private static string? PdfiumPin()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "libs", "pdfium", "win-x64", "VERSION");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r", "").Replace("\n", " ").Trim();
            dir = dir.Parent;
        }
        return null;
    }

    private sealed class Stats
    {
        public int Total;
        public int Done;
        public int Ok;
        public int NonOk;
        public int Crashes;
        public int Hangs;
        public long Pages;
    }

    private static void Progress(string outDir, Stats s, TimeSpan elapsed, int alreadyDone)
    {
        var doneThisRun = s.Done - alreadyDone;
        var rate = doneThisRun / Math.Max(1.0, elapsed.TotalSeconds);
        var remaining = s.Total - s.Done;
        var eta = rate > 0 ? TimeSpan.FromSeconds(remaining / rate) : TimeSpan.Zero;
        Log(outDir, $"progress {s.Done}/{s.Total} ok={s.Ok} nonok={s.NonOk} crashes={s.Crashes} hangs={s.Hangs} pages={s.Pages} " +
                    $"elapsed={elapsed:hh\\:mm\\:ss} rate={rate * 60:F1}/min eta={eta:hh\\:mm\\:ss}");
    }

    private static readonly object LogLock = new();

    private static void Log(string outDir, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        lock (LogLock)
        {
            Console.WriteLine(line);
            File.AppendAllText(Path.Combine(outDir, "run.log"), line + Environment.NewLine);
        }
    }

    private static void WorkerLoop(int slot, ConcurrentQueue<int> queue, string[] files, string root, string outDir,
                                   string listPath, double scale, string phases, string terms, int hangSeconds,
                                   int capBase, double capPerPage, StreamWriter results, object resultsLock,
                                   Stats stats, string stopFile, CancellationToken cancel)
    {
        Process? proc = null;
        BlockingCollection<string>? lines = null;
        StreamWriter? stderrLog = null;

        void Spawn()
        {
            Kill();
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (var a in new[] { "worker", "--list", listPath, "--root", root, "--scale", scale.ToString(System.Globalization.CultureInfo.InvariantCulture), "--phases", phases, "--terms", terms })
                psi.ArgumentList.Add(a);
            psi.Environment["DOTNET_gcServer"] = "0";
            proc = Process.Start(psi)!;
            var local = new BlockingCollection<string>();
            lines = local;
            var p = proc;
            stderrLog ??= new StreamWriter(new FileStream(Path.Combine(outDir, $"worker-{slot}.stderr.log"), FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            var errLog = stderrLog;
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) errLog.WriteLine($"[pid {p.Id}] {e.Data}"); };
            p.BeginErrorReadLine();
            Task.Run(() =>
            {
                try
                {
                    string? l;
                    while ((l = p.StandardOutput.ReadLine()) is not null)
                        local.Add(l);
                }
                catch { /* process gone */ }
                local.CompleteAdding();
            });
            Log(outDir, $"worker {slot}: started pid {proc.Id}");
        }

        void Kill()
        {
            if (proc is null)
                return;
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { proc.WaitForExit(5000); } catch { /* ignore */ }
            proc.Dispose();
            proc = null;
        }

        try
        {
            Spawn();
            while (!cancel.IsCancellationRequested && queue.TryDequeue(out var index))
            {
                if (proc is null || proc.HasExited)
                    Spawn();

                var result = RunOne(index);
                if (result.Outcome == "aborted")
                {
                    queue.Enqueue(index); // not done — a resumed run picks it up
                    break;
                }
                lock (resultsLock)
                {
                    results.WriteLine(JsonSerializer.Serialize(result, Json.Options));
                }
                lock (stats)
                {
                    stats.Done++;
                    if (result.Outcome == "ok") stats.Ok++; else stats.NonOk++;
                    if (result.Outcome == "crash") stats.Crashes++;
                    if (result.Outcome == "hang") stats.Hangs++;
                    stats.Pages += result.Pages ?? 0;
                }
                if (result.Outcome is "crash" or "hang" or "error")
                    Log(outDir, $"worker {slot}: file {index} -> {result.Outcome} {result.Error} (phase {result.LastPhase} page {result.LastPage})");
            }
        }
        finally
        {
            try { proc?.StandardInput.Close(); } catch { /* ignore */ }
            try { proc?.WaitForExit(3000); } catch { /* ignore */ }
            Kill();
            stderrLog?.Dispose();
        }

        FileResult RunOne(int index)
        {
            var p = proc!;
            var lineQueue = lines!;
            var started = Stopwatch.StartNew();
            var lastBeat = Stopwatch.StartNew();
            string? lastPhase = null;
            int? lastPage = null;
            double capSeconds = capBase;
            FileResult? result = null;

            try
            {
                p.StandardInput.WriteLine(index.ToString());
                p.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                return Failure("crash", $"could not talk to worker: {ex.Message}");
            }

            while (true)
            {
                if (lineQueue.TryTake(out var line, 1000))
                {
                    lastBeat.Restart();
                    if (line.StartsWith("HB ", StringComparison.Ordinal))
                    {
                        var parts = line.Split(' ');
                        if (parts.Length >= 4)
                        {
                            lastPhase = parts[2];
                            lastPage = int.TryParse(parts[3], out var pg) ? pg : null;
                        }
                    }
                    else if (line.StartsWith("PAGES ", StringComparison.Ordinal))
                    {
                        var parts = line.Split(' ');
                        if (parts.Length >= 3 && int.TryParse(parts[2], out var n))
                            capSeconds = capBase + n * capPerPage;
                    }
                    else if (line.StartsWith("RESULT ", StringComparison.Ordinal))
                    {
                        try { result = JsonSerializer.Deserialize<FileResult>(line[7..], Json.Options); }
                        catch (Exception ex) { result = Failure("error", $"unparseable result: {ex.Message}"); }
                    }
                    else if (line.StartsWith("DONE ", StringComparison.Ordinal))
                    {
                        return result ?? Failure("error", "worker reported DONE without a result");
                    }
                    continue;
                }

                if (p.HasExited || lineQueue.IsCompleted)
                {
                    var code = 0;
                    try { code = p.ExitCode; } catch { /* ignore */ }
                    var f = Failure("crash", $"worker exited with code {code} (0x{code:X8})");
                    f.ExitCode = code;
                    Spawn();
                    return f;
                }
                if (lastBeat.Elapsed.TotalSeconds > hangSeconds)
                {
                    var f = Failure("hang", $"no heartbeat for {hangSeconds}s");
                    Spawn();
                    return f;
                }
                if (started.Elapsed.TotalSeconds > capSeconds)
                {
                    var f = Failure("hang", $"exceeded per-file cap of {capSeconds:F0}s");
                    Spawn();
                    return f;
                }
                if (cancel.IsCancellationRequested)
                {
                    var f = Failure("aborted", "run stopped");
                    return f;
                }
            }

            FileResult Failure(string outcome, string message) => new()
            {
                Index = index,
                Path = files[index],
                Outcome = outcome,
                Error = message,
                LastPhase = lastPhase,
                LastPage = lastPage,
                WallMs = started.Elapsed.TotalMilliseconds,
                WorkerPid = SafePid(p),
            };
        }
    }

    private static int? SafePid(Process p)
    {
        try { return p.Id; } catch { return null; }
    }
}
