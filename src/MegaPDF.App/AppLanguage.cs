using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace MegaPDF.App;

/// <summary>
/// The app's UI language (#91): Windows' display language by default, overridden
/// by <c>--language &lt;tag&gt;</c> on the command line or the Language setting.
///
/// The override is <see cref="Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride"/>,
/// which MRT consults for every x:Uid and every code lookup — in a packaged
/// build, which is what ships. The unpackaged dev build has no package identity
/// and the property throws there; for that build the override reaches code
/// strings only, through an explicit <see cref="ResourceContext"/> (tried and
/// ruled out 2026-09-10: the process preferred-UI-language list, and a custom
/// <c>IResourceManager</c> via <c>Application.ResourceManagerRequested</c> — XAML
/// takes the manager but pins the Language qualifier itself). So a
/// <c>--screenshot --language fr-CA</c> run of the dev build shows French
/// dialogs and status text under English toolbar labels; the packaged app is
/// where the whole window is checked (tools/screenshots-windows/README.md).
///
/// It must run before the first XAML loads; App's constructor is the only place
/// early enough.
/// </summary>
internal static class AppLanguage
{
    /// <summary>The languages the Settings flyout offers, in radio order. "" = follow Windows.</summary>
    public static readonly string[] Choices = ["", "en-US", "fr-CA", "fr-FR"];

    private static readonly ResourceManager Manager = new();
    private static ResourceContext? _context;

    public static void ApplyOverride(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return;

        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(tag);
        }
        catch (CultureNotFoundException)
        {
            // A bad tag in settings.json or on the command line is not worth a crash;
            // the app simply comes up in Windows' language.
            return;
        }

        // Number and date formatting in dialogs ("1,4 Mo") follow the same language.
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        _context = Manager.CreateResourceContext();
        _context.QualifierValues["Language"] = culture.Name;

        try
        {
            Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = culture.Name;
        }
        catch (InvalidOperationException)
        {
            // No package identity (dev build): code strings still follow _context.
        }
    }

    /// <summary>A string from Resources.resw in the app's language.</summary>
    public static string GetString(string key)
    {
        var candidate = _context is null
            ? Manager.MainResourceMap.TryGetValue("Resources/" + key)
            : Manager.MainResourceMap.TryGetValue("Resources/" + key, _context);
        return candidate?.ValueAsString ?? key;
    }

    public static int ChoiceIndex(string tag)
    {
        var index = Array.FindIndex(Choices, c => string.Equals(c, tag, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index;
    }

    public static string TagForChoice(int index) =>
        index >= 0 && index < Choices.Length ? Choices[index] : "";
}
