using System.Text.Json;
using Dynastream.Fit;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Workouts;
using DateTime = System.DateTime;
using File = System.IO.File;

namespace SimpleBikeTrainer.Recording;

/// <summary>
/// What the FIT file does not hold: the ride's name, the planned workout it followed, and where it was uploaded. It is
/// saved next to the ride as <c>ride.fit.json</c> when recording starts, so an interrupted ride keeps it too.
/// </summary>
public sealed record RideInfo
{
    public string Name { get; init; } = CoreStrings.Ride_DefaultName;

    /// <summary>The Intervals.icu event, to pair an upload with.</summary>
    public long? EventId { get; init; }

    public int Ftp { get; init; }

    public DateTime StartUtc { get; init; }

    /// <summary>The workout as ridden, so a past ride's chart shows its steps or route.</summary>
    public Workout? Workout { get; init; }

    /// <summary>Destination name to the activity's URL (empty when the service gave none).</summary>
    public Dictionary<string, string> Uploads { get; init; } = [];

    public static string PathFor(string fitPath) => fitPath + ".json";

    public static RideInfo? Load(string fitPath)
    {
        try
        {
            return File.Exists(PathFor(fitPath)) ? JsonSerializer.Deserialize(File.ReadAllText(PathFor(fitPath)), RideInfoJsonContext.Default.RideInfo) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save(string fitPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(fitPath))!);
        File.WriteAllText(PathFor(fitPath), JsonSerializer.Serialize(this, RideInfoJsonContext.Default.RideInfo));
    }

    /// <summary>Notes a finished upload, keeping what the file already had.</summary>
    public static void RecordUpload(string fitPath, string destination, string? url)
    {
        var info = Load(fitPath) ?? new RideInfo { Name = Path.GetFileNameWithoutExtension(fitPath) };
        info.Uploads[destination] = url ?? string.Empty;
        info.Save(fitPath);
    }
}

/// <summary>A saved ride as the history page lists it.</summary>
public sealed record RideSummary(
    string FitPath,
    string Name,
    DateTime StartUtc,
    int MovingSeconds,
    double DistanceMeters,
    double AveragePower,
    double TrainingStressScore,
    double ElevationGain,
    IReadOnlyDictionary<string, string> Uploads);

/// <summary>A ride whose app stopped before it finished: only the 5-minute checkpoint is on disk.</summary>
public sealed record InterruptedRide(string PartialPath, string FitPath, string Name, DateTime StartUtc, int MovingSeconds);

/// <summary>A ride read back from its FIT file, with the samples for its chart.</summary>
public sealed record LoadedRide(RideResult Result, IReadOnlyList<Sample> Samples);

/// <summary>The rides folder: finished rides, interrupted ones, and reading a ride back.</summary>
public sealed class RideLibrary(string ridesDirectory)
{
    public RideLibrary(AppConfig config)
        : this(config.RidesDirectory)
    {
    }

    /// <summary>Finished rides, newest first.</summary>
    public IReadOnlyList<RideSummary> List()
    {
        if (!Directory.Exists(ridesDirectory))
        {
            return [];
        }

        var rides = new List<RideSummary>();
        foreach (var path in Directory.EnumerateFiles(ridesDirectory, "*.fit"))
        {
            try
            {
                var fit = FitFile.Read(path, sessionOnly: true);
                if (fit.Session is not { } session)
                {
                    continue;
                }

                var info = RideInfo.Load(path);
                rides.Add(new RideSummary(
                    path,
                    info?.Name ?? Path.GetFileNameWithoutExtension(path),
                    session.GetStartTime()?.GetDateTime() ?? File.GetCreationTimeUtc(path),
                    (int)Math.Round(session.GetTotalTimerTime() ?? 0),
                    session.GetTotalDistance() ?? 0,
                    session.GetAvgPower() ?? 0,
                    session.GetTrainingStressScore() ?? 0,
                    session.GetTotalAscent() ?? 0,
                    info?.Uploads ?? []));
            }
            catch (Exception e) when (e is IOException or FitException or UnauthorizedAccessException)
            {
                // Not a readable ride; leave it out.
            }
        }

        return [.. rides.OrderByDescending(r => r.StartUtc)];
    }

    /// <summary>Checkpoints whose ride never finished: the app or computer stopped mid-ride.</summary>
    public IReadOnlyList<InterruptedRide> FindInterrupted()
    {
        if (!Directory.Exists(ridesDirectory))
        {
            return [];
        }

        var found = new List<InterruptedRide>();
        foreach (var partial in Directory.EnumerateFiles(ridesDirectory, "*.fit.partial"))
        {
            var fitPath = partial[..^".partial".Length];
            if (File.Exists(fitPath))
            {
                continue;
            }

            try
            {
                var session = FitFile.Read(partial, sessionOnly: true).Session;
                var info = RideInfo.Load(fitPath);
                found.Add(new InterruptedRide(
                    partial,
                    fitPath,
                    info?.Name ?? Path.GetFileNameWithoutExtension(fitPath),
                    session?.GetStartTime()?.GetDateTime() ?? File.GetCreationTimeUtc(partial),
                    (int)Math.Round(session?.GetTotalTimerTime() ?? 0)));
            }
            catch (Exception e) when (e is IOException or FitException)
            {
                // A checkpoint cut off mid-write cannot be read; it stays on disk untouched.
            }
        }

        return [.. found.OrderByDescending(r => r.StartUtc)];
    }

    /// <summary>Turns a checkpoint into a finished ride and reads it.</summary>
    public LoadedRide Recover(InterruptedRide ride, int wPrime)
    {
        File.Move(ride.PartialPath, ride.FitPath);
        return Load(ride.FitPath, wPrime);
    }

    /// <summary>Deletes a checkpoint and its sidecar.</summary>
    public static void Discard(InterruptedRide ride)
    {
        File.Delete(ride.PartialPath);
        File.Delete(RideInfo.PathFor(ride.FitPath));
    }

    /// <summary>Reads a ride back: its metrics from the recorded seconds, its laps from the FIT laps.</summary>
    public static LoadedRide Load(string fitPath, int wPrime, int[]? powerZones = null, int[]? hrZones = null)
    {
        var fit = FitFile.Read(fitPath, sessionOnly: false);
        var info = RideInfo.Load(fitPath);
        var ftp = info?.Ftp is > 0 ? info.Ftp : fit.Session?.GetThresholdPower() ?? 200;
        var metrics = new RideMetrics(ftp, wPrime, powerZones, hrZones);
        var start = fit.Session?.GetStartTime()?.GetDateTime() ?? fit.Records.FirstOrDefault().Time;
        var end = start.AddSeconds(fit.Session?.GetTotalElapsedTime() ?? fit.Records.Count);

        var lapStarts = fit.LapStarts.Count > 0 ? fit.LapStarts : [start];
        var laps = lapStarts.Select((t, i) => new LapMetrics(t, i)).ToList();
        var lap = 0;
        foreach (var sample in fit.Records)
        {
            metrics.Add(sample);
            while (lap + 1 < lapStarts.Count && sample.Time > lapStarts[lap + 1])
            {
                lap++;
            }

            laps[lap].Add(sample);
        }

        var workout = info?.Workout ?? new Workout(info?.Name ?? Path.GetFileNameWithoutExtension(fitPath), []);
        workout = workout with { Name = info?.Name ?? workout.Name, EventId = info?.EventId ?? workout.EventId };
        var result = new RideResult(workout, metrics, fitPath, start, end, laps.Count(l => l.Seconds > 0), null)
        {
            Laps = [.. laps.Where(l => l.Seconds > 0)],
        };
        return new LoadedRide(result, fit.Records);
    }
}

/// <summary>The parts of a FIT activity this app reads back.</summary>
internal sealed class FitFile
{
    public List<Sample> Records { get; } = [];

    public List<DateTime> LapStarts { get; } = [];

    public SessionMesg? Session { get; private set; }

    public static FitFile Read(string path, bool sessionOnly)
    {
        var file = new FitFile();
        var decode = new Decode();
        var broadcaster = new MesgBroadcaster();
        decode.MesgEvent += broadcaster.OnMesg;
        broadcaster.SessionMesgEvent += (_, e) => file.Session = (SessionMesg)e.mesg;
        if (!sessionOnly)
        {
            broadcaster.LapMesgEvent += (_, e) =>
            {
                if (((LapMesg)e.mesg).GetStartTime() is { } t)
                {
                    file.LapStarts.Add(t.GetDateTime());
                }
            };
            broadcaster.RecordMesgEvent += (_, e) =>
            {
                var r = (RecordMesg)e.mesg;
                if (r.GetTimestamp() is not { } time)
                {
                    return;
                }

                file.Records.Add(new Sample(
                    time.GetDateTime(),
                    r.GetPower() ?? 0,
                    r.GetCadence(),
                    r.GetHeartRate(),
                    r.GetSpeed() ?? 0,
                    r.GetDistance() ?? 0,
                    null,
                    r.GetEnhancedAltitude() ?? r.GetAltitude()));
            };
        }

        using var stream = File.OpenRead(path);
        if (!decode.IsFIT(stream) || !decode.Read(stream))
        {
            throw new FitException(Loc.Format(CoreStrings.Fit_Unreadable, Path.GetFileName(path)));
        }

        file.LapStarts.Sort();
        return file;
    }
}
