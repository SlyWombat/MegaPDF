using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using MegaPDF.Avalonia.Rendering;
using MegaPDF.Avalonia.ViewModels;
using MegaPDF.Core.Imaging;
using MegaPDF.Core.Viewing;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Services;

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
        EmptyOpenButton.Click += async (_, _) => await OpenDocumentAsync();
        ShrinkButton.Click += async (_, _) => await ShrinkForEmailAsync();

        RecentList.SelectionChanged += async (_, _) =>
        {
            if (RecentList.SelectedItem is not RecentEntry entry)
                return;
            RecentList.SelectedItem = null;
            await OpenRecentAsync(entry);
        };

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
            vm.EditFieldRequested += ShowFieldEditor;
            vm.PropertyChanged += (_, args) =>
            {
                // The chrome is positioned in device-independent pixels, so it has to
                // be rebuilt when the selection changes and when zoom moves it.
                if (args.PropertyName is nameof(MainViewModel.Selection) or nameof(MainViewModel.Zoom))
                    OnSelectionChanged();
            };
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back && ViewModel is { Selection: not null } selected)
        {
            selected.DeleteSelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel is { Selection: not null } hasSelection)
        {
            hasSelection.ClearSelection();
            e.Handled = true;
            return;
        }

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
        {
            vm.DpiScale = RenderScaling;
            vm.LoadRecents();
            UpdateViewport();
        }

        PageScroller.ScrollChanged += (_, _) => UpdateViewport();
        PageScroller.SizeChanged += (_, _) => UpdateViewport();

        _ = OfferRecoveryAsync();
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

    /// <summary>
    /// Editing an AcroForm text field. The editor is sized to the widget so it reads
    /// as filling in the box that is already printed on the form, rather than as
    /// typing somewhere near it.
    /// </summary>
    private void ShowFieldEditor(int pageIndex, PdfFormField field)
    {
        if (ViewModel is not { } vm || ContainerFor(pageIndex) is not { } container)
            return;

        var dip = PageBitmap.PointsToPixels * vm.Zoom;
        // A widget's height is the box; the text inside it sits a little smaller.
        var fontSize = Math.Max(6, field.Bounds.Height * 0.7);

        ShowInlineEditor(
            container,
            new Point(field.Bounds.X * dip, field.Bounds.Y * dip),
            fontSize, "Helvetica, Arial, sans-serif",
            field.Value, field.Bounds.Width * dip,
            text => vm.SetFieldValue(pageIndex, field, text));
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

    /// <summary>
    /// Keeps the view model told how big the viewport is and which page is in it —
    /// what fit-to-width, fit-to-page and the "Page 3 of 12" readout all need.
    /// </summary>
    private void UpdateViewport()
    {
        if (ViewModel is not { } vm)
            return;

        vm.ViewportWidth = PageScroller.Viewport.Width;
        vm.ViewportHeight = PageScroller.Viewport.Height;

        if (vm.Pages.Count == 0)
            return;

        // Whichever page covers the middle of the viewport is the one being read —
        // the topmost visible page is the wrong answer when a short page is
        // scrolling off the top.
        var middle = PageScroller.Offset.Y + (PageScroller.Viewport.Height / 2);
        var y = 0.0;
        foreach (var page in vm.Pages)
        {
            y += page.LayoutHeight + PageGap;
            if (middle <= y)
            {
                vm.CurrentPage = page.Index + 1;
                return;
            }
        }
        vm.CurrentPage = vm.Pages.Count;
    }

    /// <summary>
    /// Offers to recover work that was never saved (SDD §3.4).
    ///
    /// Asked rather than done: silently reopening a document and replaying edits
    /// onto it is startling, and the person may have abandoned those changes on
    /// purpose. Only asked when a document is not already open, so a file opened
    /// from Finder is never pushed aside by a prompt.
    /// </summary>
    private async Task OfferRecoveryAsync()
    {
        if (ViewModel is not { IsDocumentOpen: false } vm)
            return;

        var sessions = vm.FindRecoverableSessions();
        if (sessions.Count == 0)
            return;

        var session = sessions[0];
        var dialog = new RecoveryWindow();
        dialog.SetSession(Path.GetFileName(session.DocumentPath), session.EntryCount, session.LastWriteUtc);
        await dialog.ShowDialog(this);

        switch (dialog.Choice)
        {
            case RecoveryWindow.Decision.Restore:
                vm.RestoreSession(session);
                break;
            case RecoveryWindow.Decision.Discard:
                vm.DiscardSession(session);
                break;
        }
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
                // A click that lands on nothing deselects, which is what every
                // desktop app does and what makes the chrome feel like chrome.
                if (vm.Selection is not null && vm.HitTest(page.Index, pagePoint).Kind == PageHitKind.None)
                {
                    vm.ClearSelection();
                    break;
                }
                vm.HandlePageClick(page.Index, pagePoint);
                break;
        }

        e.Handled = true;
    }

    /// <summary>
    /// The cursor says what a click will do before you make it (SDD §2.2). Read off
    /// the page's in-memory interaction map, so this costs a rectangle scan rather
    /// than an engine hit-test per mouse movement.
    /// </summary>
    private void UpdateCursor(object? sender, PointerEventArgs e)
    {
        if (sender is not Control container || container.DataContext is not PageViewModel page)
            return;
        if (ViewModel is not { } vm)
            return;

        // In a placement mode the cursor describes the mode, not what is underneath.
        var shape = vm.Mode switch
        {
            MainViewModel.PageMode.AddText => StandardCursorType.Ibeam,
            MainViewModel.PageMode.Whiteout => StandardCursorType.Cross,
            _ when vm.IsPlacingSignature => StandardCursorType.Cross,
            _ => CursorForContent(),
        };

        container.Cursor = new Cursor(shape);

        StandardCursorType CursorForContent()
        {
            var dipToPoint = 1.0 / (PageBitmap.PointsToPixels * vm.Zoom);
            var at = e.GetPosition(container);
            return page.KindAt(new PdfPoint(at.X * dipToPoint, at.Y * dipToPoint)) switch
            {
                PageHitKind.TextRun or PageHitKind.FormTextField => StandardCursorType.Ibeam,
                PageHitKind.FormCheckbox or PageHitKind.DrawnCheckbox
                    or PageHitKind.StampAnnotation or PageHitKind.Whiteout
                    or PageHitKind.TextBox => StandardCursorType.Hand,
                _ => StandardCursorType.Arrow,
            };
        }
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
        OnSelectionPointerMoved(e);
        UpdateCursor(sender, e);

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
        OnSelectionPointerReleased(e);

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

        ImportSignatureButton.Click += async (_, _) =>
        {
            SignButton.Flyout?.Hide();
            await ImportSignatureAsync();
        };
    }

    /// <summary>
    /// Takes a signature from a photograph or scan (SDD §3.3). Most people have a
    /// signature on paper long before they have one they are willing to draw with a
    /// trackpad, so this is the path that actually gets used.
    /// </summary>
    private async Task ImportSignatureAsync()
    {
        if (ViewModel is not { } vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a photo of your signature",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });

        if (files.Count == 0)
            return;

        try
        {
            SignatureBitmap decoded;
            await using (var stream = await files[0].OpenReadAsync())
                decoded = Rendering.SignatureImages.LoadBgra(stream);

            var name = Path.GetFileNameWithoutExtension(files[0].Name);
            var entry = vm.AddSignatureFromImage(
                string.IsNullOrWhiteSpace(name) ? "Signature" : name,
                decoded,
                Rendering.SignatureImages.EncodePng);

            vm.BeginPlacing(entry);
        }
        catch (Exception ex)
        {
            vm.Status = $"Could not read that image: {ex.Message}";
        }
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

    /// <summary>
    /// Debounce for search-as-you-type (#66). Search walks every page and opens a
    /// pdfium handle per page, on the UI thread — so without this, an eight-letter
    /// word typed into a 200-page document is 1,600 sequential page opens and the
    /// UI cannot repaint between them. The WinUI app has used 250ms for the same
    /// reason since F6 landed.
    /// </summary>
    private DispatcherTimer? _findDebounce;

    private void WireFind()
    {
        _findDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _findDebounce.Tick += (_, _) =>
        {
            _findDebounce!.Stop();
            ViewModel?.Search(FindBox.Text ?? "");
        };

        FindBox.TextChanged += (_, _) =>
        {
            // Restarted on each keystroke, so the search runs once the typing
            // pauses rather than once per character.
            _findDebounce!.Stop();
            _findDebounce.Start();
        };

        // Enter advances, Shift+Enter goes back — the convention every find bar uses.
        FindBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;

            // Enter means "now" — run any pending search before advancing, or the
            // first Enter after typing would cycle stale matches.
            if (_findDebounce is { IsEnabled: true })
            {
                _findDebounce.Stop();
                ViewModel?.Search(FindBox.Text ?? "");
            }
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
        _findDebounce?.Stop();
        ViewModel?.CloseFind();
        FindBox.Text = "";
    }

    /// <summary>
    /// Brings a hit into view, using the same rules as Windows (#32).
    ///
    /// This used to scroll vertically, always, and never horizontally — which is
    /// the bug #28 reported and Windows fixed: on a zoomed page a hit off to the
    /// side was highlighted where it could not be seen, and pressing Next jolted
    /// the view even when the hit was already on screen. Sharing the decision means
    /// the two desktops cannot drift apart on it again.
    /// </summary>
    private void ScrollToMatch(int pageIndex, PdfRect rect)
    {
        if (ViewModel is not { } vm)
            return;

        // Pages stack vertically and are centred horizontally, so a hit's content
        // position is the pages above it plus its own offset within its page.
        var above = 0.0;
        var pageWidth = 0.0;
        foreach (var page in vm.Pages)
        {
            if (page.Index == pageIndex)
            {
                pageWidth = page.LayoutWidth;
                break;
            }
            above += page.LayoutHeight + PageGap;
        }

        var scale = PageBitmap.PointsToPixels * vm.Zoom;
        var extentWidth = PageScroller.Extent.Width;
        // Where the page's own left edge sits in content space when it is narrower
        // than the extent (the panel centres it).
        var pageLeft = Math.Max(0, (extentWidth - pageWidth) / 2);

        var target = new PdfRect(
            pageLeft + (rect.X * scale), above + (rect.Y * scale),
            rect.Width * scale, rect.Height * scale);

        var decision = MatchScroll.Reveal(
            target,
            PageScroller.Offset.X, PageScroller.Offset.Y,
            PageScroller.Viewport.Width, PageScroller.Viewport.Height,
            extentWidth);

        if (decision.MovesAnything)
            PageScroller.Offset = new Vector(
                decision.Horizontal ?? PageScroller.Offset.X,
                decision.Vertical ?? PageScroller.Offset.Y);
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
        Bind(PrintButton, Key.P, KeyModifiers.None, "Print", () => ViewModel?.PrintCommand.Execute(null));
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
        await RememberAsync(vm, files[0], path);
    }

    /// <summary>
    /// Reopens a document from the recents list.
    ///
    /// On macOS under the App Sandbox the stored path is not a key to anything — the
    /// grant was to the file the user picked, in that session. The security-scoped
    /// bookmark is what carries permission across launches, so it is tried first and
    /// the path is only a fallback for platforms that do not need one.
    /// </summary>
    private async Task OpenRecentAsync(RecentEntry entry)
    {
        if (ViewModel is not { } vm)
            return;

        if (entry.Bookmark is { } bookmark)
        {
            try
            {
                if (await StorageProvider.OpenFileBookmarkAsync(bookmark) is { } file)
                {
                    var bookmarked = file.TryGetLocalPath();
                    if (bookmarked is not null)
                    {
                        _openedFile = file;
                        vm.Open(bookmarked);
                        await RememberAsync(vm, file, bookmarked);
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // A stale bookmark is an ordinary outcome — the file moved, or the
                // grant expired. Fall through and try the path.
            }
        }

        if (!File.Exists(entry.Path))
        {
            vm.Status = "That file has moved or been deleted. Open it again to restore access.";
            return;
        }

        // Ask the platform for a real file handle rather than nulling _openedFile.
        // Without one Save has nothing to write through, and because CanSave only
        // looks at "open and dirty" the button would stay enabled and do nothing —
        // silently losing the user's work, which is worse than refusing outright.
        var fromPath = await StorageProvider.TryGetFileFromPathAsync(entry.Path);
        if (fromPath is null)
        {
            vm.Status = "That file cannot be opened for editing from here. Use Open to pick it again.";
            return;
        }

        _openedFile = fromPath;
        vm.Open(entry.Path);
        await RememberAsync(vm, fromPath, entry.Path);
    }

    /// <summary>
    /// Records the document in recents, with a bookmark where the platform supports
    /// one. Failing to mint a bookmark must not stop the document being remembered.
    /// </summary>
    private static async Task RememberAsync(MainViewModel vm, IStorageFile file, string path)
    {
        string? bookmark = null;
        try
        {
            bookmark = await file.SaveBookmarkAsync();
        }
        catch (Exception)
        {
        }

        vm.RememberRecent(path, bookmark);
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
        if (ViewModel is not { } vm)
            return;

        if (_openedFile is null)
        {
            // Should not happen — but a Save that does nothing at all is the worst
            // possible outcome, so it says something and offers the way out.
            vm.Status = "There is nowhere to save this back to. Use Save As to choose a file.";
            return;
        }

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
            // One call, so the journal is marked against the file the bytes went to
            // and DocumentPath follows the copy (#68).
            await using (var stream = await file.OpenWriteAsync())
                vm.SaveAsTo(stream, file.TryGetLocalPath(), file.Name);

            _openedFile = file;
        }
        catch (Exception ex)
        {
            vm.ReportSaveFailure(ex);
        }
    }

    /// <summary>Which document the retry state below belongs to.</summary>
    private string? _passwordAskedFor;
    private bool _passwordRetry;

    private async Task<string?> AskForPasswordAsync(string fileName)
    {
        // Scoped to the file, not to the window. The flag used to persist for the
        // window's lifetime, so after unlocking one document the FIRST prompt for
        // the next one claimed a password had failed that was never entered (#65).
        if (_passwordAskedFor != fileName)
        {
            _passwordAskedFor = fileName;
            _passwordRetry = false;
        }

        var dialog = new PasswordWindow();
        dialog.SetPrompt(fileName, _passwordRetry);
        await dialog.ShowDialog(this);

        // Remember that we have asked once, so a second prompt for THIS file says
        // why it is back rather than looking like the first failed to register.
        _passwordRetry = dialog.Password is not null;
        return dialog.Password;
    }

    /// <summary>
    /// Saves a smaller copy for email (SDD §3.7). Asks where to put it first,
    /// because the shrink is destructive to image quality and belongs in a copy —
    /// never over the original.
    /// </summary>
    private async Task ShrinkForEmailAsync()
    {
        if (ViewModel is not { } vm)
            return;

        if (vm.IsDirty)
        {
            vm.Status = "Save your changes first, then shrink the saved file.";
            return;
        }

        try
        {
            // Do the work FIRST, and only ask for a destination if there is
            // something to put in it. Opening a writable stream truncates whatever
            // is there, so asking first meant a document with nothing to shrink
            // left a 0-byte file behind — or destroyed the file the user picked to
            // overwrite — while reporting that nothing had happened (#59).
            var (result, bytes) = vm.PrepareShrunkCopy();
            if (bytes is null)
            {
                vm.Status = "The pictures in this document are already small — nothing to shrink.";
                return;
            }

            var baseName = vm.DocumentName is { } n ? Path.GetFileNameWithoutExtension(n) : "document";
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save a smaller copy",
                SuggestedFileName = $"{baseName} - smaller.pdf",
                DefaultExtension = "pdf",
                FileTypeChoices = [PdfFileType],
                ShowOverwritePrompt = true,
            });

            if (file is null)
                return;

            await using (var stream = await file.OpenWriteAsync())
                await stream.WriteAsync(bytes);

            vm.Status = $"Saved a smaller copy: {result.ImagesReplaced} picture(s) re-encoded.";
        }
        catch (Exception ex)
        {
            vm.Status = $"Could not shrink: {ex.Message}";
        }
    }

    private static FilePickerFileType PdfFileType => new("PDF document")
    {
        Patterns = ["*.pdf"],
        AppleUniformTypeIdentifiers = ["com.adobe.pdf"],
        MimeTypes = ["application/pdf"],
    };
}
