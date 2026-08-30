using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MegaPDF.App;

/// <summary>
/// Reads the design tokens out of <c>Themes/Brand.xaml</c> for the code paths
/// that build chrome by hand — the stamp selection box, the whiteout preview,
/// the hover underline, the find highlights. XAML gets at the same values with
/// <c>{ThemeResource}</c>.
/// </summary>
internal static class Brand
{
    /// <summary>
    /// The brush token named <paramref name="key"/>, in the theme currently in
    /// effect.
    /// </summary>
    public static Brush Brush(string key) =>
        Lookup(key) as Brush
        ?? throw new KeyNotFoundException(
            $"Brand token '{key}' is not in Themes/Brand.xaml. Tokens are defined "
            + "in one place; see docs/design-tokens.md §5.");

    /// <summary>
    /// A copy of a brush token at a given opacity, for the places that need the
    /// accent washed back. A fresh instance each time: sharing one and mutating
    /// its Opacity would change every other user of it.
    /// </summary>
    public static Brush Brush(string key, double opacity) =>
        new SolidColorBrush(((SolidColorBrush)Brush(key)).Color) { Opacity = opacity };

    /// <summary>
    /// Resolves a key that may live inside a merged dictionary's
    /// <c>ThemeDictionaries</c>.
    ///
    /// The plain <c>Resources[key]</c> indexer is not the same lookup
    /// <c>{ThemeResource}</c> performs: theme dictionaries resolve through the
    /// element tree, and an indexer lookup for a key that exists only inside a
    /// theme dictionary is the classic WinUI <see cref="KeyNotFoundException"/>.
    /// The pattern this replaced — <c>Resources["SystemAccentColor"]</c> —
    /// worked because that key sits in the *root* of XamlControlsResources, not
    /// inside a ThemeDictionaries block. Different lookup, and the difference
    /// only shows up at runtime, on a code path nothing in CI exercises.
    ///
    /// So this walks it explicitly: direct hit first, then each merged
    /// dictionary, then that dictionary's theme block for the current theme.
    /// </summary>
    private static object? Lookup(string key)
    {
        var app = Application.Current.Resources;
        if (app.TryGetValue(key, out var direct))
            return direct;

        var themeKey = Application.Current.RequestedTheme == ApplicationTheme.Dark
            ? "Dark"
            : "Light";

        foreach (var merged in app.MergedDictionaries)
        {
            if (merged.TryGetValue(key, out var hit))
                return hit;

            if (merged.ThemeDictionaries.TryGetValue(themeKey, out var themed)
                && themed is ResourceDictionary themeDictionary
                && themeDictionary.TryGetValue(key, out var themedHit))
            {
                return themedHit;
            }
        }

        return null;
    }
}
