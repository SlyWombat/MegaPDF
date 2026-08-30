using System.Xml.Linq;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// The two desktop token files, held against each other.
///
/// Neither app can be launched from a test, so this is static: it reads
/// <c>Themes/Brand.xaml</c> and <c>Brand.axaml</c> as XML and compares which keys
/// each defines. That catches the two failures nothing else in CI can see —
/// a token added to Light and forgotten in Dark, and a token that exists on one
/// desktop platform and not the other.
///
/// macOS has <c>--brand-check</c>, which resolves its tokens against a running
/// bundle; Windows has no equivalent, because a WinUI app cannot be launched on
/// a CI runner. This is the cover for that gap.
/// </summary>
public class DesignTokenParityTests
{
    /// <summary>
    /// Keys that are deliberately on one platform only. Each needs a reason, or
    /// the list becomes somewhere to hide a mistake.
    /// </summary>
    private static readonly Dictionary<string, string> WindowsOnly = new()
    {
        ["BrandOverlayText"] = "splash credit lines over the artwork; the Mac app has no splash",
        ["BrandAccentColor"] = "WinUI needs the Color as well as the Brush to feed Fluent's ramp",
        ["BrandAccentPressedColor"] = "same",
        ["TypeDisplay"] = "the splash only; macOS never renders type that large",
        ["AccentFillColorDefault"] = "Fluent derives its brushes at XamlControlsResources load, so WinUI has to name them",
        ["AccentFillColorSecondary"] = "same",
        ["AccentFillColorTertiary"] = "same",
        ["AccentTextFillColorPrimary"] = "same",
        ["AccentTextFillColorSecondary"] = "same",
    };

    private static readonly Dictionary<string, string> MacOnly = new()
    {
        ["BrandRule"] = "the signature window's hairline rule; Windows draws that dialog differently",
        ["BrandCardShadow"] = "Avalonia takes a BoxShadows resource; WinUI shadows are a different mechanism",
    };

    /// <summary>
    /// The two files name the same token differently: WinUI wants
    /// <c>BrandAccentBrush</c> because it also defines <c>BrandAccentColor</c>
    /// separately, while Avalonia's <c>BrandAccent</c> is the brush. Comparing
    /// raw keys would report every token as a divergence, so the suffix comes off
    /// first. <c>...Color</c> keys keep theirs — those really are Windows-only
    /// companions, and collapsing them would hide a genuine difference.
    /// </summary>
    private static string Normalise(string key) =>
        key.EndsWith("Brush", StringComparison.Ordinal) ? key[..^"Brush".Length] : key;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MegaPDF.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Every x:Key in the file, grouped by the theme block it sits in.</summary>
    private static Dictionary<string, HashSet<string>> KeysByTheme(string path)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var root = XDocument.Load(path).Root!;
        var result = new Dictionary<string, HashSet<string>>
        {
            ["Light"] = [],
            ["Dark"] = [],
            ["shared"] = [],
        };

        foreach (var element in root.Descendants())
        {
            var key = element.Attribute(x + "Key")?.Value;
            if (key is null)
                continue;

            // A ResourceDictionary keyed Light or Dark is a theme block, not a token.
            if (element.Name.LocalName == "ResourceDictionary")
                continue;

            var theme = element.Ancestors()
                .Select(a => a.Attribute(x + "Key")?.Value)
                .FirstOrDefault(v => v is "Light" or "Dark");

            result[theme ?? "shared"].Add(Normalise(key));
        }

        return result;
    }

    [Fact]
    public void LightAndDarkDefineTheSameTokens()
    {
        foreach (var file in new[] { "src/MegaPDF.App/Themes/Brand.xaml", "src/MegaPDF.Avalonia/Brand.axaml" })
        {
            var keys = KeysByTheme(Path.Combine(RepoRoot(), file));
            var onlyLight = keys["Light"].Except(keys["Dark"]).OrderBy(k => k).ToList();
            var onlyDark = keys["Dark"].Except(keys["Light"]).OrderBy(k => k).ToList();

            Assert.True(onlyLight.Count == 0,
                $"{file}: defined in Light but not Dark — invisible until someone switches "
                + $"appearance: {string.Join(", ", onlyLight)}");
            Assert.True(onlyDark.Count == 0,
                $"{file}: defined in Dark but not Light: {string.Join(", ", onlyDark)}");
        }
    }

    [Fact]
    public void TheTwoDesktopAppsDefineTheSameTokens()
    {
        var root = RepoRoot();
        var windows = KeysByTheme(Path.Combine(root, "src/MegaPDF.App/Themes/Brand.xaml"));
        var mac = KeysByTheme(Path.Combine(root, "src/MegaPDF.Avalonia/Brand.axaml"));

        var windowsAll = windows.Values.SelectMany(v => v).ToHashSet();
        var macAll = mac.Values.SelectMany(v => v).ToHashSet();

        var missingOnMac = windowsAll.Except(macAll).Except(WindowsOnly.Keys).OrderBy(k => k).ToList();
        var missingOnWindows = macAll.Except(windowsAll).Except(MacOnly.Keys).OrderBy(k => k).ToList();

        Assert.True(missingOnMac.Count == 0,
            "Windows defines tokens macOS does not, and they are not in the WindowsOnly list: "
            + string.Join(", ", missingOnMac));
        Assert.True(missingOnWindows.Count == 0,
            "macOS defines tokens Windows does not, and they are not in the MacOnly list: "
            + string.Join(", ", missingOnWindows));
    }

    [Fact]
    public void ThePlatformOnlyListsAreNotStale()
    {
        var root = RepoRoot();
        var windowsAll = KeysByTheme(Path.Combine(root, "src/MegaPDF.App/Themes/Brand.xaml"))
            .Values.SelectMany(v => v).ToHashSet();
        var macAll = KeysByTheme(Path.Combine(root, "src/MegaPDF.Avalonia/Brand.axaml"))
            .Values.SelectMany(v => v).ToHashSet();

        // An entry that both platforms now define, or that neither does, is an
        // exemption nobody needs — and a place a real divergence could hide.
        foreach (var (key, reason) in WindowsOnly)
        {
            Assert.True(windowsAll.Contains(key), $"WindowsOnly lists '{key}' ({reason}) but Windows does not define it");
            Assert.False(macAll.Contains(key), $"WindowsOnly lists '{key}' but macOS defines it too — drop the exemption");
        }

        foreach (var (key, reason) in MacOnly)
        {
            Assert.True(macAll.Contains(key), $"MacOnly lists '{key}' ({reason}) but macOS does not define it");
            Assert.False(windowsAll.Contains(key), $"MacOnly lists '{key}' but Windows defines it too — drop the exemption");
        }
    }
}
