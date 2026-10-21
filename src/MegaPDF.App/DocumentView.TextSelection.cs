using System.Runtime.InteropServices;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Viewing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace MegaPDF.App;

/// <summary>
/// Selecting a document's text and copying it (#710), with no tool to pick first.
///
/// A click on a line still opens the editor, exactly as before (SDD §3.1). Only a press that
/// travels past Windows' own drag threshold (<c>SM_CXDRAG</c>/<c>SM_CYDRAG</c>) becomes a
/// selection, and WinUI raises no Tapped for it. So the two cannot be confused, and nothing an
/// armed tool or a selected object does with a drag changes:
///
/// * a placement mode (signature, text box, whiteout, redaction) keeps its press;
/// * a selected signature, box or cover keeps its chrome, which handles its own drags;
/// * a press on a checkbox, a form field, a stamp, a text box, a cover or a redaction mark keeps
///   its click, and does not start a selection if it moves;
/// * touch keeps panning the pages.
///
/// The rest of the desktop idiom: double-click selects a word on the document's own text (on an
/// added text box it still edits it), Shift+click and Shift+drag extend, Shift+Left/Right move the
/// selection's end one character, Ctrl+A selects the whole document (as Edge's PDF viewer and
/// Acrobat do), Ctrl+C and the context menu's Copy copy, and Escape lets go. The highlight is the
/// system's text selection colour over the page. The clipboard gets CR LF line ends, Windows' own.
///
/// A document whose author asked that nothing be copied out of it still selects. Copy is
/// disabled in the menu, which says why, and Ctrl+C raises the notice that says the same.
/// </summary>
public sealed partial class DocumentView
{
    private const string TextSelectionTag = "text-selection";

    /// <summary>A left press on the page that may yet become a selection, or already has.</summary>
    private sealed class TextPress(PageCanvas canvas, int page, Point start, uint pointerId)
    {
        public PageCanvas Canvas { get; } = canvas;
        public int Page { get; } = page;
        public Point Start { get; } = start;
        public uint PointerId { get; } = pointerId;
        public bool Dragging { get; set; }
    }

    private TextPress? _textPress;

    /// <summary>Why the last press was not the selection's, for the self-test's diagnostics.</summary>
    private string? _lastTextPressRefusal;

    private bool Refuse(string why)
    {
        _lastTextPressRefusal = why;
        return false;
    }

    internal string? LastTextPressRefusalForTest => _lastTextPressRefusal;

    /// <summary>The press just made was a selection gesture: the Tapped its release may raise is not a click.</summary>
    private bool _textGestureSwallowsTap;

    /// <summary>
    /// Bumped by every click that routes to the page, and by a double-click that takes the click
    /// back: the editor a first click is opening checks it after each await, so a double-click
    /// selecting a word on that line wins over the editor its first half started.
    /// </summary>
    private int _pageTapGeneration;

    /// <summary>When the last click on a page arrived (milliseconds) and which page, for the double-click.</summary>
    private (long At, int Page)? _lastPageTap;

    /// <summary>The text the open editor started with, so a double-click knows it is unchanged.</summary>
    private string? _activeEditorInitialText;

    private MenuFlyout? _textMenu;
    private MenuFlyoutItem? _textMenuCopy;
    private MenuFlyoutItem? _textMenuReason;
    private DoubleTappedEventHandler? _wordDoubleTapHandler;

    /// <summary>The reason Copy is unavailable, for the notice's x:Bind.</summary>
    public string CopyWithheldMessage => Strings.CopyWithheld;

    private void InitializeTextSelection()
    {
        var vm = ViewModel;
        Action<(int First, int Last)?, (int First, int Last)?> onChanged = (before, after) =>
        {
            foreach (var range in new[] { before, after })
            {
                if (range is not { } r)
                    continue;
                for (var i = r.First; i <= r.Last && i < vm.Pages.Count; i++)
                    DrawTextSelection(i);
            }
        };
        vm.TextSelection.Changed += onChanged;
        _unhook.Add(() => vm.TextSelection.Changed -= onChanged);
    }

    // --- The pointer ---

    private double PointsPerDip => 72.0 / 96 / ViewModel.ZoomFactor;

    private PdfPoint ToPagePoint(Point dip) => new(dip.X * PointsPerDip, dip.Y * PointsPerDip);

    /// <summary>
    /// One pointer reading, as the selection needs it: where the pointer is relative to any page
    /// (or the scroller), which pointer, and its buttons. A real event or the self-test's
    /// synthetic one drive the same three handlers below with it.
    /// </summary>
    private readonly record struct PointerSample(
        Func<UIElement, Point> PositionOn, uint PointerId, bool LeftPressed, bool IsTouch, Pointer? Pointer);

    private static PointerSample Sample(PointerRoutedEventArgs e, UIElement relativeTo) => new(
        element => e.GetCurrentPoint(element).Position, e.Pointer.PointerId,
        e.GetCurrentPoint(relativeTo).Properties.IsLeftButtonPressed,
        e.Pointer.PointerDeviceType == PointerDeviceType.Touch, e.Pointer);

    /// <summary>
    /// The press half of a selection. True when the press is the selection's to watch. A plain
    /// press is not marked handled, so one that does not move still becomes the ordinary click.
    /// </summary>
    private bool TryBeginTextPress(PageCanvas canvas, PointerSample sample, bool shift, out bool handled)
    {
        handled = false;
        _textPress = null;
        if (canvas.DataContext is not PageView pageView || ViewModel.SelectableText is null)
            return Refuse("no page or no text");
        if (sample.IsTouch || !sample.LeftPressed)
            return Refuse("touch, or not the left button");
        if (ViewModel.Busy.IsBusy || ViewModel.PendingSignature is not null || ViewModel.IsTextBoxMode
            || ViewModel.IsWhiteoutMode || ViewModel.IsRedactMode)
            return Refuse("busy, or a tool is armed");
        // An open editor commits on the click; a selected object's chrome owns its own drags.
        if (_activeEditor is not null || _selection is not null)
            return Refuse("an editor is open or an object is selected");

        var at = sample.PositionOn(canvas);
        var point = ToPagePoint(at);
        if (!ViewModel.IsReadingMode)
        {
            foreach (var region in pageView.Regions)
            {
                if (region.Kind != PageHitKind.TextRun && region.Bounds.Contains(point))
                    return Refuse($"the press is on a {region.Kind}");
            }
            if (ViewModel.IsEditingAllowed && ViewModel.RedactionMarkAt(pageView.Index, point) is not null)
                return Refuse("the press is on a redaction mark");
        }

        _lastTextPressRefusal = null;
        _textPress = new TextPress(canvas, pageView.Index, at, sample.PointerId);

        // Shift+click extends what is already selected, and Shift+drag goes on extending it.
        if (shift && ViewModel.TextSelection.Anchor is not null)
        {
            _textPress.Dragging = true;
            _textGestureSwallowsTap = true;
            if (sample.Pointer is { } pointer)
                canvas.CapturePointer(pointer);
            ExtendTextSelectionTo(pageView.Index, point);
            ParkFocusForSelection();
            handled = true;
        }
        return true;
    }

    /// <summary>The move half: past the drag threshold the press becomes a selection, and then follows the pointer.</summary>
    private bool TrackTextPress(PageCanvas canvas, PointerSample sample)
    {
        if (_textPress is not { } press || sample.PointerId != press.PointerId)
            return false;
        if (!press.Dragging)
        {
            if (!sample.LeftPressed)
            {
                _textPress = null;
                return false;
            }
            var at = sample.PositionOn(press.Canvas);
            var (dx, dy) = DragThreshold();
            if (Math.Abs(at.X - press.Start.X) <= dx && Math.Abs(at.Y - press.Start.Y) <= dy)
                return Refuse($"inside the drag threshold ({dx:0.#} x {dy:0.#} DIP)");
            if (!BeginTextDrag(press, sample))
                return false;
        }

        var (page, point) = PagePointUnder(sample, ViewModel.TextSelection.Focus?.PageIndex ?? press.Page);
        ExtendTextSelectionTo(page, point);
        AutoScrollForDrag(sample);
        canvas.SetCursorShape(InputSystemCursorShape.IBeam);
        return true;
    }

    private bool BeginTextDrag(TextPress press, PointerSample sample)
    {
        if (ViewModel.SelectableText is not { } cache)
            return Refuse("the document has no text cache");
        PdfSelectableText text;
        try
        {
            // Normally already loaded as the page was drawn; a press that beats that loads here.
            text = cache.Peek(press.Page) ?? cache.Get(press.Page);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return Refuse($"the page's text could not be read: {ex.Message}");
        }
        var anchor = text.CaretAt(ToPagePoint(press.Start)) ?? 0;
        ViewModel.TextSelection.Begin(new PdfTextPosition(press.Page, anchor));
        press.Dragging = true;
        _textGestureSwallowsTap = true;
        if (sample.Pointer is { } pointer)
            press.Canvas.CapturePointer(pointer);
        UpdateHoverOverlay(press.Canvas, null);
        ParkFocusForSelection();
        return true;
    }

    /// <summary>The release half: a drag ends; a press that never moved was a click, and Tapped has it.</summary>
    private bool EndTextPress(PointerSample sample)
    {
        if (_textPress is not { } press || sample.PointerId != press.PointerId)
            return false;
        _textPress = null;
        if (!press.Dragging)
            return false;
        if (sample.Pointer is { } pointer)
            press.Canvas.ReleasePointerCapture(pointer);
        return true;
    }

    /// <summary>
    /// Which page the pointer is over during a drag, walking from the page the selection's end is
    /// on: a drag can run off its page into the next. In the gap between two pages the point is
    /// the top of the page below.
    /// </summary>
    private (int Page, PdfPoint Point) PagePointUnder(PointerSample sample, int from)
    {
        var count = ViewModel.Pages.Count;
        var index = Math.Clamp(from, 0, Math.Max(0, count - 1));
        for (var guard = 0; guard < count; guard++)
        {
            if (FindPageCanvas(index) is not { } canvas)
                break;
            var p = sample.PositionOn(canvas);
            if (p.Y < 0 && index > 0 && FindPageCanvas(index - 1) is { } above
                && sample.PositionOn(above).Y <= above.ActualHeight)
            {
                index--;
                continue;
            }
            if (p.Y > canvas.ActualHeight && index < count - 1)
            {
                index++;
                continue;
            }
            var clamped = new Point(Math.Clamp(p.X, 0, canvas.ActualWidth), Math.Clamp(p.Y, 0, canvas.ActualHeight));
            return (index, ToPagePoint(clamped));
        }
        return (index, new PdfPoint(0, 0));
    }

    private void ExtendTextSelectionTo(int page, PdfPoint point)
    {
        if (ViewModel.SelectableText is not { } cache)
            return;
        if (cache.Peek(page) is not { } text)
        {
            // A page the drag has only just reached: read it off the UI thread, and the next
            // move extends onto it.
            _ = cache.LoadAsync(page);
            return;
        }
        var caret = text.CaretAt(point) ?? 0;
        ViewModel.TextSelection.ExtendTo(new PdfTextPosition(page, caret));
    }

    /// <summary>A drag held past the top or bottom of the pages scrolls them, by how far past it is.</summary>
    private void AutoScrollForDrag(PointerSample sample)
    {
        var y = sample.PositionOn(PagesScroll).Y;
        var over = y < 0 ? y : y > PagesScroll.ViewportHeight ? y - PagesScroll.ViewportHeight : 0;
        if (over != 0)
            PagesScroll.ChangeView(null, PagesScroll.VerticalOffset + over, null, disableAnimation: true);
    }

    /// <summary>
    /// A double-click on the document's own text selects the word (#710). The first click of the
    /// pair has already gone to the page as an ordinary click and started opening the editor on
    /// that line: that click is taken back — the editor's open is stopped, or the editor closed
    /// if it opened and nothing was typed — and the word is selected instead. On an added text
    /// box a double-click still edits it (<see cref="OnPageDoubleTapped"/>), and a double-click
    /// inside an editor that was already open is the editor's own.
    /// </summary>
    private void OnPageDoubleTappedForWord(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not PageCanvas canvas || canvas.DataContext is not PageView pageView
            || e.PointerDeviceType == PointerDeviceType.Touch)
            return;
        if (TrySelectWordAt(pageView, ToPagePoint(e.GetPosition(canvas))))
            e.Handled = true;
    }

    private bool TrySelectWordAt(PageView pageView, PdfPoint point)
    {
        if (ViewModel.SelectableText is not { } cache)
            return false;
        if (!pageView.Regions.Any(r => r.Kind == PageHitKind.TextRun && r.Bounds.Contains(point))
            || pageView.Regions.Any(r => r.Kind != PageHitKind.TextRun && r.Bounds.Contains(point)))
            return false;

        var tookTheClickBack = _lastPageTap is { } tap && tap.Page == pageView.Index
                               && Environment.TickCount64 - tap.At <= GetDoubleClickTime() + 100;
        if (!tookTheClickBack && _activeEditor is not null)
            return false;
        if (tookTheClickBack)
        {
            _pageTapGeneration++;
            if (_activeEditor is { } editor)
            {
                if (editor.Text != _activeEditorInitialText)
                    return false; // something was typed: the editor keeps the double-click
                _activeEditorKeyAction?.Invoke(VirtualKey.Escape, false);
            }
        }

        PdfSelectableText text;
        try
        {
            text = cache.Peek(pageView.Index) ?? cache.Get(pageView.Index);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        if (text.WordAt(point, tolerance: 2) is not { } word)
            return false;
        ViewModel.TextSelection.Select(new PdfTextPosition(pageView.Index, word.Start),
                                       new PdfTextPosition(pageView.Index, word.End));
        ParkFocusForSelection();
        return true;
    }

    /// <summary>
    /// Keys go to the pages once something is selected — Shift+arrows, Escape, Ctrl+C — but only
    /// from where focus would otherwise sit doing nothing; an editor or the find box keeps it.
    /// </summary>
    private void ParkFocusForSelection()
    {
        if (_activeEditor is null && !IsPagesAreaFocused() && !IsTextControlFocused())
            ParkFocusOnPages();
    }

    private bool IsTextControlFocused() =>
        XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox;

    // --- Keys and commands ---

    /// <summary>Whether there is selected text in this tab.</summary>
    internal bool HasTextSelection => !ViewModel.TextSelection.IsEmpty;

    /// <summary>
    /// The page's own selection keys, from this view's PreviewKeyDown: Escape lets go, and
    /// Shift+Left/Right move the selection's end. False is "not mine".
    /// </summary>
    private bool HandleTextSelectionKey(VirtualKey key)
    {
        if (ViewModel.TextSelection.Anchor is null || _activeEditor is not null || _selection is not null
            || IsTextControlFocused() || DialogGate.IsShowing)
            return false;
        switch (key)
        {
            case VirtualKey.Escape:
                ViewModel.TextSelection.Clear();
                return true;
            case VirtualKey.Left or VirtualKey.Right when IsShiftDown():
                StepTextSelection(key == VirtualKey.Left ? -1 : +1);
                return true;
            default:
                return false;
        }
    }

    private void StepTextSelection(int delta)
    {
        if (ViewModel.SelectableText is not { } cache)
            return;
        int Length(int page)
        {
            try
            {
                return cache.Get(page).Length;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                return 0;
            }
        }
        ViewModel.TextSelection.Step(delta, ViewModel.Pages.Count, Length);
        if (ViewModel.TextSelection.Focus is { } focus)
            EnsurePageVisible(focus.PageIndex);
    }

    private void EnsurePageVisible(int page)
    {
        if (FindPageCanvas(page) is not { } canvas)
            return;
        var top = canvas.TransformToVisual(PagesScroll).TransformPoint(new Point(0, 0)).Y;
        if (top > PagesScroll.ViewportHeight || top + canvas.ActualHeight < 0)
            PagesScroll.ChangeView(null, PagesScroll.VerticalOffset + top - 24, null, disableAnimation: true);
    }

    /// <summary>Ctrl+A on the pages: the whole document.</summary>
    internal bool SelectAllText()
    {
        if (!ViewModel.IsDocumentOpen || ViewModel.SelectableText is null || _activeEditor is not null)
            return false;
        Deselect();
        ViewModel.TextSelection.SelectAll(ViewModel.Pages.Count);
        ParkFocusForSelection();
        return true;
    }

    /// <summary>
    /// Ctrl+C and the menu's Copy. The text goes on the clipboard with CR LF line ends. On a
    /// document that asked not to be copied from, nothing is copied and the notice says why.
    /// </summary>
    internal async Task<bool> CopySelectedTextAsync()
    {
        if (ViewModel.TextSelection.IsEmpty)
            return false;
        if (!ViewModel.CanCopyText)
        {
            ViewModel.IsCopyWithheldNoticeOpen = true;
            Announce(Strings.CopyWithheld);
            return false;
        }
        if (await ViewModel.ReadSelectedTextAsync() is not { Length: > 0 } text)
            return false;
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(PdfTextSelection.WithLineEnds(text, "\r\n"));
        try
        {
            Clipboard.SetContent(package);
            // Kept after MegaPDF closes, as a copy from any other app is.
            Clipboard.Flush();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            // Another app holds the clipboard open; the copy simply did not happen.
            return false;
        }
        return true;
    }

    /// <summary>The page's right-click menu: Copy and Select all, and why Copy is off when the document asks.</summary>
    private MenuFlyout TextContextMenu()
    {
        if (_textMenu is { } built)
            return built;
        var copy = new MenuFlyoutItem
        {
            Text = Strings.TextCopy,
            Icon = new SymbolIcon(Symbol.Copy),
            KeyboardAcceleratorTextOverride = "Ctrl+C",
        };
        AutomationPropertiesSetId(copy, "TextCopyItem");
        copy.Click += async (_, _) => await CopySelectedTextAsync();
        var selectAll = new MenuFlyoutItem
        {
            Text = Strings.TextSelectAll,
            Icon = new SymbolIcon(Symbol.SelectAll),
            KeyboardAcceleratorTextOverride = "Ctrl+A",
        };
        AutomationPropertiesSetId(selectAll, "TextSelectAllItem");
        selectAll.Click += (_, _) => SelectAllText();
        // A disabled item rather than a tooltip: WinUI shows no tooltip on a disabled item, and
        // the reason belongs beside the thing it explains.
        var reason = new MenuFlyoutItem { Text = Strings.CopyWithheld, IsEnabled = false };
        AutomationPropertiesSetId(reason, "TextCopyWithheldItem");

        var menu = new MenuFlyout();
        menu.Items.Add(copy);
        menu.Items.Add(selectAll);
        menu.Items.Add(reason);
        menu.Opening += (_, _) => UpdateTextMenu();
        _textMenu = menu;
        _textMenuCopy = copy;
        _textMenuReason = reason;
        return menu;
    }

    private void UpdateTextMenu()
    {
        if (_textMenuCopy is null || _textMenuReason is null)
            return;
        var withheld = !ViewModel.CanCopyText && ViewModel.IsDocumentOpen;
        _textMenuCopy.IsEnabled = HasTextSelection && !withheld;
        _textMenuReason.Visibility = withheld ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void AutomationPropertiesSetId(DependencyObject item, string id) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, id);

    // --- Drawing ---

    /// <summary>Wires a page canvas as it arrives: the word double-click, the menu, and its text read ahead.</summary>
    private void PrepareCanvasForText(PageCanvas canvas, PageView pageView)
    {
        _wordDoubleTapHandler ??= OnPageDoubleTappedForWord;
        canvas.RemoveHandler(DoubleTappedEvent, _wordDoubleTapHandler);
        // handledEventsToo: the editor a first click opened sits over the line and takes the
        // second click, and the double-click still belongs to the page (see OnPageDoubleTappedForWord).
        canvas.AddHandler(DoubleTappedEvent, _wordDoubleTapHandler, handledEventsToo: true);
        canvas.ContextFlyout = TextContextMenu();
        // Read the page's text as it is shown, off the UI thread, so a drag never waits for it.
        if (pageView.Source is not null && ViewModel.SelectableText is { } cache && cache.Peek(pageView.Index) is null)
        {
            var index = pageView.Index;
            _ = LoadThenDrawAsync(cache, index);
        }
        else
        {
            DrawTextSelection(pageView.Index);
        }
    }

    private async Task LoadThenDrawAsync(SelectableTextCache cache, int pageIndex)
    {
        await cache.LoadAsync(pageIndex);
        if (ReferenceEquals(cache, ViewModel.SelectableText))
            DrawTextSelection(pageIndex);
    }

    /// <summary>The system's text selection colour, translucent: the page is a picture, and the words must show through.</summary>
    private static Brush SelectionBrush()
    {
        var colour = Application.Current.Resources.TryGetValue("TextControlSelectionHighlightColor", out var value) && value is SolidColorBrush brush
            ? brush.Color
            : new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        return new SolidColorBrush(colour) { Opacity = 0.4 };
    }

    /// <summary>Redraws one page's share of the selection, in DIPs over its raster.</summary>
    private void DrawTextSelection(int pageIndex)
    {
        if (FindPageCanvas(pageIndex) is not { } canvas)
            return;
        foreach (var stale in canvas.Children.OfType<Rectangle>().Where(r => r.Tag as string == TextSelectionTag).ToList())
            canvas.Children.Remove(stale);

        if (ViewModel.SelectableText is not { } cache || ViewModel.TextSelection.Pages is not { } pages
            || pageIndex < pages.First || pageIndex > pages.Last)
            return;
        if (cache.Peek(pageIndex) is not { } text)
        {
            if (ViewModel.Pages[pageIndex].Source is not null)
                _ = LoadThenDrawAsync(cache, pageIndex);
            return;
        }
        if (ViewModel.TextSelection.RangeOn(pageIndex, text.Length) is not { } range)
            return;

        var scale = 96.0 / 72 * ViewModel.ZoomFactor;
        var fill = SelectionBrush();
        foreach (var r in text.GetRects(range))
        {
            canvas.Children.Add(new Rectangle
            {
                Tag = TextSelectionTag,
                Fill = fill,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(r.X * scale, r.Y * scale, 0, 0),
                Width = Math.Max(1, r.Width * scale),
                Height = Math.Max(1, r.Height * scale),
            });
        }
    }

    // --- System metrics ---

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private const int SmCxDrag = 68;
    private const int SmCyDrag = 69;

    /// <summary>Windows' drag threshold, in DIPs: the distance a press may wander and still be a click.</summary>
    private (double X, double Y) DragThreshold()
    {
        var scale = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1.0;
        var x = GetSystemMetrics(SmCxDrag);
        var y = GetSystemMetrics(SmCyDrag);
        return ((x > 0 ? x : 4) / scale, (y > 0 ? y : 4) / scale);
    }

    // --- For the self-test (#710) ---

    /// <summary>
    /// A press at <paramref name="from"/>, moves along the way to <paramref name="to"/> (which may
    /// be on another page) and a release, through the very press, move and release handlers a
    /// mouse drives — only the pointer event is synthetic, since WinUI cannot make one. Points are
    /// page points. True when the gesture became a selection.
    /// </summary>
    internal bool DragForTest(int fromPage, PdfPoint from, int toPage, PdfPoint to, bool shift = false, int steps = 8)
    {
        if (FindPageCanvas(fromPage) is not { } start || FindPageCanvas(toPage) is not { } end)
            return Refuse("the page is not on screen");
        var dipPerPoint = 1 / PointsPerDip;
        Point Dip(PdfPoint p) => new(p.X * dipPerPoint, p.Y * dipPerPoint);
        PointerSample At(PageCanvas canvas, Point dip, bool pressed) => new(
            element => canvas.TransformToVisual(element).TransformPoint(dip), 1, pressed, false, null);

        _textGestureSwallowsTap = false;
        if (!TryBeginTextPress(start, At(start, Dip(from), true), shift, out _))
            return false;
        var startDip = Dip(from);
        var endInStart = end.TransformToVisual(start).TransformPoint(Dip(to));
        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var dip = new Point(startDip.X + (endInStart.X - startDip.X) * t, startDip.Y + (endInStart.Y - startDip.Y) * t);
            TrackTextPress(start, At(start, dip, true));
        }
        var dragged = EndTextPress(At(end, Dip(to), false));
        return dragged;
    }

    /// <summary>A plain click as a mouse makes it — press, no movement, release, then the Tapped — for the self-test.</summary>
    internal async Task ClickForTest(int pageIndex, PdfPoint point)
    {
        if (FindPageCanvas(pageIndex) is not { } canvas)
            return;
        var dip = new Point(point.X / PointsPerDip, point.Y / PointsPerDip);
        PointerSample At(bool pressed) => new(element => canvas.TransformToVisual(element).TransformPoint(dip), 1, pressed, false, null);
        _textGestureSwallowsTap = false;
        TryBeginTextPress(canvas, At(true), shift: false, out _);
        TrackTextPress(canvas, At(true));
        EndTextPress(At(false));
        await HandlePageTapAsync(canvas, ViewModel.Pages[pageIndex], point);
    }

    /// <summary>The second click of a double-click on a word, as the double-tap handler takes it, for the self-test.</summary>
    internal bool DoubleClickForTest(int pageIndex, PdfPoint point) =>
        ViewModel.Pages.ElementAtOrDefault(pageIndex) is { } pageView && TrySelectWordAt(pageView, point);

    /// <summary>A key the page's selection answers (Escape), through the same decision the real key takes.</summary>
    internal bool PressSelectionKeyForTest(VirtualKey key) => HandleTextSelectionKey(key);

    /// <summary>Whether the Tapped a release raises now would be swallowed as the end of a selection gesture.</summary>
    internal bool NextTapIsSwallowedForTest => _textGestureSwallowsTap;

    internal void ScrollPagesToTopForTest() => PagesScroll.ChangeView(null, 0, null, disableAnimation: true);

    /// <summary>Whether the page has a canvas in the window yet, for the self-test.</summary>
    internal bool HasPageCanvasForTest(int pageIndex) => FindPageCanvas(pageIndex) is { ActualHeight: > 0 };

    /// <summary>How many highlight rectangles a page shows, for the self-test.</summary>
    internal int SelectionRectsShownForTest(int pageIndex) =>
        FindPageCanvas(pageIndex)?.Children.OfType<Rectangle>().Count(r => r.Tag as string == TextSelectionTag) ?? 0;

    /// <summary>The context menu's Copy as it would be shown now: enabled, and whether the reason is showing.</summary>
    internal (bool CopyEnabled, bool ReasonShown) TextMenuStateForTest()
    {
        TextContextMenu();
        UpdateTextMenu();
        return (_textMenuCopy!.IsEnabled, _textMenuReason!.Visibility == Visibility.Visible);
    }

    internal bool StepTextSelectionForTest(int delta)
    {
        if (ViewModel.TextSelection.Anchor is null)
            return false;
        StepTextSelection(delta);
        return true;
    }
}
