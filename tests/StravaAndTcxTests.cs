using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using ErgoStravaLogger;

public static class StravaAndTcxTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "ErgoStravaLogger-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await TcxContainsCyclingMeasurementsAsync(directory);
            await TcxExcludesPauseGapsAsync(directory);
            await TokenRotationSurvivesRestartAsync(directory);
            await AcceptedUploadIsPolledAndRememberedAsync(directory);
            await PendingUploadResumesAfterCancellationAsync(directory);
            await UnauthorizedIsRefreshedOnceAsync(directory);
            await HttpAndProcessingFailuresPreserveRecordingAsync(directory);
            await AuthorizationCodeIsExchangedAsync(directory);
            await ApplicationConfigurationSurvivesRestartAsync(directory);
            await TokenSaveFailureBlocksUploadsUntilRecoveredAsync(directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task TcxContainsCyclingMeasurementsAsync(string directory)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var first = new DateTimeOffset(2026, 10, 12, 18, 0, 0, TimeSpan.FromHours(2));
            TrainingSample[] samples =
            [
                new(first, 150, 80, null, 5.5, 0),
                new(first.AddSeconds(2), 250, 90, 130, 6.25, 11.75),
                new(first.AddSeconds(4), 200, 85, 140, 6.75, 24.75)
            ];
            var path = Path.Combine(directory, "cycling.tcx");
            await TcxWriter.WriteAsync(path, samples);
            var xml = XDocument.Load(path);
            XNamespace tcx = "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";
            XNamespace extension = "http://www.garmin.com/xmlschemas/ActivityExtension/v2";
            var activity = xml.Descendants(tcx + "Activity").Single();
            Assert((string?)activity.Attribute("Sport") == "Biking", "TCX must identify cycling.");
            Assert(activity.Element(tcx + "Id")!.Value == "2026-10-12T16:00:00.000Z", "TCX IDs must use UTC.");
            var lap = activity.Element(tcx + "Lap")!;
            Assert(lap.Element(tcx + "TotalTimeSeconds")!.Value == "4", "Lap duration must reflect timestamps.");
            Assert(lap.Element(tcx + "DistanceMeters")!.Value == "24.75", "Lap distance must use meters and invariant decimal syntax.");
            var points = lap.Descendants(tcx + "Trackpoint").ToArray();
            Assert(points.Length == samples.Length, "Every sample needs a trackpoint.");
            Assert(points.All(point => point.Element(tcx + "Time")!.Value.EndsWith('Z')), "Trackpoints must use UTC.");
            Assert(points[0].Element(tcx + "HeartRateBpm") is null, "Unavailable heart rate must be omitted.");
            Assert(points[1].Element(tcx + "HeartRateBpm")!.Element(tcx + "Value")!.Value == "130", "Heart rate was lost.");
            Assert(points[1].Element(tcx + "Cadence")!.Value == "90", "Cadence was lost.");
            Assert(points[1].Descendants(extension + "Watts").Single().Value == "250", "TCX must include Garmin power extensions.");
            Assert(points[1].Descendants(extension + "Speed").Single().Value == "6.25", "TCX speed must use meters per second.");
            Assert(!xml.Descendants(tcx + "Position").Any(), "Indoor cycling must not invent GPS coordinates.");
            await AssertThrowsAsync<ArgumentException>(() => TcxWriter.WriteAsync(Path.Combine(directory, "empty.tcx"), []));
            await AssertThrowsAsync<ArgumentException>(() => TcxWriter.WriteAsync(Path.Combine(directory, "backward.tcx"), [samples[1], samples[0]]));
            await AssertThrowsAsync<ArgumentException>(() => TcxWriter.WriteAsync(Path.Combine(directory, "invalid.tcx"), [samples[0] with { SpeedMetersPerSecond = double.NaN }]));
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    private static async Task TcxExcludesPauseGapsAsync(string directory)
    {
        var first = new DateTimeOffset(2026, 10, 13, 18, 0, 0, TimeSpan.FromHours(2));
        TrainingSample[] samples =
        [
            new(first, 100, 80, 120, 5, 0),
            new(first.AddSeconds(10), 100, 80, 120, 5, 50),
            new(first.AddSeconds(310), 1000, 90, 140, 5, 50, StartsNewTrack: true),
            new(first.AddSeconds(330), 1000, 90, 140, 5, 150)
        ];
        var path = Path.Combine(directory, "paused-cycling.tcx");
        await TcxWriter.WriteAsync(path, samples);
        var xml = XDocument.Load(path);
        XNamespace tcx = "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";
        XNamespace extension = "http://www.garmin.com/xmlschemas/ActivityExtension/v2";
        var lap = xml.Descendants(tcx + "Lap").Single();
        var tracks = lap.Elements(tcx + "Track").ToArray();
        Assert(tracks.Length == 2 && tracks.All(track => track.Elements(tcx + "Trackpoint").Count() == 2),
            "A resumed segment must start its own TCX track without an interpolated pause.");
        Assert(lap.Element(tcx + "TotalTimeSeconds")!.Value == "30" && TcxWriter.RecordedSeconds(samples) == 30,
            "Recording duration must exclude the 300-second pause gap.");
        Assert(tracks[1].Elements(tcx + "Trackpoint").First().Element(tcx + "Time")!.Value == "2026-10-13T16:05:10.000Z",
            "Resume timestamps must retain the actual UTC wall-clock time.");
        Assert(lap.Descendants(extension + "AvgSpeed").Single().Value == "5",
            "Average speed must divide recorded distance by recorded time rather than elapsed time.");
        Assert(lap.Descendants(extension + "AvgWatts").Single().Value == "700",
            "Time-weighted power must weight segment durations without interpolating watts across pause gaps.");

        var legacyJson = JsonSerializer.Serialize(new
        {
            samples[0].Timestamp, samples[0].PowerWatts, samples[0].CadenceRpm,
            samples[0].HeartRateBpm, samples[0].SpeedMetersPerSecond, samples[0].DistanceMeters
        });
        var legacySample = JsonSerializer.Deserialize<TrainingSample>(legacyJson)!;
        Assert(!legacySample.StartsNewTrack, "Old journals without the new track marker must remain readable.");
        Assert(TcxWriter.RecordedSeconds([]) == 0 && TcxWriter.RecordedSeconds([legacySample]) == 0,
            "Empty and single-sample recordings must have no recorded interval.");
    }

    private static async Task TokenRotationSurvivesRestartAsync(string directory)
    {
        var options = Options(directory, "rotation");
        options.RefreshToken = "refresh-original";
        var firstPath = await RecordingAsync(directory, "rotation-first");
        using (var handler = new FakeHandler(
            async (request, ct) =>
            {
                Assert(request.RequestUri!.AbsolutePath == "/oauth/token", "An absent access token requires OAuth refresh.");
                var form = await request.Content!.ReadAsStringAsync(ct);
                Assert(form.Contains("refresh_token=refresh-original"), "Refresh must use the configured token initially.");
                return Tokens("access-rotated", "refresh-rotated");
            },
            (request, _) =>
            {
                Assert(request.Headers.Authorization?.ToString() == "Bearer access-rotated", "Upload must use the refreshed bearer token.");
                Assert(File.Exists(options.TokenFile), "Rotated token must be on disk before upload.");
                return Task.FromResult(Json(HttpStatusCode.Created, "{\"id\":101,\"activity_id\":201}"));
            }))
        using (var http = new HttpClient(handler))
        using (var client = new StravaClient(options, http))
        {
            Assert(await client.UploadAsync(firstPath) == 201, "Initial authenticated upload failed.");
            handler.AssertComplete();
        }
        var tokenJson = JsonNode.Parse(await File.ReadAllTextAsync(options.TokenFile))!;
        Assert(tokenJson["refresh_token"]!.GetValue<string>() == "refresh-rotated", "Rotated refresh token must be persisted.");
        tokenJson["expires_at"] = 1;
        await File.WriteAllTextAsync(options.TokenFile, tokenJson.ToJsonString());
        var nextPath = await RecordingAsync(directory, "rotation-second");
        using var restartedHandler = new FakeHandler(
            async (request, ct) =>
            {
                var form = await request.Content!.ReadAsStringAsync(ct);
                Assert(form.Contains("refresh_token=refresh-rotated"), "Restart must refresh with the persisted token rather than stale config.");
                return Tokens("access-next", "refresh-next");
            },
            (request, _) =>
            {
                Assert(request.Headers.Authorization?.Parameter == "access-next", "Restart upload used the wrong token.");
                return Task.FromResult(Json(HttpStatusCode.Created, "{\"id\":102,\"activity_id\":202}"));
            });
        using var restartedHttp = new HttpClient(restartedHandler);
        using var restartedClient = new StravaClient(options, restartedHttp);
        Assert(await restartedClient.UploadAsync(nextPath) == 202, "Restarted authenticated upload failed.");
        restartedHandler.AssertComplete();
    }

    private static async Task AcceptedUploadIsPolledAndRememberedAsync(string directory)
    {
        var options = Options(directory, "accepted");
        options.AccessToken = "access-existing";
        var path = await RecordingAsync(directory, "accepted");
        var submittedAt = DateTimeOffset.UtcNow;
        using var handler = new FakeHandler(
            async (request, ct) =>
            {
                Assert(request.Method == HttpMethod.Post, "New activity must be submitted by POST.");
                Assert(request.Headers.Authorization?.ToString() == "Bearer access-existing", "Access token belongs in the bearer header.");
                var multipart = (MultipartFormDataContent)request.Content!;
                var fields = new Dictionary<string, string>();
                foreach (var field in multipart)
                {
                    var name = field.Headers.ContentDisposition!.Name!.Trim('"');
                    fields[name] = await field.ReadAsStringAsync(ct);
                }
                Assert(fields["data_type"] == "tcx" && fields["trainer"] == "1" && fields["sport_type"] == "Ride", "Upload metadata must identify indoor TCX cycling.");
                Assert(fields["external_id"].StartsWith("daum-") && fields["external_id"].EndsWith(".tcx"), "Upload requires a deterministic external ID.");
                Assert(fields.ContainsKey("file"), "TCX file is missing from multipart upload.");
                submittedAt = DateTimeOffset.UtcNow;
                return Json(HttpStatusCode.Accepted, "{\"id\":301,\"activity_id\":null,\"error\":null}");
            },
            async (request, ct) =>
            {
                Assert(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v3/uploads/301", "Accepted upload must be polled by its upload ID.");
                Assert((DateTimeOffset.UtcNow - submittedAt).TotalSeconds >= 0.95, "Strava polling must wait at least one second.");
                using var sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(path + ".upload.json", ct));
                Assert(sidecar.RootElement.GetProperty("UploadId").GetInt64() == 301, "Persist the upload ID before polling.");
                return Json(HttpStatusCode.OK, "{\"id\":301,\"activity_id\":401}");
            });
        using var http = new HttpClient(handler);
        using var client = new StravaClient(options, http);
        Assert(await client.UploadAsync(path) == 401, "Asynchronous processing did not return the activity ID.");
        Assert(await client.UploadAsync(path) == 401, "Completed upload should be returned from its persisted state.");
        handler.AssertComplete();
        using var noRequests = new FakeHandler();
        using var afterRestartHttp = new HttpClient(noRequests);
        using var afterRestart = new StravaClient(options, afterRestartHttp);
        Assert(await afterRestart.UploadAsync(path) == 401, "Completed upload state must survive restart.");
        noRequests.AssertComplete();
    }

    private static async Task PendingUploadResumesAfterCancellationAsync(string directory)
    {
        var options = Options(directory, "resume");
        options.AccessToken = "access-resume";
        var path = await RecordingAsync(directory, "resume");
        using var cancellation = new CancellationTokenSource();
        using (var handler = new FakeHandler((_, _) =>
            {
                cancellation.CancelAfter(50);
                return Task.FromResult(Json(HttpStatusCode.Created, "{\"id\":501,\"activity_id\":null}"));
            }))
        using (var http = new HttpClient(handler))
        using (var client = new StravaClient(options, http))
        {
            await AssertThrowsAsync<OperationCanceledException>(() => client.UploadAsync(path, cancellation.Token));
            Assert(File.Exists(path + ".upload.json"), "Cancellation must retain pending upload state.");
            handler.AssertComplete();
        }
        using var resumedHandler = new FakeHandler((request, _) =>
        {
            Assert(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/501"), "Resuming must poll the original upload without another POST.");
            return Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":501,\"activity_id\":601}"));
        });
        using var resumedHttp = new HttpClient(resumedHandler);
        using var resumedClient = new StravaClient(options, resumedHttp);
        Assert(await resumedClient.UploadAsync(path) == 601, "Pending upload could not resume.");
        resumedHandler.AssertComplete();
    }

    private static async Task UnauthorizedIsRefreshedOnceAsync(string directory)
    {
        var options = Options(directory, "unauthorized");
        options.AccessToken = "access-expired";
        options.RefreshToken = "refresh-valid";
        var path = await RecordingAsync(directory, "unauthorized");
        using var handler = new FakeHandler(
            (request, _) =>
            {
                Assert(request.Headers.Authorization?.Parameter == "access-expired", "Legacy access token must initially be accepted.");
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, "{}"));
            },
            (_, _) => Task.FromResult(Tokens("access-new", "refresh-new")),
            (request, _) =>
            {
                Assert(request.Headers.Authorization?.Parameter == "access-new", "401 retry must use a refreshed token.");
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, "{}"));
            });
        using var http = new HttpClient(handler);
        using var client = new StravaClient(options, http);
        await AssertThrowsAsync<HttpRequestException>(() => client.UploadAsync(path));
        Assert(File.Exists(path), "Failed authentication must preserve offline recording.");
        handler.AssertComplete();
    }

    private static async Task HttpAndProcessingFailuresPreserveRecordingAsync(string directory)
    {
        var options = Options(directory, "errors");
        options.AccessToken = "access-errors";
        var path = await RecordingAsync(directory, "errors");
        using (var handler = new FakeHandler((_, _) => Task.FromResult(Json(HttpStatusCode.BadRequest, "{\"message\":\"invalid\"}"))))
        using (var http = new HttpClient(handler))
        using (var client = new StravaClient(options, http))
        {
            await AssertThrowsAsync<HttpRequestException>(() => client.UploadAsync(path));
            Assert(!File.Exists(path + ".upload.json"), "HTTP rejection must not be recorded as success.");
            Assert(File.Exists(path), "Rejected upload must preserve TCX recording.");
            handler.AssertComplete();
        }
        using var processingHandler = new FakeHandler(
            (_, _) => Task.FromResult(Json(HttpStatusCode.Created, "{\"id\":701,\"activity_id\":null}")),
            (_, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":701,\"error\":\"malformed activity\",\"activity_id\":null}")));
        using var processingHttp = new HttpClient(processingHandler);
        using var processingClient = new StravaClient(options, processingHttp);
        await AssertThrowsAsync<InvalidOperationException>(() => processingClient.UploadAsync(path));
        Assert(File.Exists(path) && File.Exists(path + ".upload.json"), "Processing failure must preserve both recording and server upload ID.");
        processingHandler.AssertComplete();
    }

    private static async Task AuthorizationCodeIsExchangedAsync(string directory)
    {
        var options = Options(directory, "authorization");
        using var handler = new FakeHandler(async (request, ct) =>
        {
            var form = await request.Content!.ReadAsStringAsync(ct);
            Assert(form.Contains("code=single-use-code") && form.Contains("grant_type=authorization_code"), "Authorization code exchange fields are incorrect.");
            return Tokens("authorized-access", "authorized-refresh");
        });
        using var http = new HttpClient(handler);
        using var client = new StravaClient(options, http);
        var url = client.GetAuthorizationUrl();
        Assert(url.StartsWith("https://www.strava.com/oauth/authorize?") && url.Contains("scope=activity%3Awrite"), "OAuth URL must request upload permission.");
        Assert(!url.Contains(options.ClientSecret), "OAuth browser URL must never include the client secret.");
        await client.ExchangeCodeAsync("single-use-code");
        Assert(File.Exists(options.TokenFile), "Code exchange must persist its OAuth tokens.");
        handler.AssertComplete();
    }

    private static async Task ApplicationConfigurationSurvivesRestartAsync(string directory)
    {
        var options = new StravaOptions { TokenFile = Path.Combine(directory, "configured-tokens.json") };
        using var handler = new FakeHandler((_, _) => Task.FromResult(Tokens("configured-access", "configured-refresh")));
        using var http = new HttpClient(handler);
        using (var client = new StravaClient(options, http))
        {
            Assert(!client.IsConfigured && !client.IsAuthorized, "New installation must indicate setup is required.");
            await client.ConfigureAsync("123", "configured-secret", "http://raspi:8080/auth/strava/callback");
            Assert(client.IsConfigured, "Saved application credentials must configure OAuth.");
            await client.ExchangeCodeAsync("setup-code");
            Assert(client.IsAuthorized, "Successful OAuth exchange must indicate authorization.");
            await client.ConfigureAsync("123", "", "http://raspi:8080/auth/strava/callback");
            Assert(client.IsAuthorized, "Saving the same ClientId must retain authorization.");
        }
        var restartedOptions = new StravaOptions { TokenFile = options.TokenFile };
        using (var restarted = new StravaClient(restartedOptions, http))
        {
            Assert(restarted.IsConfigured && restarted.IsAuthorized, "Application credentials and authorization must survive restart with a read-only config.");
            Assert(restarted.GetAuthorizationUrl().Contains("client_id=123"), "Restart must load the persisted ClientId.");
            Assert(restarted.GetAuthorizationUrl().Contains("raspi%3A8080"), "Restart must load the persisted callback URI.");
            await restarted.ConfigureAsync("456", "different-secret", "http://raspi:8080/auth/strava/callback");
            Assert(!restarted.IsAuthorized && !File.Exists(options.TokenFile), "Changing ClientId must remove old authorization.");
            Assert(restartedOptions.AccessToken.Length == 0 && restartedOptions.RefreshToken.Length == 0, "Changing app must clear legacy configured tokens.");
        }
        handler.AssertComplete();
    }

    private static async Task TokenSaveFailureBlocksUploadsUntilRecoveredAsync(string directory)
    {
        var options = Options(directory, "blocked-token-save");
        options.RefreshToken = "original-token";
        Directory.CreateDirectory(options.TokenFile); // A directory at the destination prevents atomic replacement.
        var path = await RecordingAsync(directory, "blocked-token-save");
        using var handler = new FakeHandler(
            (_, _) => Task.FromResult(Tokens("rotated-access", "rotated-refresh")),
            (request, _) =>
            {
                Assert(request.Headers.Authorization?.Parameter == "rotated-access", "Recovered save must preserve the already-rotated token.");
                Assert(File.Exists(options.TokenFile), "No API request may use rotated tokens before durable storage succeeds.");
                return Task.FromResult(Json(HttpStatusCode.Created, "{\"id\":801,\"activity_id\":901}"));
            });
        using var http = new HttpClient(handler);
        using var client = new StravaClient(options, http);
        // Destination-directory replacement throws IOException or UnauthorizedAccessException depending on the OS.
        await AssertThrowsAsync<Exception>(() => client.UploadAsync(path));
        Directory.Delete(options.TokenFile);
        Assert(await client.UploadAsync(path) == 901, "Upload should recover when token persistence becomes writable.");
        handler.AssertComplete();
    }

    private static StravaOptions Options(string directory, string name) => new()
    {
        ClientId = "123", ClientSecret = "fake-client-secret",
        TokenFile = Path.Combine(directory, name + "-tokens.json")
    };

    private static async Task<string> RecordingAsync(string directory, string name)
    {
        var path = Path.Combine(directory, name + ".tcx");
        await File.WriteAllTextAsync(path, "<recording>" + name + "</recording>");
        return path;
    }

    private static HttpResponseMessage Tokens(string access, string refresh) => Json(HttpStatusCode.OK,
        JsonSerializer.Serialize(new { access_token = access, refresh_token = refresh, expires_at = DateTimeOffset.UtcNow.AddHours(6).ToUnixTimeSeconds(), scope = "activity:write" }));

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Assertion failed: " + message);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }

    private sealed class FakeHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps) : HttpMessageHandler
    {
        private int _next;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_next >= steps.Length)
                throw new InvalidOperationException("Unexpected HTTP request: " + request.Method + " " + request.RequestUri!.AbsolutePath);
            return steps[_next++](request, cancellationToken);
        }

        public void AssertComplete() => Assert(_next == steps.Length, "Not all expected HTTP requests were sent.");
    }
}
