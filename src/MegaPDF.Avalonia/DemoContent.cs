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

    /// <summary>
    /// What the Redact capture marks (#173): a line of the demo agreement with something on
    /// it worth removing. Marked by what it says rather than by a rectangle, so the shot
    /// lands on a sentence in every language rather than on whatever a fixed box covers.
    /// </summary>
    internal static string RedactedWord => ForLanguage(
        english: "customer named", frenchCanadian: "client nommé", french: "client nommé");

    /// <summary>
    /// What the demo document is called in a capture.
    ///
    /// The fixture on disk is demo.pdf / demo-fr.pdf, and that name is in the status
    /// line of every shot that has a document open — beside a home shot whose recents
    /// said "Contrat de location.pdf". One set, two names for the same file (#146 §3).
    /// The capture script copies the fixture to this name before it opens it.
    /// </summary>
    internal static string DocumentFileName => ForLanguage(
        english: "Rental Agreement.pdf",
        frenchCanadian: "Contrat de location.pdf",
        french: "Contrat de location.pdf");

    /// <summary>
    /// The rows the home screenshot shows (#146 §3): a name and the place under it.
    ///
    /// Made up rather than read from the machine, for the reason iOS's demoRecents
    /// gives: a capture has to look the same every time it is taken, and the machine's
    /// own list is whatever it last opened — which on the capture Mac was a path
    /// through the sandbox container. The first row is the document the rest of the
    /// set is about; the other two are there so the list is a list.
    ///
    /// The place lines use the same separator the real rows do, so nothing about the
    /// shot is a special case in the view.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Location)> Recents =>
    [
        (DocumentFileName,
         ForLanguage("Documents › Clients", "Documents › Clients", "Documents › Clients")),
        (ForLanguage("Field Trip Permission.pdf", "Autorisation de sortie scolaire.pdf", "Autorisation de sortie scolaire.pdf"),
         ForLanguage("Downloads", "Téléchargements", "Téléchargements")),
        (ForLanguage("Insurance Claim Form.pdf", "Formulaire de réclamation.pdf", "Formulaire de réclamation.pdf"),
         // The one row whose place is not the same on both of this project's desktops.
         // "iCloud Drive" is where a Mac keeps a file and is right in the Mac listing's
         // home shot; on a Linux listing it advertises an Apple service the app cannot
         // reach, in a screenshot whose whole job is to show the app on this platform
         // (#254 A1). The macOS value is untouched, so the signed-off Mac set is
         // unchanged byte for byte.
         OperatingSystem.IsLinux()
             ? ForLanguage("Desktop", "Bureau", "Bureau")
             : ForLanguage("iCloud Drive", "iCloud Drive", "iCloud Drive")),
    ];

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
