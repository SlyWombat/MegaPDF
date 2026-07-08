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
        InitializeComponent();
    }

    private static void LogCrash(Exception? ex, string context)
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
        var splash = new SplashWindow();
        splash.Activate();

        await Task.Delay(TimeSpan.FromSeconds(2.5));

        // Order matters: the app exits when its last window closes, so the
        // main window must be up before the splash goes away.
        var mainWindow = new MainWindow();
        _window = mainWindow;
        mainWindow.Activate();
        splash.Close();

        // "Open with MegaPDF" / command-line launch.
        var commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Length > 1
            && commandLine[1].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            && File.Exists(commandLine[1]))
        {
            await mainWindow.ViewModel.OpenDocumentAsync(Path.GetFullPath(commandLine[1]));
        }
    }
}
