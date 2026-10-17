using System.Net.Sockets;

namespace ErgoStravaLogger;

public sealed class LoggerRuntime
{
    private readonly LoggerConfig config;
    private readonly StravaClient strava;
    public ZwiftBridgeManager ZwiftBridge { get; }
    public TrainerControl TrainerControl { get; }

    public LoggerRuntime(LoggerConfig config, StravaClient strava)
    {
        this.config = config;
        this.strava = strava;
        TrainerControl = new TrainerControl(config, GetBridgeTelemetry, ReadPowerLimitsAsync, WritePowerAsync);
        ZwiftBridge = new ZwiftBridgeManager(config, GetBridgeTelemetry, trainerControl: TrainerControl);
    }

    private readonly object stateLock = new();
    private readonly SemaphoreSlim uploadGate = new(1);
    private readonly SemaphoreSlim deviceGate = new(1);
    private readonly SemaphoreSlim uploadWake = new(0, 1);
    private readonly SemaphoreSlim deviceWake = new(0, 1);
    private DaumClient? activeDevice;
    private TrainingRecorder? activeRecorder;
    private DeviceSample? lastSample;
    private DateTimeOffset? lastSeen;
    private bool connected;
    private bool recording;
    private bool recordingPaused;
    private bool recordingSessionOpen;
    private string? deviceError;
    private string? softwareVersion;
    private string? uploadError;
    private DateTimeOffset? lastUpload;

    public object DeviceStatus()
    {
        lock (stateLock) return new { host = config.ErgoIp, port = config.ErgoPort, connected, lastSeen, softwareVersion, error = deviceError, speedUnit = config.SpeedUnit };
    }
    public object RecordingStatus()
    {
        lock (stateLock) return new { enabled = config.RecordingEnabled, active = recording, paused = recordingPaused,
            sessionOpen = recordingSessionOpen, pauseSeconds = config.PauseSeconds, idleSeconds = config.IdleSeconds };
    }
    public object? SampleStatus()
    {
        lock (stateLock) return lastSample is null ? null : new { timestamp = lastSeen, lastSample.PowerWatts, lastSample.CadenceRpm,
            lastSample.HeartRateBpm, lastSample.SpeedMetersPerSecond, lastSample.DistanceMeters, lastSample.NativeGear };
    }
    public object StravaStatus(string callbackUrl)
    {
        lock (stateLock) return new { configured = strava.IsConfigured, authorized = strava.IsAuthorized, enabled = config.Strava.Enabled,
            callbackUrl, lastError = uploadError, lastUpload };
    }

    private BridgeTelemetry GetBridgeTelemetry()
    {
        lock (stateLock) return new BridgeTelemetry(lastSample, lastSeen, connected);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await RecordLoopAsync(ct);
        }
        finally { await ZwiftBridge.StopAsync(); }
    }

    private async Task RecordLoopAsync(CancellationToken ct)
    {
        await using var recorder = new TrainingRecorder(config);
        await recorder.RecoverAsync();
        await using var device = new DaumClient(config);
        await deviceGate.WaitAsync(ct);
        try { activeDevice = device; activeRecorder = recorder; }
        finally { deviceGate.Release(); }
        await ZwiftBridge.InitializeAsync(ct);
        Console.WriteLine($"Daum Premium 8i: {config.ErgoIp}:{config.ErgoPort}. Pause nach {config.PauseSeconds}s; neues Training bei zurückgesetzter Distanz.");
        using var uploadsCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var uploader = UploadLoopAsync(uploadsCancellation.Token);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!config.RecordingEnabled) { await deviceWake.WaitAsync(TimeSpan.FromSeconds(1), ct); continue; }
                var retry = false;
                await deviceGate.WaitAsync(ct);
                try
                {
                    DeviceSample? data = null;
                    try
                    {
                        data = await device.ReadTrainingAsync(ct);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or TimeoutException)
                    {
                        device.Disconnect();
                        lock (stateLock) { connected = false; deviceError = ex.Message; }
                        Console.Error.WriteLine($"Geräteverbindung: {ex.Message}; neuer Versuch in {config.ReconnectDelaySeconds}s.");
                        await recorder.FinishIfIdleAsync(DateTimeOffset.UtcNow);
                        lock (stateLock)
                        {
                            recordingSessionOpen = recorder.IsRecording;
                            recordingPaused = recorder.IsPaused;
                            recording = recorder.IsRecording && !recorder.IsPaused;
                        }
                        retry = true;
                    }
                    if (data is not null)
                    {
                        // Include recording and status updates in the gate: an IP change must never accept old-device samples.
                        // Storage/export failures must stop the service, rather than masquerade as network failures.
                        var now = DateTimeOffset.UtcNow;
                        await recorder.AddAsync(data, now);
                        lock (stateLock)
                        {
                            lastSample = data; lastSeen = now; connected = true; deviceError = null;
                            recordingSessionOpen = recorder.IsRecording;
                            recordingPaused = recorder.IsPaused;
                            recording = recorder.IsRecording && !recorder.IsPaused;
                        }
                    }
                }
                finally { deviceGate.Release(); }
                var bridgeData = GetBridgeTelemetry();
                await ZwiftBridge.ObserveCockpitGearAsync(bridgeData.Connected ? bridgeData.Sample?.NativeGear : null, ct);
                await TrainerControl.TickAsync(ct);
                var delay = retry ? TimeSpan.FromSeconds(config.ReconnectDelaySeconds) : TimeSpan.FromMilliseconds(config.LoggingIntervalMs);
                await deviceWake.WaitAsync(delay, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            uploadsCancellation.Cancel();
            await ZwiftBridge.StopAsync();
            try
            {
                await deviceGate.WaitAsync();
                try
                {
                    await recorder.FinishAsync();
                    activeDevice = null;
                    activeRecorder = null;
                    lock (stateLock) { recording = false; recordingPaused = false; recordingSessionOpen = false; }
                }
                finally { deviceGate.Release(); }
            }
            finally
            {
                try { await uploader; } catch (OperationCanceledException) { }
            }
        }
    }

    public async Task UpdateDeviceAddressAsync(string ip, CancellationToken ct = default)
    {
        var normalized = DeviceSettingsStore.NormalizeIp(ip);
        await deviceGate.WaitAsync(ct);
        try
        {
            var changed = config.ErgoIp != normalized;
            if (changed && activeRecorder is not null)
            {
                // Do not join measurements from two devices in one workout.
                await activeRecorder.FinishAsync();
                lock (stateLock) { recording = false; recordingPaused = false; recordingSessionOpen = false; }
            }
            await DeviceSettingsStore.SaveAsync(config.OutputDirectory, normalized, ct);
            if (!changed) return;
            TrainerControl.Invalidate();
            activeDevice?.Disconnect();
            lock (stateLock)
            {
                config.ErgoIp = normalized;
                connected = false;
                lastSample = null;
                lastSeen = null;
                softwareVersion = null;
                deviceError = null;
                recording = false;
                recordingPaused = false;
                recordingSessionOpen = false;
            }
            try { deviceWake.Release(); } catch (SemaphoreFullException) { }
            Console.WriteLine($"Ergometer-IP gespeichert: {normalized}; Verbindung wird neu aufgebaut.");
        }
        finally { deviceGate.Release(); }
        await ZwiftBridge.ObserveCockpitGearAsync(null, ct);
    }

    public async Task<DaumPowerLimits> ReadPowerLimitsAsync(CancellationToken ct)
    {
        await deviceGate.WaitAsync(ct);
        try
        {
            if (!config.RecordingEnabled || activeDevice is null) throw new IOException("Keine aktive Ergometer-Verbindung für die Laststeuerung.");
            return await activeDevice.ReadPowerLimitsAsync(ct);
        }
        catch { activeDevice?.Disconnect(); throw; }
        finally { deviceGate.Release(); }
    }

    private async Task<double> WritePowerAsync(int watts, CancellationToken ct)
    {
        await deviceGate.WaitAsync(ct);
        try
        {
            var data = GetBridgeTelemetry();
            var now = DateTimeOffset.UtcNow;
            if (!config.Zwift.ControlEnabled || !data.Connected || data.Sample is null || data.Timestamp is not { } seen ||
                seen > now || now - seen > TimeSpan.FromSeconds(config.Zwift.StaleAfterSeconds) || activeDevice is null)
                throw new IOException("Keine aktuellen Gerätedaten; Laständerung nicht ausgeführt.");
            if (watts < TrainerControl.MinimumPowerWatts || watts > TrainerControl.MaximumPowerWatts)
                throw new ArgumentException("Die Sollleistung liegt außerhalb der erlaubten Leistungsgrenzen.");
            return await activeDevice.SetTargetPowerAsync(watts, ct);
        }
        catch { activeDevice?.Disconnect(); throw; }
        finally { deviceGate.Release(); }
    }

    public async Task<int> ActivateCockpitShiftingAsync(CancellationToken ct = default)
    {
        if (!config.Zwift.ControlEnabled)
            throw new InvalidOperationException("Aktiviere zuerst die Zwift-Widerstandssteuerung.");
        await deviceGate.WaitAsync(ct);
        try
        {
            var data = GetBridgeTelemetry();
            if (!TrainerControl.Available || activeDevice is null || data.Sample is null)
                throw new IOException("Keine aktuellen Gerätedaten; Cockpit-Schaltung nicht aktiviert.");
            if (data.Sample.CadenceRpm > 0)
                throw new InvalidOperationException("Zum Aktivieren der Cockpit-Schaltung kurz anhalten.");
            if (TrainerControl.Mode == "erg")
                throw new InvalidOperationException("Cockpit-Schaltung vor der Fahrt oder im Streckenmodus aktivieren; ERG zuerst beenden.");
            if (data.Sample.NativeGear is { } existing) return existing;

            var wanted = config.Zwift.VirtualGear;
            int applied;
            try { applied = await activeDevice.SetGearAsync(wanted, ct); }
            finally
            {
                // Restore the bounded minimum even after an unrecognized gear reply or shutdown cancellation.
                using var restoreDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var watts = TrainerControl.MinimumPowerWatts;
                try
                {
                    var restored = await activeDevice.SetTargetPowerAsync(watts, restoreDeadline.Token);
                    if (Math.Abs(restored - watts) > .5) throw new IOException("Abweichende Wattbestätigung.");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or TimeoutException or OperationCanceledException)
                {
                    throw new IOException("Cockpit-Schaltung: Mindestlast wurde nicht bestätigt. Belastung am Cockpit prüfen.", ex);
                }
            }
            if (applied != wanted)
                throw new IOException($"Das Cockpit bestätigte Gang {applied} statt {wanted}.");
            return applied;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or TimeoutException)
        {
            activeDevice?.Disconnect();
            lock (stateLock) { connected = false; deviceError = ex.Message; }
            throw;
        }
        finally { deviceGate.Release(); }
    }

    public async Task DiagnoseAsync(CancellationToken ct)
    {
        var device = activeDevice ?? throw new IOException("Logger wird noch gestartet.");
        await deviceGate.WaitAsync(ct);
        try
        {
            var version = await device.ReadVersionAsync(ct);
            var sample = await device.ReadTrainingAsync(ct);
            lock (stateLock) { softwareVersion = version; lastSample = sample; lastSeen = DateTimeOffset.UtcNow; connected = true; deviceError = null; }
        }
        catch (Exception ex)
        {
            device.Disconnect();
            lock (stateLock) { connected = false; deviceError = ex.Message; }
            throw;
        }
        finally { deviceGate.Release(); }
    }

    private async Task UploadLoopAsync(CancellationToken ct)
    {
        var requested = false;
        while (!ct.IsCancellationRequested)
        {
            if ((config.Strava.Enabled || requested) && strava.IsAuthorized) await UploadPendingAsync(ct);
            requested = await uploadWake.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
    }

    public void RequestUploads()
    {
        try { uploadWake.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task<bool> UploadPendingAsync(CancellationToken ct)
    {
        await uploadGate.WaitAsync(ct);
        try
        {
            foreach (var file in Directory.EnumerateFiles(config.OutputDirectory, "*.tcx").Order(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (RecordingFiles.IsUploaded(file)) continue;
                try
                {
                    var activity = await strava.UploadAsync(file, ct);
                    lock (stateLock) { uploadError = null; if (activity.HasValue) lastUpload = DateTimeOffset.UtcNow; }
                    if (activity.HasValue) Console.WriteLine($"Strava: {Path.GetFileName(file)} → Aktivität {activity}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lock (stateLock) uploadError = ex.Message;
                    Console.Error.WriteLine($"Strava: Datei bleibt lokal gespeichert ({ex.Message}).");
                    return false;
                }
            }
            return true;
        }
        finally { uploadGate.Release(); }
    }
}
