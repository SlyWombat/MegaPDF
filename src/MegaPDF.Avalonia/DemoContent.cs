using System.Globalization;

namespace MegaPDF.Avalonia;

/// <summary>
/// The content the store captures put *into* the demo document — as opposed to
/// the app's own UI text, which lives in Strings.resx.
///
/// It is deliberately not in the string catalogue: nobody using MegaPDF ever
/// sees any of this. It only exists so `--story` and `--screenshot-state
/// textbox` can pose a French capture that is French all the way through,
/// rather than a French window over an English name (#146 §3).
///
/// The same values, chosen by the same rule, are in `ios/MegaPDF/DemoContent.swift`
/// and `tools/macos-record-demo.sh` — keep the three in step.
/// </summary>
internal static class DemoContent
{
    /// <summary>
    /// Who signs the demo agreement. Dave, 2026-09-15: the French captures must
    /// not show "Jane Whitfield", and the names must carry accents — which is
    /// also what proves the accented glyphs survive whatever face the demo picks.
    /// </summary>
    internal static string PrintedName => ForLanguage(
        english: "Jane Whitfield", frenchCanadian: "Hélène Bélanger", french: "Céline Lefèvre");

    /// <summary>
    /// What the find steps search for. Three hits on the demo agreement in both
    /// languages, so "1 of 3" reads the same — see tools/gen_test_fixtures.py,
    /// where the French page is translated to keep exactly that.
    /// </summary>
    internal static string SearchTerm => ForLanguage(
        english: "rental", frenchCanadian: "location", french: "location");

    private static string ForLanguage(string english, string frenchCanadian, string french)
    {
        var culture = CultureInfo.CurrentUICulture;
        if (!culture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase))
            return english;
        // fr-CA is its own listing language, with its own name; every other
        // French locale takes the France one, as the catalogue does.
        return culture.Name.Equals("fr-CA", StringComparison.OrdinalIgnoreCase) ? frenchCanadian : french;
    }
}
