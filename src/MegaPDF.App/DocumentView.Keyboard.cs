using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MegaPDF.App;

/// <summary>
/// Driving this tab's page from the keyboard (SDD §2.2 — required, #2). Moved from
/// <c>MainWindow.Keyboard.cs</c> (#348 phase 1) — per tab, because the page focus, the
/// selection and the pages scroller all are. The pieces that stayed on the window (the
/// toolbar buttons a Tab walk falls off the end into, whether focus is currently in the
/// toolbar) are reached through <see cref="DocumentView.FocusOpenButton"/> and
/// <see cref="FocusIsInToolbar"/>.
/// </summary>
public sealed partial class DocumentView
{
    private static readonly Windows.UI.ViewManagement.UISettings SystemUiSettings = new();

    private PageFocusRing? _focusRing;
    private FocusNavigationDirection _pagesFocusDirection = FocusNavigationDirection.None;
    private bool _parkingFocus;

    /// <summary>MainWindow's hook: whether keyboard focus is currently on a toolbar control.</summary>
    internal Func<bool>? FocusIsInToolbar { get; set; }

    private void InitializePageKeyboard()
    {
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DocumentViewModel.PageFocus))
                UpdateFocusRing();
        };
        ViewModel.Pages.CollectionChanged += OnPagesChangedForFocus;
        ViewModel.Announced += text => DispatcherQueue.TryEnqueue(() => Announce(text));

        // #169: keyboard focus left on a toolbar button must not stay live behind work on
        // the page, a busy state or a dialog.
        PagesScroll.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPagesPointerPressedForFocus), handledEventsToo: true);
    }

    /// <summary>
    /// A click on the page takes keyboard focus off the toolbar (#169). Focus used to
    /// stay on the last toolbar button used, so a later Space or Enter pressed that
    /// button: after a whiteout, Space on "the page" undid it. Focus goes to the pages
    /// scroller, programmatically so no ring is drawn and no region is picked, and only
    /// from the toolbar: an inline editor or the find box keeps it, since taking it
    /// would turn a click that commits an edit into commit-and-click.
    /// </summary>
    private void OnPagesPointerPressedForFocus(object sender, PointerRoutedEventArgs e)
    {
        if (_activeEditor is not null || FocusIsInToolbar is not { } isInToolbar || !isInToolbar())
            return;
        // After the press is handled, and after a light-dismissed menu has put focus back on its button.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_activeEditor is null && (FocusIsInToolbar?.Invoke() ?? false))
                ParkFocusOnPages();
        });
    }

    /// <summary>MainWindow's toolbar-losing-focus handler: park focus on this tab's pages instead.</summary>
    internal bool ParkFocusFromToolbar(LosingFocusEventArgs args)
    {
        _parkingFocus = true;
        if (!args.TrySetNewFocusedElement(PagesScroll))
        {
            _parkingFocus = false;
            return false;
        }
        return true;
    }

    internal void ParkFocusOnPages()
    {
        _parkingFocus = true;
        if (!PagesScroll.Focus(FocusState.Programmatic))
            _parkingFocus = false;
    }

    internal void ClickPagesForTest() => ParkFocusOnPages();

    private static bool IsShiftDown() =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
         & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    /// <summary>Reduced motion (SDD §2.2): no animated scrolling when Windows animations are off.</summary>
    private static bool AnimationsEnabled => SystemUiSettings.AnimationsEnabled;

    private bool IsPagesAreaFocused() =>
        PagesScroll.XamlRoot is { } root && FocusManager.GetFocusedElement(root) == PagesScroll;

    /// <summary>
    /// The page's share of the window's keys. False means "not mine": an inline
    /// editor, the find box or a dialog has focus, or the key means nothing here.
    /// Called from this control's own PreviewKeyDown, which decides before any await, so the
    /// work itself runs after the key has been claimed.
    /// </summary>
    private bool HandlePageKey(VirtualKey key)
    {
        if (!ViewModel.IsDocumentOpen || _activeEditor is not null || !IsPagesAreaFocused() || DialogGate.IsShowing)
            return false;

        switch (key)
        {
            case VirtualKey.Tab:
                _ = StepPageFocusAsync(forward: !IsShiftDown());
                return true;

            // A selected text box edits on Enter — the keyboard's double-click (SDD §3.3).
            case VirtualKey.Enter when _selection is { } selected
                                       && selected.Id.StartsWith("textbox:", StringComparison.Ordinal):
                _ = EditTextBoxAtAsync(selected.Canvas, selected.Page, selected.Bounds.Center);
                return true;

            case VirtualKey.Enter or VirtualKey.Space when ViewModel.PageFocus is not null:
                _ = ActivatePageFocusAsync();
                return true;

            case VirtualKey.Escape when ViewModel.PageFocus is not null:
                ViewModel.ClearPageFocus();
                FocusOpenButton?.Invoke();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Pointer focus on the scroller is refused: a click on the page used to leave
    /// focus where it was (an open editor commits through the tap itself), and
    /// making the scroller take it would turn that commit into commit-and-click.
    /// </summary>
    private void OnPagesGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (args.NewFocusedElement != PagesScroll)
            return;
        if (args.FocusState == FocusState.Pointer)
        {
            args.TryCancel();
            return;
        }
        _pagesFocusDirection = args.Direction;
    }

    /// <summary>Tabbing onto the pages lands straight on a region rather than on the scroller.</summary>
    private async void OnPagesGotFocus(object sender, RoutedEventArgs e)
    {
        // Focus parked here off the toolbar (#169) is not a Tab onto the page: pick no region.
        if (_parkingFocus)
        {
            _parkingFocus = false;
            return;
        }
        if (e.OriginalSource != PagesScroll || PagesScroll.FocusState != FocusState.Keyboard
            || ViewModel.PageFocus is not null || !ViewModel.IsDocumentOpen)
        {
            return;
        }
        var forward = _pagesFocusDirection != FocusNavigationDirection.Previous;
        if (await MovePageFocusAsync(forward) == DocumentViewModel.FocusMove.NothingToFocus)
            Announce(Strings.NothingKeyboardEditable);
    }

    /// <summary>Tab or Shift+Tab on the page; past either end, focus moves on to the next control.</summary>
    private async Task StepPageFocusAsync(bool forward)
    {
        if (await MovePageFocusAsync(forward) == DocumentViewModel.FocusMove.Moved)
            return;

        ViewModel.ClearPageFocus();
        var options = new FindNextElementOptions { SearchRoot = XamlRoot.Content };
        if (!FocusManager.TryMoveFocus(forward ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous, options))
            FocusOpenButton?.Invoke();
    }

    private async Task<DocumentViewModel.FocusMove> MovePageFocusAsync(bool forward)
    {
        // Moving on lets go of a selected signature, keeping a nudge that has not
        // been committed yet — it would otherwise be dropped with the selection.
        if (_selection is not null)
        {
            if (_nudgeTimer is { IsRunning: true })
                await CommitChromeAsync();
            Deselect();
        }

        var result = await ViewModel.MovePageFocusAsync(forward);
        if (result == DocumentViewModel.FocusMove.Moved && ViewModel.PageFocus is { } focus)
        {
            ScrollMatchIntoView(ViewModel.ContentTargetFor(focus.PageIndex, [focus.Bounds]));
            Announce(focus.AccessibleName);
        }
        return result;
    }

    /// <summary>
    /// Enter or Space: the same routing a tap at the region's centre gets, so the
    /// keyboard cannot drift from the mouse as either changes. The page may change
    /// underneath (a ticked box, a placed signature), so the region is re-read after.
    /// </summary>
    private async Task ActivatePageFocusAsync()
    {
        if (ViewModel.PageFocus is not { } focus
            || FindPageCanvas(focus.PageIndex) is not { } canvas
            || canvas.DataContext is not PageView pageView)
        {
            return;
        }

        // Redact is armed: Enter marks the focused region rather than opening it (#173).
        // Marking was drag-only, which left redaction — a privacy feature — out of reach
        // for anyone who does not use a pointer.
        if (ViewModel.IsRedactMode)
        {
            await ViewModel.AddRedactionMarkAsync(focus.PageIndex, focus.Bounds);
            RefreshRedactionOverlay(canvas, pageView);
            Announce(ViewModel.RedactionMarkCount == 1
                ? Strings.RedactMarkCountOne
                : Strings.RedactMarkCount(ViewModel.RedactionMarkCount));
            return;
        }

        await RoutePageActivationAsync(canvas, pageView, focus.Bounds.Center);

        // An editor that opened announces itself by taking focus.
        if (_activeEditor is not null)
            return;
        if (_selection is { } selection)
        {
            Announce(SelectionKeyHint(selection));
            return;
        }
        if (await ViewModel.RereadPageFocusAsync() is { } updated)
            Announce(updated.AccessibleName);
    }

    private static string SelectionKeyHint(StampSelection selection) =>
        selection.Id.StartsWith("textbox:", StringComparison.Ordinal) ? Strings.TextBoxSelectedKeyHint
        : selection.Id.StartsWith("whiteout:", StringComparison.Ordinal) ? Strings.CoverSelectedKeyHint
        : selection.Id.StartsWith("redaction:", StringComparison.Ordinal) ? Strings.RedactMarkSelectedKeyHint
        : Strings.SignatureSelectedKeyHint;

    /// <summary>
    /// After an inline editor opened from the keyboard closes, focus goes back to the
    /// page so the next Tab continues from the same region. A pointer-opened editor
    /// has no page focus and leaves focus alone.
    /// </summary>
    private void ReturnFocusToPage()
    {
        if (ViewModel.PageFocus is not null)
            PagesScroll.Focus(FocusState.Keyboard);
    }

    /// <summary>
    /// Says <paramref name="text"/> through Narrator. The ring never takes focus, so
    /// the move would otherwise go unannounced; raised on the scroller because that
    /// is what holds focus. Internal: <see cref="MainWindow"/> uses this for
    /// toolbar-driven announcements (a tool switched off, a signature placed) that
    /// have no other page to anchor on but the active tab's.
    /// </summary>
    internal void Announce(string text)
    {
        var peer = FrameworkElementAutomationPeer.FromElement(PagesScroll)
                   ?? FrameworkElementAutomationPeer.CreatePeerForElement(PagesScroll);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.ImportantMostRecent,
            text,
            "page-focus");
    }

    /// <summary>
    /// A page's container is rebuilt whenever its slot is replaced — by an edit, a
    /// render or a zoom step — and the ring goes with it. Put it back, and re-read the
    /// region when the replacement carries a fresh map.
    /// </summary>
    private void OnPagesChangedForFocus(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Replace
            || ViewModel.PageFocus is not { } focus || e.NewStartingIndex != focus.PageIndex)
        {
            return;
        }
        var freshMap = e.NewItems?[0] is PageView { Source: not null, IsPreview: false };
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            if (freshMap)
                await ViewModel.RereadPageFocusAsync();
            PagesItems.UpdateLayout();
            UpdateFocusRing();
        });
    }

    /// <summary>
    /// Draws the ring around the focused region. Two strokes in the system focus
    /// colours: a dark outer stroke and a light inner one, so the ring reads on white
    /// paper and on a dark scan alike. The brushes are theme resources, so a
    /// high-contrast theme replaces them with its own focus colours; the ring asks for
    /// the light theme's pair because it sits on paper, which is white in either app
    /// theme. Not a hit target — clicks go to the page beneath.
    /// </summary>
    private void UpdateFocusRing()
    {
        if (_focusRing is not null)
        {
            (_focusRing.Parent as Panel)?.Children.Remove(_focusRing);
            _focusRing = null;
        }
        if (ViewModel.PageFocus is not { } focus || FindPageCanvas(focus.PageIndex) is not { } canvas)
            return;

        const double gap = 3;       // clear of the region's edge, so the ring never hides a tick
        const double minimum = 16;  // a 6 pt box still gets a ring you can see
        var toDip = 96.0 / 72 * ViewModel.ZoomFactor;
        var bounds = focus.Bounds;
        var width = Math.Max(bounds.Width * toDip + 2 * gap, minimum);
        var height = Math.Max(bounds.Height * toDip + 2 * gap, minimum);
        var centerX = (bounds.X + bounds.Width / 2) * toDip;
        var centerY = (bounds.Y + bounds.Height / 2) * toDip;

        var ring = new PageFocusRing
        {
            Width = width,
            Height = height,
            Margin = new Thickness(centerX - width / 2, centerY - height / 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            RequestedTheme = ElementTheme.Light,
        };
        ring.Children.Add(new Border { Style = (Style)Resources["PageFocusRingOuterStyle"] });
        ring.Children.Add(new Border { Style = (Style)Resources["PageFocusRingInnerStyle"] });
        AutomationProperties.SetName(ring, focus.AccessibleName);
        AutomationProperties.SetAutomationId(ring, $"page-{focus.PageIndex}-region-{focus.RegionIndex}");

        canvas.Children.Add(ring);
        _focusRing = ring;
    }
}
