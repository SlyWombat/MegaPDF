using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Avalonia.Rendering;
using MegaPDF.Avalonia.ViewModels;
using MegaPDF.Core.Engine;

namespace MegaPDF.Avalonia.Views;

public partial class MainWindow : Window
{
    /// <summary>The file the document was opened from, kept so Save can write back to it.</summary>
    private IStorageFile? _openedFile;

    public MainWindow()
    {
        InitializeComponent();

        // ADR-002 called this one of the two MainViewModel touch points that is a
        // reshape rather than a rename: WinUI's FileOpenPicker is a type you
        // construct, Avalonia's IStorageProvider is reached through the TopLevel and
        // is async. Keeping it in the view is what lets the view model stay UI-free.
        OpenButton.Click += async (_, _) => await OpenDocumentAsync();

        BindShortcuts();

        // Only realised pages rasterise. ContainerPrepared/ContainerClearing are the
        // virtualization hooks — this is where "render what you can see" happens, and
        // where each page surface gets its click handler.
        PageList.ContainerPrepared += (_, e) =>
        {
            if (e.Container.DataContext is not PageViewModel page)
                return;

            page.EnsureRendered(RenderScaling);
            e.Container.PointerPressed -= OnPagePointerPressed;
            e.Container.PointerPressed += OnPagePointerPressed;
        };

        PageList.ContainerClearing += (_, e) =>
        {
            e.Container.PointerPressed -= OnPagePointerPressed;
            if (e.Container.DataContext is PageViewModel page)
                page.Unrender();
        };
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is { } vm)
            vm.SaveRequested += () => _ = SaveAsync();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // RenderScaling is only meaningful once there is a window on a screen. Feeding
        // it to the view model is what makes a page sharp on a retina Mac rather than
        // upscaled from a 96 DPI raster.
        if (ViewModel is { } vm)
            vm.DpiScale = RenderScaling;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ViewModel?.Dispose();
    }

    // --- Clicking the page (SDD §3.2) ---

    private void OnPagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control container || container.DataContext is not PageViewModel page)
            return;
        if (ViewModel is not { } vm)
            return;
        if (!e.GetCurrentPoint(container).Properties.IsLeftButtonPressed)
            return;

        // The page surface is laid out at exactly LayoutWidth/Height, so a position
        // inside it converts straight back to page points. Same conversion the WinUI
        // app uses (72/96 divided by zoom), which is what keeps a click landing on the
        // same checkbox on both desktops.
        var position = e.GetPosition(container);
        var dipToPoint = 1.0 / (PageBitmap.PointsToPixels * vm.Zoom);
        var pagePoint = new PdfPoint(position.X * dipToPoint, position.Y * dipToPoint);

        vm.HandlePageClick(page.Index, pagePoint);
        e.Handled = true;
    }

    // --- Shortcuts ---

    /// <summary>
    /// macOS uses Cmd where Windows uses Ctrl. Avalonia does not translate this for
    /// you, so a XAML `HotKey="Ctrl+O"` would give Mac users the wrong shortcut — one
    /// of the "Mac idioms need explicit wiring" costs ADR-002 flagged against Option
    /// B. Bound here so the gesture and the tooltip advertising it cannot disagree.
    /// </summary>
    private static KeyModifiers CommandModifier =>
        OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private static string CommandSymbol => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";

    private void BindShortcuts()
    {
        Bind(OpenButton, Key.O, KeyModifiers.None, "Open a PDF", () => _ = OpenDocumentAsync());
        Bind(SaveButton, Key.S, KeyModifiers.None, "Save", () => ViewModel?.SaveCommand.Execute(null));
        Bind(UndoButton, Key.Z, KeyModifiers.None, "Undo", () => ViewModel?.UndoCommand.Execute(null));
        // Redo is Shift+Cmd+Z on macOS and Ctrl+Y on Windows — genuinely different
        // conventions, not just a different modifier.
        if (OperatingSystem.IsMacOS())
            Bind(RedoButton, Key.Z, KeyModifiers.Shift, "Redo", () => ViewModel?.RedoCommand.Execute(null));
        else
            Bind(RedoButton, Key.Y, KeyModifiers.None, "Redo", () => ViewModel?.RedoCommand.Execute(null));
        Bind(ZoomOutButton, Key.OemMinus, KeyModifiers.None, "Zoom out", () => ViewModel?.ZoomOutCommand.Execute(null));
        Bind(ZoomInButton, Key.OemPlus, KeyModifiers.None, "Zoom in", () => ViewModel?.ZoomInCommand.Execute(null));
        Bind(ZoomResetButton, Key.D0, KeyModifiers.None, "Actual size", () => ViewModel?.ZoomResetCommand.Execute(null));

        void Bind(Button button, Key key, KeyModifiers extra, string description, Action invoke)
        {
            var modifiers = CommandModifier | extra;
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(key, modifiers),
                Command = new RelayCommand(invoke),
            });
            var shiftLabel = extra.HasFlag(KeyModifiers.Shift) ? (OperatingSystem.IsMacOS() ? "⇧" : "Shift+") : "";
            ToolTip.SetTip(button, $"{description} ({CommandSymbol}{shiftLabel}{KeyLabel(key)})");
        }
    }

    private static string KeyLabel(Key key) => key switch
    {
        Key.OemMinus => "-",
        Key.OemPlus => "+",
        Key.D0 => "0",
        _ => key.ToString(),
    };

    // --- Files ---

    private async Task OpenDocumentAsync()
    {
        if (ViewModel is not { } vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a PDF",
            AllowMultiple = false,
            FileTypeFilter = [PdfFileType],
        });

        if (files.Count == 0)
            return;

        // TryGetLocalPath returns null for a document the OS handed us out of a
        // sandboxed or virtual location; the engine takes a path, so say so rather
        // than failing silently.
        var path = files[0].TryGetLocalPath();
        if (path is null)
        {
            vm.Status = "That file is not on the local disk. Copy it somewhere local and try again.";
            return;
        }

        _openedFile = files[0];
        vm.Open(path);
    }

    /// <summary>
    /// Saves back over the opened file.
    ///
    /// Two paths on purpose. Where there is a usable local path — Windows, and macOS
    /// outside the sandbox — the write goes through AtomicFileWriter, which swaps a
    /// fully written temp file into place so the destination is never seen
    /// half-written (SDD §3.4). Under the App Sandbox that protocol is denied: the
    /// grant is to the file, not its folder, so the write goes through the
    /// already-open stream instead and accepts the weaker guarantee that
    /// StagedStreamWriter documents.
    /// </summary>
    private async Task SaveAsync()
    {
        if (ViewModel is not { } vm || _openedFile is null)
            return;

        try
        {
            var path = _openedFile.TryGetLocalPath();
            if (path is not null && !OperatingSystem.IsMacOS())
            {
                vm.SaveToPath(path);
                return;
            }

            await using var stream = await _openedFile.OpenWriteAsync();
            vm.SaveTo(stream);
        }
        catch (Exception ex)
        {
            vm.ReportSaveFailure(ex);
        }
    }

    private static FilePickerFileType PdfFileType => new("PDF document")
    {
        Patterns = ["*.pdf"],
        AppleUniformTypeIdentifiers = ["com.adobe.pdf"],
        MimeTypes = ["application/pdf"],
    };
}
