using MegaPDF.Core.Engine;
using MegaPDF.Core.Viewing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MegaPDF.App;

/// <summary>
/// One tab's pages area (#348 phase 1): the page list, the inline editor, the selection
/// chrome, the rubber band, the hover overlay, the find bar, the page busy spinner and the
/// keyboard focus ring — everything the plan's §4 table says has to become per tab rather
/// than per window. Extracted from <c>MainWindow.xaml(.cs)</c> and <c>MainWindow.Keyboard.cs</c>
/// unchanged in behaviour; only the cross-references to window-level chrome (the toolbar's
/// font/size pickers, the Open button as a keyboard fallback) became small callbacks/events
/// so this control does not need to know about <see cref="MainWindow"/> at all.
///
/// One instance per tab, created once and never rebound to a different
/// <see cref="DocumentViewModel"/> — its <see cref="ViewModel"/> reads the DataContext the
/// owning <c>TabViewItem</c> set, which TabView never changes for the life of the tab.
/// </summary>
public sealed partial class DocumentView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(DocumentViewModel), typeof(DocumentView), new PropertyMetadata(null));

    /// <summary>The tab's document. Set once, by the TabView's item template (never rebound).</summary>
    public DocumentViewModel ViewModel
    {
        get => (DocumentViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>
    /// Focus fallback for "past the last page region" and the page-focus Escape key
    /// (originally <c>OpenButton.Focus(...)</c>, a toolbar control this view does not
    /// own). Set by <see cref="MainWindow"/> when the tab is created.
    /// </summary>
    internal Action? FocusOpenButton { get; set; }

    /// <summary>
    /// Raised whenever what the toolbar's font/size pickers should show may have changed:
    /// a selection with a run, an inline editor over added text opening/closing, or Add
    /// text arming/disarming. <see cref="MainWindow"/> subscribes only for the active tab
    /// (re-subscribing on every switch, per the plan's §4 note) and re-runs its own
    /// <c>UpdateTextPickers</c>/<c>ShowStyleInPickers</c>.
    /// </summary>
    internal event EventHandler? TextStyleContextChanged;

    /// <summary>Whether something in this tab wants the toolbar's pickers shown.</summary>
    internal bool WantsTextStylePickers =>
        ViewModel.IsTextBoxMode || _styleEditorOpen || _restylingSelection || _selection is { Run: not null };

    /// <summary>The style an added text box selection carries, for the pickers to show.</summary>
    internal TextStyleChoice? SelectedRunStyle =>
        _selection is { Run: { } run } ? new TextStyleChoice(run.FontSize, run.TextBoxFont ?? StandardTextBoxFonts.Default) : null;

    private TextBox? _activeEditor;
    private Func<Task>? _activeEditorCommit;

    private bool _wired;

    public DocumentView()
    {
        InitializeComponent();
        // DataContext (the TabViewItem's data item) is not necessarily set until after
        // construction — the same reason x:Bind in this file resolves lazily. Wiring that
        // reads ViewModel waits for Loaded.
        Loaded += OnDocumentViewLoaded;
    }

    private void OnDocumentViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_wired)
            return;
        _wired = true;

        ViewModel.View = this;
        ViewModel.ScrollRestoreRequested += offset =>
            DispatcherQueue.TryEnqueue(() => PagesScroll.ChangeView(null, offset, null, disableAnimation: true));
        ViewModel.SearchScrollRequested += target =>
            DispatcherQueue.TryEnqueue(() => ScrollMatchIntoView(target));
        // A mark lives in the core and is never drawn into the page (#329), so nothing else
        // tells the view the overlays are stale: placed, undone, redone and cleared marks all
        // arrive here.
        ViewModel.RedactionMarksChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshRedactionOverlays);
        // The page-level spinner (#145): on the line the text-edit check is about, or at the
        // top of the page a change waits on.
        ViewModel.Busy.PropertyChanged += (_, _) => UpdatePageBusyIndicator();

        ViewModel.PropertyChanged += (_, ev) =>
        {
            // A different document means the matches are gone — close the stale bar.
            if (ev.PropertyName is nameof(DocumentViewModel.DocumentPath) && FindBar.Visibility == Visibility.Visible)
                CloseFindBar();
            if (ev.PropertyName is nameof(DocumentViewModel.IsTextBoxMode))
                TextStyleContextChanged?.Invoke(this, EventArgs.Empty);
        };

        InitializePageKeyboard();

        // Keyboard interaction with the selected signature (SDD §3.3):
        // Delete removes, arrows nudge 1pt (Shift = 10pt), Esc deselects.
        PreviewKeyDown += async (_, args) =>
        {
            // Esc cancels any placement mode first.
            if (args.Key == VirtualKey.Escape && _activeEditor is null
                && (ViewModel.PendingSignature is not null || ViewModel.IsWhiteoutMode || ViewModel.IsTextBoxMode
                    || ViewModel.IsRedactMode))
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

        // A tap outside a selected signature deselects it. A click within 600ms of an
        // arrow-key nudge races the nudge's debounce timer (#2): without this, Deselect
        // ripped the chrome (and its already-moved position) out from under the timer,
        // which then found _selection/_selectionChrome null and committed nothing — the
        // nudge was silently lost. Flushing first, the same guard MovePageFocusAsync
        // already uses for Tab, commits the pending move before the click lets go of it.
        if (_selection is not null)
        {
            if (_nudgeTimer is { IsRunning: true })
                await CommitChromeAsync();
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
            var newStyle = PickedOrLastTextStyle();
            ViewModel.CancelPlacementModes();
            ShowInlineEditor(pageGrid, new PdfRect(pagePoint.X, pagePoint.Y, 0, newStyle.FontSize),
                "", newStyle.FontSize, newStyle,
                (newText, style) => string.IsNullOrWhiteSpace(newText)
                    ? Task.CompletedTask
                    : ViewModel.AddTextBoxAsync(pageView.Index, pagePoint, newText,
                        style!.FontName, style.FontSize));
            return;
        }

        // A redaction mark is an overlay the hit test knows nothing about (#329), so it is
        // looked for before the hit test rather than in it: a click on a mark selects it to
        // move, resize or remove, and does not reach the content underneath — the mark is what
        // the person can see, and it is what they mean.
        if (ViewModel.IsEditingAllowed && ViewModel.RedactionMarkAt(pageView.Index, pagePoint) is { } mark)
        {
            SelectStamp(pageGrid, pageView, $"{RedactionIdPrefix}{mark.MarkId}", mark.Bounds);
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
        var current = new TextStyleChoice(run.FontSize, run.TextBoxFont ?? StandardTextBoxFonts.Default);
        ShowInlineEditor(canvas, line.Bounds, line.Text, line.FontSize, current,
            (newText, style) => string.IsNullOrWhiteSpace(newText)
                ? ViewModel.DeleteLineAsync(pageView.Index, line)
                : ViewModel.RestyleTextBoxAsync(pageView.Index, run, newText,
                    style!.FontName, style.FontSize));
        return true;
    }

    /// <summary>The style a picker-driven edit should start from: the last one used, until the pickers say otherwise.</summary>
    private TextStyleChoice PickedOrLastTextStyle() => ViewModel.LastTextStyle;

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
            // The words here are the document's, and Windows would check them against
            // the display language rather than the document's: on an English desktop
            // every French surname came back red-underlined (#212). Nothing in a PDF
            // is ours to mark as misspelled.
            IsSpellCheckEnabled = false,
        };

        TextStyleChoice? ChosenStyle() => style is null ? null : new TextStyleChoice(
            _lastPickedFontSize ?? style.FontSize, _lastPickedFontName ?? style.FontName);

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
            else if (args.Key == VirtualKey.Tab && style is not null && !IsShiftDown() && RequestPickerFocus())
            {
                args.Handled = true;
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
        // commit and tear the editor down (#144) — MainWindow decides that (it owns the
        // pickers) through IsFocusMovingToPickers.
        editor.LostFocus += async (_, _) =>
        {
            if (style is not null && IsFocusMovingToPickers is { } check && check())
                return;
            await CommitAsync();
        };

        AutomationProperties.SetName(editor, Strings.EditTextName);
        if (style is not null)
        {
            _styleEditorOpen = true;
            _lastPickedFontSize = null;
            _lastPickedFontName = null;
            TextStyleContextChanged?.Invoke(this, EventArgs.Empty);
        }
        pageGrid.Children.Add(editor);
        _activeEditor = editor;
        _activeEditorCommit = CommitAsync;
        editor.Focus(FocusState.Programmatic);
        editor.SelectAll();
    }

    /// <summary>MainWindow's hook: whether focus is moving from the open editor into its own font/size pickers.</summary>
    internal Func<bool>? IsFocusMovingToPickers { get; set; }

    /// <summary>MainWindow's hook: move keyboard focus into the font picker (Tab from the editor).</summary>
    internal Func<bool>? RequestPickerFocusCallback { get; set; }

    private bool RequestPickerFocus() => RequestPickerFocusCallback?.Invoke() ?? false;

    private double? _lastPickedFontSize;
    private string? _lastPickedFontName;

    /// <summary>MainWindow calls this while an editor with pickers is open and a picker changes.</summary>
    internal void ApplyPickedStyleToOpenEditor(double fontSize, string fontName)
    {
        _lastPickedFontSize = fontSize;
        _lastPickedFontName = fontName;
        if (_styleEditorOpen && _activeEditor is { } editor)
            editor.FontSize = Math.Max(fontSize * 96.0 / 72 * ViewModel.ZoomFactor, 10);
    }

    /// <summary>MainWindow calls this after a picker change with an added box selected.</summary>
    internal async Task RestyleSelectedRunAsync(double fontSize, string fontName)
    {
        if (_selection is not { Run: { } run } selection)
            return;
        var current = new TextStyleChoice(run.FontSize, run.TextBoxFont ?? StandardTextBoxFonts.Default);
        if (fontName == current.FontName && Math.Abs(fontSize - current.FontSize) < 0.01)
            return;

        _restylingSelection = true;
        try
        {
            Deselect();
            await ViewModel.RestyleTextBoxAsync(selection.Page.Index, run, run.Text, fontName, fontSize);
            // The page re-rendered: select the box again as it now is — or as it still is,
            // after Cancel at the #139 warning, which also puts the pickers back.
            if (selection.Page.Index < ViewModel.Pages.Count
                && ViewModel.FindTextBox(selection.Page.Index, run.TextBoxId, run.ObjectIndex) is { } box
                && FindPageCanvas(selection.Page.Index) is { } canvas)
            {
                SelectStamp(canvas, ViewModel.Pages[selection.Page.Index], $"textbox:{box.ObjectIndex}", box.Bounds,
                            resizable: false, run: box);
            }
        }
        finally
        {
            _restylingSelection = false;
            TextStyleContextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Back to typing once a picker has been used over an open editor.</summary>
    internal void FocusActiveEditor()
    {
        if (_styleEditorOpen)
            _activeEditor?.Focus(FocusState.Programmatic);
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
                TextStyleContextChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>True when <paramref name="node"/> is <paramref name="ancestor"/> or sits inside it.</summary>
    internal static bool IsWithin(DependencyObject node, DependencyObject ancestor)
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
    internal sealed record StampSelection(PageCanvas Canvas, PageView Page, string Id, PdfRect Bounds, bool Movable, PdfTextRun? Run = null, double Pad = 0);

    private StampSelection? _selection;
    private Grid? _selectionChrome;

    private bool _styleEditorOpen;
    private bool _restylingSelection;

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
        // Signatures and text boxes resize proportionally (SDD §3.3 — a face may not be
        // stretched). A redaction mark is the exception (#329): it is an area, and the person
        // is deciding what it covers, so its corner handle drags each edge on its own.
        var aspectLocked = !annotationId.StartsWith("redaction:", StringComparison.Ordinal);

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

        // Corner handle: proportional-only resize (SDD §3.3 — no distortion possible),
        // except for a redaction mark, which keeps the free aspect it was given (#329).
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
            Content = new FontIcon { Glyph = "", FontSize = 10 },
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
            chrome.Height = aspectLocked
                ? newWidth * aspect
                : Math.Max(18, chrome.Height + args.Delta.Translation.Y);
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
        TextStyleContextChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>✕ chip / Delete key: whiteouts, marks and stamps remove through different operations.</summary>
    private async Task RemoveSelectedAsync(StampSelection selection)
    {
        if (selection.Id.StartsWith("whiteout:", StringComparison.Ordinal))
            await ViewModel.RemoveWhiteoutAsync(selection.Page.Index,
                int.Parse(selection.Id.AsSpan("whiteout:".Length)), selection.Bounds);
        else if (selection.Id.StartsWith("textbox:", StringComparison.Ordinal))
            await ViewModel.RemoveTextBoxAsync(selection.Page.Index,
                int.Parse(selection.Id.AsSpan("textbox:".Length)), selection.Bounds);
        else if (selection.Id.StartsWith(RedactionIdPrefix, StringComparison.Ordinal))
            // A mark is not in the file (#329), so this is undoable and dirties nothing.
            await ViewModel.RemoveRedactionMarkAsync(selection.Page.Index, MarkIdOf(selection.Id), selection.Bounds);
        else
            await ViewModel.RemoveStampAsync(selection.Page.Index, selection.Id, selection.Bounds);
    }

    private const string RedactionIdPrefix = "redaction:";

    /// <summary>The mark id behind a chrome id, or -1 when the id is not a redaction mark's.</summary>
    private static int MarkIdOf(string chromeId) =>
        chromeId.StartsWith(RedactionIdPrefix, StringComparison.Ordinal)
            ? int.Parse(chromeId.AsSpan(RedactionIdPrefix.Length))
            : -1;

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
        var isMark = selection.Id.StartsWith(RedactionIdPrefix, StringComparison.Ordinal);
        var moved = true;
        if (isTextBox)
            moved = await ViewModel.MoveTextBoxAsync(selection.Page.Index,
                int.Parse(selection.Id.AsSpan("textbox:".Length)), selection.Bounds, newBounds);
        else if (isMark)
            // A mark moves and resizes without the file noticing and without a re-render
            // (#329): the container below is still there, which is why this one always
            // re-selects.
            await ViewModel.MoveRedactionMarkAsync(selection.Page.Index, MarkIdOf(selection.Id),
                selection.Bounds, newBounds);
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
        TextStyleContextChanged?.Invoke(this, EventArgs.Empty);
    }

    // --- Hover affordances (SDD §2.2: the document teaches what's clickable) ---

    private FrameworkElement? _hoverOverlay;
    private PageCanvas? _hoverCanvas;

    // --- Whiteout and redaction drag placement ---

    private bool _suppressNextTap;
    private PageCanvas? _whiteoutCanvas;
    private Border? _whiteoutPreview;
    private Windows.Foundation.Point _whiteoutStart;

    /// <summary>Whether the drag in progress marks a redaction rather than covering (#173).</summary>
    private bool _dragIsRedaction;

    private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // A new press is a new gesture. The end of a whiteout or redaction drag sets
        // _suppressNextTap for the Tapped its release raises, but a drag that moved raises
        // none, so the flag used to wait and swallow the next real click — the first click
        // on a line, a box or the signature line after any whiteout did nothing.
        _suppressNextTap = false;

        if ((!ViewModel.IsWhiteoutMode && !ViewModel.IsRedactMode) || ViewModel.Busy.IsBusy ||
            sender is not PageCanvas canvas)
        {
            return;
        }
        _dragIsRedaction = ViewModel.IsRedactMode;
        _whiteoutCanvas = canvas;
        _whiteoutStart = e.GetCurrentPoint(canvas).Position;
        _whiteoutPreview = new Border
        {
            // White because whiteout covers the page with paper. Not a theme
            // colour: it must stay white when the app is dark, or the preview
            // would show something the saved PDF will not contain.
            //
            // A redaction band is the opposite: translucent ink, so the text under it stays
            // readable while it is being marked — a mark is something you check before you
            // apply it, and it is never written to the file at all (#173).
            Background = _dragIsRedaction
                ? Brand.Brush("BrandRedactionMarkBrush")
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.75 },
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
        if (_dragIsRedaction)
        {
            _dragIsRedaction = false;
            // The overlay redraw comes from the document's own marks-changed signal, so a
            // gesture that marked nothing needs no redraw either (#329).
            await ViewModel.AddRedactionMarkAsync(pageView.Index, rect);
            return;
        }
        await ViewModel.AddWhiteoutAsync(pageView.Index, rect);
    }

    /// <summary>
    /// Draws the page's redaction marks over the raster (#173). Over, not into: a mark is
    /// never written to the file, so there is nothing in the page image to draw, and
    /// marking costs no re-render.
    /// </summary>
    private void RefreshRedactionOverlay(PageCanvas canvas, PageView pageView)
    {
        foreach (var stale in canvas.Children.OfType<Border>().Where(b => b.Tag as string == RedactionMarkTag).ToList())
            canvas.Children.Remove(stale);

        var scale = 96.0 / 72 * ViewModel.ZoomFactor;
        foreach (var mark in ViewModel.RedactionMarksOn(pageView.Index))
        {
            canvas.Children.Add(new Border
            {
                Tag = RedactionMarkTag,
                Background = Brand.Brush("BrandRedactionMarkBrush"),
                BorderBrush = Brand.Brush("BrandRedactionMarkOutlineBrush"),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(mark.Bounds.X * scale, mark.Bounds.Y * scale, 0, 0),
                Width = mark.Bounds.Width * scale,
                Height = mark.Bounds.Height * scale,
                IsHitTestVisible = false,
            });
        }
    }

    private const string RedactionMarkTag = "redaction-mark";

    /// <summary>
    /// Redraws every page's marks after the document's marks changed (#329), and brings a
    /// mark's chrome back in step with the core — undo, redo and Clear all marks each take
    /// marks away or move them, and a chrome left on a mark that is no longer there would let
    /// a person drag nothing.
    /// </summary>
    private void RefreshRedactionOverlays()
    {
        for (var i = 0; i < ViewModel.Pages.Count; i++)
        {
            if (FindPageCanvas(i) is { } canvas)
                RefreshRedactionOverlay(canvas, ViewModel.Pages[i]);
        }

        if (_selection is not { } selection
            || !selection.Id.StartsWith(RedactionIdPrefix, StringComparison.Ordinal))
        {
            return;
        }

        // The core is the truth for a mark's rectangle, and the only thing that knows which
        // ids still exist: a mark is re-made under a fresh id when its removal is undone. So a
        // selection whose mark is gone lets go, and one whose mark has moved — an undo of a
        // drag — is re-anchored onto the core's rectangle rather than the one it had, which is
        // what the next drag would otherwise record as its "from".
        RedactionMark? live = null;
        foreach (var mark in ViewModel.RedactionMarksOn(selection.Page.Index))
        {
            if (mark.MarkId == MarkIdOf(selection.Id))
            {
                live = mark;
                break;
            }
        }
        if (live is not { } current)
            Deselect();
        else if (current.Bounds != selection.Bounds)
            SelectStamp(selection.Canvas, selection.Page, selection.Id, current.Bounds);
    }

    /// <summary>
    /// A page's container is rebuilt whenever its slot is replaced — a render, a zoom step,
    /// an edit — and the marks drawn over it go with it (#173). They are overlay, not page
    /// content, so they are put back as each canvas arrives; without this a mark vanished
    /// the moment the page it was on re-rendered, which on Windows was immediately.
    /// </summary>
    private void OnPageCanvasLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is PageCanvas canvas && canvas.DataContext is PageView pageView)
            RefreshRedactionOverlay(canvas, pageView);
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
            ViewModel.PendingSignature is not null || ViewModel.IsWhiteoutMode || ViewModel.IsRedactMode
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

    /// <summary>The page scroller, for --screenshot-state find-zoomed (#32) and zoom-to-fit.</summary>
    internal ScrollViewer PageScroller => PagesScroll;

    private void ScrollMatchIntoView(DocumentViewModel.SearchScrollTarget target)
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

    /// <summary>The restricted notice's action: ask for the owner password and reopen (#131).</summary>
    private async void OnUnlockClicked(object sender, RoutedEventArgs e) =>
        await ViewModel.UnlockAsync();

    // --- Find in document (toolbar Find / Ctrl+F, issue #26: the Edge-style find bar) ---

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _findDebounce;

    /// <summary>
    /// Opens the find bar, or refocuses and reselects it when it is already open — it never toggles the bar
    /// shut, so the button and the accelerator behave identically. Returns false when there is no document.
    /// </summary>
    internal bool ShowFindBar()
    {
        if (!ViewModel.IsDocumentOpen)
            return false;
        FindBar.Visibility = Visibility.Visible;
        FindQuery.Focus(FocusState.Programmatic);
        FindQuery.SelectAll();
        return true;
    }

    /// <summary>
    /// ⋮ → Clear all marks (#329): drops every mark on the document as **one** undo step, so
    /// a person who over-marked starts again with one press of Undo to regret it.
    /// </summary>
    internal async Task<bool> ClearRedactionMarksAsync()
    {
        var pageIndex = Math.Clamp(ViewModel.CurrentPage - 1, 0, Math.Max(0, ViewModel.PageCount - 1));
        return await ViewModel.ClearRedactionMarksAsync(pageIndex);
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
    internal void CloseFindBar()
    {
        _findDebounce?.Stop();
        FindBar.Visibility = Visibility.Collapsed;
        FindQuery.Text = "";
        ViewModel.ClearSearch();
    }

    private void OnCancelPlacementClicked(InfoBar sender, object args) =>
        ViewModel.CancelPlacementModes();

    /// <summary>
    /// For the `textbox` screenshot state (#144): adds a box and selects it, which brings
    /// the pickers onto the row — <see cref="MainWindow"/> checks their visibility itself.
    /// </summary>
    internal async Task<bool> SelectNewTextBoxForScreenshotAsync(string text)
    {
        if (!ViewModel.IsDocumentOpen)
            return false;
        // Where the Mac --story prints the name: under the demo agreement's signature line
        // (tools/gen_test_fixtures.py demo.pdf). On another document it may land on text.
        await ViewModel.AddTextBoxAsync(0, new PdfPoint(72, 405), text);
        await Task.Delay(900);
        if (ViewModel.LastTextBoxOn(0) is not { } box || FindPageCanvas(0) is not { } canvas)
            return false;
        SelectStamp(canvas, ViewModel.Pages[0], $"textbox:{box.ObjectIndex}", box.Bounds, resizable: false, run: box);
        return true;
    }
}
