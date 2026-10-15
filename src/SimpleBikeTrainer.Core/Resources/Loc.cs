using System.Globalization;

namespace SimpleBikeTrainer.Resources;

/// <summary>Fills a resource string's placeholders with the current culture's number and date formats.</summary>
public static class Loc
{
    public static string Format(string format, params ReadOnlySpan<object?> args) =>
        string.Format(CultureInfo.CurrentCulture, format, args);
}
