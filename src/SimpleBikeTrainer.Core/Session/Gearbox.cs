namespace SimpleBikeTrainer.Session;

/// <summary>
/// Virtual gears for a single-speed setup such as the Zwift Cog. The chain never moves; the app works out the road
/// speed from cadence and the virtual gear ratio, then asks the trainer for the power that speed costs.
/// </summary>
public sealed class Gearbox
{
    /// <summary>24 ratios (chainring teeth / cog teeth), close to Zwift's virtual gears.</summary>
    public static readonly double[] Ratios =
    [
        0.75, 0.87, 0.99, 1.11, 1.23, 1.38, 1.53, 1.68, 1.86, 2.04, 2.22, 2.40,
        2.61, 2.82, 3.03, 3.24, 3.49, 3.74, 3.99, 4.24, 4.54, 4.84, 5.14, 5.49,
    ];

    /// <summary>700x28c wheel circumference in meters.</summary>
    public const double WheelCircumference = 2.136;

    public Gearbox(int gear = 12)
    {
        Gear = Math.Clamp(gear, 1, Ratios.Length);
    }

    /// <summary>1 is the easiest gear.</summary>
    public int Gear { get; private set; }

    public double Ratio => Ratios[Gear - 1];

    public int GearCount => Ratios.Length;

    public bool Shift(int delta)
    {
        var next = Math.Clamp(Gear + delta, 1, Ratios.Length);
        var changed = next != Gear;
        Gear = next;
        return changed;
    }

    public double SpeedMs(double cadenceRpm) => Math.Max(0, cadenceRpm) / 60 * Ratio * WheelCircumference;
}
