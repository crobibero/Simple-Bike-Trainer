using SimpleBikeTrainer.Intervals;

namespace SimpleBikeTrainer.Uploads;

public enum UploadTarget
{
    Intervals,
    Garmin,
}

/// <summary>Sends a saved ride to Intervals.icu and Garmin Connect, which can pass it on to Strava. Each destination fails on its own.</summary>
public sealed class UploadService(AppConfig config, IHttpClientFactory httpClientFactory, GarminClient? garmin = null)
{
    private readonly IReadOnlyList<IUploadDestination> _destinations =
    [
        new IntervalsDestination(config, httpClientFactory),
        new GarminDestination(config, httpClientFactory, garmin),
    ];

    public bool IsAvailable(UploadTarget target) => Find(target)?.IsAvailable ?? false;

    /// <summary>The destinations ticked in settings that are set up.</summary>
    public IReadOnlyList<UploadTarget> DefaultTargets() =>
        [.. _destinations.Where(d => d.IsChosen && d.IsAvailable).Select(d => d.Target)];

    public async Task<UploadOutcome> UploadAsync(UploadTarget target, string fitPath, string name, long? eventId, string? description, CancellationToken ct)
    {
        var outcome = await SendAsync(target, fitPath, name, eventId, description, ct);
        if (outcome.Succeeded)
        {
            try
            {
                Recording.RideInfo.RecordUpload(fitPath, target.ToString(), outcome.Url);
            }
            catch (IOException)
            {
                // The upload happened; only the history page's note of it is missing.
            }
        }

        return outcome;
    }

    private IUploadDestination? Find(UploadTarget target) => _destinations.FirstOrDefault(d => d.Target == target);

    private async Task<UploadOutcome> SendAsync(UploadTarget target, string fitPath, string name, long? eventId, string? description, CancellationToken ct)
    {
        try
        {
            var destination = Find(target) ?? throw new ArgumentOutOfRangeException(nameof(target));
            return await destination.SendAsync(fitPath, name, eventId, description, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Anything but the caller's own cancel, so one destination's failure never takes the others down.
            return UploadOutcome.Failed(e.Message);
        }
    }
}

internal interface IUploadDestination
{
    UploadTarget Target { get; }

    /// <summary>Whether it is set up: an API key, or a Garmin sign-in.</summary>
    bool IsAvailable { get; }

    /// <summary>Whether it is ticked in settings, to upload to after every ride.</summary>
    bool IsChosen { get; }

    Task<UploadOutcome> SendAsync(string fitPath, string name, long? eventId, string? description, CancellationToken ct);
}

internal sealed class IntervalsDestination(AppConfig config, IHttpClientFactory httpClientFactory) : IUploadDestination
{
    public UploadTarget Target => UploadTarget.Intervals;

    public bool IsAvailable => config.HasApiKey;

    public bool IsChosen => config.Uploads.Intervals;

    public async Task<UploadOutcome> SendAsync(string fitPath, string name, long? eventId, string? description, CancellationToken ct)
    {
        using var intervals = new IntervalsClient(httpClientFactory, config.Intervals.ApiKey, config.Intervals.AthleteId);
        var result = await intervals.UploadActivityAsync(fitPath, name, eventId, description, ct);
        return UploadOutcome.Uploaded(result.ActivityId is { } id ? $"https://intervals.icu/activities/{id}" : null);
    }
}

/// <summary>Garmin Connect, through the caller's signed-in client when it has one, or one from the saved tokens.</summary>
internal sealed class GarminDestination(AppConfig config, IHttpClientFactory httpClientFactory, GarminClient? garmin) : IUploadDestination
{
    public UploadTarget Target => UploadTarget.Garmin;

    public bool IsAvailable => garmin?.HasTokens ?? GarminClient.IsSignedIn(config);

    public bool IsChosen => config.Uploads.Garmin;

    /// <summary>Garmin takes the file alone; the name, planned workout and description stay behind.</summary>
    public async Task<UploadOutcome> SendAsync(string fitPath, string name, long? eventId, string? description, CancellationToken ct)
    {
        if (garmin is not null)
        {
            return await garmin.UploadAsync(fitPath, ct);
        }

        using var client = new GarminClient(config, httpClientFactory);
        return await client.UploadAsync(fitPath, ct);
    }
}
