using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Avalonia.Rendering;
using MegaPDF.Avalonia.ViewModels;
using MegaPDF.Core.Imaging;
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
        SaveAsButton.Click += async (_, _) => await SaveAsAsync();

        BindShortcuts();
        WireSignatures();
        WireFind();

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
            e.Container.PointerMoved -= OnPagePointerMoved;
            e.Container.PointerMoved += OnPagePointerMoved;
            e.Container.PointerReleased -= OnPagePointerReleased;
            e.Container.PointerReleased += OnPagePointerReleased;
        };

        PageList.ContainerClearing += (_, e) =>
        {
            e.Container.PointerPressed -= OnPagePointerPressed;
            e.Container.PointerMoved -= OnPagePointerMoved;
            e.Container.PointerReleased -= OnPagePointerReleased;
            if (e.Container.DataContext is PageViewModel page)
                page.Unrender();
        };
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is { } vm)
        {
            vm.SaveRequested += () => _ = SaveAsync();
            vm.ScrollToRequested += ScrollToMatch;
            vm.EditLineRequested += ShowLineEditor;
            vm.PasswordRequested += AskForPasswordAsync;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape is how every desktop app leaves a mode. Placement first: if both are
        // active, the one the user most recently entered is the one they mean.
        if (e.Key == Key.Escape && ViewModel is { IsPlacingSignature: true } vm)
        {
            vm.CancelPlacing();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel is { IsModeActive: true } modal)
        {
            DismissInlineEditor();
            modal.CancelModes();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel is { IsFindOpen: true })
        {
            CloseFind();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
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

    // --- Text boxes and whiteout on the page (SDD §3.1, §3.3) ---

    /// <summary>The in-place editor, while one is open. Only ever one at a time.</summary>
    private TextBox? _inlineEditor;

    /// <summary>Rubber band for the whiteout drag, and where it started.</summary>
    private Rectangle? _band;
    private Point _bandOrigin;
    private Control? _bandHost;

    /// <summary>
    /// An editor placed where the user clicked, showing the face and size the text
    /// will actually be written in. Typing into a dialog and hoping is the thing
    /// SDD §2.2 is against — you should see the words land where they will sit.
    /// </summary>
    private void ShowInlineEditor(
        Control container, Point at, double fontSizePoints, string fontFamily,
        string initialText, double minWidth, Action<string> commit)
    {
        if (ViewModel is not { } vm || container is not ContentPresenter presenter)
            return;

        DismissInlineEditor();

        var dip = PageBitmap.PointsToPixels * vm.Zoom;
        var editor = new TextBox
        {
            MinWidth = Math.Max(140, minWidth),
            Text = initialText,
            FontSize = fontSizePoints * dip,
            FontFamily = new FontFamily(fontFamily),
            Margin = new Thickness(at.X, at.Y, 0, 0),
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
            Watermark = "Type, then press Enter",
        };

        void Commit()
        {
            var text = editor.Text ?? "";
            DismissInlineEditor();
            commit(text);
        }

        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                DismissInlineEditor();
                vm.CancelModes();
                e.Handled = true;
            }
        };
        // Clicking away commits rather than discarding: losing typing to a stray
        // click is the more annoying failure.
        editor.LostFocus += (_, _) => { if (_inlineEditor == editor) Commit(); };

        if (OverlayOf(presenter) is { } overlay)
        {
            overlay.Children.Add(editor);
            _inlineEditor = editor;
            editor.Focus();
            editor.SelectAll();
        }
    }

    /// <summary>New text at the click point, in the toolbar's chosen face and size.</summary>
    private void ShowNewTextEditor(Control container, PageViewModel page, Point at, PdfPoint pagePoint)
    {
        if (ViewModel is not { } vm)
            return;

        var dip = PageBitmap.PointsToPixels * vm.Zoom;
        ShowInlineEditor(
            container, new Point(at.X, at.Y - (vm.TextSize * dip)),
            vm.TextSize, FamilyFor(vm.TextFont), "", 0,
            text =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                    vm.AddTextBox(page.Index, pagePoint, text);
                else
                    vm.CancelModes();
            });
    }

    /// <summary>
    /// Editing the document's own text (SDD §3.1). The editor sits on the line, at
    /// its size, so the replacement is judged where it will live rather than in a
    /// dialog somewhere else.
    /// </summary>
    private void ShowLineEditor(int pageIndex, PdfTextLine line)
    {
        if (ViewModel is not { } vm)
            return;

        var container = ContainerFor(pageIndex);
        if (container is null)
            return;

        var dip = PageBitmap.PointsToPixels * vm.Zoom;
        ShowInlineEditor(
            container,
            new Point(line.Bounds.X * dip, line.Bounds.Y * dip),
            line.FontSize, "Helvetica, Arial, sans-serif",
            line.Text, line.Bounds.Width * dip,
            text => vm.EditLine(pageIndex, line, text));
    }

    private Control? ContainerFor(int pageIndex)
    {
        for (var i = 0; i < PageList.ItemCount; i++)
        {
            if (PageList.ContainerFromIndex(i) is { DataContext: PageViewModel page } container
                && page.Index == pageIndex)
                return container;
        }
        return null;
    }

    private void DismissInlineEditor()
    {
        if (_inlineEditor is null)
            return;
        (_inlineEditor.Parent as Panel)?.Children.Remove(_inlineEditor);
        _inlineEditor = null;
    }

    /// <summary>Maps the three permitted base-14 names to fonts the OS actually has.</summary>
    private static string FamilyFor(string standardFont) => standardFont switch
    {
        "Times-Roman" => "Times New Roman, Times, serif",
        "Courier" => "Courier New, Courier, monospace",
        _ => "Helvetica, Arial, sans-serif",
    };

    /// <summary>The Panel inside a page's Border that overlays the raster.</summary>
    private static Panel? OverlayOf(ContentPresenter presenter) =>
        presenter.GetVisualDescendants().OfType<Panel>().FirstOrDefault(p => p is not StackPanel);

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

        switch (vm.Mode)
        {
            case MainViewModel.PageMode.AddText:
                ShowNewTextEditor(container, page, position, pagePoint);
                break;

            case MainViewModel.PageMode.Whiteout:
                BeginBand(container, position, e);
                break;

            default:
                DismissInlineEditor();
                vm.HandlePageClick(page.Index, pagePoint);
                break;
        }

        e.Handled = true;
    }

    // --- Whiteout drag ---

    private void BeginBand(Control container, Point origin, PointerPressedEventArgs e)
    {
        if (container is not ContentPresenter presenter || OverlayOf(presenter) is not { } overlay)
            return;

        _bandOrigin = origin;
        _bandHost = container;
        _band = new Rectangle
        {
            Fill = Brushes.White,
            Opacity = 0.75,
            Stroke = Brushes.Gray,
            StrokeThickness = 1,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(origin.X, origin.Y, 0, 0),
            Width = 0,
            Height = 0,
        };
        overlay.Children.Add(_band);
        e.Pointer.Capture(container);
    }

    private void OnPagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_band is null || _bandHost is null)
            return;

        var p = e.GetPosition(_bandHost);
        var x = Math.Min(p.X, _bandOrigin.X);
        var y = Math.Min(p.Y, _bandOrigin.Y);
        _band.Margin = new Thickness(x, y, 0, 0);
        _band.Width = Math.Abs(p.X - _bandOrigin.X);
        _band.Height = Math.Abs(p.Y - _bandOrigin.Y);
    }

    private void OnPagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_band is null || _bandHost is null || ViewModel is not { } vm)
            return;

        if (_bandHost.DataContext is PageViewModel page)
        {
            var dipToPoint = 1.0 / (PageBitmap.PointsToPixels * vm.Zoom);
            vm.AddWhiteout(page.Index, new PdfRect(
                _band.Margin.Left * dipToPoint, _band.Margin.Top * dipToPoint,
                _band.Width * dipToPoint, _band.Height * dipToPoint));
        }

        (_band.Parent as Panel)?.Children.Remove(_band);
        _band = null;
        _bandHost = null;
        e.Pointer.Capture(null);
    }

    // --- Signatures (SDD §3.3) ---

    private void WireSignatures()
    {
        // Choosing one arms placement and closes the flyout, so the next click lands
        // on the page rather than being swallowed by an open popup.
        SignatureList.SelectionChanged += (_, _) =>
        {
            if (SignatureList.SelectedItem is not SignatureItem item || ViewModel is not { } vm)
                return;

            SignatureList.SelectedItem = null;
            SignButton.Flyout?.Hide();
            vm.BeginPlacing(item);
        };

        DrawSignatureButton.Click += async (_, _) =>
        {
            SignButton.Flyout?.Hide();
            await CaptureSignatureAsync();
        };
    }

    private async Task CaptureSignatureAsync()
    {
        if (ViewModel is not { } vm)
            return;

        var capture = new SignatureCaptureWindow();
        await capture.ShowDialog(this);

        if (capture.Result is not { } bitmap)
            return;

        try
        {
            var png = Rendering.SignatureImages.EncodePng(bitmap);
            var entry = vm.AddSignature(capture.ResultName, png);
            // Straight into placement: someone who just drew a signature wants to put
            // it somewhere, not to admire the library.
            vm.BeginPlacing(entry);
        }
        catch (Exception ex)
        {
            vm.Status = $"Could not save that signature: {ex.Message}";
        }
    }

    // --- Find (SDD §3.6) ---

    private void WireFind()
    {
        FindBox.TextChanged += (_, _) => ViewModel?.Search(FindBox.Text ?? "");

        // Enter advances, Shift+Enter goes back — the convention every find bar uses.
        FindBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                ViewModel?.FindPreviousCommand.Execute(null);
            else
                ViewModel?.FindNextCommand.Execute(null);
            e.Handled = true;
        };

        CloseFindButton.Click += (_, _) => CloseFind();
    }

    private void OpenFind()
    {
        if (ViewModel is not { } vm)
            return;
        vm.IsFindOpen = true;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void CloseFind()
    {
        ViewModel?.CloseFind();
        FindBox.Text = "";
    }

    /// <summary>
    /// Brings a hit into view. Scrolls the match to a third of the way down rather
    /// than to the very top, so there is context above it — a hit pinned to the top
    /// edge reads as if the document starts there.
    /// </summary>
    private void ScrollToMatch(int pageIndex, PdfRect rect)
    {
        if (ViewModel is not { } vm)
            return;

        var offsetBefore = 0.0;
        foreach (var page in vm.Pages)
        {
            if (page.Index == pageIndex)
                break;
            offsetBefore += page.LayoutHeight + PageGap;
        }

        var scale = PageBitmap.PointsToPixels * vm.Zoom;
        var target = offsetBefore + (rect.Y * scale) - (PageScroller.Viewport.Height / 3);
        PageScroller.Offset = PageScroller.Offset.WithY(Math.Max(0, target));
    }

    /// <summary>Bottom margin on each page surface in MainWindow.axaml.</summary>
    private const double PageGap = 16;

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

        // Cmd/Ctrl+F has no toolbar button to hang a tooltip on — the find bar is
        // its own affordance once open.
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.F, CommandModifier),
            Command = new RelayCommand(OpenFind),
        });

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

    /// <summary>
    /// Save a copy (SDD §3.4). The picker gives back a file the sandbox has granted
    /// us, so this writes through its stream — the same path the sandboxed Save
    /// takes — and then adopts it as the document's home, which is what "Save As"
    /// means everywhere else.
    /// </summary>
    private async Task SaveAsAsync()
    {
        if (ViewModel is not { } vm)
            return;

        var suggested = vm.DocumentName is { } name
            ? Path.GetFileNameWithoutExtension(name) + " copy.pdf"
            : "document.pdf";

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save a copy",
            SuggestedFileName = suggested,
            DefaultExtension = "pdf",
            FileTypeChoices = [PdfFileType],
            ShowOverwritePrompt = true,
        });

        if (file is null)
            return;

        try
        {
            await using (var stream = await file.OpenWriteAsync())
                vm.SaveTo(stream);

            _openedFile = file;
            vm.AdoptSavedAs(file.Name);
        }
        catch (Exception ex)
        {
            vm.ReportSaveFailure(ex);
        }
    }

    private bool _passwordRetry;

    private async Task<string?> AskForPasswordAsync(string fileName)
    {
        var dialog = new PasswordWindow();
        dialog.SetPrompt(fileName, _passwordRetry);
        await dialog.ShowDialog(this);

        // Remember that we have asked once, so a second prompt says why it is back
        // rather than looking like the first one failed to register.
        _passwordRetry = dialog.Password is not null;
        return dialog.Password;
    }

    private static FilePickerFileType PdfFileType => new("PDF document")
    {
        Patterns = ["*.pdf"],
        AppleUniformTypeIdentifiers = ["com.adobe.pdf"],
        MimeTypes = ["application/pdf"],
    };
}
