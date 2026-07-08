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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
