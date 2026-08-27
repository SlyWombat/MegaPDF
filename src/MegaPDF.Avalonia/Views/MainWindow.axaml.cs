using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Avalonia.ViewModels;

namespace MegaPDF.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // ADR-002 called this one of the two MainViewModel touch points that is a
        // reshape rather than a rename: WinUI's FileOpenPicker is a type you
        // construct, Avalonia's IStorageProvider is reached through the TopLevel
        // and is async. Keeping it in the view is what lets the view model stay
        // UI-free and portable.
        OpenButton.Click += async (_, _) => await OpenDocumentAsync();

        BindShortcuts();

        // Only realised pages rasterise. ContainerPrepared/ContainerClearing are
        // the virtualization hooks — this is where "render what you can see"
        // actually happens.
        PageList.ContainerPrepared += (_, e) =>
        {
            if (e.Container.DataContext is PageViewModel page)
                page.EnsureRendered(RenderScaling);
        };

        PageList.ContainerClearing += (_, e) =>
        {
            if (e.Container.DataContext is PageViewModel page)
                page.Unrender();
        };
    }

    /// <summary>
    /// macOS uses Cmd where Windows uses Ctrl. Avalonia does not translate this
    /// for you, so a XAML `HotKey="Ctrl+O"` would give Mac users the wrong
    /// shortcut — one of the "Mac idioms need explicit wiring" costs ADR-002
    /// flagged against Option B. Bound here so the gesture and the tooltip that
    /// advertises it can never disagree.
    /// </summary>
    private static KeyModifiers CommandModifier =>
        OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private static string CommandSymbol => OperatingSystem.IsMacOS() ? "\u2318" : "Ctrl+";

    private void BindShortcuts()
    {
        var mod = CommandModifier;

        Bind(OpenButton, Key.O, "Open a PDF", () => _ = OpenDocumentAsync());
        Bind(ZoomOutButton, Key.OemMinus, "Zoom out", () => ViewModel?.ZoomOutCommand.Execute(null));
        Bind(ZoomInButton, Key.OemPlus, "Zoom in", () => ViewModel?.ZoomInCommand.Execute(null));
        Bind(ZoomResetButton, Key.D0, "Actual size", () => ViewModel?.ZoomResetCommand.Execute(null));

        void Bind(Button button, Key key, string description, Action invoke)
        {
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(key, mod),
                Command = new RelayCommand(invoke),
            });
            ToolTip.SetTip(button, $"{description} ({CommandSymbol}{KeyLabel(key)})");
        }
    }

    private static string KeyLabel(Key key) => key switch
    {
        Key.OemMinus => "-",
        Key.OemPlus => "+",
        Key.D0 => "0",
        _ => key.ToString(),
    };

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // RenderScaling is only meaningful once there is a window on a screen.
        // Feeding it to the view model is what makes a page sharp on a retina Mac
        // rather than upscaled from a 96 DPI raster.
        if (ViewModel is { } vm)
            vm.DpiScale = RenderScaling;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ViewModel?.Dispose();
    }

    private async Task OpenDocumentAsync()
    {
        if (ViewModel is not { } vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a PDF",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PDF document")
                {
                    Patterns = ["*.pdf"],
                    AppleUniformTypeIdentifiers = ["com.adobe.pdf"],
                    MimeTypes = ["application/pdf"],
                },
            ],
        });

        // TryGetLocalPath returns null for a document the OS handed us out of a
        // sandboxed or virtual location; the engine takes a path, so say so
        // rather than failing silently.
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (files.Count > 0 && path is null)
        {
            vm.Status = "That file is not on the local disk. Copy it somewhere local and try again.";
            return;
        }

        if (path is not null)
            vm.Open(path);
    }
}
