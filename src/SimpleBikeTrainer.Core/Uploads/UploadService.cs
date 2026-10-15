using SimpleBikeTrainer.Intervals;

namespace SimpleBikeTrainer.Uploads;

public enum UploadTarget
{
    Intervals,
    Garmin,
}

/// <summary>Sends a saved ride to Intervals.icu and Garmin Connect, which can pass it on to Strava. Each destination fails on its own.</summary>
public sealed class UploadService(AppConfig config, GarminClient? garmin = null)
{
    public bool IsAvailable(UploadTarget target) => target switch
    {
        UploadTarget.Intervals => config.HasApiKey,
        UploadTarget.Garmin => garmin?.HasTokens ?? GarminClient.IsSignedIn(config),
        _ => false,
    };

    /// <summary>The destinations ticked in settings that are set up.</summary>
    public IReadOnlyList<UploadTarget> DefaultTargets() =>
    [
        .. new[]
        {
            (UploadTarget.Intervals, config.Uploads.Intervals),
            (UploadTarget.Garmin, config.Uploads.Garmin),
        }.Where(t => t.Item2 && IsAvailable(t.Item1)).Select(t => t.Item1),
    ];

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

    private async Task<UploadOutcome> SendAsync(UploadTarget target, string fitPath, string name, long? eventId, string? description, CancellationToken ct)
    {
        try
        {
            switch (target)
            {
                case UploadTarget.Intervals:
                    using (var intervals = new IntervalsClient(config.Intervals.ApiKey, config.Intervals.AthleteId))
                    {
                        var result = await intervals.UploadActivityAsync(fitPath, name, eventId, description, ct);
                        return UploadOutcome.Uploaded(result.ActivityId is { } id ? $"https://intervals.icu/activities/{id}" : null);
                    }

                case UploadTarget.Garmin:
                    if (garmin is not null)
                    {
                        return await garmin.UploadAsync(fitPath, ct);
                    }

                    using (var client = new GarminClient(config))
                    {
                        return await client.UploadAsync(fitPath, ct);
                    }

                default:
                    throw new ArgumentOutOfRangeException(nameof(target));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Anything but the caller's own cancel, so one destination's failure never takes the others down.
            return UploadOutcome.Failed(e.Message);
        }
    }
}
