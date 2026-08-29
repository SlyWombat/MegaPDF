using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MegaPDF.Avalonia.ViewModels;
using MegaPDF.Avalonia.Views;

namespace MegaPDF.Avalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };

            // The engine holds a native document handle and a pinned byte[]; let
            // it go on the way out rather than at finalisation.
            desktop.ShutdownRequested += (_, _) => viewModel.Dispose();

            // A PDF passed on the command line (Finder "Open With", or the
            // Windows file association) opens straight away.
            var path = desktop.Args?.FirstOrDefault(a =>
                a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
            if (path is not null)
                viewModel.Open(path);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
