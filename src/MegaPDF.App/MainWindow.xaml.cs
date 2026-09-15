using System.Runtime.InteropServices.WindowsRuntime;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Viewing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private readonly PdfPrinter _printer;

    public MainWindow()
    {
        ViewModel = new MainViewModel(this);
        ViewModel.WatchBusyState();
        InitializeComponent();
        _printer = new PdfPrinter(this, () => ViewModel.CurrentDocument, () => ViewModel.OpenDocumentName,
                                  ViewModel.Busy, ViewModel.ShowErrorAsync);
        _printer.Register();
        // The page-level spinner (#145): on the line the text-edit check is about, or at the
        // top of the page a change waits on.
        ViewModel.Busy.PropertyChanged += (_, _) => UpdatePageBusyIndicator();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "megapdf.ico"));
        ViewModel.LoadSignatures();
        ViewModel.LoadRecentDocuments();
        ApplyTheme();
        ViewModel.ScrollRestoreRequested += offset =>
            DispatcherQueue.TryEnqueue(() => PagesScroll.ChangeView(null, offset, null, disableAnimation: true));
        ViewModel.SearchScrollRequested += target =>
            DispatcherQueue.TryEnqueue(() => ScrollMatchIntoView(target));
        AppWindow.Closing += OnAppWindowClosing;
        InitializePageKeyboard();
        InitializeToolbar();

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

                // Tab, Enter and Space on the page (SDD §2.2, #2). Esc lets go of a
                // selected signature before it lets go of the page.
                if ((args.Key != VirtualKey.Escape || _selection is null) && HandlePageKey(args.Key))
                {
                    args.Handled = true;
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
            // A different document means the matches are gone — close the stale bar.
            if (e.PropertyName is nameof(MainViewModel.DocumentPath) && FindBar.Visibility == Visibility.Visible)
                CloseFindBar();
            // Arming Add text brings the font and size pickers onto the toolbar (#144).
            if (e.PropertyName is nameof(MainViewModel.IsTextBoxMode))
                OnTextBoxModeChanged();
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

    private async void OnPageTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Grid pageGrid || pageGrid.DataContext is not PageView pageView)
            return;

        // A pointer takes over from the keyboard (#2): the ring would otherwise stay
        // on a region the person has moved away from.
        ViewModel.ClearPageFocus();

        var position = e.GetPosition(pageGrid);
        var dipToPoint = 72.0 / 96 / ViewModel.ZoomFactor;
        await RoutePageActivationAsync(pageGrid, pageView,
            new PdfPoint(position.X * dipToPoint, position.Y * dipToPoint));
    }

    /// <summary>
    /// The document is the interface (SDD §2.2): what you click determines what happens —
    /// checkboxes toggle, form fields and body text edit in place, empty space does nothing.
    ///
    /// A tap comes here, and so do Enter and Space on a keyboard-focused region, aimed
    /// at its centre (#2) — one routing, so the keyboard cannot diverge from the mouse.
    /// </summary>
    private async Task RoutePageActivationAsync(Grid pageGrid, PageView pageView, PdfPoint pagePoint)
    {
        // Taps while work runs — a change on its way, its page check, the text-edit check, a
        // save — are ignored rather than queued (#145).
        if (ViewModel.Busy.IsBusy)
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

        // Signature placement mode: the next page click stamps the pending signature.
        if (ViewModel.PendingSignature is not null)
        {
            await ViewModel.PlacePendingSignatureAsync(pageView.Index, pagePoint);
            return;
        }

        // Text-box mode: the click chooses where the new text goes (SDD-style inline editor).
        if (ViewModel.IsTextBoxMode)
        {
            // The toolbar's pickers chose the face and size while the mode was armed (#144).
            var newStyle = PickedTextStyle(ViewModel.LastTextStyle);
            ViewModel.CancelPlacementModes();
            ShowInlineEditor(pageGrid, new PdfRect(pagePoint.X, pagePoint.Y, 0, newStyle.FontSize),
                "", newStyle.FontSize, newStyle,
                (newText, style) => string.IsNullOrWhiteSpace(newText)
                    ? Task.CompletedTask
                    : ViewModel.AddTextBoxAsync(pageView.Index, pagePoint, newText,
                        style!.FontName, style.FontSize));
            return;
        }

        var hit = await Task.Run(() => ViewModel.HitTestPage(pageView.Index, pagePoint));
        // #131: on a restricted document a click on something the owner does not allow
        // changing opens no editor and selects nothing; the notice says why.
        if (!ViewModel.AllowsInteraction(hit.Kind))
            return;
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

            case PageHitKind.TextBox:
                // Added text moves/nudges like a signature; double-click edits (no resize).
                SelectStamp(pageGrid, pageView, $"textbox:{hit.ObjectIndex}", hit.Bounds!.Value, resizable: false, run: hit.TextRun);
                break;

            case PageHitKind.FormTextField:
            {
                var field = hit.Field!;
                ShowInlineEditor(pageGrid, field.Bounds, field.Value, fontSizePoints: 12,
                    style: null,
                    (newText, _) => ViewModel.ApplyFormTextAsync(pageView.Index, field, newText));
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
                // #118: on pages PDFium cannot rewrite faithfully, say so before typing.
                // #128: and say why. #145: under a spinner on the line, taps ignored meanwhile.
                LayoutVerdict? refusal;
                try
                {
                    refusal = await ViewModel.CheckLineAsync(pageView.Index, line);
                }
                catch (Exception ex)
                {
                    await ViewModel.ShowErrorAsync(Strings.CannotEditTextTitle, UserFacing.Describe(ex));
                    break;
                }
                if (refusal is not null)
                {
                    await ViewModel.ShowLayoutRefusalAsync(refusal);
                    break;
                }
                // Clearing all text means "remove this text" (undoable).
                // style: null — §3.1 is explicit that no formatting UI appears when
                // editing the document's own text. Its formatting is inherited from
                // the run, so there is nothing to choose.
                ShowInlineEditor(pageGrid, line.Bounds, line.Text, line.FontSize,
                    style: null,
                    (newText, _) => string.IsNullOrWhiteSpace(newText)
                        ? ViewModel.DeleteLineAsync(pageView.Index, line)
                        : ViewModel.ApplyLineEditAsync(pageView.Index, line, newText));
                break;
            }
        }
    }

    /// <summary>
    /// Double-click on an added text box reopens the inline editor to change its
    /// characters (single-click selects it for move/nudge, SDD §3.3). Fires when the
    /// box isn't yet selected; once selected, its chrome handles the double-tap instead.
    /// </summary>
    private async void OnPageDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not PageCanvas canvas || canvas.DataContext is not PageView pageView)
            return;
        var position = e.GetPosition(canvas);
        var dipToPoint = 72.0 / 96 / ViewModel.ZoomFactor;
        if (await EditTextBoxAtAsync(canvas, pageView, new PdfPoint(position.X * dipToPoint, position.Y * dipToPoint)))
            e.Handled = true;
    }

    /// <summary>Opens the inline editor over the text box at <paramref name="pagePoint"/>, if one is there.</summary>
    private async Task<bool> EditTextBoxAtAsync(PageCanvas canvas, PageView pageView, PdfPoint pagePoint)
    {
        if (_activeEditor is not null)
            return false;
        var hit = await Task.Run(() => ViewModel.HitTestPage(pageView.Index, pagePoint));
        if (hit.Kind != PageHitKind.TextBox || hit.TextLine is not { } line)
            return false;
        if (!ViewModel.AllowsInteraction(hit.Kind))
            return true; // #131: handled — the restricted notice is showing

        Deselect();
        // An *added* box, not the document's own text: it has no inherited
        // formatting, so this is where the size and face pickers belong (#43).
        var run = hit.TextRun!;
        var current = new TextStyleChoice(
            run.FontSize, run.TextBoxFont ?? StandardTextBoxFonts.Default);
        ShowInlineEditor(canvas, line.Bounds, line.Text, line.FontSize, current,
            (newText, style) => string.IsNullOrWhiteSpace(newText)
                ? ViewModel.DeleteLineAsync(pageView.Index, line)
                : ViewModel.RestyleTextBoxAsync(pageView.Index, run, newText,
                    style!.FontName, style.FontSize));
        return true;
    }

    /// <summary>What a base-14 face is called in the UI; the PDF names are exact.</summary>
    private static string FontLabel(string fontName) =>
        fontName == StandardTextBoxFonts.Serif ? "Times" : fontName;

    /// <summary>
    /// The inline editor. <paramref name="style"/> non-null brings the toolbar's size
    /// and face pickers, showing it (#144; they used to sit above the editor) — passed
    /// only for MegaPDF's own text boxes. It is null for the document's own text and
    /// for form fields, where SDD §3.1 is explicit that no formatting UI appears
    /// because the formatting is inherited.
    /// </summary>
    private void ShowInlineEditor(Grid pageGrid, PdfRect bounds, string initialText,
                                  double fontSizePoints, TextStyleChoice? style,
                                  Func<string, TextStyleChoice?, Task> commit)
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

        TextStyleChoice? ChosenStyle() => style is null ? null : PickedTextStyle(style);

        async Task CommitAsync()
        {
            if (!pageGrid.Children.Contains(editor))
                return; // already committed or cancelled
            var newText = editor.Text;
            var chosen = ChosenStyle();
            CloseEditor(pageGrid, editor);
            await commit(newText, chosen);
        }

        // PreviewKeyDown, not KeyDown: TextBox handles Escape internally (reverting
        // its text) and marks it handled, so KeyDown never sees it.
        //
        // Opened from the keyboard (#2), closing hands focus back to the page, and Tab
        // commits and moves on to the next region — filling a form is type, Tab, type.
        // Not for added text boxes: there Tab goes to the face and size pickers on the
        // toolbar (#144), and the edit stays open while they are used.
        editor.PreviewKeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                var committing = CommitAsync(); // closes the editor before its first await
                ReturnFocusToPage();
                await committing;
            }
            else if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                CloseEditor(pageGrid, editor);
                ReturnFocusToPage();
            }
            else if (args.Key == VirtualKey.Tab && style is not null && !IsShiftDown()
                     && FontPickerItem.Visibility == Visibility.Visible && !FontPickerItem.IsInOverflow)
            {
                args.Handled = true;
                FontPicker.Focus(FocusState.Keyboard);
            }
            else if (args.Key == VirtualKey.Tab && style is null && ViewModel.PageFocus is not null)
            {
                args.Handled = true;
                var forward = !IsShiftDown();
                var committing = CommitAsync();
                ReturnFocusToPage();
                await committing;
                await StepPageFocusAsync(forward);
            }
        };
        // Focus moving into the toolbar's pickers, or the list one has open, must not
        // commit and tear the editor down (#144).
        editor.LostFocus += async (_, _) =>
        {
            if (style is not null
                && FocusManager.GetFocusedElement(pageGrid.XamlRoot) is DependencyObject focused
                && (IsWithin(focused, FontPicker) || IsWithin(focused, SizePicker) || focused is ComboBoxItem))
            {
                return;
            }
            await CommitAsync();
        };

        AutomationProperties.SetName(editor, Strings.EditTextName);
        if (style is not null)
        {
            _styleEditorOpen = true;
            ShowStyleInPickers(style);
            UpdateTextPickers();
        }
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
            if (_styleEditorOpen)
            {
                _styleEditorOpen = false;
                UpdateTextPickers();
            }
        }
    }

    /// <summary>True when <paramref name="node"/> is <paramref name="ancestor"/> or sits inside it.</summary>
    private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
    {
        for (DependencyObject? current = node; current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current == ancestor)
                return true;
        }
        return false;
    }

    // --- Signature selection chrome (SDD §3.3: drag to move, handle to resize, ✕/Delete to remove) ---

    /// <summary><paramref name="Run"/> is set for an added text box: what the toolbar's pickers restyle (#144).</summary>
    /// <param name="Pad">DIPs the chrome stands off the item on every side; taken back out when a move is committed.</param>
    private sealed record StampSelection(PageCanvas Canvas, PageView Page, string Id, PdfRect Bounds, bool Movable, PdfTextRun? Run = null, double Pad = 0);

    private StampSelection? _selection;
    private Grid? _selectionChrome;

    private void SelectStamp(Grid pageGrid, PageView pageView, string annotationId, PdfRect bounds, bool movable = true, bool resizable = true, PdfTextRun? run = null)
    {
        Deselect();
        if (pageGrid is not PageCanvas canvas)
            return;
        var toDip = 96.0 / 72 * ViewModel.ZoomFactor;
        // An added text box's bounds are its glyphs' tight box: chrome drawn exactly on them put
        // the border across the letters and the × chip over the last one. It stands off the
        // text instead, and the chip sits outside it.
        var isTextBox = annotationId.StartsWith("textbox:", StringComparison.Ordinal);
        var pad = isTextBox ? 4 : 0;
        _selection = new StampSelection(canvas, pageView, annotationId, bounds, movable, run, pad);

        var accent = Brand.Brush("BrandAccentBrush");
        var aspect = bounds.Height / bounds.Width;

        var chrome = new Grid
        {
            Width = (bounds.Width * toDip) + (2 * pad),
            Height = (bounds.Height * toDip) + (2 * pad),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness((bounds.X * toDip) - pad, (bounds.Y * toDip) - pad, 0, 0),
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
            Visibility = movable && resizable ? Visibility.Visible : Visibility.Collapsed,
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
            // On a text box the chip sits beside the box, clear of the text.
            Margin = isTextBox ? new Thickness(0, -11, -26, 0) : new Thickness(0, -11, -11, 0),
        };
        chrome.Children.Add(remove);

        chrome.Tapped += (_, args) => args.Handled = true;

        // Double-click a selected text box to edit its characters (single-click already
        // selected it, so the chrome covers the box and must catch the double-tap itself).
        if (annotationId.StartsWith("textbox:", StringComparison.Ordinal))
            chrome.DoubleTapped += async (_, args) =>
            {
                args.Handled = true;
                await EditTextBoxAtAsync(canvas, pageView, bounds.Center);
            };

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

        // An added box or a cover selected is about to be moved, restyled or removed: its
        // page's #139 check starts now, so the answer is usually ready by the change (#145).
        if (annotationId.StartsWith("textbox:", StringComparison.Ordinal) || annotationId.StartsWith("whiteout:", StringComparison.Ordinal))
            ViewModel.PreparePageCheck(pageView.Index);

        // An added text box brings the toolbar's pickers, showing its own face and size (#144).
        if (run is not null)
            ShowStyleInPickers(new TextStyleChoice(run.FontSize, run.TextBoxFont ?? StandardTextBoxFonts.Default));
        UpdateTextPickers();
    }

    /// <summary>✕ chip / Delete key: whiteouts and stamps remove through different operations.</summary>
    private async Task RemoveSelectedAsync(StampSelection selection)
    {
        if (selection.Id.StartsWith("whiteout:", StringComparison.Ordinal))
            await ViewModel.RemoveWhiteoutAsync(selection.Page.Index,
                int.Parse(selection.Id.AsSpan("whiteout:".Length)), selection.Bounds);
        else if (selection.Id.StartsWith("textbox:", StringComparison.Ordinal))
            await ViewModel.RemoveTextBoxAsync(selection.Page.Index,
                int.Parse(selection.Id.AsSpan("textbox:".Length)), selection.Bounds);
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
        var pad = selection.Pad;
        var width = (chrome.Width - (2 * pad)) * toPoint;
        var height = (chrome.Height - (2 * pad)) * toPoint;
        var x = Math.Clamp((chrome.Margin.Left + pad) * toPoint, 0, Math.Max(0, selection.Page.PointsWidth - width));
        var y = Math.Clamp((chrome.Margin.Top + pad) * toPoint, 0, Math.Max(0, selection.Page.PointsHeight - height));
        var newBounds = new PdfRect(x, y, width, height);

        Deselect();
        var isTextBox = selection.Id.StartsWith("textbox:", StringComparison.Ordinal);
        var moved = true;
        if (isTextBox)
            moved = await ViewModel.MoveTextBoxAsync(selection.Page.Index,
                int.Parse(selection.Id.AsSpan("textbox:".Length)), selection.Bounds, newBounds);
        else
            await ViewModel.MoveSignatureAsync(selection.Page.Index, selection.Id, selection.Bounds, newBounds);

        // The page container was regenerated by the re-render; find it and re-select. A move
        // cancelled at the #139 warning left the box where it was, so the selection goes back there.
        if (newBounds != selection.Bounds
            && selection.Page.Index < ViewModel.Pages.Count
            && FindPageCanvas(selection.Page.Index) is { } canvas)
        {
            var run = isTextBox && selection.Run is { } before
                ? ViewModel.FindTextBox(selection.Page.Index, before.TextBoxId, before.ObjectIndex) ?? before
                : null;
            SelectStamp(canvas, ViewModel.Pages[selection.Page.Index], selection.Id, moved ? newBounds : selection.Bounds,
                        resizable: !isTextBox, run: run);
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
        UpdateTextPickers();
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
        if (!ViewModel.IsWhiteoutMode || ViewModel.Busy.IsBusy || sender is not PageCanvas canvas)
            return;
        _whiteoutCanvas = canvas;
        _whiteoutStart = e.GetCurrentPoint(canvas).Position;
        _whiteoutPreview = new Border
        {
            // White because whiteout covers the page with paper. Not a theme
            // colour: it must stay white when the app is dark, or the preview
            // would show something the saved PDF will not contain.
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.75 },
            BorderBrush = Brand.Brush("BrandAccentBrush"),
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
                // No clickable affordance for what the document's owner does not allow (#131).
                if (candidate.Bounds.Contains(point) && ViewModel.Capabilities.Allows(candidate.Kind))
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
                    or PageHitKind.Whiteout or PageHitKind.TextBox => Microsoft.UI.Input.InputSystemCursorShape.Hand,
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
        var accent = Brand.Brush("BrandAccentBrush", 0.75);

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

    private async void OnPrintClicked(object sender, RoutedEventArgs e)
    {
        // The button is disabled without the print permission; Ctrl+P rides on it (#131).
        if (ViewModel.IsPrintAllowed)
            await _printer.ShowPrintUiAsync();
    }

    /// <summary>The restricted notice's action: ask for the owner password and reopen (#131).</summary>
    private async void OnUnlockClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.UnlockAsync();

    // --- Find in document (toolbar Find / Ctrl+F, issue #26: the Edge-style find bar) ---

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _findDebounce;

    /// <summary>Ctrl+F: open (or refocus) the find bar.</summary>
    private void OnFindAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        args.Handled = ShowFindBar();

    /// <summary>Toolbar Find button: the visible twin of Ctrl+F (SDD §2.2 — a labelled control for every capability).</summary>
    private void OnFindClicked(object sender, RoutedEventArgs e) => ShowFindBar();

    /// <summary>
    /// Opens the find bar, or refocuses and reselects it when it is already open — it never toggles the bar
    /// shut, so the button and the accelerator behave identically. Returns false when there is no document.
    /// </summary>
    private bool ShowFindBar()
    {
        if (!ViewModel.IsDocumentOpen)
            return false;
        FindBar.Visibility = Visibility.Visible;
        FindQuery.Focus(FocusState.Programmatic);
        FindQuery.SelectAll();
        return true;
    }

    /// <summary>Search as you type — a small debounce keeps big documents responsive.</summary>
    private void OnFindQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (_findDebounce is null)
        {
            _findDebounce = DispatcherQueue.CreateTimer();
            _findDebounce.Interval = TimeSpan.FromMilliseconds(250);
            _findDebounce.IsRepeating = false;
            _findDebounce.Tick += async (_, _) => await ViewModel.SearchAsync(FindQuery.Text);
        }
        _findDebounce.Stop();
        _findDebounce.Start();
    }

    /// <summary>Enter = next, Shift+Enter = previous, Esc = close — from anywhere in the bar.</summary>
    private void OnFindBarKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            var shift = (Microsoft.UI.Input.InputKeyboardSource
                .GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            ViewModel.MoveToMatch(shift ? -1 : 1);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseFindBar();
        }
    }

    private void OnFindPreviousClicked(object sender, RoutedEventArgs e) => ViewModel.MoveToMatch(-1);

    private void OnFindNextClicked(object sender, RoutedEventArgs e) => ViewModel.MoveToMatch(1);

    private void OnFindCloseClicked(object sender, RoutedEventArgs e) => CloseFindBar();

    /// <summary>Esc closes AND clears (issue #26) — reopening starts fresh.</summary>
    private void CloseFindBar()
    {
        _findDebounce?.Stop();
        FindBar.Visibility = Visibility.Collapsed;
        FindQuery.Text = "";
        ViewModel.ClearSearch();
    }

    /// <summary>
    /// Brings the current search match into view. Zoomed in, a match is just as
    /// likely to be off to the side as below the fold, and the horizontal offset used
    /// to be left alone — so pressing next appeared to do nothing (#28). Each axis
    /// only moves when the match is actually outside the viewport, so stepping
    /// through hits that are already on screen doesn't jolt the page around.
    /// </summary>
    /// <summary>
    /// The page scroller, for --screenshot-state find-zoomed (#32). x:Name fields
    /// are private, and that check is about scroll offsets: it has to read them
    /// before and after a search to say whether both axes actually moved.
    /// </summary>
    internal ScrollViewer PageScroller => PagesScroll;

    private void ScrollMatchIntoView(MainViewModel.SearchScrollTarget target)
    {
        // The decision lives in Core so it can be tested and so macOS uses the same
        // rules (#32) — watching this work needs a window, which is why it went
        // unverified for a release.
        var decision = MatchScroll.Reveal(
            new PdfRect(target.X, target.Y, target.Width, target.Height),
            PagesScroll.HorizontalOffset, PagesScroll.VerticalOffset,
            PagesScroll.ViewportWidth, PagesScroll.ViewportHeight,
            PagesScroll.ExtentWidth);

        if (decision.MovesAnything)
            PagesScroll.ChangeView(decision.Horizontal, decision.Vertical, null,
                disableAnimation: !AnimationsEnabled); // reduced motion (SDD §2.2)
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
            Title = Strings.RestoreTitle,
            Content = Strings.RestoreBody(MainViewModel.AppName, Path.GetFileName(session.DocumentPath)),
            PrimaryButtonText = Strings.Restore,
            CloseButtonText = Strings.Discard,
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
        if (_allowClose || (!ViewModel.HasUnsavedChanges && !ViewModel.Busy.IsWorking))
        {
            // Consented close — nothing left to recover (SDD §3.4).
            ViewModel.EndJournalSession();
            return;
        }
        args.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    private bool _confirmingClose;

    /// <summary>
    /// Waits for work still running — a save, a change — and then asks about unsaved changes
    /// (#145: Close waits while a save runs). The question is the view model's, so opening
    /// another document asks it the same way (D5).
    /// </summary>
    private async Task ConfirmCloseAsync()
    {
        if (_confirmingClose)
            return;
        _confirmingClose = true;
        try
        {
            await ViewModel.Busy.WhenIdleAsync();
            if (await ViewModel.ConfirmSaveChangesAsync())
            {
                _allowClose = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            await ViewModel.ShowErrorAsync(Strings.CouldNotSaveTitle, UserFacing.Describe(ex));
        }
        finally
        {
            _confirmingClose = false;
        }
    }

    // --- The page-level busy spinner (#145) ---

    private FrameworkElement? _pageBusyIndicator;
    private PageCanvas? _pageBusyCanvas;

    /// <summary>
    /// A small ProgressRing beside the line the text-edit check is about, or a labelled one at
    /// the top of the page a change waits on. Shown and hidden with BusyState's timing.
    /// </summary>
    private void UpdatePageBusyIndicator()
    {
        if (_pageBusyIndicator is not null)
        {
            _pageBusyCanvas?.Children.Remove(_pageBusyIndicator);
            _pageBusyIndicator = null;
            _pageBusyCanvas = null;
        }

        var busy = ViewModel.Busy;
        if (!busy.ShowsPageSpinner || busy.PageIndex < 0 || FindPageCanvas(busy.PageIndex) is not { } canvas)
            return;

        var toDip = 96.0 / 72 * ViewModel.ZoomFactor;
        FrameworkElement indicator;
        if (busy.Area is { } line)
        {
            const double ring = 16;
            indicator = new ProgressRing
            {
                IsActive = true,
                Width = ring,
                Height = ring,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness((line.X + line.Width) * toDip + 8,
                                       line.Y * toDip + Math.Max(0, ((line.Height * toDip) - ring) / 2), 0, 0),
                IsHitTestVisible = false,
            };
        }
        else
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
            content.Children.Add(new TextBlock { Text = busy.Label, VerticalAlignment = VerticalAlignment.Center });
            indicator = new Border
            {
                Child = content,
                Padding = new Thickness(12, 6, 12, 6),
                CornerRadius = new CornerRadius(6),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 12, 0, 0),
                IsHitTestVisible = false,
            };
        }
        AutomationProperties.SetName(indicator, busy.Label);
        canvas.Children.Add(indicator);
        _pageBusyIndicator = indicator;
        _pageBusyCanvas = canvas;
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
        LanguageChoice.SelectedIndex = AppLanguage.ChoiceIndex(ViewModel.LanguageSetting);
        ReopenToggle.IsOn = ViewModel.ReopenLastFile;
        FlattenToggle.IsOn = ViewModel.FlattenOnSave;
        var version = typeof(MainWindow).Assembly.GetName().Version;
        AboutVersion.Text = Strings.AboutVersion(version?.ToString(3) ?? "dev");
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

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsLoading || LanguageChoice.SelectedIndex < 0)
            return;
        ViewModel.LanguageSetting = AppLanguage.TagForChoice(LanguageChoice.SelectedIndex);
        // Resolved at startup, not live: the note says so instead of pretending.
        LanguageRestartNote.Visibility = Visibility.Visible;
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

    /// <summary>Opens the bundled THIRD-PARTY-NOTICES.txt in a scrollable in-app viewer.</summary>
    private async void OnThirdPartyNoticesClicked(object sender, RoutedEventArgs e)
    {
        string text;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "THIRD-PARTY-NOTICES.txt");
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            text = Strings.NoticesLoadFailed + "\n\n" + ex.Message;
        }

        var viewer = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            MinWidth = 480,
            MinHeight = 360,
        };
        Microsoft.UI.Xaml.Controls.ScrollViewer.SetVerticalScrollBarVisibility(viewer, ScrollBarVisibility.Auto);
        viewer.SetValue(AutomationProperties.NameProperty, Strings.NoticesTextName);

        var dialog = new ContentDialog
        {
            Title = Strings.NoticesTitle,
            Content = viewer,
            CloseButtonText = Strings.Close,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
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

    private void OnSignaturePicked(object sender, SignatureItem item)
    {
        ViewModel.SelectSignatureForPlacement(item);
        SignaturesFlyout.Hide();
        // The placement hint says "click"; a keyboard user places it with Enter (#2).
        if (ViewModel.PendingSignature is not null)
            Announce(Strings.PlaceSignatureKeyHint);
    }

    /// <summary>
    /// Shows the in-tree copy of the library where the flyout would open (the `sign`
    /// screenshot state): the popup layer is invisible to RenderTargetBitmap.
    /// </summary>
    public void ShowSignatureLibraryForScreenshot()
    {
        var below = SignaturesToolbarButton.TransformToVisual(RootGrid)
            .TransformPoint(new Windows.Foundation.Point(0, SignaturesToolbarButton.ActualHeight + 4));
        SignatureLibraryShot.Visibility = Visibility.Visible;
        SignatureLibraryShot.UpdateLayout();
        // A real flyout slides left to stay inside the window; so does this.
        var x = Math.Max(8, Math.Min(below.X, RootGrid.ActualWidth - SignatureLibraryShot.ActualWidth - 8));
        SignatureLibraryShot.Margin = new Thickness(x, below.Y, 0, 0);
    }

    /// <summary>
    /// Rename from a card's overflow or right-click (#100). The flyout light-dismisses
    /// under the dialog, so it is reopened afterwards: the user was in the library
    /// and still is.
    /// </summary>
    private async void OnRenameSignatureRequested(object sender, SignatureItem item)
    {
        SignaturesFlyout.Hide();

        var input = new TextBox { Text = item.Name, PlaceholderText = Strings.SignatureNamePlaceholder };
        input.SelectAll();
        var dialog = new ContentDialog
        {
            Title = Strings.RenameSignatureTitle,
            Content = input,
            PrimaryButtonText = Strings.Rename,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.RenameSignatureAsync(item, input.Text);
        ShowSignaturesFlyout();
    }

    /// <summary>Delete asks once; a signature is not recoverable once its file is gone.</summary>
    private async void OnDeleteSignatureRequested(object sender, SignatureItem item)
    {
        SignaturesFlyout.Hide();

        var dialog = new ContentDialog
        {
            Title = Strings.DeleteSignatureTitle(item.Name),
            Content = Strings.DeleteSignatureBody,
            PrimaryButtonText = Strings.Delete,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ViewModel.RemoveSignatureFromLibrary(item);
        ShowSignaturesFlyout();
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

    private async void OnAddSignatureFromImageClicked(object sender, EventArgs e)
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

    private async void OnTypeSignatureClicked(object sender, EventArgs e)
    {
        SignaturesFlyout.Hide();
        var input = new TextBox { PlaceholderText = Strings.YourNamePlaceholder, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Script"), FontSize = 24 };
        var dialog = new ContentDialog
        {
            Title = Strings.TypeSignatureTitle,
            Content = input,
            PrimaryButtonText = Strings.Add,
            CloseButtonText = Strings.Cancel,
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
    private async void OnDrawSignatureClicked(object sender, EventArgs e)
    {
        SignaturesFlyout.Hide();

        var strokes = new Canvas();
        var drawHost = new Grid
        {
            Width = 460,
            Height = 180,
            // White, and not themeable: the drawn signature is rasterised from this
            // surface and then background-removed at luminance > 235 (SDD §6.2). A
            // dark pad would survive the cleanup as a black rectangle.
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
                // SDD §6.2: the user's mark is #202020, near-black, so it reads as
                // ink on paper rather than as UI. Not a theme colour, not a token.
                Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)),
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

        var nameInput = new TextBox { PlaceholderText = Strings.SignatureNamePlaceholder, Text = Strings.MySignature };
        var clear = new Button { Content = Strings.Clear };
        clear.Click += (_, _) => strokes.Children.Clear();

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = Strings.DrawHint,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        content.Children.Add(drawHost);
        content.Children.Add(clear);
        content.Children.Add(nameInput);

        var dialog = new ContentDialog
        {
            Title = Strings.DrawSignatureTitle,
            Content = content,
            PrimaryButtonText = Strings.Add,
            CloseButtonText = Strings.Cancel,
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

        var name = string.IsNullOrWhiteSpace(nameInput.Text) ? Strings.MySignature : nameInput.Text.Trim();
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
