using Avalonia.Media;

namespace SimpleBikeTrainer.Desktop.Controls;

/// <summary>The app's bike mark: a line drawing on a 64×64 grid, drawn with a round 4-unit stroke. Assets/icon.svg draws the app icon from it.</summary>
public static class BikeIcon
{
    public static readonly Geometry Geometry = StreamGeometry.Parse(
        "M4,42 a10,10 0 1,0 20,0 a10,10 0 1,0 -20,0 Z " +   // rear wheel
        "M40,42 a10,10 0 1,0 20,0 a10,10 0 1,0 -20,0 Z " +  // front wheel
        "M14,42 L30,42 L25,24 Z " +                          // chainstay, seat tube, seatstay
        "M30,42 L46,26 L50,42 " +                            // down tube, fork
        "M25,24 L46,26 " +                                   // top tube
        "M25,24 L24,19 M19,19 L29,19 " +                     // seat post, saddle
        "M46,26 L44,17 L50,16");                             // stem, bars
}
