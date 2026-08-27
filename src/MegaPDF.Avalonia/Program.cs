using Avalonia;
using MegaPDF.Avalonia.ViewModels;
using MegaPDF.Core.Engine;
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
            using var vm = new MainViewModel();
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
            using var vm = new MainViewModel();
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

        Console.WriteLine(failures == 0 ? "self-test: PASS" : $"::error::self-test: {failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }
}
