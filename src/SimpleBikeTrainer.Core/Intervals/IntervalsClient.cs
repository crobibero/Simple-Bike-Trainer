using System.Globalization;
using System.Net.Http.Headers;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Intervals;

/// <summary>The ride settings this app needs from Intervals.icu Settings &gt; Ride.</summary>
public sealed record RideSettings(int? Ftp, int? IndoorFtp, int? WPrime, int[]? PowerZones, int[]? HrZones, int? MaxHr)
{
    /// <summary>The FTP for a trainer ride: the indoor FTP when the athlete set one.</summary>
    public int? TrainerFtp => IndoorFtp is > 0 ? IndoorFtp : Ftp;

    /// <summary>The athlete's names for <see cref="HrZones"/>, e.g. "Recovery".</summary>
    public string[]? HrZoneNames { get; init; }
}

public sealed record PlannedRide(long Id, DateOnly Date, string Type, Workout Workout);

public sealed record UploadResult(string? ActivityId);

public sealed class IntervalsException(string message, HttpStatusCode? status = null) : Exception(message)
{
    /// <summary>The HTTP status Intervals.icu answered with, when the request got that far.</summary>
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Intervals.icu API client. Auth is HTTP Basic with username API_KEY and the personal API key as password.</summary>
public sealed class IntervalsClient : IDisposable
{
    public const string BaseUrl = "https://intervals.icu/api/v1/";

    private static readonly string[] RideTypes = ["Ride", "VirtualRide", "MountainBikeRide", "GravelRide"];

    private readonly HttpClient _http;
    private readonly string _athlete;

    public IntervalsClient(string apiKey, string athleteId = "0", HttpMessageHandler? handler = null)
    {
        _athlete = athleteId;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(30);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"API_KEY:{apiKey}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("simple-bike-trainer/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Planned rides between two dates, inclusive, with targets resolved to watts.</summary>
    public async Task<IReadOnlyList<PlannedRide>> GetPlannedRidesAsync(DateOnly oldest, DateOnly newest, CancellationToken ct = default)
    {
        var url = $"athlete/{_athlete}/events?oldest={oldest:yyyy-MM-dd}&newest={newest:yyyy-MM-dd}&category=WORKOUT&resolve=true";
        using var doc = await GetJsonAsync(url, ct);
        var rides = new List<PlannedRide>();
        foreach (var evt in doc.RootElement.EnumerateArray())
        {
            var type = evt.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (!RideTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var date = evt.TryGetProperty("start_date_local", out var d) && d.GetString() is { Length: >= 10 } s
                ? DateOnly.ParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture)
                : oldest;
            var workout = WorkoutDocParser.ParseEvent(evt);
            rides.Add(new PlannedRide(workout.EventId ?? 0, date, type, workout));
        }

        return [.. rides.OrderBy(r => r.Date)];
    }

    public async Task<PlannedRide> GetPlannedRideAsync(long eventId, CancellationToken ct = default)
    {
        // A single event has no resolve option, so fetch its day's list, which does.
        using var doc = await GetJsonAsync($"athlete/{_athlete}/events/{eventId}", ct);
        var date = doc.RootElement.TryGetProperty("start_date_local", out var d) && d.GetString() is { Length: >= 10 } s
            ? DateOnly.ParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : DateOnly.FromDateTime(DateTime.Today);
        var rides = await GetPlannedRidesAsync(date, date, ct);
        return rides.FirstOrDefault(r => r.Id == eventId)
            ?? throw new IntervalsException(Loc.Format(CoreStrings.Intervals_NotRide, eventId));
    }

    public async Task<RideSettings> GetRideSettingsAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"athlete/{_athlete}/sport-settings/Ride", ct);
        var root = doc.RootElement;
        return new RideSettings(
            Int(root, "ftp"),
            Int(root, "indoor_ftp"),
            Int(root, "w_prime"),
            IntArray(root, "power_zones"),
            IntArray(root, "hr_zones"),
            Int(root, "max_hr"))
        {
            HrZoneNames = root.TryGetProperty("hr_zone_names", out var names) && names.ValueKind == JsonValueKind.Array
                ? [.. names.EnumerateArray().Select(n => n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty)]
                : null,
        };
    }

    /// <summary>The athlete's weight in kg from Intervals.icu, if set.</summary>
    public async Task<double?> GetWeightAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"athlete/{_athlete}", ct);
        return doc.RootElement.TryGetProperty("icu_weight", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetDouble() : null;
    }

    /// <summary>Sets the ride FTP. Also sets the indoor FTP when the athlete keeps a separate one.</summary>
    public async Task SetFtpAsync(int ftp, bool alsoIndoor, CancellationToken ct = default)
    {
        var body = new JsonObject { ["ftp"] = ftp };
        if (alsoIndoor)
        {
            body["indoor_ftp"] = ftp;
        }

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PutAsync($"athlete/{_athlete}/sport-settings/Ride", content, ct);
        await EnsureSuccessAsync(response, "update FTP", ct);
    }

    public async Task<UploadResult> UploadActivityAsync(string fitPath, string name, long? pairedEventId, string? description, CancellationToken ct = default)
    {
        var query = $"athlete/{_athlete}/activities?name={Uri.EscapeDataString(name)}&device_name={Uri.EscapeDataString(AppInfo.Name)}";
        if (pairedEventId is { } id and > 0)
        {
            query += $"&paired_event_id={id}";
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            query += $"&description={Uri.EscapeDataString(description)}";
        }

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(fitPath, ct));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(fitPath));

        using var response = await _http.PostAsync(query, form, ct);
        await EnsureSuccessAsync(response, "upload", ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            return new UploadResult(doc.RootElement.TryGetProperty("id", out var a) ? a.ToString() : null);
        }
        catch (JsonException)
        {
            return new UploadResult(null);
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, $"GET {url.Split('?')[0]}", ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(ct);
        var hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? CoreStrings.Intervals_KeyHint
            : string.Empty;
        throw new IntervalsException(Loc.Format(CoreStrings.Intervals_Failed, what, (int)response.StatusCode, hint, detail[..Math.Min(detail.Length, 300)]), response.StatusCode);
    }

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)Math.Round(v.GetDouble()) : null;

    private static int[]? IntArray(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => (int)Math.Round(x.GetDouble()))]
            : null;
}
