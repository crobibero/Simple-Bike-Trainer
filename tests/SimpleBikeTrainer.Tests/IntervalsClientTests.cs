using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SimpleBikeTrainer.Intervals;
using SimpleBikeTrainer.Session;

namespace SimpleBikeTrainer.Tests;

public class IntervalsClientTests
{
    [Fact]
    public async Task ListsPlannedRidesWithResolvedTargets()
    {
        var handler = new FakeHandler(_ => Json($"[{File.ReadAllText(Path.Combine("Fixtures", "event-sweet-spot.json"))}, {{\"id\": 5, \"type\": \"Run\", \"name\": \"Easy\", \"start_date_local\": \"2026-12-02T00:00:00\"}}]"));
        using var client = new IntervalsClient("secret", "i123", handler);

        var rides = await client.GetPlannedRidesAsync(new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 8), TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://intervals.icu/api/v1/athlete/i123/events?oldest=2026-12-02&newest=2026-12-08&category=WORKOUT&resolve=true", request.Uri);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("API_KEY:secret")), request.Authorization);

        // The run is filtered out.
        var ride = Assert.Single(rides);
        Assert.Equal(91234567, ride.Id);
        Assert.Equal(new DateOnly(2026, 12, 2), ride.Date);
        Assert.Equal("VirtualRide", ride.Type);
        Assert.Equal(9, ride.Workout.Steps.Count);
    }

    [Fact]
    public async Task ReadsRideSettings()
    {
        var handler = new FakeHandler(_ => Json("""{"ftp": 250, "indoor_ftp": 240, "w_prime": 18000, "power_zones": [55,75,90,105,120,150,999], "hr_zones": [130,150,160,170,180,190,200], "hr_zone_names": ["Recovery","Aerobic","Tempo","SubThreshold","SuperThreshold","Aerobic Capacity","Anaerobic"], "max_hr": 195}"""));
        using var client = new IntervalsClient("k", "0", handler);

        var settings = await client.GetRideSettingsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("https://intervals.icu/api/v1/athlete/0/sport-settings/Ride", handler.Requests[0].Uri);
        Assert.Equal(240, settings.TrainerFtp);
        Assert.Equal(18000, settings.WPrime);
        Assert.Equal(7, settings.PowerZones!.Length);
        Assert.Equal(195, settings.MaxHr);
        Assert.Equal("Z4 SubThreshold", HeartRateZones.Name(3, settings.HrZones!.Length, settings.HrZoneNames));
    }

    [Fact]
    public async Task UploadsTheFitFilePairedWithTheEventAndItsDescription()
    {
        var handler = new FakeHandler(_ => Json("""{"id": "i98765"}"""));
        using var client = new IntervalsClient("k", "0", handler);
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);

        var result = await client.UploadActivityAsync(path, "Sweet spot 3x12", 91234567, "3x12 at 88-93%", TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://intervals.icu/api/v1/athlete/0/activities?name=Sweet%20spot%203x12&device_name=Simple%20Bike%20Trainer&paired_event_id=91234567&description=3x12%20at%2088-93%25", request.Uri);
        Assert.StartsWith("multipart/form-data", request.ContentType, StringComparison.Ordinal);
        Assert.Contains("name=file", request.Body, StringComparison.Ordinal);
        Assert.Equal("i98765", result.ActivityId);
        File.Delete(path);
    }

    [Fact]
    public async Task SetsFtp()
    {
        var handler = new FakeHandler(_ => Json("{}"));
        using var client = new IntervalsClient("k", "0", handler);

        await client.SetFtpAsync(245, alsoIndoor: true, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("""{"ftp":245,"indoor_ftp":245}""", handler.Requests[0].Body);
    }

    [Fact]
    public async Task ExplainsABadKey()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("nope") });
        using var client = new IntervalsClient("bad", "0", handler);

        var e = await Assert.ThrowsAsync<IntervalsException>(() => client.GetRideSettingsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("API key", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetriesAReadThatHitAServerError()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json("""{"ftp": 250}"""));
        using var client = new IntervalsClient(Factory(handler), "k");

        var settings = await client.GetRideSettingsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(250, settings.Ftp);
    }

    [Fact]
    public async Task DoesNotRetryAnUpload()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") });
        using var client = new IntervalsClient(Factory(handler), "k");
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IntervalsException>(() => client.UploadActivityAsync(path, "Ride", null, null, TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
        File.Delete(path);
    }

    /// <summary>The app's registration, with only the network swapped for <paramref name="handler"/>.</summary>
    private static IHttpClientFactory Factory(HttpMessageHandler handler)
    {
        var services = new ServiceCollection().AddTrainerHttpClients();
        services.AddHttpClient(HttpClientNames.Intervals).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Captured(HttpMethod Method, string Uri, string? Authorization, string? ContentType, string Body);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Captured(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.MediaType,
                body));
            return respond(request);
        }
    }
}
