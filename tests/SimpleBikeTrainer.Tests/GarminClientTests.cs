using System.Net;
using System.Text.Json.Nodes;
using System.Text;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Uploads;

namespace SimpleBikeTrainer.Tests;

public sealed class GarminClientTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trainer-garmin").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string TokenDir => Path.Combine(_dir, "garmin");

    [Fact]
    public async Task SignInWithMfaSavesOnlyTokens()
    {
        var garmin = new FakeGarmin { Mfa = true };
        using (var client = new GarminClient(TokenDir, garmin))
        {
            Assert.True(await client.SignInAsync("rider@example.com", "secret-password", TestContext.Current.CancellationToken));
            Assert.False(client.HasTokens);
            await client.SubmitMfaAsync(" 123456 ", TestContext.Current.CancellationToken);
            Assert.True(client.HasTokens);
        }

        // The login went to the iOS endpoint, and the code to its MFA endpoint.
        var login = Assert.Single(garmin.Requests, r => r.Path == "/mobile/api/login");
        Assert.Contains("clientId=GCM_IOS_DARK", login.Query, StringComparison.Ordinal);
        Assert.Equal("rider@example.com", JsonNode.Parse(login.Body)!["username"]!.GetValue<string>());
        var mfa = Assert.Single(garmin.Requests, r => r.Path == "/mobile/api/mfa/verifyCode");
        Assert.Equal("123456", JsonNode.Parse(mfa.Body)!["mfaVerificationCode"]!.GetValue<string>());
        var exchange = Assert.Single(garmin.Requests, r => r.Path == "/di-oauth2-service/oauth/token");
        Assert.Contains("service_ticket=ST-1", exchange.Body, StringComparison.Ordinal);

        // Tokens only, owner-only.
        var file = Path.Combine(TokenDir, "garmin_tokens.json");
        var text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("secret-password", text, StringComparison.Ordinal);
        Assert.DoesNotContain("rider@example.com", text, StringComparison.Ordinal);
        Assert.Equal(garmin.AccessToken, JsonNode.Parse(text)!["di_token"]!.GetValue<string>());
        Assert.Equal("GARMIN_CONNECT_MOBILE_ANDROID_DI_2025Q2", JsonNode.Parse(text)!["di_client_id"]!.GetValue<string>());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(TokenDir));
        }

        // A new client signs in from the saved tokens.
        using var again = new GarminClient(TokenDir, garmin);
        Assert.Equal("Test Rider", await again.GetNameAsync(TestContext.Current.CancellationToken));
        Assert.Equal($"Bearer {garmin.AccessToken}", garmin.Requests[^1].Authorization);
    }

    [Fact]
    public async Task SignInWithoutMfaFallsBackToTheWebLoginWhenBlocked()
    {
        var garmin = new FakeGarmin { BlockMobile = true };
        using var client = new GarminClient(TokenDir, garmin, portalDelay: TimeSpan.Zero);
        Assert.False(await client.SignInAsync("rider@example.com", "pw", TestContext.Current.CancellationToken));
        Assert.True(client.HasTokens);
        Assert.Contains(garmin.Requests, r => r.Path == "/portal/sso/en-US/sign-in");
        var exchange = Assert.Single(garmin.Requests, r => r.Path == "/di-oauth2-service/oauth/token");
        Assert.Contains(Uri.EscapeDataString("https://connect.garmin.com/app"), exchange.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnMfaCodeTheWebLoginAcceptsIsExchangedForTheWebLogin()
    {
        var garmin = new FakeGarmin { Mfa = true, BlockMobileVerify = true };
        using var client = new GarminClient(TokenDir, garmin);
        Assert.True(await client.SignInAsync("rider@example.com", "secret-password", TestContext.Current.CancellationToken));
        await client.SubmitMfaAsync("123456", TestContext.Current.CancellationToken);

        Assert.True(client.HasTokens);
        var exchange = garmin.Requests.First(r => r.Path == "/di-oauth2-service/oauth/token");
        Assert.Contains("service_ticket=ST-PORTAL", exchange.Body, StringComparison.Ordinal);
        Assert.Contains("service_url=" + Uri.EscapeDataString("https://connect.garmin.com/app"), exchange.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongPasswordIsReported()
    {
        var garmin = new FakeGarmin { WrongPassword = true };
        using var client = new GarminClient(TokenDir, garmin);
        var e = await Assert.ThrowsAsync<GarminException>(() => client.SignInAsync("rider@example.com", "bad", TestContext.Current.CancellationToken));
        Assert.Contains("password", e.Message, StringComparison.Ordinal);
        Assert.False(client.HasTokens);
    }

    [Fact]
    public async Task UploadsAndRecognizesDuplicates()
    {
        var garmin = new FakeGarmin();
        using var client = new GarminClient(TokenDir, garmin);
        await client.SignInAsync("rider@example.com", "pw", TestContext.Current.CancellationToken);
        var fit = Path.Combine(_dir, "ride.fit");
        await File.WriteAllBytesAsync(fit, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var first = await client.UploadAsync(fit, TestContext.Current.CancellationToken);
        Assert.Equal(UploadState.Uploaded, first.State);
        Assert.Equal("https://connect.garmin.com/modern/activity/987654", first.Url);
        var upload = Assert.Single(garmin.Requests, r => r.Path == "/upload-service/upload");
        Assert.Equal("multipart/form-data", upload.ContentType);
        Assert.Contains("filename=ride.fit", upload.Body, StringComparison.Ordinal);

        var second = await client.UploadAsync(fit, TestContext.Current.CancellationToken);
        Assert.Equal(UploadState.Duplicate, second.State);
        Assert.Equal("https://connect.garmin.com/modern/activity/987654", second.Url);
    }

    [Fact]
    public async Task AnUnexpectedFailureReplyIsReported()
    {
        var garmin = new FakeGarmin { FailUpload = """{"message": {"code": 7}, "detailedImportResult": {"failures": {}}}""" };
        using var client = new GarminClient(TokenDir, garmin);
        await client.SignInAsync("rider@example.com", "pw", TestContext.Current.CancellationToken);
        var fit = Path.Combine(_dir, "ride.fit");
        await File.WriteAllBytesAsync(fit, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var e = await Assert.ThrowsAsync<GarminException>(() => client.UploadAsync(fit, TestContext.Current.CancellationToken));
        Assert.Contains("400", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADamagedTokenFileFailsTheUploadInsteadOfThrowing()
    {
        Directory.CreateDirectory(TokenDir);
        await File.WriteAllTextAsync(Path.Combine(TokenDir, "garmin_tokens.json"), "{not json", TestContext.Current.CancellationToken);
        var fit = Path.Combine(_dir, "ride.fit");
        await File.WriteAllBytesAsync(fit, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        using var client = new GarminClient(TokenDir, new FakeGarmin());

        var outcome = await new UploadService(new AppConfig(), client).UploadAsync(UploadTarget.Garmin, fit, "Ride", null, null, TestContext.Current.CancellationToken);

        Assert.Equal(UploadState.Failed, outcome.State);
    }

    [Fact]
    public async Task ARejectedTokenIsRefreshedOnce()
    {
        var garmin = new FakeGarmin();
        using var client = new GarminClient(TokenDir, garmin);
        await client.SignInAsync("rider@example.com", "pw", TestContext.Current.CancellationToken);
        var old = garmin.AccessToken;
        garmin.RejectToken = old;

        Assert.Equal("Test Rider", await client.GetNameAsync(TestContext.Current.CancellationToken));
        Assert.NotEqual(old, garmin.AccessToken);
        Assert.Contains(garmin.Requests, r => r.Body.Contains("grant_type=refresh_token", StringComparison.Ordinal));
        Assert.Contains(garmin.AccessToken, await File.ReadAllTextAsync(Path.Combine(TokenDir, "garmin_tokens.json"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignOutDeletesTheTokens()
    {
        var garmin = new FakeGarmin();
        using var client = new GarminClient(TokenDir, garmin);
        await client.SignInAsync("rider@example.com", "pw", TestContext.Current.CancellationToken);
        client.SignOut();
        Assert.False(client.HasTokens);
        await Assert.ThrowsAsync<GarminException>(() => client.GetNameAsync(TestContext.Current.CancellationToken));
    }

    private sealed record Captured(string Path, string Query, string? Authorization, string? ContentType, string Body);

    /// <summary>Plays Garmin's SSO, token and API hosts.</summary>
    private sealed class FakeGarmin : HttpMessageHandler
    {
        private int _tokenNumber;
        private readonly HashSet<string> _uploaded = [];

        public FakeGarmin() => AccessToken = Jwt();

        public bool Mfa { get; init; }

        public bool BlockMobile { get; init; }

        public bool WrongPassword { get; init; }

        public bool BlockMobileVerify { get; init; }

        public string? FailUpload { get; init; }

        public string AccessToken { get; private set; }

        public string? RejectToken { get; set; }

        public List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Captured(uri.AbsolutePath, uri.Query, request.Headers.Authorization?.ToString(), request.Content?.Headers.ContentType?.MediaType, body));

            return (uri.Host, uri.AbsolutePath) switch
            {
                ("sso.garmin.com", "/mobile/api/login") when BlockMobile => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html>challenge</html>") },
                ("sso.garmin.com", "/mobile/api/login" or "/portal/api/login") => Json(
                    WrongPassword ? """{"responseStatus": {"type": "INVALID_USERNAME_PASSWORD"}}"""
                    : Mfa ? """{"responseStatus": {"type": "MFA_REQUIRED"}, "customerMfaInfo": {"mfaLastMethodUsed": "email"}}"""
                    : """{"responseStatus": {"type": "SUCCESSFUL"}, "serviceTicketId": "ST-1"}"""),
                ("sso.garmin.com", "/portal/sso/en-US/sign-in") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") },
                ("sso.garmin.com", "/mobile/api/mfa/verifyCode") when BlockMobileVerify => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html>challenge</html>") },
                ("sso.garmin.com", "/portal/api/mfa/verifyCode") => Json("""{"responseStatus": {"type": "SUCCESSFUL"}, "serviceTicketId": "ST-PORTAL"}"""),
                ("sso.garmin.com", "/mobile/api/mfa/verifyCode") => Json(
                    body.Contains("123456", StringComparison.Ordinal)
                        ? """{"responseStatus": {"type": "SUCCESSFUL"}, "serviceTicketId": "ST-1"}"""
                        : """{"responseStatus": {"type": "INVALID_MFA_CODE"}}"""),
                ("diauth.garmin.com", _) => Token(body),
                ("connectapi.garmin.com", _) when request.Headers.Authorization?.Parameter is var token && (token != AccessToken || token == RejectToken)
                    => new HttpResponseMessage(HttpStatusCode.Unauthorized),
                ("connectapi.garmin.com", "/userprofile-service/socialProfile") => Json("""{"displayName": "rider123", "fullName": "Test Rider"}"""),
                ("connectapi.garmin.com", "/upload-service/upload") when FailUpload is not null => Json(FailUpload, HttpStatusCode.BadRequest),
                ("connectapi.garmin.com", "/upload-service/upload") => _uploaded.Add(body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    ? Json("""{"detailedImportResult": {"successes": [{"internalId": 987654}], "failures": []}}""", HttpStatusCode.Created)
                    : Json("""{"detailedImportResult": {"successes": [], "failures": [{"internalId": 987654, "messages": [{"content": "Duplicate Activity."}]}]}}""", HttpStatusCode.Conflict),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private HttpResponseMessage Token(string form)
        {
            if (form.Contains("grant_type=refresh_token", StringComparison.Ordinal))
            {
                if (!form.Contains("refresh_token=refresh-", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }

                AccessToken = Jwt();
                RejectToken = null;
            }

            return Json($$"""{"access_token": "{{AccessToken}}", "refresh_token": "refresh-{{_tokenNumber}}", "expires_in": 3600}""");
        }

        private string Jwt()
        {
            _tokenNumber++;
            static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            return $"{Part("""{"alg":"RS256"}""")}.{Part($$"""{"client_id":"GARMIN_CONNECT_MOBILE_ANDROID_DI_2025Q2","exp":{{exp}},"n":{{_tokenNumber}}}""")}.sig";
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

public class HeartRateZonesTests
{
    [Fact]
    public void ZonesFromMaxHeartRate()
    {
        Assert.Equal([114, 133, 152, 171, 190], HeartRateZones.FromMax(190)!);
        Assert.Null(HeartRateZones.FromMax(0));
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(130, 0)]
    [InlineData(131, 1)]
    [InlineData(175, 4)]
    [InlineData(230, 6)]
    public void ZoneOfAHeartRate(int bpm, int zone) =>
        Assert.Equal(zone, HeartRateZones.IndexOf([130, 150, 160, 170, 180, 190, 200], bpm));

    [Fact]
    public void ZoneNames()
    {
        Assert.Equal("Z3 Tempo", HeartRateZones.Name(2, 7));
        Assert.Equal("Z5 Maximum", HeartRateZones.Name(4, 5));
        Assert.Equal("Z2", HeartRateZones.Name(1, 3));
        Assert.Equal("Z2 Easy", HeartRateZones.Name(1, 7, ["Rest", "Easy"]));
    }
}
