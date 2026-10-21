using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using MegaPDF.Avalonia.Rendering;
using MegaPDF.Avalonia.ViewModels;
using MegaPDF.Core.Engine;
using MegaPDF.Core.Viewing;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// Selecting a document's text and copying it (#710), on the Mac and on Linux, with no tool to
/// pick first. The same rules as the Windows app, each in this platform's idiom.
///
/// A click on a line still opens the editor. Only a press that travels past the drag threshold
/// becomes a selection, so the editor now opens as the click is released rather than as it is
/// pressed: a press cannot know yet whether it is a click. Everything else a press does is done
/// on the press, exactly as before — a checkbox, a field, a stamp, a box, a cover, a redaction
/// mark, an armed tool, and a selected object's chrome, which drags itself.
///
/// The rest: double-click selects a word on the document's own text (on an added text box it
/// still edits); Shift+click and Shift+drag extend; Shift+Left/Right move the selection's end a
/// character; ⌘A / Ctrl+A selects the whole document, as Preview does; ⌘C / Ctrl+C, Edit ▸ Copy
/// on the Mac and the page's context menu copy; Escape lets go. The clipboard gets LF line ends,
/// these platforms' own, which is what the core already gives.
///
/// A document whose author asked that nothing be copied out of it still selects. Copy is greyed
/// in the Edit menu and the context menu, the context menu says why, and so does the status line
/// as a selection starts and whenever Copy is asked for.
/// </summary>
public partial class MainWindow
{
    /// <summary>A left press on a page that may yet become a selection, or already has.</summary>
    private sealed class TextPress(Control container, PageViewModel page, Point start, PdfPoint startPoint, IPointer pointer)
    {
        public Control Container { get; } = container;
        public PageViewModel Page { get; } = page;
        public Point Start { get; } = start;
        public PdfPoint StartPoint { get; } = startPoint;
        public IPointer Pointer { get; } = pointer;
        public bool Dragging { get; set; }
    }

    private TextPress? _textPress;

    /// <summary>When the last click on a page landed (milliseconds) and on which page: the double-click takes it back.</summary>
    private (long At, int Page)? _lastPageClick;

    /// <summary>A double-click took back the click before it while that click's line check was still running.</summary>
    private (long At, int Page)? _lineEditTakenBack;

    /// <summary>
    /// How far a press may wander and still be a click, in DIPs. Neither platform hands Avalonia
    /// its own: AppKit starts a drag at about 3 points, and GTK's default gtk-dnd-drag-threshold
    /// is 8 pixels.
    /// </summary>
    private static double DragThreshold => OperatingSystem.IsMacOS() ? 3 : 8;

    /// <summary>The longest gap between the two clicks of a double-click, as the platform sets it.</summary>
    private TimeSpan DoubleClickTime =>
        TopLevel.GetTopLevel(this)?.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse) ?? TimeSpan.FromMilliseconds(500);

    private void WireTextSelection()
    {
        ApplyTextSelectionFill();
        if (PlatformSettings is { } settings)
            settings.ColorValuesChanged += (_, _) => ApplyTextSelectionFill();
    }

    /// <summary>
    /// The selection's fill: the platform's accent — macOS and the Linux desktops derive their own
    /// text selection colour from it — translucent, so the page's words show through.
    /// </summary>
    private void ApplyTextSelectionFill()
    {
        var accent = PlatformSettings?.GetColorValues().AccentColor1 is { A: > 0 } platform
            ? platform
            : this.TryFindResource("BrandSystemAccentColor", out var brand) && brand is Color c ? c : Color.Parse("#0E6FD8");
        Resources["TextSelectionFill"] = new SolidColorBrush(accent, 0.38);
    }

    /// <summary>A page container arrived on screen: its text is read ahead, its share of the selection drawn, its menu given.</summary>
    private void PrepareContainerForText(Control container, PageViewModel page)
    {
        container.RemoveHandler(PointerPressedEvent, OnPagePreviewPointerPressed);
        container.AddHandler(PointerPressedEvent, OnPagePreviewPointerPressed, RoutingStrategies.Tunnel);
        container.ContextMenu ??= BuildTextContextMenu();
        if (Active is { SelectableText: { } cache } vm && cache.Peek(page.Index) is null)
        {
            var index = page.Index;
            _ = cache.LoadAsync(index).ContinueWith(_ => global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(Active, vm))
                    vm.ShowTextSelectionOn(index);
            }), TaskScheduler.Default);
        }
        else
        {
            Active?.ShowTextSelectionOn(page.Index);
        }
    }

    // --- The pointer ---

    /// <summary>
    /// The press half. True when this press is the selection's to watch: a plain one defers the
    /// click to its release (<see cref="EndTextPress"/>), and a Shift one extends at once.
    /// </summary>
    private bool TryBeginTextPress(Control container, PageViewModel page, Point position, PdfPoint pagePoint,
                                   IPointer pointer, KeyModifiers modifiers, bool readingMode)
    {
        _textPress = null;
        if (Active is not { SelectableText: { } cache } vm || pointer.Type == PointerType.Touch)
            return false;
        if (!readingMode)
        {
            if (vm.Mode != DocumentViewModel.PageMode.Select || vm.IsPlacingSignature || vm.Selection is not null)
                return false;
            // A press on something clickable that is not the document's text keeps its click.
            var kind = vm.HitTest(page.Index, pagePoint).Kind;
            if (kind is not (PageHitKind.TextRun or PageHitKind.None))
                return false;
        }

        _textPress = new TextPress(container, page, position, pagePoint, pointer);
        pointer.Capture(container);
        if (modifiers.HasFlag(KeyModifiers.Shift) && vm.TextSelection.Anchor is not null)
        {
            _textPress.Dragging = true;
            ExtendTextSelectionTo(vm, cache, page.Index, pagePoint);
        }
        return true;
    }

    /// <summary>The move half: past the threshold the press becomes a selection, which then follows the pointer.</summary>
    private bool TrackTextPress(PointerEventArgs e)
    {
        if (_textPress is not { } press || !ReferenceEquals(e.Pointer, press.Pointer)
            || Active is not { SelectableText: { } cache } vm)
            return false;
        if (!press.Dragging)
        {
            var at = e.GetPosition(SurfaceOf(press.Container));
            if (Math.Abs(at.X - press.Start.X) <= DragThreshold && Math.Abs(at.Y - press.Start.Y) <= DragThreshold)
                return true; // still a click; nothing else gets this move
            PdfSelectableText text;
            try
            {
                text = cache.Peek(press.Page.Index) ?? cache.Get(press.Page.Index);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                _textPress = null;
                return false;
            }
            DismissInlineEditor();
            vm.TextSelection.Begin(new PdfTextPosition(press.Page.Index, text.CaretAt(press.StartPoint) ?? 0));
            press.Dragging = true;
        }
        var (page, point) = PagePointUnder(e, vm.TextSelection.Focus?.PageIndex ?? press.Page.Index);
        ExtendTextSelectionTo(vm, cache, page, point);
        AutoScrollForDrag(e);
        press.Container.Cursor = new Cursor(StandardCursorType.Ibeam);
        return true;
    }

    /// <summary>
    /// The release half. A drag ends where it is. A press that never moved was a click: the
    /// selection is let go of and the click does what a click on the page always did.
    /// </summary>
    private bool EndTextPress(PointerReleasedEventArgs e)
    {
        if (_textPress is not { } press || !ReferenceEquals(e.Pointer, press.Pointer))
            return false;
        _textPress = null;
        e.Pointer.Capture(null);
        if (press.Dragging || Active is not { } vm)
            return true;
        vm.TextSelection.Clear();
        if (!IsReadingMode)
        {
            _lastPageClick = (Environment.TickCount64, press.Page.Index);
            vm.HandlePageClick(press.Page.Index, press.StartPoint);
        }
        return true;
    }

    private void CancelTextPress()
    {
        _textPress?.Pointer.Capture(null);
        _textPress = null;
    }

    private void ExtendTextSelectionTo(DocumentViewModel vm, SelectableTextCache cache, int page, PdfPoint point)
    {
        if (cache.Peek(page) is not { } text)
        {
            // A page the drag has only just reached: read off the UI thread; the next move extends onto it.
            _ = cache.LoadAsync(page);
            return;
        }
        vm.TextSelection.ExtendTo(new PdfTextPosition(page, text.CaretAt(point) ?? 0));
    }

    /// <summary>
    /// Which page the pointer is over during a drag, walking from the page the selection's end is
    /// on — only pages on screen have a container, and a drag only reaches pages on screen. In the
    /// gap between two pages the point is the top of the page below.
    /// </summary>
    private (int Page, PdfPoint Point) PagePointUnder(PointerEventArgs e, int from)
    {
        var vm = Active!;
        var index = from;
        for (var guard = 0; guard < vm.Pages.Count; guard++)
        {
            if (PageContainer(index) is not { } container)
                break;
            var surface = SurfaceOf(container);
            var p = e.GetPosition(surface);
            var height = surface.Bounds.Height;
            if (p.Y < 0 && PageContainer(index - 1) is { } above && e.GetPosition(SurfaceOf(above)).Y <= SurfaceOf(above).Bounds.Height)
            {
                index--;
                continue;
            }
            if (p.Y > height && PageContainer(index + 1) is not null)
            {
                index++;
                continue;
            }
            var dipToPoint = 1.0 / (PageBitmap.PointsToPixels * vm.Zoom);
            var x = Math.Clamp(p.X, 0, surface.Bounds.Width);
            var y = Math.Clamp(p.Y, 0, height);
            return (index, new PdfPoint(x * dipToPoint, y * dipToPoint));
        }
        return (from, new PdfPoint(0, 0));
    }

    private Control? PageContainer(int pageIndex) =>
        pageIndex >= 0 && PageList.ContainerFromIndex(pageIndex) is { DataContext: PageViewModel page } container && page.Index == pageIndex
            ? container
            : pageIndex >= 0 ? ContainerFor(pageIndex) : null;

    /// <summary>A drag held past the top or bottom of the pages scrolls them, by how far past it is.</summary>
    private void AutoScrollForDrag(PointerEventArgs e)
    {
        var y = e.GetPosition(PageScroller).Y;
        var height = PageScroller.Bounds.Height;
        var over = y < 0 ? y : y > height ? y - height : 0;
        if (over != 0)
            PageScroller.Offset = PageScroller.Offset.WithY(Math.Max(0, PageScroller.Offset.Y + over));
    }

    /// <summary>
    /// The second press of a double-click, seen on its way down — before an editor the first
    /// click opened can take it. On the document's own text it selects the word, and takes back
    /// the first click: the editor it opened goes, or the check it started opens nothing. On an
    /// added text box, or inside an editor that was already open, it is left alone.
    /// </summary>
    private void OnPagePreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount < 2 || sender is not Control container || container.DataContext is not PageViewModel page
            || Active is not { } vm || e.Pointer.Type == PointerType.Touch
            || !e.GetCurrentPoint(container).Properties.IsLeftButtonPressed)
            return;
        var position = e.GetPosition(SurfaceOf(container));
        var dipToPoint = 1.0 / (PageBitmap.PointsToPixels * vm.Zoom);
        if (TrySelectWordAt(vm, page, new PdfPoint(position.X * dipToPoint, position.Y * dipToPoint)))
        {
            CancelTextPress();
            e.Handled = true;
        }
    }

    private bool TrySelectWordAt(DocumentViewModel vm, PageViewModel page, PdfPoint point)
    {
        if (vm.SelectableText is not { } cache || page.KindAt(point) != PageHitKind.TextRun)
            return false;
        var takesTheClickBack = _lastPageClick is { } click && click.Page == page.Index
                                && Environment.TickCount64 - click.At <= DoubleClickTime.TotalMilliseconds + 100;
        if (_inlineEditor is { } editor)
        {
            // An editor that was already open keeps its double-click, as does one already typed in.
            if (!takesTheClickBack || editor.Text != InlineEditorStartText)
                return false;
            DismissInlineEditor();
        }
        else if (takesTheClickBack)
        {
            _lineEditTakenBack = (Environment.TickCount64, page.Index);
        }

        PdfSelectableText text;
        try
        {
            text = cache.Peek(page.Index) ?? cache.Get(page.Index);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        if (text.WordAt(point, tolerance: 2) is not { } word)
            return false;
        vm.TextSelection.Select(new PdfTextPosition(page.Index, word.Start), new PdfTextPosition(page.Index, word.End));
        return true;
    }

    /// <summary>
    /// Whether the line editor about to open was asked for by a click a double-click has since
    /// taken back. Asked by <see cref="ShowLineEditor"/>; true once, then forgotten.
    /// </summary>
    private bool LineEditWasTakenBack(int pageIndex)
    {
        if (_lineEditTakenBack is not { } taken)
            return false;
        _lineEditTakenBack = null;
        return taken.Page == pageIndex && Environment.TickCount64 - taken.At < 5000;
    }

    // --- Keys and commands ---

    /// <summary>Escape and Shift+Left/Right on selected text; false is "not mine".</summary>
    private bool HandleTextSelectionKey(KeyEventArgs e)
    {
        if (Active is not { } vm || vm.TextSelection.Anchor is null || _inlineEditor is not null
            || FindBox.IsFocused || vm.Selection is not null || FocusManager?.GetFocusedElement() is TextBox)
            return false;
        switch (e.Key)
        {
            case Key.Escape when e.KeyModifiers == KeyModifiers.None:
                vm.TextSelection.Clear();
                return true;
            case Key.Left or Key.Right when e.KeyModifiers == KeyModifiers.Shift:
                StepTextSelection(vm, e.Key == Key.Left ? -1 : +1);
                return true;
            default:
                return false;
        }
    }

    private static void StepTextSelection(DocumentViewModel vm, int delta)
    {
        if (vm.SelectableText is not { } cache)
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
        vm.TextSelection.Step(delta, vm.Pages.Count, Length);
    }

    /// <summary>⌘C / Ctrl+C and Copy: a text field that has focus copies its own text; otherwise the page's selection.</summary>
    internal async Task CopyCommandAsync()
    {
        switch (FocusManager?.GetFocusedElement())
        {
            case TextBox box:
                box.Copy();
                return;
            case SelectableTextBlock block:
                block.Copy();
                return;
        }
        await CopySelectedTextAsync();
    }

    /// <summary>⌘A / Ctrl+A and Select All: a focused text field selects its own text; otherwise the whole document.</summary>
    internal void SelectAllCommand()
    {
        switch (FocusManager?.GetFocusedElement())
        {
            case TextBox box:
                box.SelectAll();
                return;
            case SelectableTextBlock block:
                block.SelectAll();
                return;
        }
        SelectAllText();
    }

    internal bool SelectAllText()
    {
        if (Active is not { IsDocumentOpen: true, SelectableText: not null } vm || _inlineEditor is not null)
            return false;
        vm.ClearSelection();
        vm.TextSelection.SelectAll(vm.Pages.Count);
        return true;
    }

    /// <summary>Copies the page's selection, LF line ends; on a document that asked not to be copied from, says why instead.</summary>
    internal async Task<bool> CopySelectedTextAsync()
    {
        if (Active is not { } vm || !vm.MayCopySelection())
            return false;
        if (await vm.ReadSelectedTextAsync() is not { Length: > 0 } text || Clipboard is not { } clipboard)
            return false;
        await clipboard.SetTextAsync(PdfTextSelection.WithLineEnds(text, "\n"));
        return true;
    }

    private ContextMenu BuildTextContextMenu()
    {
        var copy = new MenuItem { Header = Strings.TextCopy, InputGesture = new KeyGesture(Key.C, CommandModifier) };
        copy.Click += async (_, _) => await CopySelectedTextAsync();
        var selectAll = new MenuItem { Header = Strings.TextSelectAll, InputGesture = new KeyGesture(Key.A, CommandModifier) };
        selectAll.Click += (_, _) => SelectAllText();
        // A disabled row rather than a tooltip: it is read where the greyed Copy is, by sight and by
        // a screen reader alike.
        var reason = new MenuItem { Header = Strings.CopyWithheld, IsEnabled = false };
        var menu = new ContextMenu { ItemsSource = new[] { copy, selectAll, reason } };
        menu.Opening += (_, _) => UpdateTextMenu(menu);
        return menu;
    }

    /// <summary>The context menu as it should read now: Copy on only with a selection a document lets go, and why not.</summary>
    private (bool CopyEnabled, bool ReasonShown) UpdateTextMenu(ContextMenu menu)
    {
        if (menu.ItemsSource is not MenuItem[] { Length: 3 } items)
            return (false, false);
        var (copy, selectAll, reason) = (items[0], items[1], items[2]);
        var vm = Active;
        var withheld = vm is { IsDocumentOpen: true, CanCopyText: false };
        copy.IsEnabled = vm is { HasTextSelection: true } && !withheld;
        selectAll.IsEnabled = vm is { IsDocumentOpen: true };
        reason.IsVisible = withheld;
        return (copy.IsEnabled, reason.IsVisible);
    }

    // --- For the self-test (#710) ---

    /// <summary>
    /// A press, moves to <paramref name="to"/> (possibly on another page) and a release, as real
    /// headless pointer input would deliver them, expressed in page points. Returns the window
    /// points used, so the caller can send them with <c>HeadlessWindowExtensions</c>.
    /// </summary>
    internal Point? WindowPointForTest(int pageIndex, PdfPoint point)
    {
        if (Active is not { } vm || PageContainer(pageIndex) is not { } container)
            return null;
        var dip = PageBitmap.PointsToPixels * vm.Zoom;
        return SurfaceOf(container).TranslatePoint(new Point(point.X * dip, point.Y * dip), this);
    }

    /// <summary>Whether a press is being watched as a possible selection, for the self-test.</summary>
    internal bool HasTextPressForTest => _textPress is not null;

    /// <summary>The context menu's Copy and its reason as they would show now, for the self-test.</summary>
    internal (bool CopyEnabled, bool ReasonShown) TextMenuStateForTest() =>
        PageContainer(0)?.ContextMenu is { } menu ? UpdateTextMenu(menu) : (false, false);

    /// <summary>The second click of a double-click on a word, through the same decision the real one takes.</summary>
    internal bool DoubleClickWordForTest(int pageIndex, PdfPoint point) =>
        Active is { } vm && vm.Pages.FirstOrDefault(p => p.Index == pageIndex) is { } page && TrySelectWordAt(vm, page, point);

    /// <summary>A selection key (Escape, Shift+Left/Right) through the same decision the real one takes.</summary>
    internal bool PressSelectionKeyForTest(Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        HandleTextSelectionKey(new KeyEventArgs { Key = key, KeyModifiers = modifiers, RoutedEvent = KeyDownEvent });
}
