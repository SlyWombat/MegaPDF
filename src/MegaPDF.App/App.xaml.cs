using Microsoft.UI.Xaml;

namespace MegaPDF.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        UnhandledException += (_, e) =>
        {
            LogCrash(e.Exception, e.Message);
            e.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash(e.ExceptionObject as Exception, "AppDomain unhandled");

        // --theme dark, before InitializeComponent and before any window exists.
        // Setting ElementTheme on the content root instead flips the foreground
        // brushes and leaves the ThemeDictionaries alone, so Brand.xaml and
        // Brand.cs keep handing back light values — which photographs as white
        // icons on a white toolbar.
        if (Screenshot.ArgumentAfter("--theme") is "dark")
            RequestedTheme = ApplicationTheme.Dark;

        // --language fr-CA, or the Language setting, before any XAML is loaded:
        // x:Uid resolution happens as each element is created, so an override set
        // any later leaves the first window in the previous language (#91).
        AppLanguage.ApplyOverride(Screenshot.ArgumentAfter("--language") ?? new MegaPDF.Core.Services.AppSettings().Language);

        InitializeComponent();
    }

    internal static void LogCrash(Exception? ex, string context)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "MegaPDF-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {context}\n{ex}\n\n");
        }
        catch
        {
            // Never let crash logging itself crash the handler.
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // --engine-check <report.txt> --term <word> <document.pdf>: the engine through
        // this executable and the DLLs beside it, no window, then quit (#288).
        if (Screenshot.ArgumentAfter("--engine-check") is { } report)
        {
            var pdf = Environment.GetCommandLineArgs()
                .FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
            var code = pdf is null
                ? 2
                : EngineCheck.Run(pdf, Screenshot.ArgumentAfter("--term") ?? "CANARY-42-XYZ", report);
            Environment.Exit(code);
            return;
        }

        // --screenshot <out.png>: render the window and quit (#84). Taken before
        // the splash, because two and a half seconds of artwork is not what is
        // being photographed, and before the crash-recovery prompt, which would
        // appear on top of it.
        if (Screenshot.ArgumentAfter("--screenshot") is { } shotPath)
        {
            await RunScreenshotAsync(shotPath);
            return;
        }

        var splash = new SplashWindow();
        splash.Activate();

        await Task.Delay(TimeSpan.FromSeconds(2.5));

        // Order matters: the app exits when its last window closes, so the
        // main window must be up before the splash goes away.
        var mainWindow = new MainWindow();
        _window = mainWindow;
        mainWindow.Activate();
        splash.Close();

        // Crash recovery is offered before anything else opens, including a file the app
        // was launched with (#145): opening that first used to return without offering it,
        // so a double-clicked PDF after a crash silently left the crash's edits behind.
        await mainWindow.OfferCrashRecoveryAsync();

        // "Open with MegaPDF" / command-line launch — after the offer, and not again if
        // the restore has just opened this same document with its recovered edits.
        var commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Length > 1
            && commandLine[1].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            && File.Exists(commandLine[1]))
        {
            var launched = Path.GetFullPath(commandLine[1]);
            if (Core.Recovery.LaunchedDocument.NeedsOpening(launched, mainWindow.ViewModel.DocumentPath))
                await mainWindow.ViewModel.OpenDocumentAsync(launched);
            return;
        }

        // "Reopen last file" setting (off by default).
        if (!mainWindow.ViewModel.IsDocumentOpen
            && mainWindow.ViewModel.ReopenLastFile
            && mainWindow.ViewModel.MostRecentDocument is { } lastDocument)
        {
            await mainWindow.ViewModel.OpenDocumentAsync(lastDocument);
        }

        // First-run "Make MegaPDF your PDF app?" card (SDD §5.4) — once, dismissible forever.
        mainWindow.ViewModel.MaybeShowDefaultAppCard();
    }

    private async Task RunScreenshotAsync(string path)
    {
        var mainWindow = new MainWindow();
        _window = mainWindow;

        // Fixed size, so a screenshot compared against a previous one differs
        // because the app changed rather than because the window did.
        // --window 1900x950 overrides it: 1400 is below the toolbar's Full
        // breakpoint, so the labels — the part that changes with language — are
        // never in the default frame (#91).
        var (width, height) = (1400, 950);
        if (Screenshot.ArgumentAfter("--window") is { } size
            && size.Split('x') is [var w, var h]
            && int.TryParse(w, out var parsedWidth) && int.TryParse(h, out var parsedHeight))
        {
            (width, height) = (parsedWidth, parsedHeight);
        }
        mainWindow.AppWindow.Resize(new global::Windows.Graphics.SizeInt32(width, height));
        mainWindow.Activate();

        var ok = true;
        var pdf = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (pdf is not null)
            await mainWindow.ViewModel.OpenDocumentAsync(Path.GetFullPath(pdf));

        // Let layout settle and the first page raster before touching state:
        // page rendering is asynchronous with respect to layout, and a find with
        // no rendered page to highlight photographs nothing.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Console.Error.WriteLine(mainWindow.DescribeToolbar());

        if (Screenshot.ArgumentAfter("--screenshot-state") is { } state)
            ok = await Screenshot.ApplyStateAsync(mainWindow, state);

        if (ok)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            Console.Error.WriteLine(mainWindow.DescribeToolbar());
            // --hold <seconds>: stay up before capturing, so a popup the render cannot
            // see (the `more` state) can be photographed from outside (#144).
            if (int.TryParse(Screenshot.ArgumentAfter("--hold"), out var hold) && hold > 0)
                await Task.Delay(TimeSpan.FromSeconds(hold));
            ok = await Screenshot.CaptureAsync(mainWindow, path);
        }

        Environment.Exit(ok ? 0 : 1);
    }
}
