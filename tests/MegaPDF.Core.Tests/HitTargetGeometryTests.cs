using MegaPDF.Core.Viewing;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// #2 — enlarging the selected signature's resize handle and ✕ button to the 40x40
/// accessibility target (WCAG 2.5.5/2.5.8) without moving or resizing what is drawn.
/// The XAML in <c>DocumentView.xaml.cs</c> needs a real window to check by eye; the
/// centring arithmetic underneath it does not.
/// </summary>
public class HitTargetGeometryTests
{
    /// <summary>A control already centred on its anchor corner (margin = -size / 2) stays
    /// centred on that same corner once the host around it grows.</summary>
    [Fact]
    public void ControlCenteredOnItsCorner_StaysOnThatCorner()
    {
        // The resize handle: 14x14 at a -7 margin sits exactly on the chrome's corner.
        var hostMargin = HitTargetGeometry.CenteredHostMargin(elementMargin: -7, elementSize: 14, hostSize: 40);

        // The host is 40, so it sits on the same corner when its margin is -20 either side.
        Assert.Equal(-20, hostMargin);
    }

    /// <summary>The ✕ chip's plain-target case: 22x22 at a -11 margin, also corner-centred.</summary>
    [Fact]
    public void RemoveChip_PlainCase_StaysOnTheCorner()
    {
        var hostMargin = HitTargetGeometry.CenteredHostMargin(elementMargin: -11, elementSize: 22, hostSize: 40);
        Assert.Equal(-20, hostMargin);
    }

    /// <summary>The ✕ chip's text-box case sits 15px past the corner (26 - 11); growing
    /// the host must keep that same 15px offset, not snap back to the plain corner.</summary>
    [Fact]
    public void RemoveChip_TextBoxCase_KeepsItsOffsetFromTheCorner()
    {
        var hostMargin = HitTargetGeometry.CenteredHostMargin(elementMargin: -26, elementSize: 22, hostSize: 40);
        Assert.Equal(-35, hostMargin);
    }

    /// <summary>
    /// General property behind all three cases above: for any corner-anchored element,
    /// the point <c>-margin - size / 2</c> past the corner is its centre. Growing the
    /// host to <paramref name="hostSize"/> must land that same centre.
    /// </summary>
    [Theory]
    [InlineData(-7, 14, 40)]
    [InlineData(-11, 22, 40)]
    [InlineData(-26, 22, 40)]
    [InlineData(0, 20, 44)]
    public void HostAndElement_ShareTheSameCentre(double elementMargin, double elementSize, double hostSize)
    {
        var elementCentre = -elementMargin - elementSize / 2;
        var hostMargin = HitTargetGeometry.CenteredHostMargin(elementMargin, elementSize, hostSize);
        var hostCentre = -hostMargin - hostSize / 2;

        Assert.Equal(elementCentre, hostCentre, precision: 9);
    }

    /// <summary>A host no bigger than the element it wraps needs no offset at all.</summary>
    [Fact]
    public void HostSameSizeAsElement_KeepsTheSameMargin()
    {
        Assert.Equal(-7, HitTargetGeometry.CenteredHostMargin(-7, 14, 14));
    }
}
