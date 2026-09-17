using System.Runtime.Versioning;

namespace MegaPDF.Avalonia.Platform;

/// <summary>
/// The language the desktop is running in, from the POSIX locale environment
/// (#91, #158).
///
/// .NET on Linux derives <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>
/// from <c>LC_ALL</c>/<c>LC_MESSAGES</c>/<c>LANG</c>, and then stops. It never
/// reads <c>LANGUAGE</c> — which is the one every desktop environment actually
/// sets when you pick a *display* language separate from your formats. GNOME's
/// Region &amp; Language does exactly that: choosing French Canadian while keeping
/// Canadian English formats leaves <c>LANG=en_CA.UTF-8</c> and
/// <c>LANGUAGE=fr_CA:fr</c>, so the app would have come up in English on a French
/// desktop. Reading the whole chain ourselves is what makes fr-CA and fr work.
///
/// The precedence is gettext's, because that is what the rest of the desktop
/// obeys: <c>LANGUAGE</c> wins, but only while the locale is not the untranslated
/// "C"/"POSIX" one; otherwise <c>LC_ALL</c>, then <c>LC_MESSAGES</c>, then
/// <c>LANG</c>.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxLanguage
{
    /// <summary>
    /// The desktop's UI language as a BCP 47 tag ("fr-CA", "fr", "en-GB"), or null
    /// when the environment asks for no translation at all. Never throws: the
    /// caller falls back to .NET's own default, and an unreadable environment must
    /// not stop the app launching.
    /// </summary>
    internal static string? PreferredLanguageTag()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        try
        {
            return PreferredLanguageTag(Environment.GetEnvironmentVariable);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The same decision against an arbitrary environment, so <c>--print-check</c>
    /// can check the precedence rules without setting variables on the real process.
    /// </summary>
    internal static string? PreferredLanguageTag(Func<string, string?> environment)
    {
        // The locale category that decides whether translation happens at all.
        var locale = FirstSet(environment, "LC_ALL", "LC_MESSAGES", "LANG");

        // "C" and "POSIX" mean "no translation", and gettext lets them veto
        // LANGUAGE. Without this, a script that sets LC_ALL=C to get stable
        // output would still get a French UI from an inherited LANGUAGE.
        if (!IsUntranslated(locale) && environment("LANGUAGE") is { Length: > 0 } languages)
        {
            // A colon-separated preference list ("fr_CA:fr:en"). The first entry
            // that yields a tag is the one the desktop is running in; the rest are
            // gettext's fallbacks, which .NET's own resource lookup already does.
            foreach (var candidate in languages.Split(':', StringSplitOptions.RemoveEmptyEntries))
                if (ToBcp47(candidate) is { } fromList)
                    return fromList;
        }

        return ToBcp47(locale);
    }

    private static string? FirstSet(Func<string, string?> environment, params string[] names)
    {
        foreach (var name in names)
            if (environment(name) is { Length: > 0 } value)
                return value;
        return null;
    }

    private static bool IsUntranslated(string? locale) =>
        locale is null
        || string.Equals(locale, "C", StringComparison.Ordinal)
        || string.Equals(locale, "POSIX", StringComparison.Ordinal)
        || locale.StartsWith("C.", StringComparison.Ordinal);

    /// <summary>
    /// "fr_CA.UTF-8@euro" -> "fr-CA". Null for the untranslated locales and for
    /// anything that does not start with a language.
    /// </summary>
    internal static string? ToBcp47(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale) || IsUntranslated(locale))
            return null;

        // The POSIX shape is language[_TERRITORY][.codeset][@modifier]. Neither the
        // codeset nor the modifier has a BCP 47 meaning we want — "@euro" is a
        // currency, not a script — so both are dropped rather than translated.
        var name = locale.Split('@', 2)[0].Split('.', 2)[0].Trim();
        if (name.Length == 0)
            return null;

        var tag = name.Replace('_', '-');

        // Reject anything that is not a plausible tag before CultureInfo sees it:
        // on Linux with ICU, an arbitrary string is happily accepted as a custom
        // culture with no resources, which would silently blank the whole UI.
        foreach (var part in tag.Split('-'))
            if (part.Length is 0 or > 8 || !part.All(char.IsAsciiLetterOrDigit))
                return null;

        return tag;
    }
}
