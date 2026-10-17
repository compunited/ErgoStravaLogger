using System.Security.Cryptography;

namespace ErgoStravaLogger;

public static class LoggerWeb
{
    public static async Task RunAsync(LoggerConfig config, StravaClient strava, LoggerRuntime runtime, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.UseUrls(config.ListenUrl);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/auth"))
                context.Response.Headers.CacheControl = "no-store";
            if (HttpMethods.IsPost(context.Request.Method))
            {
                // Cross-site pages cannot send this header without a CORS preflight; CORS is disabled.
                var origin = context.Request.Headers.Origin.ToString();
                if (context.Request.Headers["X-Ergo-Request"] != "1" ||
                    (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed) ||
                     !string.Equals(parsed.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))))
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new { error = "Anfrage muss vom Webinterface stammen." });
                    return;
                }
            }
            await next(context);
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        string Callback(HttpRequest request) =>
            (config.PublicBaseUrl.Length > 0 ? config.PublicBaseUrl : $"{request.Scheme}://{request.Host}") + "/auth/strava/callback";

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/api/status", (HttpRequest request) => Results.Ok(new
        {
            device = runtime.DeviceStatus(), recording = runtime.RecordingStatus(), sample = runtime.SampleStatus(),
            strava = runtime.StravaStatus(Callback(request)), zwift = runtime.ZwiftBridge.GetStatus(),
            recordings = RecordingFiles.List(config.OutputDirectory)
        }));
        app.MapPost("/api/zwift/settings", async (ZwiftSettings settings) =>
        {
            if (!settings.Enabled.HasValue) return Results.BadRequest(new { error = "enabled muss true oder false sein." });
            try
            {
                await runtime.ZwiftBridge.SetEnabledAsync(settings.Enabled.Value, ct);
                var status = runtime.ZwiftBridge.GetStatus();
                return Results.Ok(new { status.Enabled, status.Active });
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Zwift-Bridge: {ex.Message}");
                return Results.Json(new { error = "Die Zwift-Bridge konnte nicht umgeschaltet werden: " + ex.Message }, statusCode: 503);
            }
        });
        app.MapPost("/api/zwift/control/settings", async (ZwiftControlSettings settings) =>
        {
            if (!settings.Enabled.HasValue || !settings.RiderWeightKg.HasValue || !settings.MaxPowerWatts.HasValue)
                return Results.BadRequest(new { error = "enabled, riderWeightKg und maxPowerWatts müssen angegeben werden." });
            try
            {
                await runtime.ZwiftBridge.UpdateControlSettingsAsync(settings.Enabled.Value, settings.RiderWeightKg.Value, settings.MaxPowerWatts.Value, ct);
                var status = runtime.ZwiftBridge.GetStatus();
                return Results.Ok(new { status.ControlEnabled, status.RiderWeightKg, status.MaxPowerWatts, status.Controllable });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Zwift-Steuerung: {ex.Message}");
                return Results.Json(new { error = "Die Einstellungen für die Zwift-Steuerung konnten nicht gespeichert werden: " + ex.Message }, statusCode: 503);
            }
        });
        app.MapPost("/api/zwift/control/limits", async () =>
        {
            try { return Results.Ok(await runtime.ReadPowerLimitsAsync(ct)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Sockets.SocketException or TimeoutException)
            { return Results.Json(new { error = "Die Leistungsgrenzen konnten nicht ausgelesen werden: " + ex.Message }, statusCode: 503); }
        });
        app.MapPost("/api/zwift/control/gear", async (ZwiftGearSettings settings) =>
        {
            if (!settings.Gear.HasValue) return Results.BadRequest(new { error = "gear muss angegeben werden (1–24)." });
            try
            {
                await runtime.ZwiftBridge.SetVirtualGearAsync(settings.Gear.Value, ct);
                return Results.Ok(runtime.ZwiftBridge.GetStatus());
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Zwift-Gangwahl: {ex.Message}");
                return Results.Json(new { error = "Der virtuelle Gang konnte nicht gespeichert werden. Bitte den Datenordner prüfen." }, statusCode: 503);
            }
        });
        app.MapPost("/api/zwift/control/cockpit/activate", async () =>
        {
            try
            {
                var gear = await runtime.ActivateCockpitShiftingAsync(ct);
                return Results.Ok(new { nativeGear = gear });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 409); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Sockets.SocketException or TimeoutException)
            {
                Console.Error.WriteLine($"Cockpit-Schaltung: {ex.Message}");
                return Results.Json(new { error = "Die Cockpit-Schaltung konnte nicht aktiviert werden: " + ex.Message }, statusCode: 503);
            }
        });
        app.MapPost("/api/device/settings", async (DeviceSettings settings) =>
        {
            try
            {
                await runtime.UpdateDeviceAddressAsync(settings.Ip ?? "", ct);
                return Results.Ok(new { ip = config.ErgoIp, saved = true });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Geräteeinstellungen: {ex.Message}");
                return Results.Json(new { error = "Die Ergometer-IP konnte nicht gespeichert werden. Bitte den Datenordner prüfen." }, statusCode: 500);
            }
        });
        app.MapGet("/api/recordings/{name}", (string name) =>
        {
            if (name != Path.GetFileName(name) || name.Contains('\\') || !name.EndsWith(".tcx", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
            var path = Path.Combine(config.OutputDirectory, name);
            return File.Exists(path) ? Results.File(path, "application/vnd.garmin.tcx+xml", name) : Results.NotFound();
        });
        app.MapPost("/api/strava/settings", async (PairingSettings settings, HttpRequest request) =>
        {
            try
            {
                await strava.ConfigureAsync(settings.ClientId?.Trim() ?? "", settings.ClientSecret?.Trim() ?? "", Callback(request), ct);
                return Results.Ok(new { configured = true });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapGet("/auth/strava/start", async (HttpContext context) =>
        {
            if (!strava.IsConfigured) return Results.Redirect("/?strava=error&message=" + Uri.EscapeDataString("Zuerst Client ID und Client Secret speichern."));
            await strava.ConfigureAsync(config.Strava.ClientId, config.Strava.ClientSecret, Callback(context.Request), ct);
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            context.Response.Cookies.Append("ergo-oauth-state", state, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = context.Request.IsHttps,
                MaxAge = TimeSpan.FromMinutes(10), Path = "/auth/strava"
            });
            return Results.Redirect(strava.GetAuthorizationUrl() + "&state=" + state);
        });
        app.MapGet("/auth/strava/callback", async (HttpContext context) =>
        {
            var expected = context.Request.Cookies["ergo-oauth-state"];
            var actual = context.Request.Query["state"].ToString();
            context.Response.Cookies.Delete("ergo-oauth-state", new CookieOptions { Path = "/auth/strava" });
            if (expected is null || expected.Length != actual.Length ||
                !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(actual)))
                return Results.BadRequest(new { error = "Ungültige oder abgelaufene Strava-Freigabe. Bitte erneut verbinden." });
            if (context.Request.Query.ContainsKey("error")) return Results.Redirect("/?strava=error&message=Strava-Freigabe%20abgebrochen.");
            var scopes = context.Request.Query["scope"].ToString().Split(',');
            if (!scopes.Contains("activity:write")) return Results.Redirect("/?strava=error&message=" + Uri.EscapeDataString("Die Berechtigung activity:write für Trainingsuploads fehlt."));
            try
            {
                await strava.ExchangeCodeAsync(context.Request.Query["code"].ToString(), ct);
                if (config.Strava.Enabled) runtime.RequestUploads();
                return Results.Redirect("/?strava=connected");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or IOException)
            { return Results.Redirect("/?strava=error&message=" + Uri.EscapeDataString(ex.Message)); }
        });
        app.MapPost("/api/diagnose", async () =>
        {
            try { await runtime.DiagnoseAsync(ct); return Results.Ok(new { ok = true }); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or System.Net.Sockets.SocketException)
            { return Results.Json(new { error = ex.Message }, statusCode: 503); }
        });
        app.MapPost("/api/uploads/retry", () =>
        {
            if (!strava.IsAuthorized) return Results.BadRequest(new { error = "Zuerst mit Strava verbinden." });
            runtime.RequestUploads();
            return Results.Accepted(value: new { queued = true });
        });
        var lifetime = app.Lifetime;
        using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping);
        using var registration = ct.Register(lifetime.StopApplication);
        var logger = runtime.RunAsync(runtimeCancellation.Token);
        // Failure to journal/export must be visible and restartable, rather than leaving a healthy UI.
        _ = logger.ContinueWith(task => { if (task.IsFaulted) lifetime.StopApplication(); }, TaskScheduler.Default);
        try
        {
            await app.StartAsync(ct);
            await app.WaitForShutdownAsync();
        }
        finally
        {
            runtimeCancellation.Cancel();
            try { await logger; }
            finally { await app.DisposeAsync(); }
        }
    }

    public sealed record PairingSettings(string? ClientId, string? ClientSecret);
    public sealed record DeviceSettings(string? Ip);
    public sealed record ZwiftSettings(bool? Enabled);
    public sealed record ZwiftControlSettings(bool? Enabled, double? RiderWeightKg, int? MaxPowerWatts);
    public sealed record ZwiftGearSettings(int? Gear);
}
