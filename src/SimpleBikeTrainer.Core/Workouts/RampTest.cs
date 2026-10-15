using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Workouts;

/// <summary>
/// A ramp FTP test in the style of Zwift's: 5 minutes of warmup, then 1-minute steps that rise by 20 W until the
/// rider cannot hold them. FTP is 75% of the best 1-minute power.
/// </summary>
public static class RampTest
{
    public const int WarmupSeconds = 300;
    public const int StepSeconds = 60;
    public const int CooldownSeconds = 300;
    public const double FtpFactor = 0.75;

    public static Workout Create(int startWatts = 100, int stepWatts = 20, int maxWatts = 1000)
    {
        var steps = new List<WorkoutStep>
        {
            new(CoreStrings.Ramp_Warmup, WarmupSeconds, new PowerTarget(startWatts * 0.6, startWatts, PowerUnit.Watts, Ramp: true)),
        };

        for (var watts = startWatts; watts <= maxWatts; watts += stepWatts)
        {
            steps.Add(new WorkoutStep(Loc.Format(CoreStrings.Ramp_Step, watts), StepSeconds, new PowerTarget(watts, watts, PowerUnit.Watts)));
        }

        steps.Add(new WorkoutStep(CoreStrings.Ramp_Cooldown, CooldownSeconds, new PowerTarget(startWatts * 0.8, startWatts * 0.8, PowerUnit.Watts)));
        return new Workout(CoreStrings.Ramp_Name, steps) { IsRampTest = true };
    }

    /// <summary>The 1-minute steps; the warmup and cooldown are 5 minutes. The label is translated, so it is not checked.</summary>
    public static bool IsRampStep(WorkoutStep step) => step.DurationSeconds == StepSeconds && step.Power is { Ramp: false };

    public static int EstimateFtp(double bestOneMinutePower) => (int)Math.Round(bestOneMinutePower * FtpFactor);

    /// <summary>
    /// True when a planned workout is a calendar entry for an FTP test, like trainsync's <c>test 45</c>: no
    /// steps, and "FTP" or "ramp" in the name. The words are matched in the Intervals.icu name, so they stay in English.
    /// </summary>
    public static bool LooksLikeTestEntry(Workout workout) =>
        workout.Steps.Count == 0
        && (workout.Name.Contains("FTP", StringComparison.OrdinalIgnoreCase)
            || workout.Name.Contains("ramp", StringComparison.OrdinalIgnoreCase));
}
