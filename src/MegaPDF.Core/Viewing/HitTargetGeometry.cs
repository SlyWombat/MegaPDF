namespace MegaPDF.Core.Viewing;

/// <summary>
/// Growing a small control's hit-test area without moving what is on screen (#2;
/// WCAG 2.5.5/2.5.8 target size). The selected signature's resize handle (14x14) and
/// its ✕ button (22x22) are both anchored to a corner by a margin — a negative offset
/// on the two edges meeting at that corner, so the visible control straddles it. A
/// bigger, transparent host takes over that anchor; this is the margin that keeps the
/// host centred on the exact point the smaller control used to be centred on, so the
/// enlargement is invisible.
///
/// Pure and platform-agnostic (the same corner-anchor convention Windows Thickness
/// margins use), so the arithmetic is pinned by a test rather than by eyeballing a
/// screenshot — the same reasoning <see cref="PageReadingOrder"/> was pulled out here for.
/// </summary>
public static class HitTargetGeometry
{
    /// <summary>
    /// The margin (on the two edges meeting at the anchor corner) for a
    /// <paramref name="hostSize"/> square host that keeps the same centre point a
    /// smaller, <paramref name="elementSize"/> element already had at
    /// <paramref name="elementMargin"/> — growing the target without moving it.
    ///
    /// A corner-anchored element's centre sits <c>-elementMargin - elementSize / 2</c>
    /// past the corner (the handle's -7 margin at 14x14 sits exactly on the corner: 7 -
    /// 7 = 0; the text-box chip's -26 margin at 22x22 sits 15 past it: 26 - 11 = 15).
    /// Solving the same equation for the host's margin at <paramref name="hostSize"/>
    /// and simplifying leaves growth split evenly either side of the fixed centre:
    /// <c>elementMargin - (hostSize - elementSize) / 2</c>.
    /// </summary>
    public static double CenteredHostMargin(double elementMargin, double elementSize, double hostSize) =>
        elementMargin - (hostSize - elementSize) / 2;
}
