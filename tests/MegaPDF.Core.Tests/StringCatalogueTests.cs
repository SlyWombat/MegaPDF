using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MegaPDF.Core.Tests;

/// <summary>
/// The four platforms' string catalogues, held against themselves (#91).
///
/// None of the apps can be launched from a test, so this is static, in the
/// shape of <see cref="DesignTokenParityTests"/>: it reads each catalogue off
/// disk and checks the three things a missing translation looks like —
/// a key present in English and absent in French (or the reverse), a French
/// value that is byte-for-byte the English one, and a placeholder set that
/// differs between the two ("{0} of {1}" translated without its "{1}").
///
/// It also checks the generated accessor classes name every key, so the
/// desktop apps cannot compile against a string that is not in the catalogue.
/// </summary>
public class StringCatalogueTests
{
    /// <summary>
    /// Keys whose French is legitimately the English. Each needs a reason, or the
    /// list becomes somewhere to hide an untranslated string.
    /// </summary>
    private static readonly Dictionary<string, string> SameInFrench = new()
    {
        // Windows
        ["AboutVersion"] = "\"MegaPDF {0}\" is a brand name and a number",
        ["LabelSignatures.Text"] = "\"Signatures\" is the same word",
        ["SignaturesButton.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"] = "same",
        ["LanguageEnglish.Text"] = "language names are written in their own language",
        ["LanguageFrench.Text"] = "same",
        ["OK"] = "OK is OK",
        ["PageN"] = "\"Page {0}\" is the same in both",
        ["SplashCopyright.Text"] = "a copyright line with no words to translate",
        // macOS
        ["DefaultDocumentName"] = "\"document\" is a file name stem, same spelling",
        ["Options"] = "same word",
        ["PageRegionDescription"] = "\"Page {0}, {1}\" carries no words of its own",
        ["RegionPage"] = "\"Page\" is the same word",
        ["SignatureDefaultName"] = "\"Signature\" is the same word",
        ["WindowTitleFormat"] = "\"{0} — MegaPDF\" carries no words of its own",
        // Android
        ["document"] = "file name stem",
        ["page_n"] = "\"Page %1$d\"",
        ["signature_default_name"] = "\"Signature %1$d\"",
        ["signatures"] = "same word",
        ["version_label"] = "\"Version %1$s\"",
        // iOS
        ["Document"] = "file name stem",
        ["Page %lld"] = "same",
        ["Photos"] = "the Photos app keeps its name",
        ["Signature %lld"] = "same",
        ["Signatures"] = "same word",
        ["Version %@"] = "same",
    };

    private static readonly Regex DotNetPlaceholder = new(@"\{(\d+)(?:[:,][^}]*)?\}", RegexOptions.Compiled);
    private static readonly Regex JavaPlaceholder = new(@"%(\d+\$)?[sd]", RegexOptions.Compiled);
    private static readonly Regex ApplePlaceholder = new(@"%(\d+\$)?(lld|ld|d|@)", RegexOptions.Compiled);

    // --- Windows: Strings/<lang>/Resources.resw ---

    [Fact]
    public void WindowsCatalogueIsComplete()
    {
        var en = ResxValues("src/MegaPDF.App/Strings/en-US/Resources.resw");
        var fr = ResxValues("src/MegaPDF.App/Strings/fr-CA/Resources.resw");
        AssertParity("Windows", en, fr, DotNetPlaceholder);
    }

    [Fact]
    public void WindowsAccessorsCoverEveryCodeKey() =>
        AssertAccessors("src/MegaPDF.App/Strings/en-US/Resources.resw", "src/MegaPDF.App/Strings.g.cs");

    // --- macOS: Strings/Strings.resx, Strings.fr.resx ---

    [Fact]
    public void MacCatalogueIsComplete()
    {
        var en = ResxValues("src/MegaPDF.Avalonia/Strings/Strings.resx");
        var fr = ResxValues("src/MegaPDF.Avalonia/Strings/Strings.fr.resx");
        AssertParity("macOS", en, fr, DotNetPlaceholder);
    }

    [Fact]
    public void MacAccessorsCoverEveryCodeKey() =>
        AssertAccessors("src/MegaPDF.Avalonia/Strings/Strings.resx", "src/MegaPDF.Avalonia/Strings.g.cs");

    // --- Android: res/values/strings.xml, res/values-fr/strings.xml ---

    [Fact]
    public void AndroidCatalogueIsComplete()
    {
        var en = AndroidValues("android/app/src/main/res/values/strings.xml");
        var fr = AndroidValues("android/app/src/main/res/values-fr/strings.xml");
        AssertParity("Android", en, fr, JavaPlaceholder);
    }

    // --- iOS: Localizable.xcstrings ---

    [Fact]
    public void IosCatalogueIsComplete()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "ios/MegaPDF/Localizable.xcstrings")));
        var strings = doc.RootElement.GetProperty("strings");

        var en = new Dictionary<string, string>();
        var fr = new Dictionary<string, string>();
        foreach (var entry in strings.EnumerateObject())
        {
            if (entry.Value.TryGetProperty("shouldTranslate", out var flag) && !flag.GetBoolean())
                continue;
            en[entry.Name] = entry.Name; // SwiftUI convention: the English text is the key
            if (entry.Value.TryGetProperty("localizations", out var locs)
                && locs.TryGetProperty("fr-CA", out var frCa)
                && frCa.GetProperty("stringUnit") is var unit
                && unit.GetProperty("state").GetString() == "translated")
            {
                fr[entry.Name] = unit.GetProperty("value").GetString() ?? "";
            }
        }
        AssertParity("iOS", en, fr, ApplePlaceholder);
    }

    // --- The exemption list must not rot ---

    [Fact]
    public void TheSameInFrenchListIsNotStale()
    {
        var all = new Dictionary<string, (string en, string fr)>();
        void Add(Dictionary<string, string> en, Dictionary<string, string> fr)
        {
            foreach (var (k, v) in en)
                if (fr.TryGetValue(k, out var f))
                    all[k] = (v, f);
        }
        Add(ResxValues("src/MegaPDF.App/Strings/en-US/Resources.resw"), ResxValues("src/MegaPDF.App/Strings/fr-CA/Resources.resw"));
        Add(ResxValues("src/MegaPDF.Avalonia/Strings/Strings.resx"), ResxValues("src/MegaPDF.Avalonia/Strings/Strings.fr.resx"));
        Add(AndroidValues("android/app/src/main/res/values/strings.xml"), AndroidValues("android/app/src/main/res/values-fr/strings.xml"));

        var stale = SameInFrench.Keys
            .Where(k => all.TryGetValue(k, out var pair) && pair.en != pair.fr)
            .ToList();
        Assert.True(stale.Count == 0,
            "These keys are listed as identical in French but are now translated — remove them from SameInFrench: "
            + string.Join(", ", stale));
    }

    // --- helpers ---

    private static void AssertParity(string platform, Dictionary<string, string> en, Dictionary<string, string> fr, Regex placeholder)
    {
        Assert.True(en.Count > 0, $"{platform}: the English catalogue is empty");

        var missingInFrench = en.Keys.Except(fr.Keys).Order().ToList();
        var extraInFrench = fr.Keys.Except(en.Keys).Order().ToList();
        Assert.True(missingInFrench.Count == 0, $"{platform}: keys with no French: {string.Join(", ", missingInFrench)}");
        Assert.True(extraInFrench.Count == 0, $"{platform}: French keys with no English: {string.Join(", ", extraInFrench)}");

        var untranslated = en.Keys
            .Where(k => en[k] == fr[k] && !SameInFrench.ContainsKey(k))
            .Order().ToList();
        Assert.True(untranslated.Count == 0,
            $"{platform}: French is identical to English (translate, or add to SameInFrench with a reason): {string.Join(", ", untranslated)}");

        var placeholders = en.Keys
            .Where(k => !Slots(en[k], placeholder).SetEquals(Slots(fr[k], placeholder)))
            .Order().ToList();
        Assert.True(placeholders.Count == 0,
            $"{platform}: placeholder sets differ between English and French: {string.Join(", ", placeholders)}");
    }

    /// <summary>Placeholder identities, so "%1$lld" and "%lld" in first position compare equal.</summary>
    private static HashSet<string> Slots(string value, Regex placeholder)
    {
        var slots = new HashSet<string>();
        var position = 0;
        foreach (Match m in placeholder.Matches(value))
        {
            var explicitIndex = m.Groups[1].Value.TrimEnd('$');
            slots.Add(explicitIndex.Length > 0 ? explicitIndex : (position + 1).ToString());
            position++;
        }
        return slots;
    }

    private static void AssertAccessors(string catalogue, string generated)
    {
        var keys = ResxValues(catalogue).Keys.Where(k => !k.Contains('.')).ToList();
        var source = File.ReadAllText(Path.Combine(RepoRoot(), generated));
        var missing = keys.Where(k => !source.Contains($"Get(\"{k}\")", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0,
            $"{generated} is stale — run tools/gen_strings.py. Missing: {string.Join(", ", missing)}");
    }

    private static Dictionary<string, string> ResxValues(string relativePath) =>
        XDocument.Load(Path.Combine(RepoRoot(), relativePath)).Root!
            .Elements("data")
            .ToDictionary(d => d.Attribute("name")!.Value, d => d.Element("value")?.Value ?? "");

    /// <summary>Every translatable string and plurals name; plurals compare by their "other" text.</summary>
    private static Dictionary<string, string> AndroidValues(string relativePath) =>
        XDocument.Load(Path.Combine(RepoRoot(), relativePath)).Root!
            .Elements()
            .Where(e => (e.Name == "string" || e.Name == "plurals") && e.Attribute("translatable")?.Value != "false")
            .ToDictionary(
                e => e.Attribute("name")!.Value,
                e => e.Name == "string"
                    ? e.Value
                    : e.Elements("item").FirstOrDefault(i => i.Attribute("quantity")?.Value == "other")?.Value ?? "");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MegaPDF.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
