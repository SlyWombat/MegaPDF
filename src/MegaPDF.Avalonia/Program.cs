using Avalonia;
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
        => args.Contains("--render-check") ? RenderCheck(args)
         : args.Contains("--self-test") ? SelfTest(args)
         : args.Contains("--print-check") ? PrintCheck(args)
         : BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
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
    private static int SelfTest(string[] args)
    {
        var dir = args.FirstOrDefault(a => Directory.Exists(a));
        if (dir is null)
        {
            Console.Error.WriteLine("usage: MegaPDF --self-test <fixtures-dir>");
            return 2;
        }

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
            Check("the summary counts it", vm.MatchSummary == $"1 of {vm.MatchCount}");

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
            Check("and says so in words", vm.MatchSummary == "Not found");

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
                      vm.PageFocus!.Describe(false) == "Box to tick");

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

        Console.WriteLine(failures == 0 ? "self-test: PASS" : $"::error::self-test: {failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }
}
