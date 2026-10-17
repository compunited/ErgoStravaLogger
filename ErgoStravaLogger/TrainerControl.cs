namespace ErgoStravaLogger;

/// <summary>Translates FTMS requests into bounded Daum watt targets on the logger's existing connection.</summary>
public sealed class TrainerControl(LoggerConfig config, Func<BridgeTelemetry> telemetry,
    Func<CancellationToken, Task<DaumPowerLimits>> readLimits,
    Func<int, CancellationToken, Task<double>> writePower, TimeProvider? clock = null) : ITrainerControl
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateLock = new();
    private DaumPowerLimits? limits;
    private string mode = "idle";
    private int? target;
    private string? error;
    private FtmsSimulationParameters? simulation;
    private bool hasControlled;
    private long generation;
    private double? virtualSpeedKmh;
    private int? desiredPower;
    private bool powerLimited;
    private double? rampTarget;
    private long? rampTimestamp;
    private const double SimulationRiseWattsPerSecond = 20;

    public bool Enabled { get { lock (stateLock) return config.Zwift.ControlEnabled && limits is not null; } }
    public bool Available => Enabled && FreshTelemetry() is not null;
    public int MinimumPowerWatts { get { lock (stateLock) return (int)Math.Ceiling(limits?.MinimumWatts ?? 20); } }
    public int MaximumPowerWatts { get { lock (stateLock) return Math.Min(config.Zwift.MaxPowerWatts, (int)Math.Floor(limits?.MaximumWatts ?? 1000)); } }
    public int PowerStepWatts => 1;
    public string Mode { get { lock (stateLock) return mode; } }
    public int? TargetPowerWatts { get { lock (stateLock) return target; } }
    public string? LastError { get { lock (stateLock) return error; } }
    public double? GradePercent { get { lock (stateLock) return simulation?.GradePercent; } }
    public double? VirtualSpeedKmh { get { lock (stateLock) return virtualSpeedKmh; } }
    public int? DesiredPowerWatts { get { lock (stateLock) return desiredPower; } }
    public bool PowerLimited { get { lock (stateLock) return powerLimited; } }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!config.Zwift.ControlEnabled) return;
            var received = await readLimits(ct);
            if (Math.Ceiling(received.MinimumWatts) > Math.Min(config.Zwift.MaxPowerWatts, Math.Floor(received.MaximumWatts)))
                throw new ArgumentException("Die Leistungsgrenze liegt unter der Mindestleistung des Ergometer.");
            lock (stateLock) { limits = received; error = null; }
        }
        catch (Exception ex) { lock (stateLock) { limits = null; error = ex.Message; } throw; }
        finally { gate.Release(); }
    }

    public async Task<bool> SetTargetPowerAsync(int watts, CancellationToken ct)
    {
        long requestGeneration;
        lock (stateLock) requestGeneration = generation;
        await gate.WaitAsync(ct);
        try
        {
            if (!Available || watts < MinimumPowerWatts || watts > MaximumPowerWatts) return false;
            lock (stateLock) if (requestGeneration != generation) return false;
            if (!await ApplyAsync(watts, ct, requestGeneration)) return false;
            lock (stateLock)
            {
                if (requestGeneration != generation) return false;
                mode = "erg"; simulation = null; ClearSimulationState();
            }
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> SetSimulationAsync(FtmsSimulationParameters parameters, CancellationToken ct)
    {
        long requestGeneration;
        lock (stateLock) requestGeneration = generation;
        await gate.WaitAsync(ct);
        try
        {
            var data = FreshTelemetry();
            if (!Enabled || data?.Sample is null || !ValidSimulation(parameters)) return false;
            lock (stateLock) if (requestGeneration != generation) return false;
            var watts = NextSimulationTarget(parameters, data.Sample, requestGeneration);
            if (!watts.HasValue || !await ApplyAsync(watts.Value, ct, requestGeneration)) return false;
            lock (stateLock)
            {
                if (requestGeneration != generation) return false;
                mode = "simulation"; simulation = parameters;
            }
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> StopAsync(CancellationToken ct)
    {
        // Stop automation immediately, even if a blocked device command prevents minimum-load restoration.
        lock (stateLock) { generation++; mode = "idle"; simulation = null; ClearSimulationState(); }
        try { await gate.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            lock (stateLock) if (hasControlled) error = "Steuerung beendet; Mindestlast wurde nicht bestätigt.";
            throw;
        }
        try
        {
            bool shouldRestore;
            long restoreGeneration;
            lock (stateLock) { shouldRestore = hasControlled; restoreGeneration = generation; mode = "idle"; simulation = null; ClearSimulationState(); }
            if (!shouldRestore) return true; // Never change load before a client has actually controlled it.
            if (!Available) { lock (stateLock) error = "Steuerung beendet; Ergometer für die Mindestlast nicht erreichbar."; return false; }
            bool restored = await ApplyAsync(MinimumPowerWatts, ct, restoreGeneration);
            if (restored) lock (stateLock) hasControlled = false;
            return restored;
        }
        finally { gate.Release(); }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        if (config.Zwift.ControlEnabled && !Enabled)
        {
            try { await InitializeAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return; }
        }
        await gate.WaitAsync(ct);
        try
        {
            FtmsSimulationParameters? parameters;
            long tickGeneration;
            lock (stateLock) { parameters = simulation; tickGeneration = generation; }
            if (!Enabled || parameters is null) return;
            var data = FreshTelemetry();
            if (data?.Sample is null) return; // No target calculated from stale cadence.
            var watts = NextSimulationTarget(parameters, data.Sample, tickGeneration);
            if (!watts.HasValue) return;
            lock (stateLock) if (tickGeneration != generation) return;
            if (watts.Value != TargetPowerWatts) await ApplyAsync(watts.Value, ct, tickGeneration);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { lock (stateLock) { error = ex.Message; mode = "idle"; simulation = null; ClearSimulationState(); } }
        finally { gate.Release(); }
    }

    public void Invalidate()
    {
        lock (stateLock) { generation++; limits = null; mode = "idle"; simulation = null; target = null; hasControlled = false; ClearSimulationState(); }
    }

    // Called under stateLock. Gears affect only simulation; an ERG target stays independent of cadence.
    private void ClearSimulationState()
    {
        virtualSpeedKmh = null; desiredPower = null; powerLimited = false; rampTarget = null; rampTimestamp = null;
    }

    private int? NextSimulationTarget(FtmsSimulationParameters parameters, DeviceSample sample, long expectedGeneration)
    {
        var load = CalculateSimulation(parameters, config.Zwift.RiderWeightKg, sample.CadenceRpm,
            config.Zwift.VirtualGear, MinimumPowerWatts, MaximumPowerWatts);
        var now = time.GetTimestamp();
        lock (stateLock)
        {
            if (generation != expectedGeneration) return null;
            virtualSpeedKmh = load.SpeedMetersPerSecond * 3.6;
            desiredPower = load.DesiredPowerWatts;
            powerLimited = load.DesiredPowerWatts > MaximumPowerWatts;
            var baseline = rampTarget ?? Math.Clamp(target ?? sample.PowerWatts, MinimumPowerWatts, MaximumPowerWatts);
            // Cap elapsed time so an idle/stale connection cannot accumulate permission for a large jump.
            var seconds = rampTimestamp.HasValue ? Math.Clamp(time.GetElapsedTime(rampTimestamp.Value, now).TotalSeconds, 0, 1) : 0;
            rampTarget = Math.Min(load.PowerWatts, baseline + SimulationRiseWattsPerSecond * seconds);
            rampTimestamp = now;
            return (int)Math.Round(rampTarget.Value);
        }
    }

    // stateLock must be held. A failed/unconfirmed write must not let the internal ramp run ahead of the bike.
    private void HaltSimulation(long expectedGeneration)
    {
        if (generation != expectedGeneration || simulation is null) return;
        generation++; mode = "idle"; simulation = null; ClearSimulationState();
    }

    private async Task<bool> ApplyAsync(int watts, CancellationToken ct, long expectedGeneration)
    {
        try
        {
            lock (stateLock)
            {
                if (generation != expectedGeneration) return false;
                hasControlled = true;
            }
            double applied = await writePower(watts, ct);
            lock (stateLock)
            {
                target = (int)Math.Round(applied);
                // Show the actual confirmed watt value, but an obsolete ACK must not erase a failed Stop warning.
                if (generation != expectedGeneration) return false;
                if (Math.Abs(applied - watts) > 0.5)
                {
                    error = $"Ergometer bestätigte {applied:F1} W statt {watts} W.";
                    HaltSimulation(expectedGeneration);
                    return false;
                }
                error = null;
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (stateLock) if (generation == expectedGeneration) { error = "Wattvorgabe wurde nicht bestätigt."; HaltSimulation(expectedGeneration); }
            throw;
        }
        catch (Exception ex)
        {
            lock (stateLock) if (generation == expectedGeneration) { error = ex.Message; HaltSimulation(expectedGeneration); }
            return false;
        }
    }

    private BridgeTelemetry? FreshTelemetry()
    {
        var data = telemetry();
        var now = time.GetUtcNow();
        return data.Connected && data.Sample is not null && data.Timestamp is { } seen &&
            seen <= now && now - seen <= TimeSpan.FromSeconds(config.Zwift.StaleAfterSeconds) ? data : null;
    }

    public static bool ValidSimulation(FtmsSimulationParameters p) =>
        double.IsFinite(p.WindSpeedMetersPerSecond) && Math.Abs(p.WindSpeedMetersPerSecond) <= 32.768 &&
        double.IsFinite(p.GradePercent) && Math.Abs(p.GradePercent) <= 327.68 &&
        double.IsFinite(p.RollingResistance) && p.RollingResistance is >= 0 and <= 0.0255 &&
        double.IsFinite(p.WindResistance) && p.WindResistance is >= 0 and <= 2.55;

    public static int SimulationPower(FtmsSimulationParameters p, double riderWeightKg, DeviceSample sample,
        int minWatts, int maxWatts, int virtualGear = 12) =>
        CalculateSimulation(p, riderWeightKg, sample.CadenceRpm, virtualGear, minWatts, maxWatts).PowerWatts;

    public sealed record SimulationLoad(double SpeedMetersPerSecond, int DesiredPowerWatts, int PowerWatts);

    public static SimulationLoad CalculateSimulation(FtmsSimulationParameters p, double riderWeightKg,
        int cadenceRpm, int virtualGear, int minWatts, int maxWatts)
    {
        if (!ValidSimulation(p) || !double.IsFinite(riderWeightKg) || riderWeightKg is < 30 or > 250 ||
            minWatts < 0 || maxWatts < minWatts || maxWatts > 1000 || cadenceRpm < 0 || virtualGear is < 1 or > 24)
            throw new ArgumentException("Ungültige Simulationsparameter oder Leistungsgrenzen.");
        if (cadenceRpm == 0) return new(0, 0, minWatts);
        // Daum's calculated speed can depend on load. Feeding it back into a watt target races to the cap.
        // Use an independent virtual transmission: gear 12 = 4.9 m per crank turn, 15% between gears.
        var speed = Math.Min(cadenceRpm * 4.9 * Math.Pow(1.15, virtualGear - 12) / 60, 40);
        var slope = p.GradePercent / 100d;
        var cosine = 1 / Math.Sqrt(1 + slope * slope);
        var weightForce = (riderWeightKg + 10) * 9.80665; // Virtual bicycle: 10 kg.
        var relativeWind = speed + p.WindSpeedMetersPerSecond;
        // Cw is modelled as rho * Cd * A (kg/m); aerodynamic force includes the physical one-half factor.
        var force = weightForce * cosine * (slope + p.RollingResistance) + 0.5 * p.WindResistance * relativeWind * Math.Abs(relativeWind);
        var wanted = (int)Math.Round(force * speed / 0.97);
        return new(speed, wanted, Math.Clamp(wanted, minWatts, maxWatts));
    }
}
