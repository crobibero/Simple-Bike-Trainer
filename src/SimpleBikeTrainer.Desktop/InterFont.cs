using Avalonia;
using Avalonia.Media.Fonts;

namespace SimpleBikeTrainer.Desktop;

/// <summary>
/// Inter as fonts:Inter, in the weights the app and ShadUI draw with: regular, medium, semibold and bold. The
/// Avalonia.Fonts.Inter package adds thin and light as well, 600 KB that nothing uses.
/// </summary>
internal static class InterFont
{
    public static AppBuilder WithInterFont(this AppBuilder builder) =>
        builder.ConfigureFonts(fonts => fonts.AddFontCollection(
            new EmbeddedFontCollection(new Uri("fonts:Inter"), new Uri("avares://simple-bike-trainer/Assets/Fonts"))));
}
