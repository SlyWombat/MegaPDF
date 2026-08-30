using MegaPDF.Core.Engine;

namespace MegaPDF.Core.Viewing;

/// <summary>How a viewport should move to reveal something. Null means "leave that axis alone".</summary>
public readonly record struct ScrollDecision(double? Horizontal, double? Vertical)
{
    public bool MovesAnything => Horizontal is not null || Vertical is not null;
}

/// <summary>
/// Where to scroll so a search hit is actually visible (#28, #32).
///
/// Extracted from the Windows implementation and shared, for two reasons. It is
/// the only part of that fix a test can reach — #32 stayed open because watching
/// it needs a real window, three attempts to drive one were lost to unrelated
/// collateral, and nothing pinned the reasoning in the meantime. And the macOS
/// port had quietly reintroduced the original bug: it scrolled vertically, always,
/// and never horizontally, so on a zoomed page a hit off to the side was
/// highlighted where nobody could see it and every Next jolted the view even when
/// the hit was already on screen.
///
/// Coordinates are the scroll content's own, in device-independent pixels.
/// </summary>
public static class MatchScroll
{
    /// <summary>Keeps a revealed hit off the very edge of the viewport.</summary>
    public const double Margin = 24;

    /// <summary>
    /// A revealed hit lands this far down the viewport rather than at the top —
    /// a hit pinned to the top edge reads as if the document starts there.
    /// </summary>
    public const double VerticalRestFraction = 1.0 / 3.0;

    /// <param name="target">The hit, in content coordinates.</param>
    /// <param name="extentWidth">
    /// Total content width. Horizontal movement is skipped unless the content
    /// genuinely overflows, because below that the panel centres its content and
    /// content coordinates no longer line up with the scroll offset.
    /// </param>
    public static ScrollDecision Reveal(
        PdfRect target,
        double offsetX, double offsetY,
        double viewportWidth, double viewportHeight,
        double extentWidth)
    {
        double? horizontal = null, vertical = null;

        if (extentWidth > viewportWidth + 0.5)
        {
            var left = offsetX;
            var right = left + viewportWidth;
            // Only when it is actually outside — otherwise pressing Next through
            // hits that are already on screen drags the view about for no reason.
            if (target.X < left + Margin || target.X + target.Width > right - Margin)
                horizontal = Math.Max(0, target.X + (target.Width / 2) - (viewportWidth / 2));
        }

        var top = offsetY;
        var bottom = top + viewportHeight;
        if (target.Y < top + Margin || target.Y + target.Height > bottom - Margin)
            vertical = Math.Max(0, target.Y - (viewportHeight * VerticalRestFraction));

        return new ScrollDecision(horizontal, vertical);
    }
}
