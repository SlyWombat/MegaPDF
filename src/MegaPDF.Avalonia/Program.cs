using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using MegaPDF.Avalonia.ViewModels;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Imaging;
using MegaPDF.Core.Engine.Pdfium;

namespace MegaPDF.Avalonia;

internal static class Program
{
    // Avalonia's initialisation must not be moved into Main's body — the visual
    // designer and `dotnet run` both look for BuildAvaloniaApp by convention.
    [STAThread]
    public static int Main(string[] args)
    {
        // Before anything reads a setting or writes a temporary file — the first
        // happens as the window is built, and the second before a save touches the
        // document (#193, #195). Linux only; it returns at once anywhere else.
        Platform.LinuxPaths.PrepareUserDirectories();

        // Before anything reads a string: the toolbar labels are resolved when the
        // window is built, and the diagnostics below print Strings.* too.
        ApplyLanguage(args);

        return args.Contains("--render-check") ? RenderCheck(args)
             : args.Contains("--self-test") ? SelfTest(args)
             : args.Contains("--print-check") ? PrintCheck(args)
             : args.Contains("--language-check") ? LanguageCheck()
             : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Which language the app runs in (#91). `--language fr-CA` wins, so a
    /// French run can be checked on an English machine; otherwise, on macOS, the
    /// language the person chose in System Settings — .NET's own default comes
    /// from the POSIX locale, which a Dock-launched .app is not given; and on
    /// Linux the POSIX locale environment read in full, because .NET reads all of
    /// it except LANGUAGE, which is the one a desktop sets when the display
    /// language differs from the formats (#158). Anywhere else .NET's default
    /// stands.
    ///
    /// Never fatal: a tag that does not parse is ignored, and the app comes up in
    /// whatever .NET picked. There is no English-only fallback to force, because
    /// the neutral catalogue *is* English.
    /// </summary>
    private static void ApplyLanguage(string[] args)
    {
        try
        {
            string? tag = null;
            var flag = Array.IndexOf(args, "--language");
            if (flag >= 0 && flag + 1 < args.Length)
                tag = args[flag + 1];
            else if (OperatingSystem.IsMacOS())
                tag = Platform.MacLanguage.PreferredLanguageTag();
            else if (OperatingSystem.IsLinux())
                tag = Platform.LinuxLanguage.PreferredLanguageTag();

            if (string.IsNullOrWhiteSpace(tag))
                return;

            var culture = CultureInfo.GetCultureInfo(tag);
            // Both halves: the UI culture picks the catalogue, the culture formats
            // the numbers and dates that go into it.
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
        }
        catch (Exception)
        {
            // A bad tag must never stop the app.
        }
    }

    /// <summary>Where --screenshot writes, read by App once the window is up.</summary>
    internal static string? ScreenshotPath;

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        // Name the macOS UI font explicitly (#160). Left to itself, Avalonia resolves
        // a face on macOS that has no precomposed capital accented glyphs and silently
        // drops the accent: "Échap pour annuler" was drawn as "Echap pour annuler",
        // and "À partir d'une photo" as "A partir d'une photo". Only marks *above* the
        // letter were lost — Ç kept its cedilla and every lower-case accent was fine —
        // which is why it went unnoticed for so long.
        //
        // The note below this method says macOS gets San Francisco. It never did, and
        // asking for it by name does not help: ".AppleSystemUIFont" and ".SF NS" both
        // draw the accented capitals correctly but mis-advance U+2026, so every "…" in
        // the app collides with the character after it — "Password…" came out as
        // "Password..", and the app is full of "Opening…", "Saving…", "Save As…".
        // "SF Pro" and "SF Pro Text" just fall back to the broken default.
        //
        // Helvetica Neue is the one that gets both right, and it is all but what the
        // app already draws: the default Avalonia was picking is a Helvetica, not San
        // Francisco. `--brand-check` prints the resolved family so this is checkable
        // rather than a claim. Genuine San Francisco would need the accent and advance
        // bugs fixed upstream in Avalonia, not a different string here.
        //
        // global:: because this file's own namespace is MegaPDF.Avalonia, so a bare
        // Avalonia.Media here would be read as MegaPDF.Avalonia.Media.
        if (OperatingSystem.IsMacOS())
            builder = builder.With(new global::Avalonia.Media.FontManagerOptions
            {
                DefaultFamilyName = "Helvetica Neue",
            });

        // The application menu is ours, all of it (#191). Left to itself Avalonia
        // appends Services, Hide, Hide Others, Show All and Quit to whatever app menu
        // it finds, with English titles compiled into the framework — so a French run
        // read "À propos de MegaPDF" above "Hide MegaPDF", and no amount of work on
        // our side could reach those four words. App.BuildAppMenu adds them itself,
        // with the wording macOS uses and Apple's shortcuts.
        if (OperatingSystem.IsMacOS())
            builder = builder.With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true });

        // A flyout is normally its own OS window, which RenderTargetBitmap cannot
        // see, so the `sign` screenshot state (#100) would capture a closed library.
        // Drawing popups inside the main window for capture runs only keeps the
        // shipped behaviour untouched.
        if (Environment.GetCommandLineArgs().Contains("--screenshot"))
        {
            builder = builder
                .With(new Win32PlatformOptions { OverlayPopups = true })
                .With(new X11PlatformOptions { OverlayPopups = true })
                .With(new AvaloniaNativePlatformOptions { OverlayPopups = true });
        }
        return builder;
    }
    // No .WithInterFont(): bundling Inter would make the app look the same
    // everywhere, which is the opposite of what we want. The system default is
    // San Francisco on macOS and Segoe UI on Windows — each native to its host.

    /// <summary>
    /// Headless diagnostic: open a PDF, rasterise page 1, report. No window, so it
    /// runs on a CI machine with no display.
    ///
    /// This is what lets the macOS workflow prove a freshly built .app *launches
    /// and loads its native PDFium* rather than merely that it assembled — the
    /// failure mode a bundle-and-upload job would otherwise ship straight to a
    /// user. Deliberately not a hidden feature: it renders nothing to disk and
    /// changes no state.
    /// </summary>
    private static int RenderCheck(string[] args)
    {
        var path = args.FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        if (path is null || !File.Exists(path))
        {
            Console.Error.WriteLine("usage: MegaPDF --render-check <file.pdf>");
            return 2;
        }

        try
        {
            using var engine = new PdfiumEngine();
            using var document = engine.Open(path);
            using var page = document.GetPage(0);

            var pixelWidth = (int)Math.Round(page.Width * Rendering.PageBitmap.PointsToPixels);
            var pixelHeight = (int)Math.Round(page.Height * Rendering.PageBitmap.PointsToPixels);
            var rendered = page.Render(pixelWidth, pixelHeight);

            var distinct = new HashSet<uint>();
            for (var i = 0; i + 3 < rendered.Bgra.Length && distinct.Count <= 8; i += 4)
                distinct.Add(BitConverter.ToUInt32(rendered.Bgra, i));

            Console.WriteLine($"render-check: {Path.GetFileName(path)}, {document.PageCount} page(s), "
                              + $"page 1 -> {rendered.PixelWidth}x{rendered.PixelHeight} px, "
                              + $"{distinct.Count} distinct pixel values");

            if (distinct.Count < 2)
            {
                Console.Error.WriteLine("::error::render-check: the page came back blank.");
                return 1;
            }

            Console.WriteLine("render-check: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::render-check: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Verifies the macOS printing interop without printing anything.
    ///
    /// Every step up to the print panel is checkable headlessly, and the last one is
    /// the point: it reads the page count back through PDFKit and compares it with
    /// what PdfiumEngine says about the same file. A wrong objc_msgSend signature
    /// returns a plausible-looking pointer rather than failing, so only comparing a
    /// value against a known-good number proves the marshalling is actually correct.
    ///
    /// It stops before runOperation — the modal panel is the one part CI cannot reach.
    /// </summary>
    private static int PrintCheck(string[] args)
    {
        // Linux prints through CUPS' lp rather than a framework, so there is no
        // marshalling to prove — what can be wrong is the lpstat parsing that
        // decides which queues exist and which is the default, and whether the
        // client tools are installed at all (#158). Checked against a fixed sample,
        // because a build runner has no printers and a probe that only asked the
        // machine would pass by finding nothing.
        if (OperatingSystem.IsLinux())
        {
            var linux = Platform.LinuxPrinter.Probe();
            Console.WriteLine($"print-check: {linux.Message}");
            if (!linux.Ok)
            {
                Console.Error.WriteLine("::error::print-check FAILED — the CUPS printing route is not sound.");
                return 1;
            }
            Console.WriteLine("print-check: PASS");
            return 0;
        }

        if (!OperatingSystem.IsMacOS())
        {
            Console.WriteLine("print-check: skipped (not macOS)");
            return 0;
        }

        var path = args.FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        if (path is null || !File.Exists(path))
        {
            Console.Error.WriteLine("usage: MegaPDF --print-check <file.pdf>");
            return 2;
        }

        int pageCount;
        try
        {
            using var engine = new PdfiumEngine();
            using var document = engine.Open(path);
            pageCount = document.PageCount;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::print-check: could not read the fixture: {ex.Message}");
            return 1;
        }

        var outcome = Platform.MacPrinter.Probe(path, pageCount);
        Console.WriteLine($"print-check: {outcome.Message}");

        if (!outcome.Ok)
        {
            Console.Error.WriteLine("::error::print-check FAILED — the PDFKit/AppKit interop is not sound.");
            return 1;
        }

        Console.WriteLine("print-check: PASS");
        return 0;
    }

    /// <summary>
    /// Headless diagnostic: which language the app would run in, and — on Linux —
    /// that the POSIX locale environment is read the way the rest of the desktop
    /// reads it (#91, #158).
    ///
    /// The Linux half is the part with rules worth asserting, and they are asserted
    /// against a table rather than against the machine's own environment, so the
    /// check means the same thing on a French desktop and on an English CI runner.
    /// </summary>
    private static int LanguageCheck()
    {
        Console.WriteLine($"language-check: the app would run in {CultureInfo.CurrentUICulture.Name} "
                          + $"(formats {CultureInfo.CurrentCulture.Name})");

        if (OperatingSystem.IsMacOS())
        {
            Console.WriteLine("language-check: System Settings says "
                              + (Platform.MacLanguage.PreferredLanguageTag() ?? "(unreadable)"));
            Console.WriteLine("language-check: PASS");
            return 0;
        }

        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("language-check: no platform rules to check here (.NET's own default stands)");
            Console.WriteLine("language-check: PASS");
            return 0;
        }

        // (what the locale environment holds, what the app must run in, why it matters)
        (Dictionary<string, string> Variables, string? Expected, string Why)[] cases =
        [
            (new() { ["LANG"] = "fr_CA.UTF-8" }, "fr-CA",
             "a plain French Canadian desktop"),
            (new() { ["LANG"] = "fr_FR.UTF-8" }, "fr-FR",
             "France French, which falls back to the neutral fr catalogue"),
            (new() { ["LANG"] = "en_CA.UTF-8", ["LANGUAGE"] = "fr_CA:fr" }, "fr-CA",
             "GNOME's display language set to French with Canadian English formats — "
             + "the case .NET alone gets wrong, because it never reads LANGUAGE"),
            (new() { ["LANGUAGE"] = "fr:en", ["LANG"] = "en_US.UTF-8" }, "fr",
             "a bare language with no territory"),
            (new() { ["LC_ALL"] = "fr_CA.UTF-8", ["LANG"] = "en_US.UTF-8" }, "fr-CA",
             "LC_ALL outranks LANG"),
            (new() { ["LC_MESSAGES"] = "fr_CA.UTF-8", ["LANG"] = "en_US.UTF-8" }, "fr-CA",
             "LC_MESSAGES outranks LANG"),
            (new() { ["LC_ALL"] = "C", ["LANGUAGE"] = "fr_CA:fr" }, null,
             "LC_ALL=C vetoes LANGUAGE, so a script asking for stable output gets it"),
            (new() { ["LANG"] = "POSIX" }, null,
             "the untranslated locale asks for no translation"),
            (new() { ["LANG"] = "fr_CA.UTF-8@euro" }, "fr-CA",
             "a codeset and a modifier are dropped, not translated"),
            (new(), null,
             "an empty environment leaves .NET's own default alone"),
        ];

        var failures = 0;
        foreach (var (variables, expected, why) in cases)
        {
            var got = Platform.LinuxLanguage.PreferredLanguageTag(
                name => variables.TryGetValue(name, out var value) ? value : null);
            var ok = string.Equals(got, expected, StringComparison.Ordinal);
            if (!ok) failures++;
            var shown = string.Join(" ", variables.Select(v => v.Key + "=" + v.Value));
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {why}");
            Console.WriteLine($"          {(shown.Length == 0 ? "(nothing set)" : shown)} -> {got ?? "(.NET's default)"}"
                              + (ok ? "" : $", expected {expected ?? "(.NET's default)"}"));
        }

        // And that every tag the table produces is a culture .NET actually has, so a
        // pass here cannot mean "resolved to a custom culture with no resources".
        foreach (var tag in new[] { "fr-CA", "fr", "fr-FR", "en-CA" })
        {
            var culture = CultureInfo.GetCultureInfo(tag);
            var ok = !culture.Equals(CultureInfo.InvariantCulture);
            if (!ok) failures++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {tag} resolves to a real culture ({culture.EnglishName})");
        }

        Console.WriteLine($"language-check: {(failures == 0 ? "PASS" : $"FAIL — {failures} check(s)")}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// End-to-end check of the fill-check-sign path, with no window: hit-test a
    /// checkbox, click it, save, reopen, and confirm the edit is really in the bytes.
    ///
    /// This exists because clicking is the one thing CI cannot do, and "the app
    /// launches" says nothing about whether ticking a box works. It drives the real
    /// MainViewModel, so it exercises the same routing a click does, and it verifies
    /// through a fresh engine open of the saved file rather than by asking the object
    /// that just did the work.
    ///
    /// Coordinates come from tools/gen_test_fixtures.py, in top-left page space:
    /// fixture.pdf's drawn square is (72,600)-(84,612) in PDF space on a 792-tall
    /// page, so 180-192 from the top; forms.pdf's "agree" widget is (100,600)-(115,615)
    /// => 177-192.
    /// </summary>
    /// <summary>
    /// Whether the document still says the canary, read back through a save. Used before a
    /// redaction is applied, to prove that marking alone removes nothing (#173).
    /// </summary>
    private static bool DocumentSaysCanary(MainViewModel vm, string scratchPath)
    {
        using (var file = File.Create(scratchPath))
            vm.SaveTo(file);
        using var engine = new PdfiumEngine();
        using var document = engine.Open(scratchPath);
        using var page = document.GetPage(0);
        return page.GetTextRuns().Any(r => r.Text.Contains("CANARY", StringComparison.Ordinal));
    }

    private static int SelfTest(string[] args)
    {
        var dir = args.FirstOrDefault(a => Directory.Exists(a));
        if (dir is null)
        {
            Console.Error.WriteLine("usage: MegaPDF --self-test <fixtures-dir>");
            return 2;
        }

        // English, whatever the machine speaks: the checks compare against
        // Strings.* so they hold in any language, but the PASS/FAIL lines are read
        // by people in CI logs, and a French runner must not make them differ.
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.CurrentUICulture = english;
        CultureInfo.CurrentCulture = english;

        // Isolated state, wiped afterwards: the checks change settings (flatten,
        // mark style) and add signatures, and none of that belongs in the real
        // per-user files of whoever runs this.
        var state = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-state-{Guid.NewGuid():N}");

        var failures = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
            if (!ok) failures++;
        }

        // --- Drawn checkbox: hit-test, click, save, reopen ---
        Console.WriteLine("drawn checkbox (SDD §3.2 heuristic):");
        var drawnCentre = new PdfPoint(78, 186);
        var savedPath = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-{Guid.NewGuid():N}.pdf");
        try
        {
            using var vm = new MainViewModel(state);
            vm.Open(Path.Combine(dir, "fixture.pdf"));
            Check("document opened", vm.IsDocumentOpen);
            Check("the square reads as a drawn checkbox",
                  vm.HitTest(0, drawnCentre).Kind == PageHitKind.DrawnCheckbox);

            vm.HandlePageClick(0, drawnCentre);
            Check("clicking it marks the document dirty", vm.IsDirty);
            Check("and is undoable", vm.CanUndo);

            using (var file = File.Create(savedPath))
                vm.SaveTo(file);
            Check("saving clears the dirty flag", !vm.IsDirty);

            using var engine = new PdfiumEngine();
            using var reopened = engine.Open(savedPath);
            using var page = reopened.GetPage(0);
            var stamps = page.GetStamps();
            Check("the mark survived save and reopen",
                  stamps.Any(st => st.Id.StartsWith("mark:", StringComparison.Ordinal)));
            Check("clicking the mark again would clear it",
                  vm.HitTest(0, drawnCentre).Kind == PageHitKind.StampAnnotation);

            vm.UndoCommand.Execute(null);
            Check("undo removes the mark",
                  vm.HitTest(0, drawnCentre).Kind == PageHitKind.DrawnCheckbox);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::drawn checkbox: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            if (File.Exists(savedPath)) File.Delete(savedPath);
        }

        // --- AcroForm checkbox ---
        Console.WriteLine("AcroForm checkbox:");
        try
        {
            using var vm = new MainViewModel(state);
            vm.Open(Path.Combine(dir, "forms.pdf"));
            var widgetCentre = new PdfPoint(107, 184);
            var hit = vm.HitTest(0, widgetCentre);
            Check("the widget reads as a form checkbox", hit.Kind == PageHitKind.FormCheckbox);
            Check("and starts unchecked", hit.Field is { IsChecked: false });

            vm.HandlePageClick(0, widgetCentre);
            Check("clicking it ticks the field",
                  vm.HitTest(0, widgetCentre).Field is { IsChecked: true });

            vm.UndoCommand.Execute(null);
            Check("undo unticks it",
                  vm.HitTest(0, widgetCentre).Field is { IsChecked: false });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::AcroForm checkbox: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        // --- Signature placement (SDD §3.3) ---
        Console.WriteLine("signature placement:");
        var signedPath = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-sig-{Guid.NewGuid():N}.pdf");
        try
        {
            using var vm = new MainViewModel(state);
            vm.Open(Path.Combine(dir, "fixture.pdf"));

            // A 40x20 block of opaque ink. Synthesised rather than loaded so this runs
            // with no graphics stack initialised — the PNG round-trip is Avalonia's
            // job and is exercised by the app itself, the geometry is what matters here.
            const int w = 40, h = 20;
            var bgra = new byte[w * h * 4];
            for (var i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = bgra[i + 1] = bgra[i + 2] = 0x20;
                bgra[i + 3] = 255;
            }

            var at = new PdfPoint(300, 400);
            vm.PlaceSignature(0, at, new SignatureBitmap(bgra, w, h), "placed");
            Check("placing a signature marks the document dirty", vm.IsDirty);

            using (var file = File.Create(signedPath))
                vm.SaveTo(file);

            using var engine = new PdfiumEngine();
            using var reopened = engine.Open(signedPath);
            using var page = reopened.GetPage(0);
            var sig = page.GetStamps().FirstOrDefault(st => st.Id.StartsWith("sig:", StringComparison.Ordinal));
            Check("the signature survived save and reopen", sig is not null);

            if (sig is not null)
            {
                // 180pt wide, aspect preserved (40x20 => 90pt tall), centred on the
                // click. Same geometry as the WinUI app, so a document signed on one
                // desktop looks the same on the other.
                Check("it is 180pt wide", Math.Abs(sig.Bounds.Width - 180) < 0.5);
                Check("its aspect ratio is preserved", Math.Abs(sig.Bounds.Height - 90) < 0.5);
                Check("and it is centred on the click", Math.Abs(sig.Bounds.X - (300 - 90)) < 0.5);
            }

            vm.UndoCommand.Execute(null);
            Check("undo removes the signature",
                  vm.HitTest(0, at).Kind != PageHitKind.StampAnnotation);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::signature placement: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            if (File.Exists(signedPath)) File.Delete(signedPath);
        }

        // --- Find in document (SDD §3.6) ---
        Console.WriteLine("find in document:");
        try
        {
            using var vm = new MainViewModel(state);
            vm.Open(Path.Combine(dir, "fixture.pdf"));

            // fixture.pdf page 1 says "The square below is a drawn checkbox candidate."
            vm.Search("checkbox");
            Check("a term in the document is found", vm.MatchCount > 0);
            Check("and the first hit is selected", vm.CurrentMatchIndex == 0);
            Check("the summary counts it", vm.MatchSummary == Strings.MatchOf(1, vm.MatchCount));

            var first = vm.CurrentMatchIndex;
            vm.FindNextCommand.Execute(null);
            Check("next advances, wrapping when there is only one",
                  vm.CurrentMatchIndex == (first + 1) % vm.MatchCount);

            vm.Search("case-insensitivity");
            var lower = vm.MatchCount;
            vm.Search("CHECKBOX");
            Check("search is case-insensitive", vm.MatchCount > 0);

            vm.Search("zzz-not-in-this-document");
            Check("a term that is absent reports none", vm.MatchCount == 0);
            Check("and says so in words", vm.MatchSummary == Strings.NotFound);

            vm.CloseFind();
            Check("closing find clears the term", vm.SearchTerm.Length == 0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::find: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        // --- Text boxes and whiteout (SDD §3.1, §3.3) ---
        Console.WriteLine("text boxes and cover:");
        var editedPath = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-edit-{Guid.NewGuid():N}.pdf");
        try
        {
            using var vm = new MainViewModel(state);
            vm.Open(Path.Combine(dir, "fixture.pdf"));

            vm.TextFont = StandardTextBoxFonts.Serif;
            vm.TextSize = 18;
            vm.AddTextBox(0, new PdfPoint(120, 300), "Filled in on a Mac");
            Check("adding text marks the document dirty", vm.IsDirty);
            Check("and leaves placement mode", vm.Mode == MainViewModel.PageMode.Select);

            vm.AddWhiteout(0, new PdfRect(200, 200, 80, 20));

            using (var file = File.Create(editedPath))
                vm.SaveTo(file);

            using var engine = new PdfiumEngine();
            using var reopened = engine.Open(editedPath);
            using var page = reopened.GetPage(0);

            var boxes = page.GetTextBoxes();
            var mine = boxes.FirstOrDefault(b => b.Text.Contains("Filled in on a Mac", StringComparison.Ordinal));
            Check("the text box survived save and reopen", mine is not null);
            // SDD §6.2 contract 4: the face is recorded on the mark, so every
            // platform reads back exactly what was chosen rather than whatever
            // pdfium normalised the font name to.
            Check("it records the face that was chosen",
                  mine?.TextBoxFont == StandardTextBoxFonts.Serif);
            Check("the cover rectangle survived too", page.GetWhiteouts().Count > 0);

            vm.UndoCommand.Execute(null);
            vm.UndoCommand.Execute(null);
            Check("undo unwinds both edits", !vm.CanUndo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::text/cover: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            if (File.Exists(editedPath)) File.Delete(editedPath);
        }

        // --- Redaction (SDD §3.8 — F7, #173) ---
        //
        // The whole flow, in the view model the window binds to: arm the tool, mark, check
        // that nothing has been written yet, apply, and prove the words are gone from the
        // saved file rather than merely invisible in it.
        Console.WriteLine("redaction:");
        var redactedPath = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-redact-{Guid.NewGuid():N}.pdf");
        try
        {
            using var vm = new MainViewModel(state);
            vm.Open(Path.Combine(dir, "text-partial-run.pdf"));
            Check("the redaction fixture opened", vm.IsDocumentOpen);

            vm.ToggleRedactCommand.Execute(null);
            Check("the Redact tool arms", vm.IsRedactMode);
            Check("and its hint says what a drag will do", vm.ModeHint == Strings.RedactHint);

            // The canary sits in the middle of the line; the KEEP words are either side.
            vm.AddRedactionMark(0, new PdfRect(120, 74, 150, 24));
            Check("marking leaves placement mode", vm.Mode == MainViewModel.PageMode.Select);
            Check("the document now carries a mark", vm.HasRedactionMarks);
            Check("and nothing has been removed yet: the text is still there",
                  DocumentSaysCanary(vm, redactedPath));

            // A mark is not content: a document saved with one carries none.
            using (var file = File.Create(redactedPath))
                vm.SaveTo(file);
            using (var engine = new PdfiumEngine())
            using (var withMarks = engine.Open(redactedPath))
            {
                Check("a document saved with marks on it carries none",
                      withMarks.RedactionMarkCount == 0);
            }

            var applied = vm.ApplyRedactionsAsync().GetAwaiter().GetResult();
            Check("applying succeeds", applied);
            Check("the marks are gone with it", !vm.HasRedactionMarks);
            Check("undo cannot put the removed content back", !vm.CanUndo);
            Check("and the summary says what went", vm.Status.Contains("redacted", StringComparison.Ordinal));

            using (var file = File.Create(redactedPath))
                vm.SaveTo(file);

            // The point of the whole feature: the words are not in the file, by any reading
            // of it. PDFium's extraction first, then the raw bytes.
            using (var engine = new PdfiumEngine())
            using (var reopened = engine.Open(redactedPath))
            using (var page = reopened.GetPage(0))
            {
                var text = string.Join(" ", page.GetTextRuns().Select(r => r.Text));
                Check("the canary no longer extracts", !text.Contains("CANARY", StringComparison.Ordinal));
                Check("and the words beside it are still there",
                      text.Contains("KEEP", StringComparison.Ordinal));
            }
            var bytes = File.ReadAllBytes(redactedPath);
            var canary = System.Text.Encoding.ASCII.GetBytes("CANARY-42-XYZ");
            Check("the canary is not in the file's bytes either",
                  bytes.AsSpan().IndexOf(canary) < 0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::redaction: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            if (File.Exists(redactedPath)) File.Delete(redactedPath);
        }

        // --- Body text editing (SDD §3.1 — F1) ---
        Console.WriteLine("body text editing:");
        var retypedPath = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-text-{Guid.NewGuid():N}.pdf");
        try
        {
            using var vm = new MainViewModel(state);
            vm.Open(Path.Combine(dir, "fixture.pdf"));

            var lines = vm.LinesOn(0);
            Check("the page's body text reads as lines", lines.Count > 0);

            var line = lines.FirstOrDefault(l => l.Text.Contains("fixture", StringComparison.OrdinalIgnoreCase));
            Check("a known line is found", line is not null);

            if (line is not null)
            {
                vm.EditLine(0, line, "Retyped on a Mac");
                Check("editing marks the document dirty", vm.IsDirty);

                using (var file = File.Create(retypedPath))
                    vm.SaveTo(file);

                using var engine = new PdfiumEngine();
                using var reopened = engine.Open(retypedPath);
                using var page = reopened.GetPage(0);
                var text = string.Join(" ", page.GetTextLines().Select(l => l.Text));
                Check("the new words are in the saved file", text.Contains("Retyped on a Mac", StringComparison.Ordinal));
                Check("and the old ones are gone", !text.Contains("engine fixture", StringComparison.Ordinal));

                vm.UndoCommand.Execute(null);
                var afterUndo = string.Join(" ", vm.LinesOn(0).Select(l => l.Text));
                Check("undo puts the original text back",
                      afterUndo.Contains("fixture", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::body text: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            if (File.Exists(retypedPath)) File.Delete(retypedPath);
        }

        // --- AcroForm text fields ---
        Console.WriteLine("form text fields:");
        var filledPath = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-form-{Guid.NewGuid():N}.pdf");
        try
        {
            using var vm = new MainViewModel(state);
            vm.Open(Path.Combine(dir, "formtext.pdf"));

            // formtext.pdf's "fullname" widget is (100,600)-(300,620) in PDF space
            // on a 792-tall page => 172..192 from the top.
            var inTheBox = new PdfPoint(200, 182);
            var hit = vm.HitTest(0, inTheBox);
            Check("the widget reads as a form text field", hit.Kind == PageHitKind.FormTextField);
            Check("and starts empty", hit.Field is { Value: "" });

            if (hit.Field is { } field)
            {
                vm.SetFieldValue(0, field, "Pat Adams");
                Check("filling it marks the document dirty", vm.IsDirty);
                Check("and the value is readable back",
                      vm.HitTest(0, inTheBox).Field is { Value: "Pat Adams" });

                using (var file = File.Create(filledPath))
                    vm.SaveTo(file);

                using var engine = new PdfiumEngine();
                using var reopened = engine.Open(filledPath);
                using var page = reopened.GetPage(0);
                var saved = page.GetFormFields().FirstOrDefault(f => f.Name == "fullname");
                Check("the value survived save and reopen", saved is { Value: "Pat Adams" });

                vm.UndoCommand.Execute(null);
                Check("undo empties it again",
                      vm.HitTest(0, inTheBox).Field is { Value: "" });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::form fields: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            if (File.Exists(filledPath)) File.Delete(filledPath);
        }

        // --- Selection, move, resize, restyle, delete, flatten, mark style ---
        Console.WriteLine("adjusting what has been placed:");
        var adjustedPath = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-adj-{Guid.NewGuid():N}.pdf");
        try
        {
            using var vm = new MainViewModel(state);
            vm.Open(Path.Combine(dir, "fixture.pdf"));

            // Place a signature, then select it by clicking it.
            const int w = 40, h = 20;
            var bgra = new byte[w * h * 4];
            for (var i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = bgra[i + 1] = bgra[i + 2] = 0x20;
                bgra[i + 3] = 255;
            }
            var at = new PdfPoint(300, 400);
            vm.PlaceSignature(0, at, new SignatureBitmap(bgra, w, h), "placed");

            vm.HandlePageClick(0, at);
            Check("clicking a signature selects it rather than deleting it",
                  vm.Selection is { Kind: MainViewModel.SelectionKind.Signature });
            Check("and it offers move and resize", vm.Selection is { CanMove: true, CanResize: true });

            var moved = new PdfRect(120, 500, 200, 100);
            vm.CommitSelectionBounds(moved);
            Check("moving and resizing it is committed",
                  vm.Selection is { } s2 && Math.Abs(s2.Bounds.X - 120) < 0.5 && Math.Abs(s2.Bounds.Width - 200) < 0.5);

            vm.UndoCommand.Execute(null);
            Check("undo puts it back", vm.HitTest(0, at).Kind == PageHitKind.StampAnnotation);

            // Text box: select, restyle, delete.
            vm.TextFont = StandardTextBoxFonts.Sans;
            vm.TextSize = 12;
            vm.AddTextBox(0, new PdfPoint(100, 300), "before");
            var boxes = vm.BoxesOn(0);
            var mine = boxes.FirstOrDefault(b => b.Text.Contains("before", StringComparison.Ordinal));
            Check("the added box is found", mine is not null);

            if (mine is not null)
            {
                vm.RestyleTextBox(0, mine, "after", StandardTextBoxFonts.Mono, 16);
                var restyled = vm.BoxesOn(0).FirstOrDefault(b => b.Text.Contains("after", StringComparison.Ordinal));
                Check("restyling changes the words", restyled is not null);
                Check("and records the new face", restyled?.TextBoxFont == StandardTextBoxFonts.Mono);
                Check("keeping the box's id, which is contract 4's handle",
                      restyled?.TextBoxId is { Length: > 0 });
            }

            // Mark style is honoured when ticking a drawn square.
            vm.MarkStyle = CheckMarkStyle.Check;
            Check("the mark style setting persists", vm.MarkStyle == CheckMarkStyle.Check);
            vm.HandlePageClick(0, new PdfPoint(78, 186));
            Check("a square still ticks with a non-default mark", vm.IsDirty);

            // Flatten on save bakes it in.
            vm.FlattenOnSave = true;
            using (var file = File.Create(adjustedPath))
                vm.SaveTo(file);

            using var engine = new PdfiumEngine();
            using var reopened = engine.Open(adjustedPath);
            using var page = reopened.GetPage(0);
            Check("flattening leaves no interactive stamps behind", page.GetStamps().Count == 0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::adjusting: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            if (File.Exists(adjustedPath)) File.Delete(adjustedPath);
        }

        try
        {
            if (Directory.Exists(state))
                Directory.Delete(state, recursive: true);
        }
        catch (IOException)
        {
        }

        // --- Toolbar wiring (#58) ---
        //
        // This exists because the rest of the self-test could not have caught #58.
        // Every other check drives the view model directly, so it proves the logic
        // and never touches the command -> CanExecute -> IsEnabled chain that a
        // button actually binds to. Five features worked perfectly and could not be
        // clicked.
        //
        // A missing [NotifyCanExecuteChangedFor] does not change what CanExecute
        // RETURNS — it stops CanExecuteChanged from ever being raised, so the button
        // never re-queries. Only observing the event catches it.
        Console.WriteLine("toolbar wiring:");
        try
        {
            using var vm = new MainViewModel(state);

            var watched = new (string Name, System.Windows.Input.ICommand Command)[]
            {
                ("Save", vm.SaveCommand), ("Print", vm.PrintCommand),
                ("Add text", vm.ToggleAddTextCommand), ("Cover", vm.ToggleWhiteoutCommand),
                ("Zoom in", vm.ZoomInCommand), ("Zoom out", vm.ZoomOutCommand),
                ("Actual size", vm.ZoomResetCommand),
                ("Fit width", vm.FitWidthCommand), ("Fit page", vm.FitPageCommand),
            };

            var notified = new HashSet<string>();
            foreach (var (name, command) in watched)
                command.CanExecuteChanged += (_, _) => notified.Add(name);

            var shrinkNotified = false;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.CanShrink))
                    shrinkNotified = true;
            };

            vm.Open(Path.Combine(dir, "fixture.pdf"));

            foreach (var (name, _) in watched)
                Check($"opening a document re-enables \"{name}\"", notified.Contains(name));
            Check("and re-evaluates whether Shrink is available", shrinkNotified);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::toolbar wiring: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        // --- The toolbar's contextual pickers and zoom menu (#144) ---
        //
        // The pickers are on the row only while there is text to style, and choosing a
        // size with a box selected restyles that box. The view shows and hides them from
        // IsTextStyleContext, so that flag and the restyle are what is checked here.
        Console.WriteLine("toolbar pickers and zoom menu (#144):");
        try
        {
            using var vm = new MainViewModel(state);
            vm.Open(Path.Combine(dir, "fixture.pdf"));
            Check("the pickers are away with nothing to style", !vm.IsTextStyleContext);

            vm.ToggleAddTextCommand.Execute(null);
            Check("arming Add text brings them", vm.IsTextStyleContext);

            vm.TextFont = StandardTextBoxFonts.Sans;
            vm.TextSize = 12;
            vm.AddTextBox(0, new PdfPoint(100, 300), "picker");
            Check("placing the text puts them away again", !vm.IsTextStyleContext);

            var box = vm.BoxesOn(0).FirstOrDefault(b => b.Text.Contains("picker", StringComparison.Ordinal));
            Check("the placed box is found", box is not null);
            if (box is not null)
            {
                vm.HandlePageClick(0, new PdfPoint(box.Bounds.X + (box.Bounds.Width / 2),
                                                   box.Bounds.Y + (box.Bounds.Height / 2)));
                Check("selecting the box brings them back",
                      vm.IsTextStyleContext && vm.Selection is { Kind: MainViewModel.SelectionKind.TextBox });

                vm.TextSize = 18;
                var restyled = vm.BoxesOn(0).FirstOrDefault(b => b.Text.Contains("picker", StringComparison.Ordinal));
                Check("choosing a size restyles the selected box",
                      restyled is not null && Math.Abs(restyled.FontSize - 18) < 0.5);
                Check("and the box stays selected at its new size",
                      vm.Selection is { Kind: MainViewModel.SelectionKind.TextBox, Run: { } run } && Math.Abs(run.FontSize - 18) < 0.5);

                vm.TextFont = StandardTextBoxFonts.Mono;
                Check("choosing a face restyles it too",
                      vm.BoxesOn(0).Any(b => b.Text.Contains("picker", StringComparison.Ordinal)
                                             && b.TextBoxFont == StandardTextBoxFonts.Mono));

                vm.ClearSelection();
                Check("deselecting puts the pickers away", !vm.IsTextStyleContext);

                vm.UndoCommand.Execute(null);
                vm.UndoCommand.Execute(null);
                Check("undo takes the box back to its size and face",
                      vm.BoxesOn(0).Any(b => b.Text.Contains("picker", StringComparison.Ordinal)
                                             && Math.Abs(b.FontSize - 12) < 0.5
                                             && (b.TextBoxFont ?? StandardTextBoxFonts.Default) == StandardTextBoxFonts.Sans));
            }

            vm.SetZoomCommand.Execute(1.5);
            Check("a zoom preset applies", Math.Abs(vm.Zoom - 1.5) < 0.001);
            Check("and the zoom button reads it", vm.ZoomPercentLabel == Strings.ZoomPercent(150));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::toolbar pickers: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        // --- Save As adopts the copy (#68) ---
        Console.WriteLine("save as:");
        var copyPath = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-copy-{Guid.NewGuid():N}.pdf");
        try
        {
            using var vm = new MainViewModel(state);
            vm.Open(Path.Combine(dir, "fixture.pdf"));
            vm.HandlePageClick(0, new PdfPoint(78, 186));   // tick a box so the copy differs
            Check("the document is dirty before saving a copy", vm.IsDirty);

            using (var file = File.Create(copyPath))
                vm.SaveAsTo(file, copyPath, Path.GetFileName(copyPath));

            Check("saving a copy clears the dirty flag", !vm.IsDirty);
            Check("and the copy becomes the document being edited",
                  vm.DocumentPath == copyPath);
            Check("named after the copy, not the original",
                  vm.DocumentName == Path.GetFileName(copyPath));

            // The bug this guards: Shrink reopens DocumentPath, so a stale path
            // silently shrinks the file the user saved FROM rather than the one
            // they just wrote.
            using var engine = new PdfiumEngine();
            using var reopened = engine.Open(vm.DocumentPath!);
            using var page = reopened.GetPage(0);
            Check("so re-reading it finds the edit that was just saved",
                  page.GetStamps().Any(st => st.Id.StartsWith("mark:", StringComparison.Ordinal)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::save as: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            if (File.Exists(copyPath)) File.Delete(copyPath);
        }

        // --- Keyboard traversal (SDD §2.2, #2) ---
        //
        // The acceptance criterion is "the persona task completes keyboard-only".
        // The view half — Tab reaching the handler, the focus ring drawing — needs
        // a window. What is checkable here is the part that decides everything:
        // that traversal reaches the interactive regions in reading order and that
        // activating one does the same thing a click does.
        Console.WriteLine("keyboard traversal:");
        var kbPath = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-kb-{Guid.NewGuid():N}.pdf");
        try
        {
            using var vm = new MainViewModel(state);
            vm.Open(Path.Combine(dir, "fixture.pdf"));

            Check("focus starts off the page", vm.PageFocus is null);

            vm.MoveFocus(forward: true);
            Check("Tab puts focus on something", vm.PageFocus is not null);

            // Walk until the drawn checkbox is focused. fixture.pdf has a handful of
            // regions, so a bounded walk is enough and cannot spin.
            var found = false;
            for (var i = 0; i < 40 && !found; i++)
            {
                if (vm.PageFocus?.Kind == PageHitKind.DrawnCheckbox)
                    found = true;
                else
                    vm.MoveFocus(forward: true);
            }
            Check("tabbing reaches the drawn checkbox", found);

            if (found)
            {
                Check("and it announces itself meaningfully",
                      vm.PageFocus!.Describe(false) == Strings.RegionBoxToTick);

                vm.ActivateFocus();
                Check("Enter ticks it, exactly as a click would", vm.IsDirty);

                using (var file = File.Create(kbPath))
                    vm.SaveTo(file);

                using var engine = new PdfiumEngine();
                using var reopened = engine.Open(kbPath);
                using var page = reopened.GetPage(0);
                Check("and the tick is in the saved file",
                      page.GetStamps().Any(st => st.Id.StartsWith("mark:", StringComparison.Ordinal)));
            }

            // Reverse traversal has to be the inverse, or Shift+Tab strands people.
            var before = vm.PageFocus?.RegionIndex;
            vm.MoveFocus(forward: true);
            vm.MoveFocus(forward: false);
            Check("Shift+Tab undoes a Tab", vm.PageFocus?.RegionIndex == before);

            vm.ClearPageFocus();
            Check("Escape releases the page", vm.PageFocus is null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::keyboard: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            if (File.Exists(kbPath)) File.Delete(kbPath);
        }

        // --- Saving, busy state and closing (#145) ---
        //
        // D2: Save under the sandbox opened (and so truncated) the file before the verified
        // save ran, so a failed flatten or read-back left it empty. D1: closing with unsaved
        // changes deleted their journal. And a window's view model now does its engine work
        // off the UI thread, which is checked here on the thread pool.
        Console.WriteLine("saving, busy state and closing (#145):");
        var busyState = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-busy-{Guid.NewGuid():N}");
        var original = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-d2-{Guid.NewGuid():N}.pdf");
        try
        {
            File.Copy(Path.Combine(dir, "fixture.pdf"), original);
            var before = File.ReadAllBytes(original);
            var box = new PdfPoint(78, 186);

            using (var vm = new MainViewModel(busyState))
            {
                vm.Open(original);
                vm.HandlePageClick(0, box);
                Check("a tick makes the document dirty", vm.IsDirty);

                vm.FailSaveForTest = () => throw new InvalidOperationException("flattening failed (self-test)");
                var opened = false;
                var saved = vm.SaveThroughAsync(() =>
                {
                    opened = true;
                    return Task.FromResult<Stream>(new FileStream(original, FileMode.Create));
                }).GetAwaiter().GetResult();
                Check("a save that fails while making the bytes reports it",
                      !saved && vm.Status == Strings.WithDetail(Strings.CouldNotSave, "flattening failed (self-test)"));
                Check("and never opens the file for writing (D2)", !opened);
                Check("so the original is untouched", File.ReadAllBytes(original).AsSpan().SequenceEqual(before));
                Check("and the document is still unsaved", vm.IsDirty);

                vm.FailSaveForTest = null;
                saved = vm.SaveThroughAsync(() => Task.FromResult<Stream>(new FileStream(original, FileMode.Create)))
                          .GetAwaiter().GetResult();
                Check("the same save then writes the file", saved && !vm.IsDirty
                      && !File.ReadAllBytes(original).AsSpan().SequenceEqual(before));
                using (var engine = new PdfiumEngine())
                using (var reopened = engine.Open(original))
                using (var page = reopened.GetPage(0))
                    Check("and what it wrote reads back with the tick", page.GetStamps().Any(st => st.Id.StartsWith("mark:", StringComparison.Ordinal)));

                // #147: that save wrote, in place, the file the document is read from on demand. The
                // document moved onto a copy first, so it still reads what it was opened on: the next
                // save, in place again, serialises it from that copy and reads back right.
                vm.HandlePageClick(0, box); // untick
                saved = vm.SaveThroughAsync(() => Task.FromResult<Stream>(new FileStream(original, FileMode.Create)))
                          .GetAwaiter().GetResult();
                Check("a second save in place, over the file the document reads, works (#147)", saved && !vm.IsDirty);
                using (var engine = new PdfiumEngine())
                using (var reopened = engine.Open(original))
                using (var page = reopened.GetPage(0))
                    Check("and reads back without the tick", reopened.PageCount == 2
                          && !page.GetStamps().Any(st => st.Id.StartsWith("mark:", StringComparison.Ordinal)));
                vm.HandlePageClick(0, box); // tick again, and save, for what follows
                saved = vm.SaveThroughAsync(() => Task.FromResult<Stream>(new FileStream(original, FileMode.Create)))
                          .GetAwaiter().GetResult();
                Check("and a third", saved && !vm.IsDirty);

                vm.HandlePageClick(0, box); // untick: dirty again
                var kindBefore = vm.HitTest(0, box).Kind;
                using (vm.Busy.Begin(Strings.BusySaving))
                {
                    Check("while a save runs, Save, editing and file commands are disabled at once",
                          vm.Busy.IsBusy && !vm.SaveCommand.CanExecute(null) && !vm.CanEditContent && !vm.CanSign && !vm.IsIdle);
                    vm.HandlePageClick(0, box);
                    Check("and a click on the page is ignored", vm.HitTest(0, box).Kind == kindBefore);
                }
                Check("afterwards they are back", vm.SaveCommand.CanExecute(null) && vm.CanEditContent && vm.IsIdle);
                vm.DiscardChanges();
            }

            using (var vm = new MainViewModel(busyState) { RunsInBackground = true })
            {
                vm.OpenAsync(original).GetAwaiter().GetResult();
                Check("with a window, a document opens off the UI thread", vm.IsDocumentOpen && vm.Pages.Count > 0);
                vm.SearchAsync("checkbox").GetAwaiter().GetResult();
                Check("and is searched off it", vm.MatchCount > 0);
                vm.HandlePageClick(0, box);
                vm.Busy.WhenIdleAsync().GetAwaiter().GetResult();
                Check("and a click applies its change off it", vm.IsDirty && vm.CanUndo);
            } // closed with that change unsaved, and nobody agreed to lose it

            using (var vm = new MainViewModel(busyState))
            {
                Check("closing with unsaved changes keeps their journal (D1)", vm.FindRecoverableSessions().Count == 1);
                vm.Open(original);
                vm.HandlePageClick(0, box);
                vm.DiscardChanges();
            } // Don't Save
            using (var vm = new MainViewModel(busyState))
                Check("Don't Save lets the journal go", vm.FindRecoverableSessions().Count == 0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::saving and busy state: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            if (File.Exists(original)) File.Delete(original);
            try
            {
                if (Directory.Exists(busyState))
                    Directory.Delete(busyState, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        // --- The toolbar menus, opened in a window (#144) ---
        //
        // Every check above drives the view model; none of them could see that More
        // and the zoom level's menu popped up as an empty sliver on a real Mac, with
        // every command still perfectly executable. This one clicks the buttons in a
        // real window on the headless platform and asks the menu that appeared what
        // it is showing, and how big it is. Last, because it starts that platform.
        Console.WriteLine("toolbar menus, opened in a window (#144):");
        try
        {
            CheckToolbarMenus(dir, state, Check);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::toolbar menus: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        // --- Recent documents say where each file lives (#165) ---
        //
        // Every row, not only the rows whose names clash: files from one template or
        // one scanner share a name, and a list that added the folder only sometimes
        // would rearrange itself as entries came and went. The rule that has to hold
        // whatever the folders are called is that a row's second line is a place, in
        // display names, and never a path.
        Console.WriteLine("recent documents (#165):");
        var recentsState = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-recents-{Guid.NewGuid():N}");
        var recentsRoot = Path.Combine(Path.GetTempPath(), $"megapdf-selftest-recentfiles-{Guid.NewGuid():N}");
        try
        {
            // Two files of one name in sibling folders, and a third somewhere else.
            var paths = new[]
            {
                Path.Combine(recentsRoot, "Clients", "Smith", "agreement.pdf"),
                Path.Combine(recentsRoot, "Clients", "Jones", "agreement.pdf"),
                Path.Combine(recentsRoot, "Scans", "receipt.pdf"),
            };
            foreach (var path in paths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(Path.Combine(dir, "fixture.pdf"), path, overwrite: true);
            }

            using var vm = new MainViewModel(recentsState);
            foreach (var path in paths)
                vm.RememberRecent(path, null);

            var rows = vm.Recents.ToList();
            Check($"every recent is listed ({rows.Count})", rows.Count == paths.Length);
            Check("and every one of them says where it lives",
                  rows.All(r => r.HasLocation));
            foreach (var row in rows)
                Console.WriteLine($"    {row.Name} — {row.Location}");

            // The whole point of the issue: the two agreements are told apart.
            var agreements = rows.Where(r => r.Name.StartsWith("agreement", StringComparison.Ordinal)).ToList();
            Check($"the two files called {agreements.FirstOrDefault()?.Name} have different locations",
                  agreements.Count == 2 && agreements[0].Location != agreements[1].Location);
            Check("  and the folder that separates them is in both lines",
                  agreements.Any(r => r.Location?.Contains("Smith", StringComparison.Ordinal) == true)
                  && agreements.Any(r => r.Location?.Contains("Jones", StringComparison.Ordinal) == true));

            // No paths anywhere a person can see them (#162 is what happens when there are).
            Check("no location is a path",
                  rows.All(r => r.Location?.Contains('/') != true && r.Location?.Contains('\\') != true));
            Check("and the help tag is the location in full, not the path either",
                  rows.All(r => r.Tip.Contains('/') != true && r.Tip != r.Path));
            Check("a screen reader hears the name and the place",
                  rows.All(r => r.AccessibleName.Contains(r.Name, StringComparison.Ordinal)
                                && r.AccessibleName.Contains(r.Location!.Split(" \u203a ")[^1], StringComparison.Ordinal)));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::recent documents: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }
        finally
        {
            foreach (var path in new[] { recentsState, recentsRoot })
            {
                try
                {
                    if (Directory.Exists(path))
                        Directory.Delete(path, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        // --- About MegaPDF and the third-party notices (#176) ---
        //
        // The Mac shipped with Avalonia's "About Avalonia" in the first slot of the
        // first menu and no notices file anywhere in the bundle. The notices half is a
        // licence obligation, so it is checked here as well as by
        // tools/audit_macos_components.py at build time: this one proves the window
        // actually shows what the bundle carries, which the build check cannot.
        Console.WriteLine("About MegaPDF and its third-party notices (#176):");
        try
        {
            CheckAboutWindow(dir, state, Check);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::About window: {ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        Console.WriteLine(failures == 0 ? "self-test: PASS" : $"::error::self-test: {failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The app menu's first item, the window it opens, and the notices that window
    /// leads to (#176). Driven through the menu item and the button rather than by
    /// constructing the windows, because "the first thing App Review sees" is the
    /// menu item, and a window nothing opens is no fix.
    /// </summary>
    private static void CheckAboutWindow(string dir, string state, Action<string, bool> check)
    {
        EnsureHeadlessPlatform();

        using var vm = new MainViewModel(state);
        vm.Open(Path.Combine(dir, "fixture.pdf"));
        var window = new Views.MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        MenuProbe.Pump();

        // 1. The app menu. Avalonia builds its own — "About Avalonia", opening a panel
        //    about the framework — only when the Application carries none of its own.
        var application = global::Avalonia.Application.Current!;
        var appMenu = global::Avalonia.Controls.NativeMenu.GetMenu(application);
        var first = appMenu?.Items.OfType<global::Avalonia.Controls.NativeMenuItem>().FirstOrDefault();
        check($"the app menu's first item is About MegaPDF (\"{first?.Header}\")",
              first?.Header == Strings.AboutMegaPDF);
        // Since #191 the whole app menu is ours, because Avalonia's own Services, Hide,
        // Hide Others, Show All and Quit carry English titles compiled into the
        // framework and could not be translated from outside. Each one is checked by
        // the string it should show, so a French run would have caught the bug this
        // replaced: none of these headers is an English literal here.
        var appMenuHeaders = appMenu?.Items
            .OfType<global::Avalonia.Controls.NativeMenuItem>()
            .Select(i => i.Header)
            .ToList() ?? [];
        var appName = application.Name ?? "MegaPDF";
        foreach (var wanted in new[]
                 {
                     Strings.AboutMegaPDF, Strings.MenuHideApp(appName),
                     Strings.MenuHideOthers, Strings.MenuShowAll, Strings.MenuQuitApp(appName),
                 })
            check($"the app menu carries {wanted}", appMenuHeaders.Contains(wanted));
        // Services is the system's own, and the marker Avalonia uses to ask for it is
        // registered when the macOS platform initialises — which the headless platform
        // these checks run on never does. So the rule here is the one that holds
        // either way: the item is present with a submenu for macOS to fill, or it is
        // absent. Never present and empty, which would open on nothing. That it really
        // does appear is checked on a Mac, through the menu bar's accessibility tree.
        var services = appMenu?.Items.OfType<global::Avalonia.Controls.NativeMenuItem>()
            .FirstOrDefault(i => i.Header == Strings.MenuServices);
        check(services is null
                  ? "no Services item, this platform having no system to fill one"
                  : "Services is there, with a submenu for macOS to fill",
              services is null || services.Menu is not null);
        // Hide Others is ⌥⌘H on a Mac. Avalonia's own item had ⌥⌘Q, which macOS uses
        // for Quit and Keep Windows (#191).
        var hideOthers = appMenu?.Items.OfType<global::Avalonia.Controls.NativeMenuItem>()
            .FirstOrDefault(i => i.Header == Strings.MenuHideOthers);
        check($"and Hide Others is on {hideOthers?.Gesture}",
              hideOthers?.Gesture is { Key: Key.H } g
              && g.KeyModifiers == (KeyModifiers.Meta | KeyModifiers.Alt));

        // 2. Choosing it opens the About window.
        (first as global::Avalonia.Controls.INativeMenuItemExporterEventsImplBridge)?.RaiseClicked();
        MenuProbe.Pump();
        var about = App.CurrentAbout;
        check("choosing it opens About MegaPDF", about is { IsVisible: true });
        if (about is null)
        {
            window.Close();
            MenuProbe.Pump();
            return;
        }

        // 3. What it says. The version comes from the bundle's Info.plist when there is
        //    one, so this compares against the same source rather than a literal.
        var texts = about.GetLogicalDescendants()
            .OfType<global::Avalonia.Controls.TextBlock>()
            .Select(t => t.Text ?? "")
            .ToList();
        check($"it shows the app's name, not the framework's ({string.Join(" / ", texts.Take(2))})",
              texts.Contains("MegaPDF"));
        check($"it shows the version ({AppInfo.VersionLabel})",
              texts.Contains(AppInfo.VersionLabel) && AppInfo.VersionLabel.Any(char.IsDigit));
        check("it shows the copyright", texts.Contains(Strings.Copyright));
        check("it shows the credit the other three platforms carry", texts.Contains(Strings.SpecialThanks));
        check($"it links to the source ({AppInfo.ProjectUrl})",
              about.ProjectLink.NavigateUri?.ToString() == AppInfo.ProjectUrl);
        check($"and its notices button is labelled \"{Strings.ThirdPartyNoticesEllipsis}\"",
              about.NoticesButton.Content as string == Strings.ThirdPartyNoticesEllipsis);

        // 4. The button opens the notices, and they are the ones that shipped.
        about.NoticesButton.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(
            global::Avalonia.Controls.Button.ClickEvent));
        MenuProbe.Pump();
        var notices = App.CurrentNotices;
        check("the notices button opens the notices window", notices is { IsVisible: true });
        if (notices is not null)
        {
            // Loaded synchronously: the headless pump does not run the Task the window
            // starts in its constructor to completion in any bounded number of frames.
            notices.LoadNow();
            MenuProbe.Pump();
            var text = notices.Notices ?? "";
            check($"which has the notices in it ({text.Length:N0} characters)", text.Length > 10_000);
            check($"laid out as {notices.Paragraphs.ItemCount:N0} paragraphs rather than one text layout",
                  notices.Paragraphs.ItemCount > 100 && notices.Paragraphs.IsVisible);
            foreach (var required in new[] { "PDFium", "Avalonia", "SkiaSharp", "HarfBuzzSharp", "Apache License" })
                check($"  naming {required}", text.Contains(required, StringComparison.Ordinal));

            // The whole point of #176: the text on screen is the file in the bundle.
            if (AppInfo.BundledNoticesPath is { } bundled)
            {
                check($"the text is the bundle's own {Path.GetFileName(bundled)}",
                      text == File.ReadAllText(bundled));
                check("  and the copy embedded in the app agrees with it",
                      AppInfo.EmbeddedNoticesText() == text);
            }
            else
            {
                check("not running from a .app, so the embedded copy is what is shown",
                      text == AppInfo.EmbeddedNoticesText());
            }
            notices.Close();
            MenuProbe.Pump();
        }
        about.Close();
        MenuProbe.Pump();

        // 5. The menu bar routes the issue also asked for: ⌘W, and the two menus every
        //    Mac app has.
        var close = window.MenuBarItem("Close");
        check($"File > {close?.Header} is in the menu bar, on {close?.Gesture}",
              close is { Gesture: not null } && close.Gesture.Key == Key.W);
        var headers = global::Avalonia.Controls.NativeMenu.GetMenu(window)?.Items
            .OfType<global::Avalonia.Controls.NativeMenuItem>().Select(i => i.Header).ToList() ?? [];
        check($"the menu bar is {string.Join(" / ", headers)}",
              headers.Contains(Strings.MenuWindow) && headers.Contains(Strings.MenuHelp));
        check("and Help reaches the notices without going through About",
              window.MenuBarItem("Notices") is not null);

        window.Close();
        MenuProbe.Pump();
    }

    /// <summary>
    /// Opens More and the zoom menu by clicking them, at a width where commands overflow
    /// and one where they do not, and checks what was presented rather than what was
    /// filled in: a menu can hold its items and still show none of them.
    /// </summary>
    private static bool _headlessStarted;

    /// <summary>
    /// The headless platform, set up once. Avalonia allows one setup per process, and
    /// more than one check now wants a real window (#144, #176).
    /// </summary>
    private static void EnsureHeadlessPlatform()
    {
        if (_headlessStarted)
            return;
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new global::Avalonia.Headless.AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        _headlessStarted = true;
    }

    private static void CheckToolbarMenus(string dir, string state, Action<string, bool> check)
    {
        EnsureHeadlessPlatform();

        using var vm = new MainViewModel(state);
        vm.Open(Path.Combine(dir, "fixture.pdf"));
        var window = new Views.MainWindow { DataContext = vm, Width = 1280, Height = 800 };
        window.Show();
        Pump();

        // 480 is the window's minimum, where commands are sure to have overflowed into More.
        foreach (var width in new[] { 1280.0, 480.0 })
        {
            window.Width = width;
            Pump();

            var more = MenuProbe.Open(window, window.MoreButton);
            check($"at {width} DIP, clicking More opens a menu ({window.DescribeToolbar()})", more.IsOpen);
            check($"  which presents its entries ({more.Detail})",
                  more.Headers.Contains(Strings.SaveAs) && more.Headers.Contains(Strings.Print)
                  && more.Headers.Contains(Strings.Options));
            check($"  at a size that shows them ({more.Size.Width:F0}x{more.Size.Height:F0} DIP)",
                  more.Size.Width >= 100 && more.Size.Height >= 5 * 20);
            if (width < 1000)
                check("  led by the commands that overflowed", more.Headers.Contains(Strings.Redo));
            more.Close();
        }

        // The zoom control is on the row at full width.
        window.Width = 1280;
        Pump();
        vm.SetZoomCommand.Execute(2.0);
        Pump();
        var zoom = MenuProbe.Open(window, window.ZoomMenuButton);
        check("clicking the zoom level opens its menu", zoom.IsOpen);
        check($"  which presents the fits and the presets (presented {zoom.Headers.Count})",
              zoom.Headers.Contains(Strings.ActualSize) && zoom.Headers.Contains(Strings.FitPage)
              && zoom.Headers.Contains(Strings.ZoomPercent(100)));
        check($"  at a size that shows them ({zoom.Size.Width:F0}x{zoom.Size.Height:F0} DIP)",
              zoom.Size.Height >= 8 * 20);
        zoom.Click(Strings.ActualSize);
        check("  and choosing Actual size from it applies", Math.Abs(vm.Zoom - 1.0) < 0.001);

        // The same menu opened a second time still shows its entries.
        var again = MenuProbe.Open(window, window.ZoomMenuButton);
        check("opened again, it still presents its entries", again.Headers.Contains(Strings.FitWidth));
        again.Close();

        // Zoom in from the keyboard: Cmd+= as the menu shows it, and Cmd+Shift+=, which is
        // Cmd++ to anyone reading the key cap. Ctrl on Windows and Linux.
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        foreach (var (keys, modifiers) in new[] { ("Cmd+=", command), ("Cmd+Shift+=", command | RawInputModifiers.Shift) })
        {
            vm.SetZoomCommand.Execute(1.0);
            Pump();
            global::Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, global::Avalonia.Input.Key.OemPlus, modifiers,
                global::Avalonia.Input.PhysicalKey.Equal, modifiers.HasFlag(RawInputModifiers.Shift) ? "+" : "=");
            global::Avalonia.Headless.HeadlessWindowExtensions.KeyRelease(window, global::Avalonia.Input.Key.OemPlus, modifiers,
                global::Avalonia.Input.PhysicalKey.Equal, modifiers.HasFlag(RawInputModifiers.Shift) ? "+" : "=");
            Pump();
            check($"{keys} zooms in (100% -> {vm.Zoom * 100:F0}%)", vm.Zoom > 1.001);
        }

        // Cmd+S with nothing changed does nothing (#144). The window's key binding used to
        // run Save regardless of whether it could, and rewrote the file while Save sat
        // greyed out. This window has no file to write to, so a save would say so.
        global::Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Key.S, command, PhysicalKey.S, "s");
        global::Avalonia.Headless.HeadlessWindowExtensions.KeyRelease(window, Key.S, command, PhysicalKey.S, "s");
        Pump();
        check($"Cmd+S with nothing changed does not save (status: {vm.Status})",
              !vm.IsDirty && vm.Status != Strings.NowhereToSave);

        // Space on the page must not also press the toolbar button that still has keyboard
        // focus (#144). Tab moves the page's focus ring, not keyboard focus, so a toolbar
        // button used from the keyboard keeps it. Seen once on a real Mac, after moving
        // through the More menu with the arrow keys: Space opened the line editor and the
        // More menu together. Not reproduced on a fresh launch, nor here; kept as a guard.
        vm.ClearPageFocus();
        window.MoreButton.Focus();
        Pump();
        global::Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        global::Avalonia.Headless.HeadlessWindowExtensions.KeyRelease(window, Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Pump();
        var spaceRegion = vm.PageFocus?.Kind;
        global::Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        global::Avalonia.Headless.HeadlessWindowExtensions.KeyRelease(window, Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Pump();
        check($"Space on a page region ({spaceRegion}) does not also press the focused More button",
              spaceRegion is not null && window.ToolbarMenuOf(window.MoreButton) is not { IsOpen: true });
        window.ToolbarMenuOf(window.MoreButton)?.Hide();
        global::Avalonia.Headless.HeadlessWindowExtensions.KeyPress(window, Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        global::Avalonia.Headless.HeadlessWindowExtensions.KeyRelease(window, Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump();
        // #169: keyboard focus left on a toolbar button must not stay live behind work on the
        // page. A focused button disabled by the busy state must not hand focus to Undo, and
        // a click on the page takes focus off the toolbar, so a later Space or Enter can't
        // press Undo, Open or anything else behind the user's back.
        // Two waits, because this check kept failing on its own setup on GitHub's macOS
        // runners and never on the house Mac (#176, found while adding the About checks
        // below). Neither weakens what it asserts.
        //
        // The Space above opened the in-place line editor and the Escape that closes it is
        // applied on the dispatcher, not there and then; the editor's TextBox was still
        // holding the focus this check is about to set. And Sign is disabled while the view
        // model is busy — the zoom changes earlier can leave a page still rasterising — so
        // Focus() on it would quietly do nothing and leave focus nowhere at all.
        // Pump *and* sleep: Pump only runs the dispatcher's queue, and the work that
        // keeps the view model busy here — a page rasterising after the zoom changes
        // above — runs on a thread pool thread, so spinning the queue alone never
        // reaches it. A tenth of a second is plenty on the house Mac; two seconds is
        // the ceiling for a loaded runner.
        for (var i = 0; i < 400 && (window.FocusManager?.GetFocusedElement() is global::Avalonia.Controls.TextBox
                                    || !vm.IsIdle); i++)
        {
            Pump();
            Thread.Sleep(5);
        }
        vm.ClearPageFocus();
        window.SignButton.Focus();
        Pump();
        check($"Sign can take keyboard focus to begin with (enabled={window.SignButton.IsEnabled}, "
              + $"visible={window.SignButton.IsVisible}, CanSign={vm.CanSign}, idle={vm.IsIdle})",
              window.FocusManager?.GetFocusedElement() == window.SignButton);
        var focusedBefore = window.FocusManager?.GetFocusedElement() as global::Avalonia.Controls.Control;
        using (vm.Busy.Begin(Strings.BusyApplying))
            Pump();
        Pump();
        var focusedAfter = window.FocusManager?.GetFocusedElement() as global::Avalonia.Controls.Control;
        static string Describe(global::Avalonia.Controls.Control? c) =>
            c is null ? "none" : string.IsNullOrEmpty(c.Name) ? c.GetType().Name : c.Name;
        // The invariant is where focus ends up, and it holds whether or not this platform
        // let the test put focus on the button first (headless arm64 does not).
        check($"focus (on {Describe(focusedBefore)}) does not move to a toolbar button while busy (now {Describe(focusedAfter)})",
              !window.IsToolbarControl(focusedAfter));

        window.OpenButton.Focus();
        Pump();
        // Just inside the page's top-left corner: on the paper, clear of anything to click.
        if (window.PageList.ContainerFromIndex(0) is global::Avalonia.Controls.Control firstPage
            && global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(firstPage)
                   .OfType<global::Avalonia.Controls.Border>().FirstOrDefault(b => b.Name == "PageSurface") is { } surface
            && surface.TranslatePoint(new Point(6, 6), window) is { } pageCorner)
        {
            global::Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, pageCorner, global::Avalonia.Input.MouseButton.Left);
            global::Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, pageCorner, global::Avalonia.Input.MouseButton.Left);
            Pump();
            var afterClick = window.FocusManager?.GetFocusedElement() as global::Avalonia.Controls.Control;
            check($"a click on the page takes keyboard focus off Open (now {afterClick?.Name ?? afterClick?.GetType().Name ?? "none"})",
                  !window.IsToolbarControl(afterClick));
        }
        else
        {
            check("the first page is realised for the click check", false);
        }

        while (vm.CanUndo)
            vm.UndoCommand.Execute(null);
        vm.ClearPageFocus();
        Pump();

        // Accessibility (#144): what VoiceOver is handed for the pickers, the two mode
        // toggles and the page's scroll bars.
        check("each face in the font picker is named by its label, not the record",
              vm.TextFontChoices.All(f => f.ToString() == f.Label));
        foreach (var toggle in new global::Avalonia.Controls.Button[] { window.AddTextButton, window.WhiteoutButton })
        {
            var peer = global::Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(toggle);
            check($"{peer.GetName()} is exposed as a checkbox ({peer.GetAutomationControlType()})",
                  peer.GetAutomationControlType() == global::Avalonia.Automation.Peers.AutomationControlType.CheckBox);
        }

        vm.SetZoomCommand.Execute(1.0);
        Pump();
        var scrollButtons = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window.PageScroller)
            .OfType<global::Avalonia.Controls.RepeatButton>().ToList();
        // Named rather than hidden: the Mac lists every child whatever its AccessibilityView.
        var scrollNames = scrollButtons
            .Select(b => global::Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(b).GetName())
            .ToList();
        check($"the page's scroll bar buttons are named for what they do ({string.Join(", ", scrollNames.Distinct())})",
              scrollButtons.Count > 0 && scrollNames.All(n =>
                  !string.IsNullOrWhiteSpace(n) && !n.StartsWith("Avalonia.", StringComparison.Ordinal)));

        vm.ToggleAddTextCommand.Execute(null);
        Pump();
        foreach (var picker in new global::Avalonia.Controls.ComboBox[] { window.FontBox, window.SizeBox })
        {
            var children = global::Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(picker).GetChildren();
            var described = children.Select(c =>
                $"{c.GetType().Name} for {(c as global::Avalonia.Automation.Peers.ControlAutomationPeer)?.Owner.GetType().Name}"
                + $"#{(c as global::Avalonia.Automation.Peers.ControlAutomationPeer)?.Owner.Name}"
                + $"/{c.GetAutomationControlType()}/'{c.GetName()}'").ToList();
            var pickerName = global::Avalonia.Automation.AutomationProperties.GetName(picker);
            check($"{pickerName}'s accessible children are all named ({string.Join("; ", described)})",
                  children.All(c => !string.IsNullOrWhiteSpace(c.GetName())));
        }
        // Hiding the Popup element from the peer must not stop the list opening.
        window.FontBox.IsDropDownOpen = true;
        Pump();
        check($"the font picker still opens its list of {window.FontBox.ItemCount} faces",
              window.FontBox.ContainerFromIndex(0) is { IsEffectivelyVisible: true, Bounds.Height: > 0 });
        window.FontBox.IsDropDownOpen = false;
        Pump();
        vm.ToggleAddTextCommand.Execute(null);
        Pump();

        // Tools > Text font and Text size follow the pickers' context (#144). On the Mac an
        // item with a submenu is enabled whatever it is told, so out of context the
        // submenu has to come off for the item to grey out.
        foreach (var inContext in new[] { false, true })
        {
            if (inContext)
                vm.ToggleAddTextCommand.Execute(null);
            Pump();
            foreach (var id in new[] { "FontBox", "SizeBox" })
            {
                var item = window.MenuBarItem(id);
                check(inContext
                          ? $"Tools > {item?.Header} is on, with its choices, while adding text"
                          : $"Tools > {item?.Header} is off, with no submenu to open, outside Add text",
                      inContext
                          ? item is { IsEnabled: true, Menu.Items.Count: > 0 }
                          : item is { IsEnabled: false, Menu: null });
            }
        }
        vm.ToggleAddTextCommand.Execute(null);
        Pump();

        window.Close();
        Pump();

        static void Pump() => MenuProbe.Pump();
    }

    /// <summary>
    /// What a toolbar button's menu actually put on screen when it was clicked: whether
    /// it is open, the headers of the entries that were laid out with a real size, and
    /// the size of the popup showing them. Read from the presented controls, not from
    /// the flyout's item list, which is exactly what was full while the screen was empty.
    /// </summary>
    private sealed class MenuProbe
    {
        private readonly Views.MainWindow _window;
        private readonly global::Avalonia.Controls.Button _owner;
        private readonly List<(string Header, global::Avalonia.Controls.MenuItem Item)> _entries;

        public bool IsOpen { get; }
        public IReadOnlyList<string> Headers => _entries.Select(e => e.Header).ToList();
        public Size Size { get; }

        private MenuProbe(Views.MainWindow window, global::Avalonia.Controls.Button owner, bool isOpen,
                          List<(string, global::Avalonia.Controls.MenuItem)> entries, Size size, string detail)
        {
            _window = window;
            _owner = owner;
            IsOpen = isOpen;
            _entries = entries;
            Size = size;
            Detail = detail;
        }

        internal static void Pump()
        {
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            global::Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Clicks the button's centre, as a pointer would, and reads what opened.</summary>
        internal static MenuProbe Open(Views.MainWindow window, global::Avalonia.Controls.Button button)
        {
            var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)
                         ?? throw new InvalidOperationException($"{button.Name} is not in the window");
            global::Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, centre, global::Avalonia.Input.MouseButton.Left);
            global::Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, centre, global::Avalonia.Input.MouseButton.Left);
            Pump();

            if (window.ToolbarMenuOf(button) is not { IsOpen: true } flyout)
                return new MenuProbe(window, button, false, [], default,
                    $"not open: clicked {centre.X:F0},{centre.Y:F0} in a {window.ClientSize.Width:F0}x{window.ClientSize.Height:F0} window, "
                    + $"{button.Name} visible={button.IsEffectivelyVisible} bounds={button.Bounds}, "
                    + $"menu {(window.ToolbarMenuOf(button) is null ? "never built" : "built but closed")}");

            // The presenter is found through whatever the menu holds, in the visual tree:
            // an entry that was never presented has no visual ancestor at all.
            var presenter = flyout.Items.OfType<global::Avalonia.Controls.MenuItem>()
                .Select(i => global::Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<global::Avalonia.Controls.MenuFlyoutPresenter>(i))
                .FirstOrDefault(p => p is not null);
            if (presenter is null)
                return new MenuProbe(window, button, true, [], default, $"{flyout.Items.Count} item(s) held, none presented");

            var entries = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(presenter)
                .OfType<global::Avalonia.Controls.MenuItem>()
                .Where(i => i.IsEffectivelyVisible && i.Bounds.Height > 0)
                .Select(i => (i.Header?.ToString() ?? "", i))
                .ToList();
            // The presenter's own size: the headless platform draws popups inside the
            // window, so the root is the window and its size says nothing about the menu.
            var root = global::Avalonia.VisualTree.VisualExtensions.GetVisualRoot(presenter) as global::Avalonia.Controls.TopLevel;
            return new MenuProbe(window, button, true, entries, presenter.Bounds.Size,
                                 $"{flyout.Items.Count} held, {entries.Count} presented in {root?.GetType().Name ?? "no root"}");
        }

        /// <summary>What the probe saw, for the check's line when it fails.</summary>
        public string Detail { get; }

        /// <summary>Clicks the presented entry with this header, in the popup it is shown in.</summary>
        internal void Click(string header)
        {
            var (_, item) = _entries.FirstOrDefault(e => e.Header == header);
            if (item is null || global::Avalonia.VisualTree.VisualExtensions.GetVisualRoot(item) is not global::Avalonia.Controls.TopLevel root)
                throw new InvalidOperationException($"no presented entry \"{header}\"");
            var centre = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), root)!.Value;
            global::Avalonia.Headless.HeadlessWindowExtensions.MouseDown(root, centre, global::Avalonia.Input.MouseButton.Left);
            global::Avalonia.Headless.HeadlessWindowExtensions.MouseUp(root, centre, global::Avalonia.Input.MouseButton.Left);
            Pump();
        }

        /// <summary>
        /// Closes the menu the button opened. It must really close: a menu left open
        /// light-dismisses on the next click anywhere, which swallows that click.
        /// </summary>
        internal void Close()
        {
            _window.ToolbarMenuOf(_owner)?.Hide();
            Pump();
        }
    }
}
