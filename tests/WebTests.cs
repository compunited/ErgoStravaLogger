using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace ErgoStravaLogger.Tests;

/// <summary>Exercises the real HTTP app on loopback, without a device or external OAuth calls.</summary>
public static class WebTests
{
    public static async Task RunAsync()
    {
        var repository = FindRepository();
        var appDll = Path.Combine(repository, "ErgoStravaLogger", "bin", "Release", "net10.0", "ErgoStravaLogger.dll");
        Assert(File.Exists(appDll), "Build the Release app before running web integration tests.");
        var fixture = Path.Combine(AppContext.BaseDirectory, "web-test-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(fixture, "data");
        Directory.CreateDirectory(data);
        var configPath = Path.Combine(fixture, "web-test-config.json");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var address = new Uri($"http://127.0.0.1:{port}");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            ErgoIp = "127.0.0.1", ErgoPort = 51955, RecordingEnabled = false,
            ListenUrl = address.ToString().TrimEnd('/'), OutputDirectory = data,
            Zwift = new { Enabled = false },
            Strava = new { Enabled = false, ClientId = "", ClientSecret = "", AccessToken = "", RefreshToken = "", TokenFile = "tokens.json" }
        }));

        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { appDll, "web", "--config", configPath, "--no-device", "--duration", "12" })
            startInfo.ArgumentList.Add(argument);
        // Test fixtures must never inherit a real account, device, output directory, or listening URL.
        foreach (var variable in new[]
        {
            "ERGO_CONFIG", "ERGO_IP", "LISTEN_URL", "PUBLIC_BASE_URL", "OUTPUT_DIRECTORY", "STRAVA_ENABLED",
            "STRAVA_CLIENT_ID", "STRAVA_CLIENT_SECRET", "STRAVA_REFRESH_TOKEN", "STRAVA_ACCESS_TOKEN", "STRAVA_AUTH_CODE", "ASPNETCORE_URLS",
            "ZWIFT_ENABLED", "ZWIFT_PORT", "ZWIFT_DEVICE_NAME", "ZWIFT_NAME", "ZWIFT_IDENTITY"
        })
            startInfo.Environment.Remove(variable);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";

        using var process = new Process { StartInfo = startInfo };
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        Exception? failure = null;
        try
        {
            Assert(process.Start(), "Could not start the web test process.");
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
            using var http = new HttpClient(handler) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(2) };
            await WaitForHealthAsync(http, process);

            using (var home = await http.GetAsync("/"))
            {
                Assert(home.StatusCode == HttpStatusCode.OK, "Static webinterface must be available from the built app.");
                var html = await home.Content.ReadAsStringAsync();
                Assert(html.Contains("Daum", StringComparison.OrdinalIgnoreCase), "Static webinterface does not identify the device.");
                Assert(html.Contains("id=\"deviceIp\"", StringComparison.Ordinal), "Webinterface must offer a device IP input.");
                Assert(html.Contains("id=\"zwiftEnabled\"", StringComparison.Ordinal) && html.Contains("role=\"switch\"", StringComparison.Ordinal), "Webinterface must offer a Zwift bridge switch.");
                Assert(html.Contains("id=\"zwiftControlEnabled\"", StringComparison.Ordinal) && html.Contains("id=\"riderWeightKg\"", StringComparison.Ordinal) && html.Contains("id=\"maxPowerWatts\"", StringComparison.Ordinal), "Webinterface must offer separate resistance control, rider weight and power limit settings.");
            }
            await AssertStatusAsync(http, configured: false, authorized: false, "web-test-client-secret");
            await AssertDeviceSettingsAsync(http, address, data);
            await AssertZwiftSettingsAsync(http, address, data);
            await AssertZwiftControlSettingsAsync(http, address, data);
            await AssertZwiftVirtualGearAsync(http, address, data);

            var settings = new { clientId = "123456", clientSecret = "web-test-client-secret" };
            using (var rejected = await http.PostAsJsonAsync("/api/strava/settings", settings))
                Assert(rejected.StatusCode == HttpStatusCode.Forbidden, "Setup POST without the webinterface header must be rejected.");
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/strava/settings") { Content = JsonContent.Create(settings) })
            {
                request.Headers.Add("X-Ergo-Request", "1");
                request.Headers.Add("Origin", address.ToString().TrimEnd('/'));
                using var configured = await http.SendAsync(request);
                Assert(configured.StatusCode == HttpStatusCode.OK, "Setup POST with the webinterface header must save dummy credentials.");
                Assert(!(await configured.Content.ReadAsStringAsync()).Contains(settings.clientSecret), "Setup response must not return the client secret.");
            }
            Assert(File.Exists(Path.Combine(data, "tokens.json.client.json")), "Setup credentials must be saved in the fixture data directory.");
            await AssertStatusAsync(http, configured: true, authorized: false, settings.clientSecret);

            var authorization = await StartAuthorizationAsync(http, address, settings.clientSecret);
            using (var request = CallbackRequest(authorization.Cookie, "state=invalid-state&scope=activity%3Awrite"))
            using (var invalid = await http.SendAsync(request))
                Assert(invalid.StatusCode == HttpStatusCode.BadRequest, "OAuth callback must reject a mismatched state.");

            var withoutScope = await StartAuthorizationAsync(http, address, settings.clientSecret);
            Assert(withoutScope.State != authorization.State, "OAuth starts must generate fresh random state values.");
            using (var request = CallbackRequest(withoutScope.Cookie, "state=" + withoutScope.State))
            using (var deniedScope = await http.SendAsync(request))
                AssertErrorRedirect(deniedScope, "OAuth callback must reject missing upload permission before code exchange.");

            var deniedAuthorization = await StartAuthorizationAsync(http, address, settings.clientSecret);
            using (var request = CallbackRequest(deniedAuthorization.Cookie, "state=" + deniedAuthorization.State + "&error=access_denied"))
            using (var denied = await http.SendAsync(request))
                AssertErrorRedirect(denied, "Denied OAuth authorization must return to the webinterface.");
            Assert(!File.Exists(Path.Combine(data, "tokens.json")), "Rejected callbacks must never attempt OAuth token exchange.");

            const string recording = "<TrainingCenterDatabase>offline-test-recording</TrainingCenterDatabase>";
            await File.WriteAllTextAsync(Path.Combine(data, "sample.tcx"), recording);
            const string tokenSentinel = "web-test-access-token-do-not-expose";
            await File.WriteAllTextAsync(Path.Combine(data, "tokens.json"), JsonSerializer.Serialize(new { access_token = tokenSentinel }));
            await File.WriteAllTextAsync(Path.Combine(fixture, "hidden.tcx"), "outside-data-secret-sentinel");
            using (var download = await http.GetAsync("/api/recordings/sample.tcx"))
            {
                Assert(download.StatusCode == HttpStatusCode.OK, "Recorded TCX must be downloadable.");
                Assert(await download.Content.ReadAsStringAsync() == recording, "TCX download returned different data.");
            }
            foreach (var path in new[]
            {
                "/api/recordings/tokens.json", "/api/recordings/tokens.json.client.json",
                "/api/recordings/%2e%2e%5chidden.tcx", "/api/recordings/%2e%2e%2ftokens.json"
            })
            {
                using var forbidden = await http.GetAsync(path);
                Assert(forbidden.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, "Only TCX files inside the output directory may be downloaded.");
                var body = await forbidden.Content.ReadAsStringAsync();
                Assert(!body.Contains(tokenSentinel) && !body.Contains("outside-data-secret-sentinel") && !body.Contains(settings.clientSecret), "Rejected download leaked private data.");
            }
            using (var queryTraversal = await http.GetAsync("/api/recordings/sample.tcx?path=../tokens.json&name=../tokens.json"))
                Assert(await queryTraversal.Content.ReadAsStringAsync() == recording, "Download query parameters must not select a different file.");
            await AssertStatusAsync(http, configured: true, authorized: true, settings.clientSecret, tokenSentinel);
            await AssertCockpitActivationRejectedAsync(http, address, data, settings.clientSecret, tokenSentinel);
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            // --duration requests the normal cancellation/export/shutdown path; kill a hung child only as a fallback.
            if (stdout is not null && !process.HasExited)
            {
                using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await process.WaitForExitAsync(shutdownTimeout.Token); }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    await process.WaitForExitAsync();
                }
            }
            if (stdout is not null)
            {
                var output = await stdout;
                var errors = await stderr!;
                if (failure is not null)
                    failure = new InvalidOperationException("Web integration failed. Child output:\n" + output + "\n" + errors, failure);
                else if (process.ExitCode != 0)
                    failure = new InvalidOperationException("The test app did not shut down cleanly. Child output:\n" + output + "\n" + errors);
            }
            Directory.Delete(fixture, recursive: true);
        }
        if (failure is not null)
            throw failure;
    }

    private static async Task WaitForHealthAsync(HttpClient http, Process process)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(8))
        {
            Assert(!process.HasExited, "The test app exited before its health endpoint was ready.");
            try
            {
                using var health = await http.GetAsync("/health");
                if (health.IsSuccessStatusCode)
                    return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            await Task.Delay(100);
        }
        throw new InvalidOperationException("The test app did not become healthy on loopback.");
    }

    private static async Task AssertStatusAsync(HttpClient http, bool configured, bool authorized, params string[] secrets)
    {
        using var response = await http.GetAsync("/api/status");
        Assert(response.StatusCode == HttpStatusCode.OK, "Status endpoint failed.");
        var json = await response.Content.ReadAsStringAsync();
        foreach (var secret in secrets)
            Assert(!json.Contains(secret), "Status endpoint must never disclose secrets.");
        Assert(!json.Contains("clientSecret", StringComparison.OrdinalIgnoreCase) && !json.Contains("accessToken", StringComparison.OrdinalIgnoreCase), "Status must not expose credential fields.");
        using var status = JsonDocument.Parse(json);
        Assert(!status.RootElement.GetProperty("recording").GetProperty("enabled").GetBoolean(), "The test app must disable device recording.");
        Assert(!status.RootElement.GetProperty("device").GetProperty("connected").GetBoolean(), "The test app must not connect to a device.");
        Assert(status.RootElement.GetProperty("strava").GetProperty("configured").GetBoolean() == configured, "Strava setup status is incorrect.");
        Assert(status.RootElement.GetProperty("strava").GetProperty("authorized").GetBoolean() == authorized, "Strava authorization status is incorrect.");
        Assert(!status.RootElement.GetProperty("strava").GetProperty("enabled").GetBoolean(), "External uploads must stay disabled throughout this test.");
        var zwift = status.RootElement.GetProperty("zwift");
        Assert(!zwift.GetProperty("enabled").GetBoolean() && !zwift.GetProperty("active").GetBoolean(), "The bridge must remain disabled throughout this web test.");
        Assert(zwift.GetProperty("clients").GetInt32() == 0 && !zwift.GetProperty("dataAvailable").GetBoolean(), "The disabled bridge must not advertise live clients or device data.");
        Assert(!zwift.GetProperty("controllable").GetBoolean(), "The bridge must expose measurement mode without claiming resistance control.");
    }

    private static async Task AssertZwiftSettingsAsync(HttpClient http, Uri address, string data)
    {
        using (var forbidden = await http.PostAsJsonAsync("/api/zwift/settings", new { enabled = false }))
            Assert(forbidden.StatusCode == HttpStatusCode.Forbidden, "Zwift settings POST must require the webinterface header.");
        var settingsPath = Path.Combine(data, "zwift-settings.json");
        foreach (var invalidJson in new[] { "{}", "{\"enabled\":null}", "{\"enabled\":\"false\"}", "{" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/zwift/settings")
            {
                Content = new StringContent(invalidJson, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Ergo-Request", "1");
            request.Headers.Add("Origin", address.ToString().TrimEnd('/'));
            using var invalid = await http.SendAsync(request);
            Assert(invalid.StatusCode == HttpStatusCode.BadRequest, "Zwift settings must reject missing or malformed enabled values.");
        }
        Assert(!File.Exists(settingsPath), "Rejected Zwift settings must not create a settings file.");
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/zwift/settings") { Content = JsonContent.Create(new { enabled = false }) })
        {
            request.Headers.Add("X-Ergo-Request", "1");
            request.Headers.Add("Origin", address.ToString().TrimEnd('/'));
            using var response = await http.SendAsync(request);
            Assert(response.StatusCode == HttpStatusCode.OK, "Disabling the Zwift bridge must save its setting.");
            using var saved = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert(!saved.RootElement.GetProperty("enabled").GetBoolean() && !saved.RootElement.GetProperty("active").GetBoolean(), "Disabling the bridge must confirm its actual inactive state.");
        }
        Assert(File.Exists(settingsPath), "The Zwift bridge setting must persist in the fixture data directory.");
        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        var enabled = persisted.RootElement.EnumerateObject().Single(property => property.Name.Equals("enabled", StringComparison.OrdinalIgnoreCase));
        Assert(enabled.Value.ValueKind == JsonValueKind.False, "Disabling the bridge must persist a Boolean false.");
    }

    private static async Task AssertZwiftControlSettingsAsync(HttpClient http, Uri address, string data)
    {
        const string endpoint = "/api/zwift/control/settings";
        var valid = new { enabled = false, riderWeightKg = 88, maxPowerWatts = 300 };
        var settingsPath = Path.Combine(data, "zwift-settings.json");
        var originalSettings = await File.ReadAllTextAsync(settingsPath);
        using (var missingHeader = await http.PostAsJsonAsync(endpoint, valid))
            Assert(missingHeader.StatusCode == HttpStatusCode.Forbidden, "Resistance settings must require the webinterface header.");
        using (var crossOrigin = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(valid) })
        {
            crossOrigin.Headers.Add("X-Ergo-Request", "1");
            crossOrigin.Headers.Add("Origin", "http://different-origin.invalid");
            using var rejected = await http.SendAsync(crossOrigin);
            Assert(rejected.StatusCode == HttpStatusCode.Forbidden, "Resistance settings must reject a different Origin even with the webinterface header.");
        }
        foreach (var invalidJson in new[]
        {
            "{}", "{\"enabled\":false,\"riderWeightKg\":88}",
            "{\"enabled\":null,\"riderWeightKg\":88,\"maxPowerWatts\":300}",
            "{\"enabled\":false,\"riderWeightKg\":29,\"maxPowerWatts\":300}",
            "{\"enabled\":false,\"riderWeightKg\":88,\"maxPowerWatts\":1001}"
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(invalidJson, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Ergo-Request", "1");
            request.Headers.Add("Origin", address.ToString().TrimEnd('/'));
            using var rejected = await http.SendAsync(request);
            Assert(rejected.StatusCode == HttpStatusCode.BadRequest, "Resistance settings must reject incomplete values, rider weight below 30 kg and power limits above 1000 W.");
        }
        Assert(await File.ReadAllTextAsync(settingsPath) == originalSettings, "Rejected resistance settings must not change persisted bridge settings.");
        using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(valid) })
        {
            request.Headers.Add("X-Ergo-Request", "1");
            request.Headers.Add("Origin", address.ToString().TrimEnd('/'));
            using var response = await http.SendAsync(request);
            Assert(response.StatusCode == HttpStatusCode.OK, "Disabled resistance settings must save without requiring a bike connection.");
            using var saved = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert(!saved.RootElement.GetProperty("controlEnabled").GetBoolean() && !saved.RootElement.GetProperty("controllable").GetBoolean(), "Saving weight must not implicitly enable resistance control.");
            Assert(saved.RootElement.GetProperty("riderWeightKg").GetDouble() == 88 && saved.RootElement.GetProperty("maxPowerWatts").GetInt32() == 300, "Resistance settings response must confirm the selected weight and limit.");
        }
        using (var response = await http.GetAsync("/api/status"))
        {
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var zwift = status.RootElement.GetProperty("zwift");
            Assert(!zwift.GetProperty("controlEnabled").GetBoolean() && !zwift.GetProperty("controllable").GetBoolean(), "Saving resistance settings must leave the bridge in measurement mode.");
            Assert(zwift.GetProperty("riderWeightKg").GetDouble() == 88 && zwift.GetProperty("maxPowerWatts").GetInt32() == 300 && zwift.GetProperty("controlMode").GetString() == "idle", "Status must report stored weight, power limit and idle control mode.");
            Assert(status.RootElement.GetProperty("sample").ValueKind == JsonValueKind.Null && !status.RootElement.GetProperty("device").GetProperty("connected").GetBoolean(), "Saving disabled resistance settings must not contact the device.");
        }
        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        var properties = persisted.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
        Assert(properties["ControlEnabled"].ValueKind == JsonValueKind.False && properties["RiderWeightKg"].GetDouble() == 88 && properties["MaxPowerWatts"].GetInt32() == 300, "Resistance settings must persist in the host-mounted data directory.");
        Assert(properties["Enabled"].ValueKind == JsonValueKind.False && properties["Identity"].GetString()?.Length == 12, "Saving resistance settings must retain the disabled bridge and its identity.");
    }

    private static async Task AssertZwiftVirtualGearAsync(HttpClient http, Uri address, string data)
    {
        const string endpoint = "/api/zwift/control/gear";
        var settingsPath = Path.Combine(data, "zwift-settings.json");
        var originalSettings = await File.ReadAllTextAsync(settingsPath);
        using (var original = JsonDocument.Parse(originalSettings))
        {
            var properties = original.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
            Assert(properties["VirtualGear"].GetInt32() == 12, "Existing settings updates must retain the default virtual gear.");
        }
        using (var missingHeader = await http.PostAsJsonAsync(endpoint, new { gear = 1 }))
            Assert(missingHeader.StatusCode == HttpStatusCode.Forbidden, "Changing virtual gear must require the webinterface header.");
        using (var crossOrigin = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(new { gear = 1 }) })
        {
            crossOrigin.Headers.Add("X-Ergo-Request", "1");
            crossOrigin.Headers.Add("Origin", "http://different-origin.invalid");
            using var rejected = await http.SendAsync(crossOrigin);
            Assert(rejected.StatusCode == HttpStatusCode.Forbidden, "Changing virtual gear must reject a different Origin.");
        }
        foreach (var invalidJson in new[] { "{}", "{\"gear\":null}", "{\"gear\":0}", "{\"gear\":25}", "{\"gear\":1.5}", "{" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(invalidJson, System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Ergo-Request", "1");
            request.Headers.Add("Origin", address.ToString().TrimEnd('/'));
            using var rejected = await http.SendAsync(request);
            Assert(rejected.StatusCode == HttpStatusCode.BadRequest, "Changing virtual gear must reject missing, malformed and out-of-range values.");
        }
        Assert(await File.ReadAllTextAsync(settingsPath) == originalSettings, "Rejected gear requests must leave all persisted preferences unchanged.");

        foreach (var gear in new[] { 1, 24 })
        {
            using var request = GearRequest(address, gear);
            using var response = await http.SendAsync(request);
            Assert(response.StatusCode == HttpStatusCode.OK, "Both virtual gear boundaries must be accepted.");
            using var saved = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            AssertGearStatus(saved.RootElement, gear);
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
            var properties = persisted.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
            Assert(properties["VirtualGear"].GetInt32() == gear, "Virtual gear must persist in the host-mounted data directory.");
            Assert(properties["Enabled"].ValueKind == JsonValueKind.False && properties["ControlEnabled"].ValueKind == JsonValueKind.False,
                "Changing virtual gear must retain both disabled switches.");
            Assert(properties["RiderWeightKg"].GetDouble() == 88 && properties["MaxPowerWatts"].GetInt32() == 300,
                "Changing virtual gear must preserve weight and power limit.");
            using var original = JsonDocument.Parse(originalSettings);
            Assert(properties["Identity"].GetString() == original.RootElement.GetProperty("Identity").GetString(), "Changing gear must retain the trainer identity.");
        }

        // Clients using the original three-field settings contract must not reset the selected gear.
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/zwift/control/settings")
        {
            Content = JsonContent.Create(new { enabled = false, riderWeightKg = 90, maxPowerWatts = 320 })
        })
        {
            request.Headers.Add("X-Ergo-Request", "1");
            request.Headers.Add("Origin", address.ToString().TrimEnd('/'));
            using var response = await http.SendAsync(request);
            Assert(response.StatusCode == HttpStatusCode.OK, "The existing resistance settings contract must remain supported.");
        }
        using (var response = await http.GetAsync("/api/status"))
        {
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert(status.RootElement.GetProperty("zwift").GetProperty("virtualGear").GetInt32() == 24,
                "Updating existing control settings must preserve the live virtual gear.");
        }
        using (var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath)))
        {
            var gear = persisted.RootElement.EnumerateObject().Single(property => property.Name.Equals("VirtualGear", StringComparison.OrdinalIgnoreCase));
            Assert(gear.Value.GetInt32() == 24, "Updating existing control settings must preserve the persisted virtual gear.");
        }

        // A directory at the settings-file path forces an atomic replacement failure on both Windows and Linux.
        var savedSettings = await File.ReadAllTextAsync(settingsPath);
        var backupPath = settingsPath + ".test-backup";
        File.Move(settingsPath, backupPath);
        Directory.CreateDirectory(settingsPath);
        try
        {
            using var request = GearRequest(address, 7);
            using var rejected = await http.SendAsync(request);
            Assert(rejected.StatusCode == HttpStatusCode.ServiceUnavailable, "Failed gear persistence must return a service-unavailable response.");
            using var response = await http.GetAsync("/api/status");
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var zwift = status.RootElement.GetProperty("zwift");
            Assert(zwift.GetProperty("virtualGear").GetInt32() == 24, "Failed gear persistence must roll back the live selection.");
            Assert(zwift.GetProperty("riderWeightKg").GetDouble() == 90 && zwift.GetProperty("maxPowerWatts").GetInt32() == 320,
                "Failed gear persistence must preserve other live preferences.");
            Assert(!zwift.GetProperty("enabled").GetBoolean() && !zwift.GetProperty("controlEnabled").GetBoolean(), "Failed gear persistence must keep both switches disabled.");
            Assert(status.RootElement.GetProperty("sample").ValueKind == JsonValueKind.Null && !status.RootElement.GetProperty("device").GetProperty("connected").GetBoolean(),
                "Virtual gear changes in --no-device mode must not contact the device.");
            Assert(await File.ReadAllTextAsync(backupPath) == savedSettings && Directory.GetFileSystemEntries(settingsPath).Length == 0,
                "Failed gear persistence must not replace the old settings or leave temporary files.");
        }
        finally
        {
            Directory.Delete(settingsPath);
            File.Move(backupPath, settingsPath);
        }
    }

    private static async Task AssertCockpitActivationRejectedAsync(HttpClient http, Uri address, string data, params string[] secrets)
    {
        const string endpoint = "/api/zwift/control/cockpit/activate";
        var settingsPath = Path.Combine(data, "zwift-settings.json");
        var savedSettings = await File.ReadAllTextAsync(settingsPath);
        using (var missingHeader = await http.PostAsync(endpoint, null))
            Assert(missingHeader.StatusCode == HttpStatusCode.Forbidden, "Activating cockpit shifting must require the webinterface header.");
        using (var crossOrigin = new HttpRequestMessage(HttpMethod.Post, endpoint))
        {
            crossOrigin.Headers.Add("X-Ergo-Request", "1");
            crossOrigin.Headers.Add("Origin", "http://different-origin.invalid");
            using var rejected = await http.SendAsync(crossOrigin);
            Assert(rejected.StatusCode == HttpStatusCode.Forbidden, "Activating cockpit shifting must reject a different Origin.");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
        {
            request.Headers.Add("X-Ergo-Request", "1");
            request.Headers.Add("Origin", address.ToString().TrimEnd('/'));
            using var rejected = await http.SendAsync(request);
            Assert(rejected.StatusCode == HttpStatusCode.Conflict, "Cockpit activation without enabled resistance control and a device must be rejected.");
            var error = await rejected.Content.ReadAsStringAsync();
            foreach (var secret in secrets)
                Assert(!error.Contains(secret), "Rejected cockpit activation must not disclose Strava credentials.");
            Assert(!error.Contains("clientSecret", StringComparison.OrdinalIgnoreCase) && !error.Contains("accessToken", StringComparison.OrdinalIgnoreCase),
                "Cockpit activation error must not expose credential fields.");
        }
        Assert(await File.ReadAllTextAsync(settingsPath) == savedSettings, "Rejected cockpit activation must leave all persisted bridge preferences unchanged.");
        using var response = await http.GetAsync("/api/status");
        Assert(response.StatusCode == HttpStatusCode.OK, "Status must remain available after rejected cockpit activation.");
        using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var zwift = status.RootElement.GetProperty("zwift");
        Assert(!zwift.GetProperty("controlEnabled").GetBoolean() && !zwift.GetProperty("controllable").GetBoolean(),
            "Rejected cockpit activation must keep resistance control disabled.");
        Assert(zwift.GetProperty("virtualGear").GetInt32() == 24 && zwift.GetProperty("cockpitGear").ValueKind == JsonValueKind.Null,
            "Rejected cockpit activation must preserve the virtual gear without inventing a native gear.");
        Assert(status.RootElement.GetProperty("sample").ValueKind == JsonValueKind.Null && !status.RootElement.GetProperty("device").GetProperty("connected").GetBoolean(),
            "Rejected cockpit activation in --no-device mode must not contact a device.");
    }

    private static void AssertGearStatus(JsonElement zwift, int gear)
    {
        Assert(zwift.GetProperty("virtualGear").GetInt32() == gear, "Gear response must confirm the selected gear.");
        Assert(!zwift.GetProperty("enabled").GetBoolean() && !zwift.GetProperty("active").GetBoolean() && !zwift.GetProperty("controlEnabled").GetBoolean(),
            "Changing gear must not enable the bridge or resistance control.");
        Assert(zwift.GetProperty("riderWeightKg").GetDouble() == 88 && zwift.GetProperty("maxPowerWatts").GetInt32() == 300,
            "Gear response must preserve weight and power limit.");
        Assert(zwift.GetProperty("simulationGradePercent").ValueKind == JsonValueKind.Null && zwift.GetProperty("simulationSpeedKmh").ValueKind == JsonValueKind.Null &&
            zwift.GetProperty("simulationDesiredPowerWatts").ValueKind == JsonValueKind.Null && !zwift.GetProperty("simulationLimited").GetBoolean(),
            "Inactive simulation must not report invented slope, speed or requested power.");
    }

    private static HttpRequestMessage GearRequest(Uri address, int gear)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/zwift/control/gear") { Content = JsonContent.Create(new { gear }) };
        request.Headers.Add("X-Ergo-Request", "1");
        request.Headers.Add("Origin", address.ToString().TrimEnd('/'));
        return request;
    }

    private static async Task AssertDeviceSettingsAsync(HttpClient http, Uri address, string data)
    {
        using (var forbidden = await http.PostAsJsonAsync("/api/device/settings", new { ip = "192.168.0.57" }))
            Assert(forbidden.StatusCode == HttpStatusCode.Forbidden, "Device settings POST must require the webinterface header.");
        foreach (var invalidIp in new[] { "localhost", "192.168.0.256", "::1", "" })
        {
            using var request = DeviceSettingsRequest(address, invalidIp);
            using var invalid = await http.SendAsync(request);
            Assert(invalid.StatusCode == HttpStatusCode.BadRequest, "Device settings must reject invalid addresses, hostnames and IPv6.");
        }
        Assert(!File.Exists(Path.Combine(data, "device-settings.json")), "Rejected settings must not create a settings file.");
        using (var response = await http.GetAsync("/api/status"))
        {
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert(status.RootElement.GetProperty("device").GetProperty("host").GetString() == "127.0.0.1", "Rejected settings must keep the initial address.");
        }
        using (var request = DeviceSettingsRequest(address, " 192.168.0.57 "))
        using (var changed = await http.SendAsync(request))
        {
            Assert(changed.StatusCode == HttpStatusCode.OK, "Device settings must save an IPv4 address.");
            using var saved = JsonDocument.Parse(await changed.Content.ReadAsStringAsync());
            Assert(saved.RootElement.GetProperty("ip").GetString() == "192.168.0.57" && saved.RootElement.GetProperty("saved").GetBoolean(), "Settings response must confirm the normalized saved address.");
        }
        using (var response = await http.GetAsync("/api/status"))
        {
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var device = status.RootElement.GetProperty("device");
            Assert(device.GetProperty("host").GetString() == "192.168.0.57", "Status must immediately report the new device address.");
            Assert(!device.GetProperty("connected").GetBoolean() && status.RootElement.GetProperty("sample").ValueKind == JsonValueKind.Null, "Updating settings in --no-device mode must not connect or expose a sample.");
            Assert(device.GetProperty("lastSeen").ValueKind == JsonValueKind.Null && device.GetProperty("softwareVersion").ValueKind == JsonValueKind.Null && device.GetProperty("error").ValueKind == JsonValueKind.Null, "Device status must be clear after changing the address.");
        }
        var settingsPath = Path.Combine(data, "device-settings.json");
        Assert(File.Exists(settingsPath), "Device address must persist in the fixture data directory.");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        var properties = json.RootElement.EnumerateObject().ToArray();
        Assert(properties.Length == 1 && properties[0].Name.Equals("ip", StringComparison.OrdinalIgnoreCase) && properties[0].Value.GetString() == "192.168.0.57", "Device settings file must contain only the IP address.");
    }

    private static HttpRequestMessage DeviceSettingsRequest(Uri address, string ip)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/device/settings") { Content = JsonContent.Create(new { ip }) };
        request.Headers.Add("X-Ergo-Request", "1");
        request.Headers.Add("Origin", address.ToString().TrimEnd('/'));
        return request;
    }

    private static async Task<(string State, string Cookie)> StartAuthorizationAsync(HttpClient http, Uri address, string secret)
    {
        using var response = await http.GetAsync("/auth/strava/start");
        Assert(response.StatusCode == HttpStatusCode.Redirect, "OAuth start must redirect to Strava without following the redirect.");
        var location = response.Headers.Location!;
        Assert(location.Scheme == "https" && location.Host == "www.strava.com" && location.AbsolutePath == "/oauth/authorize", "OAuth start produced an unexpected authorization URL.");
        var query = ParseQuery(location.Query);
        Assert(query["scope"] == "activity:write" && query["client_id"] == "123456", "OAuth start must request upload permission for the configured app.");
        Assert(query["redirect_uri"] == address.ToString().TrimEnd('/') + "/auth/strava/callback", "OAuth callback URL must use the local test app's actual port.");
        Assert(!location.ToString().Contains(secret), "OAuth URL must never expose the client secret.");
        var state = query["state"];
        Assert(state.Length == 64 && state.All(Uri.IsHexDigit), "OAuth state must contain 32 random bytes.");
        var cookieHeader = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("ergo-oauth-state="));
        Assert(cookieHeader.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookieHeader.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase), "OAuth state cookie must be HTTP-only and SameSite=Lax.");
        var cookie = cookieHeader.Split(';')[0];
        Assert(cookie == "ergo-oauth-state=" + state, "OAuth state cookie must match the authorization URL.");
        return (state, cookie);
    }

    private static HttpRequestMessage CallbackRequest(string cookie, string query)
    {
        // Intentionally never provide an authorization code: these tests cannot call external OAuth services.
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/strava/callback?" + query);
        request.Headers.Add("Cookie", cookie);
        return request;
    }

    private static void AssertErrorRedirect(HttpResponseMessage response, string message)
    {
        Assert(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location?.ToString().StartsWith("/?strava=error&") == true, message);
    }

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]));

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ErgoStravaLogger.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Cannot locate the repository containing the web app.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Assertion failed: " + message);
    }
}
