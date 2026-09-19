using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// Which Linux package this is (#158). The environment and the file system are passed
/// in, so these run the same on every platform the suite runs on.
/// </summary>
public class LinuxInstallTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] set) =>
        name => set.FirstOrDefault(v => v.Name == name).Value;

    private static Func<string, bool> Files(params string[] present) => present.Contains;

    [Fact]
    public void Snapd_IsRecognised_ByBothOfItsVariables()
    {
        Assert.Equal(LinuxInstallKind.Snap, LinuxInstall.Detect(
            Env(("SNAP", "/snap/megapdf/12"), ("SNAP_NAME", "megapdf")), Files(), "tarball"));
    }

    [Fact]
    public void SnapAlone_IsASnapcraftBuild_NotAnInstall()
    {
        Assert.Equal(LinuxInstallKind.Tarball, LinuxInstall.Detect(Env(("SNAP", "/build")), Files(), "tarball"));
    }

    [Fact]
    public void Flatpak_IsRecognised_ByItsVariable_OrByItsInfoFile()
    {
        // The Flatpak is built from the tarball, so its tree carries the tarball's marker.
        Assert.Equal(LinuxInstallKind.Flatpak, LinuxInstall.Detect(
            Env(("FLATPAK_ID", "ca.electricrv.MegaPDF")), Files(), "tarball"));
        Assert.Equal(LinuxInstallKind.Flatpak, LinuxInstall.Detect(Env(), Files("/.flatpak-info"), "tarball"));
    }

    [Fact]
    public void TheDeb_WithTheRepository_GetsItsUpdatesFromApt()
    {
        Assert.Equal(LinuxInstallKind.AptRepository, LinuxInstall.Detect(
            Env(), Files(LinuxInstall.AptSourcesPath), "deb\n"));
    }

    [Fact]
    public void TheDeb_FromADownloadedFile_HasNothingBehindIt()
    {
        var kind = LinuxInstall.Detect(Env(), Files(), "deb");
        Assert.Equal(LinuxInstallKind.DebFile, kind);
        Assert.True(LinuxInstall.NeedsDownloadPage(kind));
    }

    [Fact]
    public void TheTarball_IsPointedAtTheDownloadPage()
    {
        var kind = LinuxInstall.Detect(Env(), Files(), "tarball");
        Assert.Equal(LinuxInstallKind.Tarball, kind);
        Assert.True(LinuxInstall.NeedsDownloadPage(kind));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something-else")]
    public void NoMarker_IsADevelopersBuild_AndTheAboutWindowSaysNothing(string? marker)
    {
        var kind = LinuxInstall.Detect(Env(), Files(), marker);
        Assert.Equal(LinuxInstallKind.None, kind);
        Assert.False(LinuxInstall.NeedsDownloadPage(kind));
    }

    [Theory]
    [InlineData(LinuxInstallKind.AptRepository)]
    [InlineData(LinuxInstallKind.Snap)]
    [InlineData(LinuxInstallKind.Flatpak)]
    public void PackageManagedInstalls_AreNotSentToTheDownloadPage(LinuxInstallKind kind)
    {
        Assert.False(LinuxInstall.NeedsDownloadPage(kind));
    }

    [Fact]
    public void TheWebsitesSourcesFile_IsWhereTheAppLooks()
    {
        // website/megapdf/apt/megapdf.sources is what the Linux page tells people to put
        // at AptSourcesPath; the file name has to agree with the constant.
        Assert.EndsWith("/megapdf.sources", LinuxInstall.AptSourcesPath);
    }
}
