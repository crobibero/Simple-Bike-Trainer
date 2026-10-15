using SimpleBikeTrainer.Devices;

namespace SimpleBikeTrainer.Session;

public static class TrainerSimulation
{
    /// <summary>The most rolling resistance FE-C can carry (FTMS goes to 0.0255).</summary>
    public const double MaxCrr = 0.0127;

    /// <summary>The most wind resistance FE-C can carry, in kg/m (FTMS goes to 2.55).</summary>
    public const double MaxWindResistance = 1.86;

    public const double MaxGrade = 40;

    /// <summary>At gear factor k the trainer must resist with k·F(k·v): grade and Crr scale by k, wind by k³. What FE-C cannot carry goes into grade.</summary>
    public static RoadSimulation Road(double gradePercent, RiderModel rider, double gearFactor, double flywheelSpeedMs)
    {
        var k = Math.Max(0.1, gearFactor);
        var crr = rider.Crr * k;
        var wind = rider.WindResistance * k * k * k;
        var sentCrr = Math.Min(crr, MaxCrr);
        var sentWind = Math.Min(wind, MaxWindResistance);
        var weight = rider.TotalMassKg * Physics.G;
        var leftOver = (crr - sentCrr) + ((wind - sentWind) * flywheelSpeedMs * flywheelSpeedMs / weight);
        var grade = Math.Clamp((gradePercent * k) + (leftOver * 100), -MaxGrade, MaxGrade);

        // Rounded so a road that has not changed is not sent again.
        return new RoadSimulation(
            Math.Round(grade, 1),
            Math.Round(sentCrr, 4),
            Math.Round(sentWind, 2),
            Math.Round(rider.TotalMassKg - rider.BikeMassKg, 1),
            Math.Round(rider.BikeMassKg, 1));
    }
}
