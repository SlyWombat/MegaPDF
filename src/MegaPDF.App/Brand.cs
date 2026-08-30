using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MegaPDF.App;

/// <summary>
/// Reads the design tokens out of <c>Themes/Brand.xaml</c> for the code paths
/// that build chrome by hand — the stamp selection box, the whiteout preview,
/// the hover underline. XAML gets at the same values with
/// <c>{ThemeResource}</c>.
///
/// Resolving on each call rather than caching a brush is deliberate: these are
/// theme-scoped, and Windows switches light and dark under a running app.
/// </summary>
internal static class Brand
{
    /// <summary>
    /// The brush token named <paramref name="key"/>, in the theme currently in
    /// effect. A key that is not in <c>Brand.xaml</c> throws rather than
    /// silently drawing the wrong colour — a missing token is a build-time
    /// mistake, not a runtime condition to survive.
    /// </summary>
    public static Brush Brush(string key) =>
        (Brush)Application.Current.Resources[key];

    /// <summary>
    /// A copy of a brush token at a given opacity, for the places that need the
    /// accent washed back. A fresh instance each time: sharing one and mutating
    /// its Opacity would change every other user of it.
    /// </summary>
    public static Brush Brush(string key, double opacity) =>
        new SolidColorBrush(((SolidColorBrush)Application.Current.Resources[key]).Color)
        {
            Opacity = opacity,
        };
}
