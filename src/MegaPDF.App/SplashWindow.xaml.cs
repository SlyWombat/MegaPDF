using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace MegaPDF.App;

/// <summary>
/// Branded launch splash: borderless, centered, shown for a few seconds while the app starts.
/// </summary>
public sealed partial class SplashWindow : Window
{
    private const int SplashWidth = 620;
    private const int SplashHeight = 300;

    public SplashWindow()
    {
        InitializeComponent();
        Title = "MegaPDF";

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var width = (int)(SplashWidth * scale);
        var height = (int)(SplashHeight * scale);

        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2,
            width,
            height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
