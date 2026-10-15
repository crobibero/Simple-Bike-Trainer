using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Desktop.ViewModels;

/// <summary>
/// A copy of everything on the ride page, taken on the ride loop's thread so the UI thread never reads the metrics
/// while the loop is updating them.
/// </summary>
public sealed record RideFrame(
    RideSnapshot Snapshot,
    int Seconds,
    double AveragePower,
    int MaxPower,
    double NormalizedPower,
    double IntensityFactor,
    double Tss,
    double KiloJoules,
    double Kilocalories,
    double WBalance,
    int WPrime,
    double? AverageHr,
    int? MaxHr,
    double? AverageCadence,
    double AverageSpeedMs,
    double ElevationGain,
    IReadOnlyDictionary<int, double> Peaks,
    int[] PowerZones,
    int[] HrZones,
    int LapIndex,
    int LapSeconds,
    double LapAveragePower,
    int LapMaxPower,
    double? LapAverageHr,
    double? LapAverageCadence,
    double? LapCompliance)
{
    public static RideFrame From(RideSnapshot s)
    {
        var m = s.Metrics;
        var lap = s.Lap;
        return new RideFrame(
            s,
            m.Seconds,
            m.AveragePower,
            m.MaxPower,
            m.NormalizedPower,
            m.IntensityFactor,
            m.TrainingStressScore,
            m.KiloJoules,
            m.Kilocalories,
            m.WBalance,
            m.WPrime,
            m.AverageHeartRate,
            m.MaxHeartRate,
            m.AverageCadence,
            m.AverageSpeedMs,
            m.ElevationGain,
            new Dictionary<int, double>(m.Peaks),
            [.. m.TimeInPowerZones],
            [.. m.TimeInHrZones],
            lap.Index,
            lap.Seconds,
            lap.AveragePower,
            lap.MaxPower,
            lap.AverageHeartRate,
            lap.AverageCadence,
            lap.Compliance);
    }
}

public static class Format
{
    public static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    public static string Clock(long seconds) => Clock(TimeSpan.FromSeconds(seconds));

    public static string Target(WorkoutStep? step) =>
        step is null ? string.Empty
        : (step.Power?.ToString() ?? Strings.Common_FreeRide) + (step.Cadence is { } c ? $"  ·  {c}" : string.Empty);
}
