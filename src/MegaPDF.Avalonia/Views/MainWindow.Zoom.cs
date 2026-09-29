using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using MegaPDF.Avalonia.ViewModels;

namespace MegaPDF.Avalonia.Views;

/// <summary>
/// Zoom anchoring (#528). Every platform's audit found the same shape of bug: zoom
/// is a re-layout (<c>PageViewModel.LayoutWidth/Height</c> growing with
/// <see cref="DocumentViewModel.Zoom"/>), the pages panel inside
/// <c>PageScroller</c> grows from its own origin, and nothing moved the scroll
/// offset to compensate — so whatever you were looking at slid out from under you.
/// The Avalonia app had it twice over: menu/toolbar/keyboard zoom lost the focal
/// point, and there was no trackpad pinch or Ctrl+wheel at all, so on the Mac —
/// where trackpad pinch IS how you zoom a document — it read as broken before
/// anchoring was even in question.
///
/// One rule, three anchors, picked by the input:
///  - a trackpad pinch on the Mac anchors on the gesture's centroid (its own
///    fixed anchor point — see the remark on <see cref="OnPageScrollerMagnify"/>);
///  - Ctrl+wheel, the Linux (and Windows) idiom, anchors on the pointer;
///  - the menu, the toolbar and the keyboard have no position to anchor on, so
///    they anchor on the viewport's centre.
///
/// <see cref="ApplyAnchoredZoom"/> is the choke point for the first two: it reads
/// <see cref="DocumentViewModel.Zoom"/> before and after the change it is given,
/// so the ratio it corrects by is whatever the view model actually committed to —
/// clamped at the stops or not — never the ratio the gesture asked for (see
/// <see cref="ZoomAnchor.Reanchor"/>'s own remark on why that distinction is the
/// whole bug). The third anchor — centre — has no gesture to hang a "before" read
/// off, so it is applied from <see cref="OnActiveDocumentPropertyChanged"/>
/// instead, the one place every <see cref="DocumentViewModel.Zoom"/> change is
/// already observed, gated by <see cref="_reanchoringZoom"/> so it does not also
/// re-correct the two paths that already corrected themselves.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Zoom the last time this window saw it change, kept so the centre-anchor
    /// path in <see cref="OnActiveDocumentPropertyChanged"/> has an old value to
    /// correct from — <see cref="System.ComponentModel.PropertyChangedEventArgs"/>
    /// carries no old value of its own. Reset to the incoming tab's own zoom in
    /// <see cref="OnActiveDocumentChanged"/>, the same choke point
    /// <c>_scrollOffsets</c> uses, so switching tabs never corrects by a stale
    /// value left over from whichever tab was active before.
    /// </summary>
    private double _lastKnownZoom = 1.0;

    /// <summary>
    /// True while <see cref="ApplyAnchoredZoom"/> or the guarded call to
    /// <c>FitOnOpen</c> in <c>UpdateViewport</c> is already correcting the offset
    /// for a <see cref="DocumentViewModel.Zoom"/> change it made itself — so the
    /// generic centre-anchor in <see cref="OnActiveDocumentPropertyChanged"/> does
    /// not also run for it. Without this, a wheel step or a pinch would be
    /// corrected twice: once precisely, on the pointer or the centroid, and once
    /// more on the centre, which would drag the page the second time undid the
    /// first. <c>FitOnOpen</c> needs the same guard for the opposite reason: a
    /// freshly opened document fitting to the window is not a zoom the user
    /// aimed anywhere, and correcting it at the centre would scroll a document
    /// that just opened away from its own top.
    /// </summary>
    private bool _reanchoringZoom;

    /// <summary>
    /// Wires the two gestures the menu, the toolbar and the keyboard do not reach
    /// (#528): Ctrl+wheel and a Mac trackpad pinch. Both are attached to
    /// <c>PageScroller</c> itself, the same control the pointer has to be over
    /// for either to mean "zoom the document" rather than "do whatever this
    /// gesture means somewhere else in the window".
    /// </summary>
    private void WireZoom()
    {
        // Tunnel, and ahead of PageScroller's own bubble-phase wheel handling
        // (which is what turns an un-Handled wheel event into a scroll): a Ctrl
        // held down has to pre-empt the scroll, not run alongside it, or the
        // document would zoom and scroll on the same notch.
        PageScroller.AddHandler(PointerWheelChangedEvent, OnPageScrollerPointerWheelChanged,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        // Not part of the multi-touch GestureRecognizer pipeline (Gestures.PinchEvent) —
        // a trackpad's magnify gesture reaches Avalonia through Avalonia.Native's own
        // NSMagnificationGestureRecognizer bridge as PointerTouchPadGestureMagnifyEvent,
        // confirmed by reading MouseDevice.GestureMagnify and AvnView.mm's magnifyWithEvent:
        // rather than trusted from the issue's own audit, which named Gestures.PinchEvent.
        // Gestures.PinchEvent is the touch-screen recognizer built from two raw pointer
        // contacts; nothing on a Mac or Linux desktop delivers those for a trackpad.
        PageScroller.AddHandler(Gestures.PointerTouchPadGestureMagnifyEvent, OnPageScrollerMagnify);
    }

    /// <summary>
    /// Ctrl+wheel (#528): the idiom every platform's audit calls the one Windows
    /// testers reach for first, and Linux's equivalent of a trackpad pinch. One
    /// notch is one step through the same stops <see cref="DocumentViewModel.ZoomInCommand"/>
    /// and <see cref="DocumentViewModel.ZoomOutCommand"/> already use — continuous
    /// wheel-zoom is a trackpad's job, not a mouse wheel's — anchored on wherever
    /// the pointer was for that notch, not on the layout's origin.
    /// </summary>
    private void OnPageScrollerPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (Active is not { IsDocumentOpen: true } vm)
            return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        e.Handled = true;
        var anchor = e.GetPosition(PageScroller);
        ApplyAnchoredZoom(vm, anchor, () =>
        {
            if (e.Delta.Y > 0 && vm.ZoomInCommand.CanExecute(null))
                vm.ZoomInCommand.Execute(null);
            else if (e.Delta.Y < 0 && vm.ZoomOutCommand.CanExecute(null))
                vm.ZoomOutCommand.Execute(null);
        });
    }

    /// <summary>
    /// A Mac trackpad pinch (#528), delivered as <c>PointerTouchPadGestureMagnifyEvent</c>
    /// (see the remark on <see cref="WireZoom"/>). <c>Delta.X</c> is
    /// <c>NSEvent.magnification</c> passed straight through by
    /// <c>AvnView.mm</c>'s <c>magnifyWithEvent:</c> — the CHANGE in magnification
    /// since the previous callback of the same gesture, not a cumulative total —
    /// so each callback is its own small continuous zoom step, applied as
    /// <c>zoom * (1 + delta)</c>, around the same anchor.
    ///
    /// That anchor is <see cref="PointerEventArgs.GetPosition"/> at the time of
    /// the callback — the system cursor position, since a trackpad reports no
    /// finger position of its own. On a real trackpad the cursor does not move
    /// during a pinch, so this is the same point, held fixed, for every callback
    /// in the gesture: the nearest thing to "the gesture's centroid" a platform
    /// with no on-screen touch points can give.
    /// </summary>
    private void OnPageScrollerMagnify(object? sender, PointerDeltaEventArgs e)
    {
        if (Active is not { IsDocumentOpen: true } vm)
            return;

        e.Handled = true;
        var anchor = e.GetPosition(PageScroller);
        var factor = 1.0 + e.Delta.X;
        ApplyAnchoredZoom(vm, anchor, () => vm.SetZoomCommand.Execute(vm.Zoom * factor));
    }

    /// <summary>
    /// Runs <paramref name="applyZoom"/> and then moves <c>PageScroller</c>'s
    /// offset so the content under <paramref name="anchorViewport"/> is still
    /// there afterwards (#528) — the correction every zoom entry point in this
    /// file shares, wrapped around whichever <see cref="DocumentViewModel"/>
    /// command the caller's gesture maps to.
    /// </summary>
    private void ApplyAnchoredZoom(DocumentViewModel vm, Point anchorViewport, Action applyZoom)
    {
        var oldZoom = vm.Zoom;
        var oldOffset = PageScroller.Offset;

        _reanchoringZoom = true;
        try
        {
            applyZoom();
        }
        finally
        {
            _reanchoringZoom = false;
        }

        // The pages panel has to have actually re-measured at the new Zoom before
        // Offset is assigned below, or the assignment is clamped against the OLD
        // extent and silently dropped — the same trap TryRestoreScrollOffset
        // documents for a tab switch, here forced closed instead of retried.
        PageScroller.UpdateLayout();
        PageScroller.Offset = ZoomAnchor.Reanchor(oldOffset, oldZoom, vm.Zoom, anchorViewport);
        _lastKnownZoom = vm.Zoom;
    }
}
