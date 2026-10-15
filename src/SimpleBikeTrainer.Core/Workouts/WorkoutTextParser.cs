using System.Globalization;
using System.Text.RegularExpressions;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Workouts;

/// <summary>
/// Parses the common subset of Intervals.icu workout text, which is what trainsync writes:
/// <code>
/// - Warmup 10m 55-65% 80rpm
///
/// Sweet spot 3x
/// - Sweet spot 12m 88-93%
/// - Recover 4m 55-65% 80rpm
///
/// - Cooldown 5m ramp 65-40%
/// </code>
/// Lengths: 1h, 10m, 30s, 1m30s. Targets: 75%, 55-65%, ramp 50-75%, 200w, 180-220w, Z2. Cadence: 90rpm, 85-95rpm.
/// "freeride" makes a step without a target. Other words are the step's label.
/// </summary>
public static partial class WorkoutTextParser
{
    private static readonly double[] ZoneMidpoints = [45, 65, 83, 98, 113, 135, 160];

    public static Workout Parse(string text, string name)
    {
        var steps = new List<WorkoutStep>();
        List<WorkoutStep>? block = null;
        var reps = 0;

        void CloseBlock()
        {
            if (block is null)
            {
                return;
            }

            for (var i = 1; i <= reps; i++)
            {
                steps.AddRange(block.Select(s => s with { Label = $"{s.Label} {i}/{reps}" }));
            }

            block = null;
        }

        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0)
            {
                CloseBlock();
                continue;
            }

            if (line.StartsWith('-'))
            {
                var step = ParseStep(line[1..].Trim(), lineNumber);
                (block ?? steps).Add(step);
                continue;
            }

            var header = RepeatHeader().Match(line);
            if (header.Success)
            {
                CloseBlock();
                block = [];
                reps = int.Parse(header.Groups["reps"].Value, CultureInfo.InvariantCulture);
            }

            // Any other line is a comment or description.
        }

        CloseBlock();
        return new Workout(name, steps);
    }

    private static WorkoutStep ParseStep(string body, int lineNumber)
    {
        var label = new List<string>();
        int? duration = null;
        PowerTarget? power = null;
        CadenceTarget? cadence = null;
        var ramp = false;
        var freeRide = false;

        foreach (var token in body.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            Match m;
            if (duration is null && (m = Length().Match(token)).Success)
            {
                duration = (Int(m, "h") * 3600) + (Int(m, "m") * 60) + Int(m, "s");
            }
            else if (token.Equals("ramp", StringComparison.OrdinalIgnoreCase))
            {
                ramp = true;
            }
            else if (token.Equals("freeride", StringComparison.OrdinalIgnoreCase))
            {
                freeRide = true;
            }
            else if ((m = Percent().Match(token)).Success)
            {
                power = new PowerTarget(Num(m, "lo"), Num(m, "hi", Num(m, "lo")), PowerUnit.PercentFtp);
            }
            else if ((m = Watts().Match(token)).Success)
            {
                power = new PowerTarget(Num(m, "lo"), Num(m, "hi", Num(m, "lo")), PowerUnit.Watts);
            }
            else if ((m = Zone().Match(token)).Success)
            {
                var lo = ZoneMidpoints[Math.Clamp((int)Num(m, "lo") - 1, 0, 6)];
                var hi = ZoneMidpoints[Math.Clamp((int)Num(m, "hi", Num(m, "lo")) - 1, 0, 6)];
                power = new PowerTarget(lo, hi, PowerUnit.PercentFtp);
            }
            else if ((m = Rpm().Match(token)).Success)
            {
                cadence = new CadenceTarget((int)Num(m, "lo"), (int)Num(m, "hi", Num(m, "lo")));
            }
            else
            {
                label.Add(token);
            }
        }

        if (duration is null or <= 0)
        {
            throw new FormatException(Loc.Format(CoreStrings.Workout_NoDuration, lineNumber, body));
        }

        if (power is not null && ramp)
        {
            power = power with { Ramp = true };
        }

        return new WorkoutStep(label.Count > 0 ? string.Join(' ', label) : CoreStrings.Workout_DefaultStep, duration.Value, freeRide ? null : power, cadence);
    }

    private static int Int(Match m, string group) =>
        m.Groups[group].Success ? int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture) : 0;

    private static double Num(Match m, string group, double fallback = 0) =>
        m.Groups[group].Success ? double.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture) : fallback;

    [GeneratedRegex(@"^(?:(?<h>\d+)h)?(?:(?<m>\d+)m)?(?:(?<s>\d+)s)?$")]
    private static partial Regex Length();

    [GeneratedRegex(@"^(?<lo>\d+(?:\.\d+)?)(?:-(?<hi>\d+(?:\.\d+)?))?%$")]
    private static partial Regex Percent();

    [GeneratedRegex(@"^(?<lo>\d+)(?:-(?<hi>\d+))?w$", RegexOptions.IgnoreCase)]
    private static partial Regex Watts();

    [GeneratedRegex(@"^Z(?<lo>[1-7])(?:-Z(?<hi>[1-7]))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Zone();

    [GeneratedRegex(@"^(?<lo>\d+)(?:-(?<hi>\d+))?rpm$", RegexOptions.IgnoreCase)]
    private static partial Regex Rpm();

    [GeneratedRegex(@"^(?<label>.*?)\s*(?<reps>\d+)x$", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatHeader();
}
