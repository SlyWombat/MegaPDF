using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace MegaPDF.App;

/// <summary>
/// The keyboard focus ring on a page region (SDD §2.2, #2).
///
/// A panel has no automation peer of its own, so a plain Grid carrying the
/// region's name would be invisible to Narrator and to Accessibility Insights.
/// This one reports itself as a named "page region". It never takes keyboard
/// focus — the pages scroller keeps that, so re-rendering a page cannot drop focus
/// — which is why focus changes are also announced as notifications.
/// </summary>
public sealed partial class PageFocusRing : Grid
{
    protected override AutomationPeer OnCreateAutomationPeer() => new PageFocusRingAutomationPeer(this);

    private sealed partial class PageFocusRingAutomationPeer(PageFocusRing owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

        protected override string GetLocalizedControlTypeCore() => Strings.PageRegionControlType;

        protected override string GetClassNameCore() => nameof(PageFocusRing);

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;

        protected override bool IsKeyboardFocusableCore() => false;
    }
}
