using System.Globalization;
using System.Text.Json;
using SimpleBikeTrainer.Intervals;
using SimpleBikeTrainer.Recording;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Session;

/// <summary>The athlete numbers a ride needs, from Intervals.icu when it answers and the config file otherwise.</summary>
public sealed record Athlete(int Ftp, int WPrime, double WeightKg, double BikeWeightKg, int[]? PowerZones, int[]? HrZones, RideSettings? Settings)
{
    public static Athlete FromConfig(AppConfig config) =>
        new(config.Rider.Ftp, config.Rider.WPrime, config.Rider.WeightKg, config.Rider.BikeWeightKg, null, HeartRateZones.FromMax(config.Rider.MaxHeartRate), null);

    public static async Task<Athlete> LoadAsync(AppConfig config, IntervalsClient? intervals, CancellationToken ct)
    {
        if (intervals is null)
        {
            return FromConfig(config);
        }

        var settings = await intervals.GetRideSettingsAsync(ct);
        var weight = await intervals.GetWeightAsync(ct);
        return new Athlete(
            settings.TrainerFtp ?? config.Rider.Ftp,
            settings.WPrime ?? config.Rider.WPrime,
            weight ?? config.Rider.WeightKg,
            config.Rider.BikeWeightKg,
            settings.PowerZones,
            settings.HrZones is { Length: > 0 } zones ? zones : HeartRateZones.FromMax(settings.MaxHr ?? config.Rider.MaxHeartRate),
            settings);
    }

    public RideOptions Options(AppConfig config, Workout workout, double speedFactor = 1, int? ftpOverride = null)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
        return new RideOptions(
            ftpOverride ?? Ftp,
            WPrime,
            new RiderModel(TotalMassKg: WeightKg + BikeWeightKg),
            UnusedPath(config.RidesDirectory, $"{stamp}-{RideSetup.Slug(workout.Name)}"),
            PowerZones,
            HrZones,
            speedFactor);
    }

    /// <summary>A FIT path no earlier ride has used, so a second ride of the same workout in the same minute does not overwrite the first.</summary>
    private static string UnusedPath(string directory, string name)
    {
        var path = Path.Combine(directory, name + ".fit");
        for (var n = 2; File.Exists(path) || File.Exists(RideInfo.PathFor(path)) || File.Exists(path + ".partial"); n++)
        {
            path = Path.Combine(directory, $"{name}-{n}.fit");
        }

        return path;
    }
}

public static class RideSetup
{
    /// <summary>A planned ride as it is ridden: the ramp test for an FTP test entry, free ride for one without steps.</summary>
    public static Workout ForPlanned(Workout planned, int rampStartWatts = 100)
    {
        if (RampTest.LooksLikeTestEntry(planned))
        {
            return RampTest.Create(rampStartWatts) with { Name = planned.Name, EventId = planned.EventId };
        }

        return planned.Steps.Count == 0 ? Workout.FreeRide() with { Name = planned.Name, EventId = planned.EventId } : planned;
    }

    /// <summary>Reads Intervals.icu workout text, or JSON of an event or a <c>workout_doc</c>.</summary>
    public static Workout LoadFile(string path)
    {
        var text = File.ReadAllText(path);
        var name = Path.GetFileNameWithoutExtension(path);
        if (text.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            return root.TryGetProperty("workout_doc", out _)
                ? WorkoutDocParser.ParseEvent(root)
                : WorkoutDocParser.Parse(root, name);
        }

        var workout = WorkoutTextParser.Parse(text, name);
        return workout.Steps.Count == 0 ? throw new FormatException(Loc.Format(CoreStrings.Workout_NoSteps, path)) : workout;
    }

    /// <summary>A generated loop route, with its length and climbing typed in the display units.</summary>
    public static Workout Route(UnitSystem units, double distance, double climb, int seed)
    {
        var course = CourseGenerator.Generate(units.DistanceToMeters(distance), units.ElevationToMeters(climb), seed);
        return Workout.Route(Loc.Format(CoreStrings.Workout_RouteName, units.FormatDistance(course.DistanceMeters, "0.#"), units.FormatElevation(course.ElevationGain)), course);
    }

    public static string Slug(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length > 40 ? slug[..40] : slug;
    }
}
