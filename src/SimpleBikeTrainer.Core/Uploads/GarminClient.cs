using System.Net.Http.Headers;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Uploads;

public sealed class GarminException(string message) : Exception(message);

/// <summary>
/// Garmin Connect sign-in and upload. Garmin has no public upload API, so this signs in as the Garmin Connect iOS app
/// does (the flow python-garminconnect 0.3.17 uses) and stores only the OAuth tokens, never the password.
/// </summary>
public sealed class GarminClient : IDisposable
{
    internal const string Sso = "https://sso.garmin.com";
    internal const string ConnectApi = "https://connectapi.garmin.com";
    internal const string DiTokenUrl = "https://diauth.garmin.com/di-oauth2-service/oauth/token";

    private const string IosClientId = "GCM_IOS_DARK";
    private const string IosService = "https://mobile.integration.garmin.com/gcm/ios";
    private const string PortalClientId = "GarminConnect";
    private const string PortalService = "https://connect.garmin.com/app";
    private const string IosUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_7 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148";
    private const string DesktopUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string DiGrantType = "https://connectapi.garmin.com/di-oauth2-service/oauth/grant/service_ticket";

    // Garmin rotates these; the first one it accepts wins.
    private static readonly string[] DiClientIds =
    [
        "GARMIN_CONNECT_MOBILE_ANDROID_DI_2025Q2",
        "GARMIN_CONNECT_MOBILE_ANDROID_DI_2024Q4",
        "GARMIN_CONNECT_MOBILE_ANDROID_DI",
        "GARMIN_CONNECT_MOBILE_IOS_DI",
    ];

    private readonly string _tokenDirectory;
    private readonly HttpClient _sso;
    private readonly HttpClient _api;
    private readonly TimeSpan _portalDelay;
    private Tokens? _tokens;
    private PendingMfa? _pending;

    public GarminClient(AppConfig config)
        : this(config.GarminTokenDirectory)
    {
    }

    /// <param name="handler">For tests; it then carries both the sign-in and the API traffic.</param>
    /// <param name="portalDelay">The pause before the fallback login; Cloudflare flags a GET followed at once by a POST.</param>
    public GarminClient(string tokenDirectory, HttpMessageHandler? handler = null, TimeSpan? portalDelay = null)
    {
        _tokenDirectory = tokenDirectory;
        _portalDelay = portalDelay ?? TimeSpan.FromSeconds(12);

        // Sign-in keeps cookies: the MFA step needs the SSO session from the login. API calls carry their auth in a
        // header and keep no cookies.
        _sso = handler is null
            ? new HttpClient(new SocketsHttpHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true })
            : new HttpClient(handler, disposeHandler: false);
        _api = handler is null
            ? new HttpClient(new SocketsHttpHandler { UseCookies = false })
            : new HttpClient(handler, disposeHandler: false);
        _sso.Timeout = _api.Timeout = TimeSpan.FromSeconds(60);
    }

    public string TokenFile => Path.Combine(_tokenDirectory, TokenFileName);

    public bool HasTokens => File.Exists(TokenFile);

    private const string TokenFileName = "garmin_tokens.json";

    /// <summary>Whether a Garmin sign-in is saved, without making a client.</summary>
    public static bool IsSignedIn(AppConfig config) => File.Exists(Path.Combine(config.GarminTokenDirectory, TokenFileName));

    /// <summary>Signs in. Returns true when Garmin sent an MFA code, which then goes to <see cref="SubmitMfaAsync"/>.</summary>
    public async Task<bool> SignInAsync(string email, string password, CancellationToken ct)
    {
        _pending = null;
        LoginResult result;
        try
        {
            result = await LoginAsync(Flow.Mobile, email, password, ct);
        }
        catch (BlockedException)
        {
            // Cloudflare sometimes challenges the mobile endpoint; the web login is in another rate-limit bucket.
            result = await LoginAsync(Flow.Portal, email, password, ct);
        }

        if (result.Ticket is { } ticket)
        {
            await FinishAsync(ticket, result.Flow, ct);
            return false;
        }

        _pending = new PendingMfa(result.Flow, result.MfaMethod ?? "email");
        return true;
    }

    /// <summary>Completes a sign-in that needed an MFA code. A wrong code can be retried.</summary>
    public async Task SubmitMfaAsync(string code, CancellationToken ct)
    {
        var pending = _pending ?? throw new GarminException(CoreStrings.Garmin_NoPendingCode);
        var body = new JsonObject
        {
            ["mfaMethod"] = pending.Method,
            ["mfaVerificationCode"] = code.Trim(),
            ["rememberMyBrowser"] = true,
            ["reconsentList"] = new JsonArray(),
            ["mfaSetup"] = false,
        };

        // Both endpoints share the SSO session but sit in different rate-limit buckets.
        var failures = new List<string>();
        foreach (var flow in pending.Flow == Flow.Mobile ? new[] { Flow.Mobile, Flow.Portal } : [Flow.Portal, Flow.Mobile])
        {
            using var request = SsoPost(flow, $"{Sso}/{flow.Path}/api/mfa/verifyCode", body);
            using var response = await _sso.SendAsync(request, ct);
            var reply = await ReadJsonAsync(response, ct);
            if (reply is null)
            {
                failures.Add($"HTTP {(int)response.StatusCode}");
                continue;
            }

            var type = reply["responseStatus"]?["type"]?.GetValue<string>();
            if (type == "SUCCESSFUL" && reply["serviceTicketId"]?.GetValue<string>() is { } ticket)
            {
                _pending = null;
                await FinishAsync(ticket, flow, ct);
                return;
            }

            failures.Add(type ?? $"HTTP {(int)response.StatusCode}");
        }

        throw new GarminException(Loc.Format(CoreStrings.Garmin_BadCode, string.Join(", ", failures.Distinct())));
    }

    /// <summary>The signed-in account's name, which also checks that the stored session still works.</summary>
    public async Task<string> GetNameAsync(CancellationToken ct)
    {
        using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{ConnectApi}/userprofile-service/socialProfile"), ct);
        await EnsureSuccessAsync(response, ct);
        var profile = await ReadJsonAsync(response, ct);
        return profile?["fullName"]?.GetValue<string>() is { Length: > 0 } full ? full
            : profile?["displayName"]?.GetValue<string>() ?? string.Empty;
    }

    public void SignOut()
    {
        _tokens = null;
        _pending = null;
        if (File.Exists(TokenFile))
        {
            File.Delete(TokenFile);
        }
    }

    public async Task<UploadOutcome> UploadAsync(string fitPath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(fitPath, ct);
        var name = Path.GetFileName(fitPath);
        using var response = await SendApiAsync(() =>
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var form = new MultipartFormDataContent { { file, "file", name } };
            return new HttpRequestMessage(HttpMethod.Post, $"{ConnectApi}/upload-service/upload") { Content = form };
        }, ct);

        var reply = await ReadJsonAsync(response, ct);
        var result = reply?["detailedImportResult"];
        var id = (result?["successes"] as JsonArray)?.FirstOrDefault()?["internalId"]?.ToString()
            ?? (result?["failures"] as JsonArray)?.FirstOrDefault()?["internalId"]?.ToString();
        var url = id is null ? null : $"https://connect.garmin.com/modern/activity/{id}";

        // Garmin answers 409 when the account already has this activity.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return UploadOutcome.Duplicate(url);
        }

        if (!response.IsSuccessStatusCode)
        {
            var message = Text(((result?["failures"] as JsonArray)?.FirstOrDefault()?["messages"] as JsonArray)?.FirstOrDefault()?["content"])
                ?? Text(reply?["message"]);
            throw new GarminException(Loc.Format(CoreStrings.Garmin_UploadFailed, (int)response.StatusCode, message is null ? string.Empty : Loc.Format(CoreStrings.Garmin_Detail, message)));
        }

        return UploadOutcome.Uploaded(url);
    }

    public void Dispose()
    {
        _sso.Dispose();
        _api.Dispose();
    }

    private async Task<LoginResult> LoginAsync(Flow flow, string email, string password, CancellationToken ct)
    {
        if (flow == Flow.Portal)
        {
            // The web login wants its sign-in page fetched first, and a pause like a person typing.
            using var page = new HttpRequestMessage(HttpMethod.Get, $"{Sso}/portal/sso/en-US/sign-in?clientId={PortalClientId}&service={Uri.EscapeDataString(PortalService)}");
            page.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
            page.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            using var _ = await _sso.SendAsync(page, ct);
            await Task.Delay(_portalDelay, ct);
        }

        var body = new JsonObject { ["username"] = email, ["password"] = password, ["rememberMe"] = true, ["captchaToken"] = string.Empty };
        using var request = SsoPost(flow, $"{Sso}/{flow.Path}/api/login", body);
        using var response = await _sso.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            throw flow == Flow.Mobile
                ? new BlockedException()
                : new GarminException(Loc.Format(CoreStrings.Garmin_Refused, (int)response.StatusCode));
        }

        var reply = await ReadJsonAsync(response, ct);
        var type = reply?["responseStatus"]?["type"]?.GetValue<string>();
        switch (type)
        {
            case "SUCCESSFUL":
                return new LoginResult(flow, reply!["serviceTicketId"]?.GetValue<string>() ?? throw new GarminException(CoreStrings.Garmin_NoTicket), null);
            case "MFA_REQUIRED":
                return new LoginResult(flow, null, reply!["customerMfaInfo"]?["mfaLastMethodUsed"]?.GetValue<string>());
            case "INVALID_USERNAME_PASSWORD":
                throw new GarminException(CoreStrings.Garmin_BadPassword);
            case "CAPTCHA_REQUIRED" or null when flow == Flow.Mobile:
                throw new BlockedException();
            default:
                throw new GarminException(Loc.Format(CoreStrings.Garmin_SignInFailed, (int)response.StatusCode, type ?? CoreStrings.Garmin_NoReply));
        }
    }

    /// <summary>Swaps the service ticket for a token, checks the API accepts it, and saves it.</summary>
    private async Task FinishAsync(string ticket, Flow flow, CancellationToken ct)
    {
        Tokens? tokens = null;
        foreach (var clientId in DiClientIds)
        {
            using var request = TokenRequest(clientId, new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["service_ticket"] = ticket,
                ["grant_type"] = DiGrantType,
                ["service_url"] = flow.Service,
            });
            using var response = await _api.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new GarminException(CoreStrings.Garmin_RateLimited);
            }

            if (response.IsSuccessStatusCode && await ReadJsonAsync(response, ct) is { } reply && reply["access_token"]?.GetValue<string>() is { } access)
            {
                tokens = new Tokens(access, reply["refresh_token"]?.GetValue<string>(), ClientIdOf(access) ?? clientId);
                break;
            }
        }

        _tokens = tokens ?? throw new GarminException(CoreStrings.Garmin_NoToken);
        using var check = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{ConnectApi}/userprofile-service/socialProfile"), ct, refresh: false);
        if (check.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _tokens = null;
            throw new GarminException(CoreStrings.Garmin_TokenRejected);
        }

        Save(_tokens);
    }

    /// <summary>Sends an API request, refreshing the token when it is about to expire or Garmin rejects it.</summary>
    private async Task<HttpResponseMessage> SendApiAsync(Func<HttpRequestMessage> build, CancellationToken ct, bool refresh = true)
    {
        _tokens ??= Load() ?? throw new GarminException(CoreStrings.Garmin_NotSignedIn);
        if (refresh && ExpiresSoon(_tokens.Access))
        {
            await RefreshAsync(ct);
        }

        var response = await _api.SendAsync(Authorize(build()), ct);
        if (refresh && response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            await RefreshAsync(ct);
            response = await _api.SendAsync(Authorize(build()), ct);
        }

        return response;
    }

    private HttpRequestMessage Authorize(HttpRequestMessage request)
    {
        AddNativeHeaders(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens!.Access);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var tokens = _tokens!;
        if (tokens.Refresh is null)
        {
            throw new GarminException(CoreStrings.Garmin_Expired);
        }

        using var request = TokenRequest(tokens.ClientId, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = tokens.ClientId,
            ["refresh_token"] = tokens.Refresh,
        });
        using var response = await _api.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode || await ReadJsonAsync(response, ct) is not { } reply || reply["access_token"]?.GetValue<string>() is not { } access)
        {
            throw new GarminException(Loc.Format(CoreStrings.Garmin_ExpiredRefresh, (int)response.StatusCode));
        }

        _tokens = new Tokens(access, reply["refresh_token"]?.GetValue<string>() ?? tokens.Refresh, ClientIdOf(access) ?? tokens.ClientId);
        Save(_tokens);
    }

    private static HttpRequestMessage SsoPost(Flow flow, string url, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{url}?clientId={flow.ClientId}&locale=en-US&service={Uri.EscapeDataString(flow.Service)}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", flow.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Origin", Sso);
        return request;
    }

    private static HttpRequestMessage TokenRequest(string clientId, Dictionary<string, string> form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, DiTokenUrl) { Content = new FormUrlEncodedContent(form) };
        AddNativeHeaders(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:")));
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return request;
    }

    /// <summary>The headers of the Garmin Connect Android app, which the token service expects.</summary>
    private static void AddNativeHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "GCM-Android-5.23");
        request.Headers.TryAddWithoutValidation("X-Garmin-User-Agent", "com.garmin.android.apps.connectmobile/5.23; ; Google/sdk_gphone64_arm64/google; Android/33; Dalvik/2.1.0");
        request.Headers.TryAddWithoutValidation("X-Garmin-Paired-App-Version", "10861");
        request.Headers.TryAddWithoutValidation("X-Garmin-Client-Platform", "Android");
        request.Headers.TryAddWithoutValidation("X-App-Ver", "10861");
        request.Headers.TryAddWithoutValidation("X-Lang", "en");
        request.Headers.TryAddWithoutValidation("X-GCExperience", "GC5");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new GarminException(Loc.Format(CoreStrings.Garmin_Http, (int)response.StatusCode, body.Length is > 0 and < 200 ? $": {body}" : string.Empty));
        }
    }

    /// <summary>The reply as JSON, or null when it is not JSON (usually a Cloudflare challenge page).</summary>
    private static async Task<JsonObject?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The JWT payload, unverified: only Garmin holds the key, and the claims are used only to schedule a refresh.</summary>
    internal static JsonObject? JwtPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            return JsonNode.Parse(Convert.FromBase64String(payload)) as JsonObject;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string? ClientIdOf(string token) => JwtPayload(token)?["client_id"]?.ToString();

    /// <summary>True within 15 minutes of the token's expiry.</summary>
    private static bool ExpiresSoon(string token) =>
        JwtPayload(token)?["exp"] is JsonValue exp && exp.TryGetValue<double>(out var seconds)
        && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > seconds - 900;

    private Tokens? Load()
    {
        if (!HasTokens)
        {
            return null;
        }

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(File.ReadAllText(TokenFile));
        }
        catch (JsonException)
        {
            // A damaged file is as good as none: signing in again replaces it.
            return null;
        }

        return Text(json?["di_token"]) is { Length: > 0 } access
            ? new Tokens(access, Text(json!["di_refresh_token"]), Text(json["di_client_id"]) ?? DiClientIds[0])
            : null;
    }

    /// <summary>A string value, or null for anything else, so an unexpected reply shape cannot throw.</summary>
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Writes the tokens owner-only, through a temporary file so a reader never sees half a file.</summary>
    private void Save(Tokens tokens)
    {
        Directory.CreateDirectory(_tokenDirectory);
        var json = new JsonObject
        {
            ["di_token"] = tokens.Access,
            ["di_refresh_token"] = tokens.Refresh,
            ["di_client_id"] = tokens.ClientId,
        }.ToJsonString();
        var temp = Path.Combine(_tokenDirectory, $".garmin_tokens.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_tokenDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            stream.Write(Encoding.UTF8.GetBytes(json));
        }

        File.Move(temp, TokenFile, overwrite: true);
    }

    private sealed record Tokens(string Access, string? Refresh, string ClientId);

    private sealed record PendingMfa(Flow Flow, string Method);

    private sealed record LoginResult(Flow Flow, string? Ticket, string? MfaMethod);

    /// <summary>Cloudflare challenged the request; another login flow may get through.</summary>
    private sealed class BlockedException : Exception;

    private sealed record Flow(string Path, string ClientId, string Service, string UserAgent)
    {
        public static readonly Flow Mobile = new("mobile", IosClientId, IosService, IosUserAgent);
        public static readonly Flow Portal = new("portal", PortalClientId, PortalService, DesktopUserAgent);
    }
}
