using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Styling;

namespace RomSieve.App.Converters;

/// <summary>
/// Turns a semantic tone exposed by a ViewModel (a status, a level, a convention...) into a resource of the
/// application theme. The parameter is the resource key with <c>*</c> standing for the tone, such as
/// <c>Status*BadgeForegroundBrush</c>, optionally followed by <c>|</c> and a fallback key used when that key does not exist.
/// </summary>
/// <remarks>
/// A resource found by a converter does not follow a theme change by itself. Bindings that must follow it are
/// <c>MultiBinding</c>s whose second value is the theme variant of the control (<c>$self.ActualThemeVariant</c>):
/// a change of variant re-evaluates them with the resource of the new variant.
/// </remarks>
public sealed class ThemeResourceConverter : IValueConverter, IMultiValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Resolve(value, parameter, variant: null);

    /// <summary>Values: the tone, then the theme variant of the bound control.</summary>
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count == 0
            ? AvaloniaProperty.UnsetValue
            : Resolve(values[0], parameter, values.Count > 1 ? values[1] as ThemeVariant : null);

    private static object? Resolve(object? value, object? parameter, ThemeVariant? variant)
    {
        if (value is null || value == AvaloniaProperty.UnsetValue || parameter is not string format || Application.Current is not { } app)
        {
            return AvaloniaProperty.UnsetValue;
        }

        var themeVariant = variant ?? app.ActualThemeVariant;
        var parts = format.Split('|', 2);
        var key = parts[0].Replace("*", System.Convert.ToString(value, CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (app.TryGetResource(key, themeVariant, out var resource))
        {
            return resource;
        }

        return parts.Length == 2 && app.TryGetResource(parts[1], themeVariant, out var fallback)
            ? fallback
            : AvaloniaProperty.UnsetValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
