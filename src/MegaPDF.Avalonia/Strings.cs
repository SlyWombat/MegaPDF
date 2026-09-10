using System.Globalization;

namespace MegaPDF.Avalonia;

/// <summary>
/// The hand-written half of <see cref="Strings"/> (#91). The accessors live in the
/// generated Strings.g.cs; this holds the two helpers a flat catalogue cannot
/// express on its own.
/// </summary>
public static partial class Strings
{
    /// <summary>
    /// Picks the singular or plural form for <paramref name="n"/>.
    ///
    /// English treats only 1 as singular; French treats 0 and 1 as singular
    /// ("0 page", "1 page", "2 pages"). Those are the two languages shipped, so
    /// the rule is written out rather than pulled from a plural-rules library
    /// the app would otherwise never need.
    /// </summary>
    public static string Plural(int n, string one, string other)
    {
        var singular = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? n <= 1 : n == 1;
        return singular ? one : other;
    }

    /// <summary>
    /// A localised lead sentence followed by a technical message the engine or the
    /// OS produced. The person reads the first sentence; the second is there for
    /// the bug report.
    /// </summary>
    public static string WithDetail(string lead, string detail) =>
        string.IsNullOrWhiteSpace(detail) ? lead : $"{lead} {detail}";
}
