namespace SimpleBikeTrainer.Session;

/// <summary>The rider and bike, for turning power into road speed and back.</summary>
public sealed record RiderModel(
    double TotalMassKg = 89,
    double Crr = 0.004,
    double CdA = 0.32,
    double AirDensity = 1.225,
    double DrivetrainEfficiency = 0.97,
    double BikeMassKg = 9)
{
    /// <summary>In kg/m: what FTMS and FE-C call the wind resistance coefficient.</summary>
    public double WindResistance => 0.5 * AirDensity * CdA;
}

/// <summary>Steady-state road cycling: rolling resistance, gravity and air drag, no wind and no acceleration.</summary>
public static class Physics
{
    public const double G = 9.80665;
    private const double MaxSpeedMs = 40;

    /// <summary>Power at the pedals to hold <paramref name="speedMs"/> on a grade.</summary>
    public static double PowerForSpeed(double speedMs, double gradePercent, RiderModel rider)
    {
        if (speedMs <= 0)
        {
            return 0;
        }

        var theta = Math.Atan(gradePercent / 100);
        var force = (rider.TotalMassKg * G * ((rider.Crr * Math.Cos(theta)) + Math.Sin(theta)))
            + (0.5 * rider.AirDensity * rider.CdA * speedMs * speedMs);
        return force * speedMs / rider.DrivetrainEfficiency;
    }

    /// <summary>The steady speed a power holds on a grade. Bisection, since power rises with speed past any descent's coast speed.</summary>
    public static double SpeedForPower(double watts, double gradePercent, RiderModel rider)
    {
        double lo = 0, hi = MaxSpeedMs;
        if (PowerForSpeed(hi, gradePercent, rider) < watts)
        {
            return hi;
        }

        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            if (PowerForSpeed(mid, gradePercent, rider) < watts)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return (lo + hi) / 2;
    }
}
