using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MegaPDF.Avalonia;

/// <summary>
/// Reads the design tokens out of <c>Brand.axaml</c> for the code paths that
/// build chrome by hand — the selection box, its resize handles, the keyboard
/// focus ring. XAML gets at the same values with <c>{DynamicResource}</c>.
///
/// Resolving through the application rather than caching a
/// <see cref="SolidColorBrush"/> is deliberate: these are theme-scoped, and
/// macOS switches appearance under a running app. A cached brush would keep the
/// light-theme colour after the user goes dark.
/// </summary>
internal static class Brand
{
    /// <summary>
    /// The token named <paramref name="key"/>, in the theme that is actually on
    /// screen. A key that is not in <c>Brand.axaml</c> comes back transparent —
    /// the chrome disappears, which is loud enough to catch in the self-test
    /// rather than shipping as a subtly wrong colour.
    /// </summary>
    public static IBrush Brush(string key)
    {
        var app = Application.Current;
        if (app is not null
            && app.TryFindResource(key, app.ActualThemeVariant, out var value)
            && value is IBrush brush)
        {
            return brush;
        }

        return Brushes.Transparent;
    }
}
