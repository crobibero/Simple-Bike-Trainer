using System.Text.Json;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Workouts;

/// <summary>
/// Reads the <c>workout_doc</c> JSON that Intervals.icu returns for a planned workout. With <c>resolve=true</c> each
/// step also carries <c>_power</c> in watts, which this prefers over the %FTP target.
/// </summary>
public static class WorkoutDocParser
{
    // Midpoints of the default Coggan power zones, in %FTP, for steps written as Z1-Z7 and not resolved.
    private static readonly double[] ZoneMidpoints = [45, 65, 83, 98, 113, 135, 160];

    // A ride step given as a distance becomes a time at this speed, since the step player runs on time.
    private const double DistanceStepSpeedMs = 30 / 3.6;

    public static Workout Parse(JsonElement doc, string name)
    {
        var steps = new List<WorkoutStep>();
        if (doc.TryGetProperty("steps", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            AddSteps(list, steps, labelSuffix: null);
        }

        int? ftp = doc.TryGetProperty("ftp", out var f) && f.TryGetInt32(out var ftpValue) && ftpValue > 0 ? ftpValue : null;
        return new Workout(name, steps) { Ftp = ftp };
    }

    /// <summary>Parses one Intervals.icu calendar event (category WORKOUT).</summary>
    public static Workout ParseEvent(JsonElement evt)
    {
        var name = evt.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : CoreStrings.Workout_DefaultName;
        var workout = evt.TryGetProperty("workout_doc", out var doc) && doc.ValueKind == JsonValueKind.Object
            ? Parse(doc, name)
            : new Workout(name, []);
        return workout with
        {
            EventId = evt.TryGetProperty("id", out var id) && id.TryGetInt64(out var idValue) ? idValue : null,
            Description = evt.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
        };
    }

    private static void AddSteps(JsonElement list, List<WorkoutStep> output, string? labelSuffix)
    {
        foreach (var step in list.EnumerateArray())
        {
            if (step.TryGetProperty("reps", out var reps) && reps.TryGetInt32(out var count)
                && step.TryGetProperty("steps", out var inner) && inner.ValueKind == JsonValueKind.Array)
            {
                for (var i = 1; i <= count; i++)
                {
                    AddSteps(inner, output, $"{i}/{count}");
                }

                continue;
            }

            var parsed = ParseStep(step);
            output.Add(labelSuffix is null ? parsed : parsed with { Label = $"{parsed.Label} {labelSuffix}" });
        }
    }

    private static WorkoutStep ParseStep(JsonElement step)
    {
        var duration = 0;
        if (step.TryGetProperty("duration", out var d) && d.TryGetDouble(out var seconds))
        {
            duration = (int)Math.Round(seconds);
        }
        else if (step.TryGetProperty("distance", out var dist) && dist.TryGetDouble(out var meters))
        {
            duration = (int)Math.Round(meters / DistanceStepSpeedMs);
        }

        var label = step.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString())
            ? t.GetString()!.Trim()
            : Flag(step, "warmup") ? "Warmup" : Flag(step, "cooldown") ? "Cooldown" : "Step";

        var ramp = Flag(step, "ramp");
        PowerTarget? power = null;
        if (!Flag(step, "freeride") && !Flag(step, "maxeffort"))
        {
            power = ResolvedPower(step, ramp) ?? Power(step, ramp);
        }

        return new WorkoutStep(label, duration, power, Cadence(step));
    }

    private static PowerTarget? ResolvedPower(JsonElement step, bool ramp) =>
        step.TryGetProperty("_power", out var p) && Range(p) is var (lo, hi) ? new PowerTarget(lo, hi, PowerUnit.Watts, ramp) : null;

    private static PowerTarget? Power(JsonElement step, bool ramp)
    {
        if (!step.TryGetProperty("power", out var p) || Range(p) is not var (lo, hi))
        {
            return null;
        }

        var units = p.TryGetProperty("units", out var u) ? u.GetString() : "%ftp";
        return units switch
        {
            "w" => new PowerTarget(lo, hi, PowerUnit.Watts, ramp),
            "power_zone" => new PowerTarget(Zone(lo), Zone(hi), PowerUnit.PercentFtp, ramp),
            "%ftp" or null => new PowerTarget(lo, hi, PowerUnit.PercentFtp, ramp),
            _ => null, // %MMP and other units need data this app does not have.
        };
    }

    private static CadenceTarget? Cadence(JsonElement step) =>
        step.TryGetProperty("cadence", out var c) && Range(c) is var (lo, hi)
            ? new CadenceTarget((int)Math.Round(lo), (int)Math.Round(hi))
            : null;

    private static (double Low, double High)? Range(JsonElement target)
    {
        if (target.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (Number(target, "start") is { } start && Number(target, "end") is { } end)
        {
            return (start, end);
        }

        return Number(target, "value") is { } value ? (value, value) : null;
    }

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double Zone(double zone) => ZoneMidpoints[Math.Clamp((int)Math.Round(zone) - 1, 0, ZoneMidpoints.Length - 1)];
}
