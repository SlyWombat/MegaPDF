using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace MegaPDF.App;

/// <summary>
/// A toolbar tool that stays on until it is used or turned off: Add text, Whiteout and
/// Redact (#268). It looks exactly like any other <see cref="AppBarButton"/> — same
/// template, so the toolbar and the store captures are unchanged — but it tells UI
/// Automation whether it is on. Windows' idiom for that is the Toggle pattern: Narrator
/// and NVDA then say "toggle button, pressed" or "not pressed" on focus, and announce the
/// change when it flips. The Mac declares the same thing as a checkbox, iOS as a value and
/// Android as toggleable semantics.
///
/// An <c>AppBarToggleButton</c> would give the pattern for free, but it also paints its
/// checked state, which none of these tools has ever had on Windows.
/// </summary>
public sealed class ArmableAppBarButton : AppBarButton
{
    public ArmableAppBarButton() => DefaultStyleKey = typeof(AppBarButton);

    public static readonly DependencyProperty IsArmedProperty = DependencyProperty.Register(
        nameof(IsArmed), typeof(bool), typeof(ArmableAppBarButton),
        new PropertyMetadata(false, OnIsArmedChanged));

    /// <summary>Whether the tool is on. Bound to the view model's mode.</summary>
    public bool IsArmed
    {
        get => (bool)GetValue(IsArmedProperty);
        set => SetValue(IsArmedProperty, value);
    }

    private static void OnIsArmedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (FrameworkElementAutomationPeer.FromElement((UIElement)d) is ArmablePeer peer)
            peer.RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
                ToState((bool)e.OldValue), ToState((bool)e.NewValue));
    }

    internal static ToggleState ToState(bool armed) => armed ? ToggleState.On : ToggleState.Off;

    protected override AutomationPeer OnCreateAutomationPeer() => new ArmablePeer(this);

    private sealed class ArmablePeer(ArmableAppBarButton owner) : AppBarButtonAutomationPeer(owner), IToggleProvider
    {
        protected override object GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Toggle ? this : base.GetPatternCore(patternInterface);

        public ToggleState ToggleState => ToState(owner.IsArmed);

        // Toggling is pressing: the click handler turns the tool on, or off if it is on.
        public void Toggle() => Invoke();
    }
}
