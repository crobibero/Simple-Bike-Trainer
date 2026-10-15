using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Workouts;

public enum PowerUnit
{
    Watts,
    PercentFtp,
}

/// <summary>A power target: a fixed value when Low == High, a range (ridden at its midpoint), or a ramp.</summary>
public sealed record PowerTarget(double Low, double High, PowerUnit Unit, bool Ramp = false)
{
    /// <summary>The target in watts at a point in the step, 0 at its start and 1 at its end.</summary>
    public int WattsAt(double fraction, int ftp)
    {
        var value = Ramp ? Low + ((High - Low) * Math.Clamp(fraction, 0, 1)) : (Low + High) / 2;
        return (int)Math.Round(Unit == PowerUnit.Watts ? value : value / 100.0 * ftp);
    }

    public override string ToString()
    {
        var unit = Unit == PowerUnit.Watts ? "W" : "%";
        var value = Low.Equals(High) ? $"{Low:0}{unit}" : $"{Low:0}-{High:0}{unit}";
        return Ramp ? Loc.Format(CoreStrings.Power_Ramp, value) : value;
    }
}

public sealed record CadenceTarget(int Low, int High)
{
    public int Mid => (Low + High) / 2;

    public override string ToString() => Low == High ? Loc.Format(CoreStrings.Cadence_One, Low) : Loc.Format(CoreStrings.Cadence_Range, Low, High);
}

/// <summary>One step of a workout. A step without a power target is ridden in free ride on the virtual gears.</summary>
public sealed record WorkoutStep(string Label, int DurationSeconds, PowerTarget? Power, CadenceTarget? Cadence = null)
{
    public bool IsFreeRide => Power is null;
}

/// <summary>A workout flattened to a list of steps, with repeats expanded.</summary>
public sealed record Workout(string Name, IReadOnlyList<WorkoutStep> Steps)
{
    /// <summary>The Intervals.icu event this workout came from, to pair the uploaded ride with.</summary>
    public long? EventId { get; init; }

    /// <summary>The FTP Intervals.icu resolved the workout's %FTP targets against.</summary>
    public int? Ftp { get; init; }

    public string? Description { get; init; }

    /// <summary>A built-in ramp FTP test: it ends when the rider can no longer hold the ramp.</summary>
    public bool IsRampTest { get; init; }

    /// <summary>A virtual route ridden on the gears; its grade follows the rider's distance.</summary>
    public Course? Course { get; init; }

    public int TotalSeconds => Steps.Sum(s => s.DurationSeconds);

    /// <summary>An open-ended ride on the virtual gears.</summary>
    public static Workout FreeRide() => new(CoreStrings.Workout_FreeRide, [new WorkoutStep(CoreStrings.Workout_FreeRide, int.MaxValue, null)]);

    /// <summary>A ride along a route on the virtual gears.</summary>
    public static Workout Route(string name, Course course) => new(name, [new WorkoutStep(CoreStrings.Workout_Route, int.MaxValue, null)]) { Course = course };
}
