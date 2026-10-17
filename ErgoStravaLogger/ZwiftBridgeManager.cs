namespace ErgoStravaLogger;

/// <summary>Owns the TCP trainer and LAN discovery lifecycle; the requested state is durable.</summary>
public sealed class ZwiftBridgeManager : IAsyncDisposable
{
    private readonly LoggerConfig config;
    private readonly Func<BridgeTelemetry> telemetry;
    private readonly Func<ZwiftBridgeOptions, IReadOnlyList<Guid>, int, IBridgeDiscovery> discoveryFactory;
    private readonly TrainerControl? trainerControl;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateLock = new();
    private DirConServer? server;
    private IBridgeDiscovery? discovery;
    private string? error;
    private int? lastCockpitGear;
    private int? cockpitGear;
    private string? cockpitShiftError;
    private CancellationToken lifetime;

    public ZwiftBridgeManager(LoggerConfig config, Func<BridgeTelemetry> telemetry,
        Func<ZwiftBridgeOptions, IReadOnlyList<Guid>, int, IBridgeDiscovery>? discoveryFactory = null,
        TrainerControl? trainerControl = null)
    {
        this.config = config;
        this.telemetry = telemetry;
        this.trainerControl = trainerControl;
        this.discoveryFactory = discoveryFactory ?? ((options, services, port) => new DirConDiscovery(options, services, port));
        ZwiftBridgeSettingsStore.EnsureIdentity(config.Zwift);
    }

    public ZwiftBridgeStatus GetStatus()
    {
        var data = telemetry();
        var now = DateTimeOffset.UtcNow;
        var fresh = data.Connected && data.Sample is not null && data.Timestamp.HasValue &&
            now >= data.Timestamp.Value && now - data.Timestamp.Value <= TimeSpan.FromSeconds(config.Zwift.StaleAfterSeconds);
        lock (stateLock)
        {
            var active = server?.IsRunning == true && discovery?.IsRunning == true;
            return new ZwiftBridgeStatus(config.Zwift.Enabled, active, config.Zwift.DeviceName,
                server?.BoundPort ?? config.Zwift.Port, active ? server!.ClientCount : 0,
                active && fresh, error ?? server?.LastError ?? discovery?.LastError,
                active && trainerControl?.Enabled == true, config.Zwift.ControlEnabled, config.Zwift.RiderWeightKg,
                config.Zwift.MaxPowerWatts, trainerControl?.Mode ?? "idle", trainerControl?.TargetPowerWatts,
                trainerControl?.LastError, config.Zwift.VirtualGear, trainerControl?.GradePercent,
                trainerControl?.VirtualSpeedKmh, trainerControl?.DesiredPowerWatts, trainerControl?.PowerLimited ?? false,
                cockpitGear, cockpitShiftError);
        }
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            lifetime = ct;
            if (!config.Zwift.Enabled) return;
            try { await StartComponentsAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (stateLock) error = ex.Message;
                await StopComponentsAsync();
                Console.Error.WriteLine($"Zwift-Bridge konnte nicht starten: {ex.Message}");
            }
        }
        finally { gate.Release(); }
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            try
            {
                if (enabled)
                {
                    var active = GetStatus().Active;
                    if (!active) { await StopComponentsAsync(); await StartComponentsAsync(lifetime); }
                }
                else await StopComponentsAsync();
                var oldEnabled = config.Zwift.Enabled;
                config.Zwift.Enabled = enabled;
                try { await ZwiftBridgeSettingsStore.SaveAsync(config.OutputDirectory, config.Zwift, ct); }
                catch { config.Zwift.Enabled = oldEnabled; throw; }
                lock (stateLock) { config.Zwift.Enabled = enabled; error = null; }
                Console.WriteLine($"Zwift-Bridge {(enabled ? "aktiviert" : "deaktiviert")}.");
            }
            catch (Exception ex)
            {
                if (enabled) await StopComponentsAsync();
                lock (stateLock) error = ex.Message;
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task UpdateControlSettingsAsync(bool enabled, double riderWeightKg, int maxPowerWatts, CancellationToken ct = default)
    {
        if (!double.IsFinite(riderWeightKg) || riderWeightKg is < 30 or > 250 || maxPowerWatts is < 20 or > 1000)
            throw new ArgumentException("Fahrergewicht: 30–250 kg; Leistungsgrenze: 20–1000 W.");
        if (enabled && trainerControl is null) throw new IOException("Die Laststeuerung ist in diesem Dienst nicht verfügbar.");
        await gate.WaitAsync(ct);
        var oldControl = config.Zwift.ControlEnabled;
        var oldWeight = config.Zwift.RiderWeightKg;
        var oldMax = config.Zwift.MaxPowerWatts;
        try
        {
            // Re-pairing is required because the advertised FTMS characteristics change.
            await StopComponentsAsync();
            if (trainerControl is not null) await trainerControl.StopAsync(ct);
            config.Zwift.ControlEnabled = enabled;
            config.Zwift.RiderWeightKg = riderWeightKg;
            config.Zwift.MaxPowerWatts = maxPowerWatts;
            trainerControl?.Invalidate();
            if (enabled) await trainerControl!.InitializeAsync(ct);
            if (config.Zwift.Enabled) await StartComponentsAsync(lifetime);
            await ZwiftBridgeSettingsStore.SaveAsync(config.OutputDirectory, config.Zwift, ct);
            lock (stateLock) error = null;
        }
        catch (Exception ex)
        {
            await StopComponentsAsync();
            config.Zwift.ControlEnabled = oldControl;
            config.Zwift.RiderWeightKg = oldWeight;
            config.Zwift.MaxPowerWatts = oldMax;
            trainerControl?.Invalidate();
            if (config.Zwift.Enabled)
            {
                try { await StartComponentsAsync(lifetime); } catch { }
            }
            lock (stateLock) error = ex.Message;
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task SetVirtualGearAsync(int gear, CancellationToken ct = default)
    {
        if (gear is < 1 or > 24) throw new ArgumentException("Der virtuelle Gang muss zwischen 1 und 24 liegen.");
        await gate.WaitAsync(ct);
        try
        {
            await CommitVirtualGearAsync(gear, ct);
        }
        finally { gate.Release(); }
    }

    public async Task ObserveCockpitGearAsync(int? nativeGear, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (nativeGear is null or < 1 or > 28)
            {
                lock (stateLock) { lastCockpitGear = null; cockpitGear = null; cockpitShiftError = null; }
                return;
            }
            int? previous;
            lock (stateLock)
            {
                cockpitGear = nativeGear;
                previous = lastCockpitGear;
                if (!previous.HasValue)
                {
                    lastCockpitGear = nativeGear;
                    cockpitShiftError = null;
                    return;
                }
            }
            var delta = nativeGear.Value - previous.Value;
            if (delta == 0 || !config.Zwift.Enabled || !config.Zwift.ControlEnabled || trainerControl?.Mode != "simulation")
            {
                lock (stateLock) { lastCockpitGear = nativeGear; cockpitShiftError = null; }
                return;
            }
            var nextGear = Math.Clamp(config.Zwift.VirtualGear + delta, 1, 24);
            try
            {
                if (nextGear != config.Zwift.VirtualGear) await CommitVirtualGearAsync(nextGear, ct);
                lock (stateLock) { lastCockpitGear = nativeGear; cockpitShiftError = null; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retain the baseline for one retry when the same cockpit sample arrives again.
                lock (stateLock) cockpitShiftError = "Cockpit-Gang konnte nicht gespeichert werden: " + ex.Message;
                Console.Error.WriteLine(cockpitShiftError);
            }
        }
        finally { gate.Release(); }
    }

    private async Task CommitVirtualGearAsync(int gear, CancellationToken ct)
    {
        // Persist before exposing the new gear to a simulation tick; failed saves leave live state intact.
        await ZwiftBridgeSettingsStore.SaveGearAsync(config.OutputDirectory, config.Zwift, gear, ct);
        lock (stateLock) { config.Zwift.VirtualGear = gear; cockpitShiftError = null; }
    }

    private async Task StartComponentsAsync(CancellationToken ct)
    {
        if (config.Zwift.ControlEnabled && trainerControl is not null) await trainerControl.InitializeAsync(ct);
        var nextServer = new DirConServer(config.Zwift, telemetry, trainerControl);
        lock (stateLock) { server = nextServer; error = null; }
        await nextServer.StartAsync(ct);
        var nextDiscovery = discoveryFactory(config.Zwift, nextServer.ServiceUuids, nextServer.BoundPort);
        lock (stateLock) discovery = nextDiscovery;
        await nextDiscovery.StartAsync(ct);
        if (!nextServer.IsRunning || !nextDiscovery.IsRunning)
            throw new IOException("Direct Connect oder die Gerätesuche konnte nicht gestartet werden.");
    }

    private async Task StopComponentsAsync()
    {
        DirConServer? previousServer;
        IBridgeDiscovery? previousDiscovery;
        lock (stateLock)
        {
            previousServer = server; previousDiscovery = discovery;
            server = null; discovery = null;
        }
        try
        {
            if (previousDiscovery is not null) await previousDiscovery.DisposeAsync();
        }
        finally
        {
            if (previousServer is not null) await previousServer.DisposeAsync();
        }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try { await StopComponentsAsync(); }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        gate.Dispose();
    }
}
