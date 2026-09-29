using Microsoft.UI.Xaml.Automation.Peers;

namespace MegaPDF.App;

/// <summary>
/// Whether anything is listening to this process's automation events — in practice,
/// whether Narrator (or another assistive technology) is running (#504).
///
/// Reading mode is meant to be the screen-reader-friendly view, so the one thing the
/// floating bar must never do is fade away from somebody who cannot bring it back by
/// moving a mouse (docs/reading-mode-plan.md §2, §7). WinUI's answer to "is anyone
/// listening" is <see cref="AutomationPeer.ListenerExists"/>, which is cheap and is
/// what the plan names.
///
/// <see cref="AutomationEvents.AutomationFocusChanged"/> is the event to ask about:
/// every screen reader subscribes to focus, whereas the narrower notification events
/// are only registered once something has raised one.
/// </summary>
internal static class ScreenReader
{
    /// <summary>
    /// Stands in for the real answer in <c>--screenshot-state reading</c>. The rule
    /// under test is "the bar never fades while a reader is on", and no automated run
    /// can turn Narrator on and off around itself; overriding the answer keeps the
    /// production guard — timer, tick and all — as the thing being exercised.
    /// </summary>
    internal static Func<bool>? OverrideForTest { get; set; }

    public static bool IsRunning()
    {
        if (OverrideForTest is { } stub)
            return stub();
        try
        {
            return AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged);
        }
        catch (Exception)
        {
            // Never let "is a screen reader running?" be the thing that fails: an
            // unanswerable question means "assume one is", which keeps the bar up.
            return true;
        }
    }
}
