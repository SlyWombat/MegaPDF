using System.Runtime.InteropServices.WindowsRuntime;
using MegaPDF.Core.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace MegaPDF.App;

public sealed partial class MainWindow : Window
{
    private TextBox? _activeEditor;
    private Func<Task>? _activeEditorCommit;

    public MainViewModel ViewModel { get; }

    private bool _allowClose;

    public MainWindow()
    {
        ViewModel = new MainViewModel(this);
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "megapdf.ico"));
        ViewModel.LoadSignatures();
        ViewModel.LoadRecentDocuments();
        ApplyTheme();
        ViewModel.ScrollRestoreRequested += offset =>
            DispatcherQueue.TryEnqueue(() => PagesScroll.ChangeView(null, offset, null, disableAnimation: true));
        AppWindow.Closing += OnAppWindowClosing;

        // Keyboard interaction with the selected signature (SDD §3.3):
        // Delete removes, arrows nudge 1pt (Shift = 10pt), Esc deselects.
        if (Content is UIElement root)
        {
            root.PreviewKeyDown += async (_, args) =>
            {
                // Esc cancels any placement mode first.
                if (args.Key == VirtualKey.Escape && _activeEditor is null
                    && (ViewModel.PendingSignature is not null || ViewModel.IsWhiteoutMode || ViewModel.IsTextBoxMode))
                {
                    args.Handled = true;
                    ViewModel.CancelPlacementModes();
                    return;
                }

                if (_selection is null || _activeEditor is not null)
                    return;

                if (args.Key == VirtualKey.Delete)
                {
                    args.Handled = true;
                    var selection = _selection;
                    Deselect();
                    await RemoveSelectedAsync(selection);
                    return;
                }
                if (args.Key == VirtualKey.Escape)
                {
                    args.Handled = true;
                    Deselect();
                    return;
                }

                double dx = 0, dy = 0;
                switch (args.Key)
                {
                    case VirtualKey.Left: dx = -1; break;
                    case VirtualKey.Right: dx = 1; break;
                    case VirtualKey.Up: dy = -1; break;
                    case VirtualKey.Down: dy = 1; break;
                    default: return;
                }
                args.Handled = true;
                var shift = (Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                NudgeSelection(dx * (shift ? 10 : 1), dy * (shift ? 10 : 1));
            };
        }
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.WindowTitle))
                Title = ViewModel.WindowTitle;
        };
        Title = ViewModel.WindowTitle;
    }

    private void OnDocumentAreaDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private async void OnDocumentAreaDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        var pdf = items.OfType<StorageFile>()
            .FirstOrDefault(f => f.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf is not null)
            await ViewModel.OpenDocumentAsync(pdf.Path);
    }

    /// <summary>
    /// The document is the interface (SDD §2.2): what you click determines what happens —
    /// checkboxes toggle, form fields and body text edit in place, empty space does nothing.
    /// </summary>
    private async void OnPageTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Grid pageGrid || pageGrid.DataContext is not PageView pageView)
            return;

        // A tap outside an open editor commits it. The page canvas isn't focusable,
        // so LostFocus alone would never fire for clicks on empty page space.
        if (_activeEditorCommit is { } pendingCommit)
        {
            await pendingCommit();
            return;
        }

        // A tap outside a selected signature deselects it.
        if (_selection is not null)
        {
            Deselect();
            return;
        }

        if (_suppressNextTap)
        {
            _suppressNextTap = false;
            return;
        }

        var position = e.GetPosition(pageGrid);
        var dipToPoint = 72.0 / 96 / ViewModel.ZoomFactor;
        var pagePoint = new PdfPoint(position.X * dipToPoint, position.Y * dipToPoint);

        // Signature placement mode: the next page click stamps the pending signature.
        if (ViewModel.PendingSignature is not null)
        {
            await ViewModel.PlacePendingSignatureAsync(pageView.Index, pagePoint);
            return;
        }

        // Text-box mode: the click chooses where the new text goes (SDD-style inline editor).
        if (ViewModel.IsTextBoxMode)
        {
            ViewModel.CancelPlacementModes();
            ShowInlineEditor(pageGrid, new PdfRect(pagePoint.X, pagePoint.Y, 0, 12), "", 12,
                newText => string.IsNullOrWhiteSpace(newText)
                    ? Task.CompletedTask
                    : ViewModel.AddTextBoxAsync(pageView.Index, pagePoint, newText));
            return;
        }

        var hit = await Task.Run(() => ViewModel.HitTestPage(pageView.Index, pagePoint));
        switch (hit.Kind)
        {
            case PageHitKind.FormCheckbox:
                await ViewModel.ToggleCheckboxAsync(pageView.Index, hit.Field!);
                break;

            case PageHitKind.DrawnCheckbox:
                await ViewModel.AddMarkAsync(pageView.Index, hit.Bounds!.Value);
                break;

            case PageHitKind.StampAnnotation:
                if (hit.AnnotationId!.StartsWith("sig:", StringComparison.Ordinal))
                    // Signatures select for move/resize/delete (SDD §3.3).
                    SelectStamp(pageGrid, pageView, hit.AnnotationId, hit.Bounds!.Value);
                else
                    // Check marks stay click-to-toggle (SDD §3.2).
                    await ViewModel.RemoveStampAsync(pageView.Index, hit.AnnotationId, hit.Bounds!.Value);
                break;

            case PageHitKind.Whiteout:
                // Select with a remove-only chrome (redraw to reposition).
                SelectStamp(pageGrid, pageView, $"whiteout:{hit.ObjectIndex}", hit.Bounds!.Value, movable: false);
                break;

            case PageHitKind.FormTextField:
            {
                var field = hit.Field!;
                ShowInlineEditor(pageGrid, field.Bounds, field.Value, fontSizePoints: 12,
                    newText => ViewModel.ApplyFormTextAsync(pageView.Index, field, newText));
                break;
            }

            case PageHitKind.None when !pageView.Regions.Any(r => r.Kind == PageHitKind.TextRun):
                // A page with no text at all is a scan/photo — say so instead of
                // silently doing nothing (SDD §3.1 tier 3).
                ViewModel.IsScannedHintOpen = true;
                break;

            case PageHitKind.TextRun:
            {
                // Lines, not fragments: the editor covers the whole visual line (1.1).
                var line = hit.TextLine!;
                // Clearing all text means "remove this text" (undoable).
                ShowInlineEditor(pageGrid, line.Bounds, line.Text, line.FontSize,
                    newText => string.IsNullOrWhiteSpace(newText)
                        ? ViewModel.DeleteLineAsync(pageView.Index, line)
                        : ViewModel.ApplyLineEditAsync(pageView.Index, line, newText));
                break;
            }
        }
    }

    private void ShowInlineEditor(Grid pageGrid, PdfRect bounds, string initialText, double fontSizePoints, Func<string, Task> commit)
    {
        var toDip = 96.0 / 72 * ViewModel.ZoomFactor;
        var editor = new TextBox
        {
            Text = initialText,
            FontSize = Math.Max(fontSizePoints * toDip, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(bounds.X * toDip - 6, bounds.Y * toDip - 8, 0, 0),
            MinWidth = Math.Max(bounds.Width * toDip + 28, 140),
            AcceptsReturn = false,
        };

        async Task CommitAsync()
        {
            if (!pageGrid.Children.Contains(editor))
                return; // already committed or cancelled
            var newText = editor.Text;
            CloseEditor(pageGrid, editor);
            await commit(newText);
        }

        // PreviewKeyDown, not KeyDown: TextBox handles Escape internally (reverting
        // its text) and marks it handled, so KeyDown never sees it.
        editor.PreviewKeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                await CommitAsync();
            }
            else if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                CloseEditor(pageGrid, editor);
            }
        };
        editor.LostFocus += async (_, _) => await CommitAsync();

        AutomationProperties.SetName(editor, "Edit text");
        pageGrid.Children.Add(editor);
        _activeEditor = editor;
        _activeEditorCommit = CommitAsync;
        editor.Focus(FocusState.Programmatic);
        editor.SelectAll();
    }

    private void CloseEditor(Grid pageGrid, TextBox editor)
    {
        pageGrid.Children.Remove(editor);
        if (_activeEditor == editor)
        {
            _activeEditor = null;
            _activeEditorCommit = null;
        }
    }

    // --- Signature selection chrome (SDD §3.3: drag to move, handle to resize, ✕/Delete to remove) ---

    private sealed record StampSelection(PageCanvas Canvas, PageView Page, string Id, PdfRect Bounds, bool Movable);

    private StampSelection? _selection;
    private Grid? _selectionChrome;

    private void SelectStamp(Grid pageGrid, PageView pageView, string annotationId, PdfRect bounds, bool movable = true)
    {
        Deselect();
        if (pageGrid is not PageCanvas canvas)
            return;
        _selection = new StampSelection(canvas, pageView, annotationId, bounds, movable);

        var toDip = 96.0 / 72 * ViewModel.ZoomFactor;
        var accent = new Microsoft.UI.Xaml.Media.SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]);
        var aspect = bounds.Height / bounds.Width;

        var chrome = new Grid
        {
            Width = bounds.Width * toDip,
            Height = bounds.Height * toDip,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(bounds.X * toDip, bounds.Y * toDip, 0, 0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        if (movable)
            chrome.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        chrome.Children.Add(new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(2),
        });

        // Corner handle: proportional-only resize (SDD §3.3 — no distortion possible).
        var handle = new Border
        {
            Width = 14,
            Height = 14,
            Background = accent,
            CornerRadius = new CornerRadius(7),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -7, -7),
            ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY,
            Visibility = movable ? Visibility.Visible : Visibility.Collapsed,
        };
        chrome.Children.Add(handle);

        // ✕ chip.
        var remove = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 10 },
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -11, -11, 0),
        };
        chrome.Children.Add(remove);

        chrome.Tapped += (_, args) => args.Handled = true;

        chrome.ManipulationDelta += (_, args) =>
        {
            var m = chrome.Margin;
            chrome.Margin = new Thickness(m.Left + args.Delta.Translation.X, m.Top + args.Delta.Translation.Y, 0, 0);
        };
        chrome.ManipulationCompleted += async (_, _) => await CommitChromeAsync();

        handle.ManipulationDelta += (_, args) =>
        {
            args.Handled = true;
            var newWidth = Math.Max(24, chrome.Width + args.Delta.Translation.X);
            chrome.Width = newWidth;
            chrome.Height = newWidth * aspect;
        };
        handle.ManipulationCompleted += async (_, args) =>
        {
            args.Handled = true;
            await CommitChromeAsync();
        };

        remove.Click += async (_, _) =>
        {
            var selection = _selection;
            Deselect();
            if (selection is not null)
                await RemoveSelectedAsync(selection);
        };

        canvas.Children.Add(chrome);
        _selectionChrome = chrome;
    }

    /// <summary>✕ chip / Delete key: whiteouts and stamps remove through different operations.</summary>
    private async Task RemoveSelectedAsync(StampSelection selection)
    {
        if (selection.Id.StartsWith("whiteout:", StringComparison.Ordinal))
            await ViewModel.RemoveWhiteoutAsync(selection.Page.Index,
                int.Parse(selection.Id.AsSpan("whiteout:".Length)), selection.Bounds);
        else
            await ViewModel.RemoveStampAsync(selection.Page.Index, selection.Id, selection.Bounds);
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _nudgeTimer;

    /// <summary>Arrow-key nudge: move the chrome immediately, commit after a quiet moment.</summary>
    private void NudgeSelection(double dxPoints, double dyPoints)
    {
        if (_selectionChrome is null || _selection is not { Movable: true })
            return;
        var toDip = 96.0 / 72 * ViewModel.ZoomFactor;
        var margin = _selectionChrome.Margin;
        _selectionChrome.Margin = new Thickness(margin.Left + dxPoints * toDip, margin.Top + dyPoints * toDip, 0, 0);

        if (_nudgeTimer is null)
        {
            _nudgeTimer = DispatcherQueue.CreateTimer();
            _nudgeTimer.Interval = TimeSpan.FromMilliseconds(600);
            _nudgeTimer.IsRepeating = false;
            _nudgeTimer.Tick += async (_, _) => await CommitChromeAsync();
        }
        _nudgeTimer.Stop();
        _nudgeTimer.Start();
    }

    /// <summary>
    /// Applies the chrome's current position/size to the document, then re-selects
    /// at the new bounds so the user can keep adjusting (SDD §3.3).
    /// </summary>
    private async Task CommitChromeAsync()
    {
        if (_selection is null || _selectionChrome is null)
            return;
        _nudgeTimer?.Stop();
        var selection = _selection;
        var chrome = _selectionChrome;

        var toPoint = 72.0 / 96 / ViewModel.ZoomFactor;
        var width = chrome.Width * toPoint;
        var height = chrome.Height * toPoint;
        var x = Math.Clamp(chrome.Margin.Left * toPoint, 0, Math.Max(0, selection.Page.PointsWidth - width));
        var y = Math.Clamp(chrome.Margin.Top * toPoint, 0, Math.Max(0, selection.Page.PointsHeight - height));
        var newBounds = new PdfRect(x, y, width, height);

        Deselect();
        await ViewModel.MoveSignatureAsync(selection.Page.Index, selection.Id, selection.Bounds, newBounds);

        // The page container was regenerated by the re-render; find it and re-select.
        if (newBounds != selection.Bounds
            && selection.Page.Index < ViewModel.Pages.Count
            && FindPageCanvas(selection.Page.Index) is { } canvas)
        {
            SelectStamp(canvas, ViewModel.Pages[selection.Page.Index], selection.Id, newBounds);
        }
    }

    private PageCanvas? FindPageCanvas(int pageIndex)
    {
        var container = PagesItems.ContainerFromIndex(pageIndex);
        return container is null ? null : FindDescendant<PageCanvas>(container);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    private void Deselect()
    {
        if (_selectionChrome is not null)
            _selection?.Canvas.Children.Remove(_selectionChrome);
        _selection = null;
        _selectionChrome = null;
    }

    // --- Hover affordances (SDD §2.2: the document teaches what's clickable) ---

    private FrameworkElement? _hoverOverlay;
    private PageCanvas? _hoverCanvas;

    // --- Whiteout drag placement ---

    private bool _suppressNextTap;
    private PageCanvas? _whiteoutCanvas;
    private Border? _whiteoutPreview;
    private Windows.Foundation.Point _whiteoutStart;

    private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.IsWhiteoutMode || sender is not PageCanvas canvas)
            return;
        _whiteoutCanvas = canvas;
        _whiteoutStart = e.GetCurrentPoint(canvas).Position;
        _whiteoutPreview = new Border
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.75 },
            BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(_whiteoutStart.X, _whiteoutStart.Y, 0, 0),
            IsHitTestVisible = false,
        };
        canvas.Children.Add(_whiteoutPreview);
        canvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private async void OnPagePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_whiteoutPreview is null || sender is not PageCanvas canvas || canvas != _whiteoutCanvas)
            return;
        var end = e.GetCurrentPoint(canvas).Position;
        canvas.ReleasePointerCapture(e.Pointer);
        canvas.Children.Remove(_whiteoutPreview);
        _whiteoutPreview = null;
        _whiteoutCanvas = null;
        ViewModel.CancelPlacementModes();
        _suppressNextTap = true; // the release also raises Tapped
        e.Handled = true;

        if (canvas.DataContext is not PageView pageView)
            return;
        var toPoint = 72.0 / 96 / ViewModel.ZoomFactor;
        var rect = new PdfRect(
            Math.Min(_whiteoutStart.X, end.X) * toPoint,
            Math.Min(_whiteoutStart.Y, end.Y) * toPoint,
            Math.Abs(end.X - _whiteoutStart.X) * toPoint,
            Math.Abs(end.Y - _whiteoutStart.Y) * toPoint);
        await ViewModel.AddWhiteoutAsync(pageView.Index, rect);
    }

    private void OnPagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not PageCanvas canvas || canvas.DataContext is not PageView pageView)
            return;

        // Live rubber-band while placing a whiteout.
        if (_whiteoutPreview is not null && canvas == _whiteoutCanvas)
        {
            var current = e.GetCurrentPoint(canvas).Position;
            _whiteoutPreview.Margin = new Thickness(
                Math.Min(_whiteoutStart.X, current.X), Math.Min(_whiteoutStart.Y, current.Y), 0, 0);
            _whiteoutPreview.Width = Math.Abs(current.X - _whiteoutStart.X);
            _whiteoutPreview.Height = Math.Abs(current.Y - _whiteoutStart.Y);
            e.Handled = true;
            return;
        }

        if (_activeEditor is not null)
            return;

        var position = e.GetCurrentPoint(canvas).Position;
        var dipToPoint = 72.0 / 96 / ViewModel.ZoomFactor;
        var point = new PdfPoint(position.X * dipToPoint, position.Y * dipToPoint);

        InteractiveRegion? region = null;
        if (ViewModel.PendingSignature is null)
        {
            foreach (var candidate in pageView.Regions)
            {
                if (candidate.Bounds.Contains(point))
                {
                    region = candidate;
                    break;
                }
            }
        }

        canvas.SetCursorShape(
            ViewModel.PendingSignature is not null || ViewModel.IsWhiteoutMode
                ? Microsoft.UI.Input.InputSystemCursorShape.Cross
            : ViewModel.IsTextBoxMode
                ? Microsoft.UI.Input.InputSystemCursorShape.IBeam
            : region?.Kind switch
            {
                PageHitKind.TextRun or PageHitKind.FormTextField => Microsoft.UI.Input.InputSystemCursorShape.IBeam,
                PageHitKind.FormCheckbox or PageHitKind.DrawnCheckbox or PageHitKind.StampAnnotation
                    or PageHitKind.Whiteout => Microsoft.UI.Input.InputSystemCursorShape.Hand,
                _ => Microsoft.UI.Input.InputSystemCursorShape.Arrow,
            });

        UpdateHoverOverlay(canvas, region);
    }

    private void OnPagePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not PageCanvas canvas)
            return;
        canvas.SetCursorShape(null);
        UpdateHoverOverlay(canvas, null);
    }

    private void UpdateHoverOverlay(PageCanvas canvas, InteractiveRegion? region)
    {
        if (_hoverOverlay is not null)
        {
            _hoverCanvas?.Children.Remove(_hoverOverlay);
            _hoverOverlay = null;
            _hoverCanvas = null;
        }
        if (region is null)
            return;

        var toDip = 96.0 / 72 * ViewModel.ZoomFactor;
        var bounds = region.Bounds;
        var accent = new Microsoft.UI.Xaml.Media.SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]) { Opacity = 0.75 };

        _hoverOverlay = region.Kind == PageHitKind.TextRun
            // Faint dotted underline beneath hovered text (SDD §2.2).
            ? new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = 0, Y1 = 0, X2 = bounds.Width * toDip, Y2 = 0,
                Stroke = accent,
                StrokeThickness = 1.4,
                StrokeDashArray = [2, 2],
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(bounds.X * toDip, bounds.Bottom * toDip + 2, 0, 0),
                IsHitTestVisible = false,
            }
            // Light accent outline on checkboxes, fields, and stamps.
            : new Border
            {
                Width = bounds.Width * toDip + 8,
                Height = bounds.Height * toDip + 8,
                BorderBrush = accent,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(bounds.X * toDip - 4, bounds.Y * toDip - 4, 0, 0),
                IsHitTestVisible = false,
            };

        canvas.Children.Add(_hoverOverlay);
        _hoverCanvas = canvas;
    }

    private async void OnPagesPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        // Ctrl+wheel zooms — the idiom every tester tries first.
        var ctrl = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0;
        if (!ctrl)
            return;
        e.Handled = true;
        var delta = e.GetCurrentPoint(PagesScroll).Properties.MouseWheelDelta;
        if (delta > 0 && ViewModel.ZoomInCommand.CanExecute(null))
            await ViewModel.ZoomInCommand.ExecuteAsync(null);
        else if (delta < 0 && ViewModel.ZoomOutCommand.CanExecute(null))
            await ViewModel.ZoomOutCommand.ExecuteAsync(null);
    }

    // --- Scroll-tracking page indicator ---

    private void OnPagesScrollViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (ViewModel.Pages.Count == 0)
            return;

        var viewTop = PagesScroll.VerticalOffset;
        var viewBottom = viewTop + PagesScroll.ViewportHeight;
        var midline = viewTop + PagesScroll.ViewportHeight / 2;

        var firstVisible = -1;
        var lastVisible = 0;
        var currentPage = ViewModel.Pages.Count;
        var y = 24d; // ItemsPanel top padding
        for (var i = 0; i < ViewModel.Pages.Count; i++)
        {
            var pageHeight = ViewModel.Pages[i].Height;
            if (y + pageHeight >= viewTop && y <= viewBottom)
            {
                if (firstVisible < 0)
                    firstVisible = i;
                lastVisible = i;
            }
            if (midline <= y + pageHeight + 8 && currentPage == ViewModel.Pages.Count)
                currentPage = i + 1;
            y += pageHeight + 16; // panel spacing
        }

        ViewModel.CurrentPage = currentPage;
        ViewModel.CurrentScrollOffset = PagesScroll.VerticalOffset;
        if (firstVisible >= 0)
            _ = ViewModel.UpdateViewportAsync(firstVisible, lastVisible);
    }

    private async void OnFitWidthClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.FitWidthAsync(PagesScroll.ViewportWidth);

    private async void OnFitPageClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.FitPageAsync(PagesScroll.ViewportWidth, PagesScroll.ViewportHeight);

    // --- Crash recovery offer (SDD §3.4: one-click restore after an unclean exit) ---

    public async Task OfferCrashRecoveryAsync()
    {
        var sessions = ViewModel.FindRecoverableSessions();
        if (sessions.Count == 0)
            return;
        var session = sessions[0];

        // Right after Activate the visual tree may not be loaded yet, and
        // ContentDialog needs a live XamlRoot.
        if (Content is FrameworkElement { IsLoaded: false } root)
        {
            var loaded = new TaskCompletionSource();
            root.Loaded += (_, _) => loaded.TrySetResult();
            await loaded.Task;
        }

        var dialog = new ContentDialog
        {
            Title = "Restore unsaved changes?",
            Content = $"MegaPDF closed unexpectedly with unsaved changes to {Path.GetFileName(session.DocumentPath)}.",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Discard",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.RestoreSessionAsync(session);
        else
            Core.Recovery.RecoveryJournal.Discard(session.JournalPath);
    }

    // --- Unsaved-changes close prompt (SDD §2.2 forgiveness, P3) ---

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        ViewModel.SaveViewState();
        if (_allowClose || !ViewModel.HasUnsavedChanges)
        {
            // Consented close — nothing left to recover (SDD §3.4).
            ViewModel.EndJournalSession();
            return;
        }
        args.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        var dialog = new ContentDialog
        {
            Title = $"Save changes to {ViewModel.OpenDocumentName}?",
            Content = "Your changes will be lost if you don't save them.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                await ViewModel.SaveCommand.ExecuteAsync(null);
                if (!ViewModel.HasUnsavedChanges) // save succeeded
                {
                    _allowClose = true;
                    Close();
                }
                break;
            case ContentDialogResult.Secondary:
                _allowClose = true;
                Close();
                break;
        }
    }

    private async void OnRecentDocumentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton { DataContext: RecentDocument recent })
            await ViewModel.OpenDocumentAsync(recent.Path);
    }

    // --- Settings flyout ---

    private bool _settingsLoading;

    private void OnSettingsOpening(object sender, object e)
    {
        _settingsLoading = true;
        MarkStyleChoice.SelectedIndex = (int)ViewModel.MarkStyle;
        ThemeChoice.SelectedIndex = ViewModel.ThemeSetting switch { "Light" => 1, "Dark" => 2, _ => 0 };
        ReopenToggle.IsOn = ViewModel.ReopenLastFile;
        FlattenToggle.IsOn = ViewModel.FlattenOnSave;
        var version = typeof(MainWindow).Assembly.GetName().Version;
        AboutVersion.Text = $"MegaPDF {version?.ToString(3) ?? "dev"}";
        _settingsLoading = false;
    }

    private void OnMarkStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsLoading && MarkStyleChoice.SelectedIndex >= 0)
            ViewModel.MarkStyle = (Core.Engine.CheckMarkStyle)MarkStyleChoice.SelectedIndex;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsLoading || ThemeChoice.SelectedIndex < 0)
            return;
        ViewModel.ThemeSetting = ThemeChoice.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "" };
        ApplyTheme();
    }

    private void OnReopenToggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoading)
            ViewModel.ReopenLastFile = ReopenToggle.IsOn;
    }

    private void OnFlattenToggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoading)
            ViewModel.FlattenOnSave = FlattenToggle.IsOn;
    }

    public void ApplyTheme()
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = ViewModel.ThemeSetting switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }

    // --- Signature library & placement (SDD §3.3) ---

    private void OnSignatureClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SignatureItem item })
        {
            ViewModel.SelectSignatureForPlacement(item);
            SignaturesFlyout.Hide();
        }
    }

    private void OnRemoveSignatureClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: SignatureItem item })
            ViewModel.RemoveSignatureFromLibrary(item);
    }

    private void OnCancelPlacementClicked(InfoBar sender, object args) =>
        ViewModel.CancelPlacementModes();

    private void OnWhiteoutModeClicked(object sender, RoutedEventArgs e) =>
        ViewModel.StartWhiteoutMode();

    private void OnTextBoxModeClicked(object sender, RoutedEventArgs e) =>
        ViewModel.StartTextBoxMode();

    private void OnDefaultAppCardClosed(InfoBar sender, object args) =>
        ViewModel.DismissDefaultAppCard();

    private async void OnChooseDefaultAppsClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.DismissDefaultAppCard();
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:defaultapps"));
    }

    private async void OnAddSignatureFromImageClicked(object sender, RoutedEventArgs e)
    {
        SignaturesFlyout.Hide();
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        var image = await SignatureImageProcessor.LoadAndCleanAsync(file);
        await ViewModel.AddSignatureFromImageAsync(image, Path.GetFileNameWithoutExtension(file.Name));
    }

    private async void OnTypeSignatureClicked(object sender, RoutedEventArgs e)
    {
        SignaturesFlyout.Hide();
        var input = new TextBox { PlaceholderText = "Your name", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Script"), FontSize = 24 };
        var dialog = new ContentDialog
        {
            Title = "Type your signature",
            Content = input,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text))
            return;

        var image = await RenderTypedSignatureAsync(input.Text.Trim());
        await ViewModel.AddSignatureFromImageAsync(image, input.Text.Trim());
    }

    /// <summary>
    /// Freehand signature drawing (SDD §3.3). WinUI 3 has no InkCanvas, so this is a
    /// pointer-event stroke canvas: each press starts a rounded polyline, moves extend
    /// it, release ends it. Works with mouse, touch, and pen.
    /// </summary>
    private async void OnDrawSignatureClicked(object sender, RoutedEventArgs e)
    {
        SignaturesFlyout.Hide();

        var strokes = new Canvas();
        var drawHost = new Grid
        {
            Width = 460,
            Height = 180,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            CornerRadius = new CornerRadius(4),
        };
        drawHost.Children.Add(strokes);

        Microsoft.UI.Xaml.Shapes.Polyline? currentStroke = null;
        drawHost.PointerPressed += (_, args) =>
        {
            drawHost.CapturePointer(args.Pointer);
            currentStroke = new Microsoft.UI.Xaml.Shapes.Polyline
            {
                Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black),
                StrokeThickness = 3,
                StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Round,
                StrokeStartLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round,
                StrokeEndLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round,
            };
            currentStroke.Points.Add(args.GetCurrentPoint(drawHost).Position);
            strokes.Children.Add(currentStroke);
        };
        drawHost.PointerMoved += (_, args) =>
        {
            if (currentStroke is null)
                return;
            var point = args.GetCurrentPoint(drawHost).Position;
            var last = currentStroke.Points[^1];
            // Light smoothing: skip sub-pixel jitter.
            if (Math.Abs(point.X - last.X) + Math.Abs(point.Y - last.Y) >= 1.5)
                currentStroke.Points.Add(point);
        };
        drawHost.PointerReleased += (_, args) =>
        {
            drawHost.ReleasePointerCapture(args.Pointer);
            currentStroke = null;
        };
        drawHost.PointerCanceled += (_, _) => currentStroke = null;

        var nameInput = new TextBox { PlaceholderText = "Signature name", Text = "My signature" };
        var clear = new Button { Content = "Clear" };
        clear.Click += (_, _) => strokes.Children.Clear();

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "Draw with your mouse, finger, or pen.",
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        content.Children.Add(drawHost);
        content.Children.Add(clear);
        content.Children.Add(nameInput);

        var dialog = new ContentDialog
        {
            Title = "Draw your signature",
            Content = content,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        SignatureImage? captured = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (strokes.Children.Count == 0)
            {
                args.Cancel = true; // nothing drawn yet
                return;
            }
            // Capture while the dialog (and canvas) are still in the visual tree.
            var deferral = args.GetDeferral();
            try
            {
                var target = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                await target.RenderAsync(drawHost);
                var buffer = await target.GetPixelsAsync();
                captured = SignatureImageProcessor.Clean(
                    new SignatureImage(buffer.ToArray(), target.PixelWidth, target.PixelHeight));
            }
            finally
            {
                deferral.Complete();
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || captured is null)
            return;

        var name = string.IsNullOrWhiteSpace(nameInput.Text) ? "My signature" : nameInput.Text.Trim();
        await ViewModel.AddSignatureFromImageAsync(captured, name);
    }

    /// <summary>Renders the offscreen Segoe Script TextBlock to BGRA pixels.</summary>
    private async Task<SignatureImage> RenderTypedSignatureAsync(string text)
    {
        TypedSignatureText.Text = text;
        TypedSignatureHost.UpdateLayout();

        var target = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await target.RenderAsync(TypedSignatureHost);
        var buffer = await target.GetPixelsAsync();
        return new SignatureImage(buffer.ToArray(), target.PixelWidth, target.PixelHeight);
    }
}
