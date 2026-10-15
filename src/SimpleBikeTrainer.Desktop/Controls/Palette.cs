using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia;

namespace SimpleBikeTrainer.Desktop.Controls;

/// <summary>
/// Theme brushes for view models and drawing code: the same keys as Theme.axaml and ShadUI, looked up for
/// the current light or dark theme each time, so a theme change shows on the next update.
/// </summary>
public static class Palette
{
    public static IBrush Good => Find("GoodBrush", "#4CC38A");

    public static IBrush Warn => Find("WarnBrush", "#F2C14E");

    public static IBrush Bad => Find("BadBrush", "#F0565A");

    public static IBrush Heart => Find("HeartBrush", "#F0565A");

    /// <summary>Normal text.</summary>
    public static IBrush Text => Find("ForegroundColor", "#E5E5E5");

    /// <summary>Secondary text.</summary>
    public static IBrush Muted => Find("MutedColor", "#A0A0A9");

    public static IBrush Find(string key, string fallback, StyledElement? scope = null)
    {
        var theme = scope?.ActualThemeVariant ?? Application.Current?.ActualThemeVariant ?? ThemeVariant.Dark;
        object? value = null;
        var found = scope is not null
            ? scope.TryFindResource(key, theme, out value)
            : Application.Current?.TryGetResource(key, theme, out value) == true;
        // ShadUI's resources are colors, the app's are brushes.
        return value switch
        {
            IBrush brush when found => brush,
            Avalonia.Media.Color color when found => new SolidColorBrush(color),
            _ => new SolidColorBrush(Avalonia.Media.Color.Parse(fallback)),
        };
    }

    public static Color Color(string key, string fallback, StyledElement? scope = null) =>
        Find(key, fallback, scope) is ISolidColorBrush solid ? solid.Color : Avalonia.Media.Color.Parse(fallback);
}
