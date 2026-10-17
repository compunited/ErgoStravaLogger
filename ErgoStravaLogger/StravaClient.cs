using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ErgoStravaLogger;

public sealed class StravaOptions
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string TokenFile { get; set; } = "strava-tokens.json";
    public string RedirectUri { get; set; } = "http://localhost:8080/auth/strava/callback";
    public string ActivityName { get; set; } = "Daum Premium 8i";
}

/// <summary>OAuth authentication and resumable TCX uploads using the Strava v3 API.</summary>
public sealed class StravaClient : IDisposable
{
    private const string TokenEndpoint = "https://www.strava.com/oauth/token";
    private const string UploadEndpoint = "https://www.strava.com/api/v3/uploads";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly StravaOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Tokens? _tokens;
    private bool _tokensSaved = true;

    public StravaClient(StravaOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        LoadClientConfiguration();
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _ownsHttp = httpClient is null;
    }

    public bool IsAuthorized => File.Exists(_options.TokenFile) ||
                                !string.IsNullOrWhiteSpace(_options.AccessToken) ||
                                !string.IsNullOrWhiteSpace(_options.RefreshToken);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ClientId) &&
                                !string.IsNullOrWhiteSpace(_options.ClientSecret);

    public async Task ConfigureAsync(string clientId, string clientSecret, string redirectUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        if (!long.TryParse(clientId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var numericId) || numericId <= 0)
            throw new ArgumentException("Strava ClientId must be a positive integer.", nameof(clientId));
        ValidateRedirectUri(redirectUri);
        await _gate.WaitAsync(ct);
        try
        {
            var changedClient = _options.ClientId != clientId;
            if (string.IsNullOrWhiteSpace(clientSecret) && !changedClient)
                clientSecret = _options.ClientSecret;
            ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
            var credentials = new ClientConfiguration { ClientId = clientId, ClientSecret = clientSecret, RedirectUri = redirectUri };
            if (changedClient)
            {
                // Delete old authorization first: a crash must never pair new credentials with another app's tokens.
                if (File.Exists(_options.TokenFile))
                    File.Delete(_options.TokenFile);
                _tokens = null;
                _tokensSaved = true;
                _options.AccessToken = "";
                _options.RefreshToken = "";
            }
            await WriteJsonAtomicallyAsync(_options.TokenFile + ".client.json", credentials, sensitive: true, CancellationToken.None);
            _options.ClientId = clientId;
            _options.ClientSecret = clientSecret;
            _options.RedirectUri = redirectUri;
        }
        finally { _gate.Release(); }
    }

    private void LoadClientConfiguration()
    {
        var path = _options.TokenFile + ".client.json";
        if (!File.Exists(path) || (!string.IsNullOrWhiteSpace(_options.ClientId) && !string.IsNullOrWhiteSpace(_options.ClientSecret)))
            return;
        var stored = JsonSerializer.Deserialize<ClientConfiguration>(File.ReadAllText(path), JsonOptions)
                     ?? throw new InvalidDataException("The saved Strava application configuration is empty.");
        if (string.IsNullOrWhiteSpace(_options.ClientId) || _options.ClientId == stored.ClientId)
        {
            var useStoredRedirect = string.IsNullOrWhiteSpace(_options.ClientId);
            _options.ClientId = stored.ClientId;
            if (string.IsNullOrWhiteSpace(_options.ClientSecret))
                _options.ClientSecret = stored.ClientSecret;
            if (useStoredRedirect && !string.IsNullOrWhiteSpace(stored.RedirectUri))
                _options.RedirectUri = stored.RedirectUri;
        }
    }

    public string GetAuthorizationUrl()
    {
        RequireClientId();
        ValidateRedirectUri(_options.RedirectUri);
        return "https://www.strava.com/oauth/authorize?client_id=" + Uri.EscapeDataString(_options.ClientId) +
               "&redirect_uri=" + Uri.EscapeDataString(_options.RedirectUri) +
               "&response_type=code&approval_prompt=force&scope=activity%3Awrite";
    }

    public async Task ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        await _gate.WaitAsync(ct);
        try
        {
            RequireClientCredentials();
            await RequestTokensAsync(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId, ["client_secret"] = _options.ClientSecret,
                ["code"] = code, ["grant_type"] = "authorization_code"
            }, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Returns an activity ID, or null if server processing is still pending after one minute.</summary>
    public async Task<long?> UploadAsync(string tcxPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tcxPath);
        await _gate.WaitAsync(ct);
        try
        {
            var fullPath = Path.GetFullPath(tcxPath);
            var statePath = fullPath + ".upload.json";
            UploadState? state = null;
            if (File.Exists(statePath))
            {
                state = await ReadJsonAsync<UploadState>(statePath, ct);
                if (state is null || state.UploadId <= 0 || string.IsNullOrWhiteSpace(state.ExternalId))
                    throw new InvalidDataException("The Strava upload state file is invalid; the TCX file has been preserved.");
            }

            // File hashes also detect a TCX file replaced under an existing upload-state filename.
            string? externalId = null;
            if (File.Exists(fullPath))
            {
                await using var source = File.OpenRead(fullPath);
                externalId = "daum-" + Convert.ToHexStringLower(await SHA256.HashDataAsync(source, ct)) + ".tcx";
                if (state is not null && state.ExternalId != externalId)
                    throw new InvalidOperationException("The TCX file changed after its Strava upload. Preserve its upload state and use a new filename for a new activity.");
            }
            if (state?.ActivityId is > 0)
                return state.ActivityId;
            if (state is null)
            {
                if (externalId is null)
                    throw new FileNotFoundException("The TCX activity file does not exist.", fullPath);
                using var response = await SendAuthorizedAsync(() => CreateUploadRequest(fullPath, externalId), ct);
                var upload = await ReadUploadAsync(response, ct);
                if (upload.Id <= 0)
                    throw new InvalidDataException("Strava accepted an upload without returning a valid upload ID.");
                state = new UploadState { UploadId = upload.Id, ExternalId = externalId, ActivityId = upload.ActivityId };
                // Save the server ID before polling, so interruptions cannot cause another submission.
                await WriteJsonAtomicallyAsync(statePath, state, sensitive: false, CancellationToken.None);
                ThrowProcessingError(upload);
                if (state.ActivityId is > 0)
                    return state.ActivityId;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
                    using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get,
                        UploadEndpoint + "/" + state.UploadId.ToString(System.Globalization.CultureInfo.InvariantCulture)), timeout.Token);
                    var upload = await ReadUploadAsync(response, timeout.Token);
                    ThrowProcessingError(upload);
                    if (upload.ActivityId is > 0)
                    {
                        state.ActivityId = upload.ActivityId;
                        // Once the activity exists, persist even if caller cancellation arrived meanwhile.
                        await WriteJsonAtomicallyAsync(statePath, state, sensitive: false, CancellationToken.None);
                        return state.ActivityId;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                return null;
            }
        }
        finally { _gate.Release(); }
    }

    private HttpRequestMessage CreateUploadRequest(string path, string externalId)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("tcx"), "data_type");
        content.Add(new StringContent("Ride"), "sport_type");
        content.Add(new StringContent("1"), "trainer");
        content.Add(new StringContent(_options.ActivityName), "name");
        content.Add(new StringContent(externalId), "external_id");
        var file = new StreamContent(File.OpenRead(path));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        content.Add(file, "file", Path.GetFileName(path));
        return new HttpRequestMessage(HttpMethod.Post, UploadEndpoint) { Content = content };
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        await EnsureAccessTokenAsync(ct);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = createRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens!.AccessToken);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && !string.IsNullOrWhiteSpace(_tokens.RefreshToken))
            {
                response.Dispose();
                await RefreshAsync(ct);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); }
            catch { response.Dispose(); throw; }
            return response;
        }
        throw new InvalidOperationException("Strava authentication retry failed.");
    }

    private async Task EnsureAccessTokenAsync(CancellationToken ct)
    {
        if (_tokens is null)
        {
            _tokens = File.Exists(_options.TokenFile)
                ? await ReadJsonAsync<Tokens>(_options.TokenFile, ct) ?? throw new InvalidDataException("The Strava token file is empty.")
                : new Tokens { AccessToken = _options.AccessToken, RefreshToken = _options.RefreshToken };
        }
        if (!string.IsNullOrWhiteSpace(_tokens.ClientId) && _tokens.ClientId != _options.ClientId)
            throw new InvalidOperationException("Saved Strava tokens belong to a different ClientId. Configure and authorize the application again.");
        if (!_tokensSaved)
        {
            await WriteJsonAtomicallyAsync(_options.TokenFile, _tokens, sensitive: true, CancellationToken.None);
            _tokensSaved = true;
        }
        RequireUploadScope(_tokens);
        if (string.IsNullOrWhiteSpace(_tokens.AccessToken) ||
            (_tokens.ExpiresAt > 0 && _tokens.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds()))
            await RefreshAsync(ct);
        if (string.IsNullOrWhiteSpace(_tokens.AccessToken))
            throw new InvalidOperationException("Configure Strava OAuth tokens or exchange an authorization code first.");
    }

    private Task RefreshAsync(CancellationToken ct)
    {
        RequireClientCredentials();
        if (string.IsNullOrWhiteSpace(_tokens?.RefreshToken))
            throw new InvalidOperationException("The Strava access token expired. Supply a refresh token or authorize the application again.");
        return RequestTokensAsync(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId, ["client_secret"] = _options.ClientSecret,
            ["refresh_token"] = _tokens.RefreshToken, ["grant_type"] = "refresh_token"
        }, ct);
    }

    private async Task RequestTokensAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var tokens = await JsonSerializer.DeserializeAsync<Tokens>(stream, JsonOptions, ct)
                     ?? throw new InvalidDataException("Strava returned no OAuth tokens.");
        if (string.IsNullOrWhiteSpace(tokens.AccessToken) || string.IsNullOrWhiteSpace(tokens.RefreshToken) || tokens.ExpiresAt <= 0)
            throw new InvalidDataException("Strava returned incomplete OAuth tokens.");
        tokens.ClientId = _options.ClientId;
        // Refresh tokens rotate. Persist the new token before any further API call, independent of late cancellation.
        _tokens = tokens;
        _tokensSaved = false;
        await WriteJsonAtomicallyAsync(_options.TokenFile, tokens, sensitive: true, CancellationToken.None);
        _tokensSaved = true;
        RequireUploadScope(tokens);
    }

    private static async Task<UploadResponse> ReadUploadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<UploadResponse>(stream, JsonOptions, ct)
               ?? throw new InvalidDataException("Strava returned no upload status.");
    }

    private static void ThrowProcessingError(UploadResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Error))
            throw new InvalidOperationException("Strava could not process the activity: " + response.Error);
    }

    private static void RequireUploadScope(Tokens tokens)
    {
        if (tokens.Scope is not null && !tokens.Scope.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Contains("activity:write"))
            throw new InvalidOperationException("Strava authorization did not grant activity:write. Authorize the application with upload permission.");
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    private static async Task WriteJsonAtomicallyAsync<T>(string path, T value, bool sensitive, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (sensitive && !OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(tempPath, options))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private void RequireClientId()
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new InvalidOperationException("Configure Strava ClientId first.");
    }

    private static void ValidateRedirectUri(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect) || redirect.Scheme is not ("http" or "https"))
            throw new ArgumentException("Strava RedirectUri must be an absolute HTTP or HTTPS URL.", nameof(redirectUri));
    }

    private void RequireClientCredentials()
    {
        RequireClientId();
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new InvalidOperationException("Configure Strava ClientSecret for OAuth authentication.");
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
        _gate.Dispose();
    }

    private sealed class Tokens
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("expires_at")] public long ExpiresAt { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
        [JsonPropertyName("client_id")] public string? ClientId { get; set; }
    }

    private sealed class ClientConfiguration
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string RedirectUri { get; set; } = "";
    }

    private sealed class UploadState
    {
        public long UploadId { get; set; }
        public long? ActivityId { get; set; }
        public string ExternalId { get; set; } = "";
    }

    private sealed class UploadResponse
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("activity_id")] public long? ActivityId { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}
