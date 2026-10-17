using ErgoStravaLogger;

namespace ErgoStravaLogger.Tests;

public static class TrainerControlTests
{
    public static async Task RunAsync()
    {
        var config = new LoggerConfig();
        config.Zwift.RiderWeightKg = 88;
        var sample = new DeviceSample(120, 80, 140, 8, 0, TimeSpan.Zero);
        var data = new BridgeTelemetry(sample, DateTimeOffset.UtcNow, true);
        var written = new List<int>();
        double? reply = null;
        var engine = new TrainerControl(config, () => data,
            _ => Task.FromResult(new DaumPowerLimits(20, 1000, 75)),
            (watts, _) => { written.Add(watts); return Task.FromResult(reply ?? watts); });

        await engine.InitializeAsync(default);
        Check(!engine.Enabled && !await engine.SetTargetPowerAsync(150, default), "disabled control rejects loads");
        Check(written.Count == 0, "disabled initialization and requests never change resistance");
        config.Zwift.ControlEnabled = true;
        await engine.InitializeAsync(default);
        Check(engine.Enabled && engine.Available && engine.MaximumPowerWatts == 300, "actual range is capped by the user limit");
        Check(written.Count == 0, "reading native limits never sets a load");
        Check(await engine.StopAsync(default) && written.Count == 0, "stop before control never modifies the cockpit target");
        Check(!await engine.SetTargetPowerAsync(301, default) && written.Count == 0, "over-limit requests are rejected without writes");
        Check(await engine.SetTargetPowerAsync(150, default) && engine.Mode == "erg" && written.Single() == 150,
            "ERG forwards the requested watt target and waits for confirmation");

        var simulation = new FtmsSimulationParameters(0, 1, 0.004, 0.3);
        Check(TrainerControl.SimulationPower(simulation, 88, sample, 20, 300) == 134, "80 rpm in gear 12 on a 1% climb with Cw .3 yields 134 W");
        Check(TrainerControl.SimulationPower(simulation with { GradePercent = 30 }, 88, sample, 20, 300) == 300, "steep hills respect the configured cap");
        Check(TrainerControl.SimulationPower(simulation with { GradePercent = -30 }, 88, sample, 20, 300) == 20, "descent cannot request negative braking power");
        Check(TrainerControl.SimulationPower(simulation, 88, sample with { CadenceRpm = 0 }, 20, 300) == 20, "standing still never uses an old speed to request a high load");
        Check(await engine.SetSimulationAsync(simulation, default) && engine.Mode == "simulation", "simulation sets the calculated load");
        data = data with { Sample = sample with { SpeedMetersPerSecond = 4 }, Timestamp = DateTimeOffset.UtcNow };
        await engine.TickAsync(default);
        Check(engine.TargetPowerWatts == 134, "native speed changes cannot feed back into resistance");
        data = data with { Sample = sample with { CadenceRpm = 40 }, Timestamp = DateTimeOffset.UtcNow };
        await engine.TickAsync(default);
        Check(engine.TargetPowerWatts == 51, "lower cadence changes simulation load through the virtual transmission");
        int count = written.Count;
        data = data with { Timestamp = DateTimeOffset.UtcNow.AddSeconds(-10) };
        await engine.TickAsync(default);
        Check(written.Count == count && !await engine.SetTargetPowerAsync(160, default), "stale data causes no load command");
        data = data with { Timestamp = DateTimeOffset.UtcNow };
        Check(await engine.StopAsync(default) && written[^1] == 20 && engine.Mode == "idle", "stop restores the native minimum and cancels simulation");
        count = written.Count;
        await engine.TickAsync(default);
        Check(written.Count == count, "stopped simulation stays stopped");
        reply = 40;
        Check(!await engine.SetTargetPowerAsync(150, default) && engine.LastError is not null, "unexpected echoed power is a failure, not fake success");
        reply = null;
        Check(await engine.StopAsync(default) && written[^1] == 20, "a failed target confirmation still restores the minimum on stop");
        engine.Invalidate();
        Check(!engine.Enabled && !await engine.SetTargetPowerAsync(150, default), "changing devices invalidates the old limits");
        await HighCadenceAndGearingAsync();
        await FailedWritesCannotAccumulateRampAsync();
        await CancelledStopCannotReactivateSimulationAsync(config, sample, simulation);
        Console.WriteLine("Trainer control / physics / limits: passed");
    }

    private static async Task HighCadenceAndGearingAsync()
    {
        var flat = new FtmsSimulationParameters(0, 0, 0.004, 0.51);
        var sample = new DeviceSample(20, 95, null, 40, 0, TimeSpan.Zero);
        Check(TrainerControl.SimulationPower(flat, 88, sample with { CadenceRpm = 90 }, 20, 220) == 134,
            "90 rpm remains below the configured 220 W cap on a flat road");
        Check(TrainerControl.SimulationPower(flat, 88, sample, 20, 220) == 154,
            "95 rpm at a moderate gear gives 154 W despite a 144 km/h native speed");
        Check(TrainerControl.SimulationPower(flat, 88, sample with { CadenceRpm = 100, SpeedMetersPerSecond = double.NaN }, 20, 220) == 176,
            "100 rpm gives 176 W and never consults Daum's synthetic speed");
        Check(TrainerControl.SimulationPower(flat, 88, sample, 20, 220, 10) < 154 &&
            TrainerControl.SimulationPower(flat, 88, sample, 20, 220, 14) > 154,
            "downshifting reduces load at the same high cadence; upshifting increases it");
        var climb = flat with { GradePercent = 10 };
        Check(TrainerControl.SimulationPower(climb, 88, sample, 20, 220, 1) < 220,
            "the lowest virtual gear permits 95 rpm on a 10% climb without hitting the cap");
        Check(TrainerControl.SimulationPower(flat with { WindSpeedMetersPerSecond = 5 }, 88, sample, 20, 1000) > 154 &&
            TrainerControl.SimulationPower(flat with { WindSpeedMetersPerSecond = -5 }, 88, sample, 20, 1000) < 154,
            "headwind raises load and tailwind reduces it");

        var clock = new TestClock();
        var config = new LoggerConfig();
        config.Zwift.ControlEnabled = true; config.Zwift.RiderWeightKg = 88; config.Zwift.MaxPowerWatts = 220;
        var data = new BridgeTelemetry(sample, clock.GetUtcNow(), true);
        var writes = new List<int>();
        var engine = new TrainerControl(config, () => data,
            _ => Task.FromResult(new DaumPowerLimits(20, 1000, 20)),
            (watts, _) => { writes.Add(watts); return Task.FromResult((double)watts); }, clock);
        await engine.InitializeAsync(default);
        Check(await engine.SetSimulationAsync(flat, default) && writes[^1] == 20,
            "entering simulation does not jump from the native 20 W load to a high target");
        for (int i = 1; i <= 7; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1)); data = data with { Timestamp = clock.GetUtcNow() };
            int previous = writes[^1]; await engine.TickAsync(default);
            Check(writes[^1] - previous <= 20, "simulation increases by at most 20 W per second");
        }
        Check(engine.TargetPowerWatts == 154 && engine.DesiredPowerWatts == 154 && !engine.PowerLimited,
            "ramped load settles at the high-cadence target and reports its diagnostics");
        Check(Math.Abs(engine.VirtualSpeedKmh!.Value - 27.93) < .001 && engine.GradePercent == 0,
            "diagnostics expose independent speed and grade");
        config.Zwift.VirtualGear = 10;
        clock.Advance(TimeSpan.FromSeconds(1)); data = data with { Timestamp = clock.GetUtcNow() };
        await engine.TickAsync(default);
        Check(engine.TargetPowerWatts < 154, "a web gear change takes effect on the next tick without reconnecting");
        Check(await engine.SetSimulationAsync(climb, default) && engine.PowerLimited && engine.DesiredPowerWatts > 220,
            "diagnostics distinguish a capped hill request from the confirmed/ramped load");
        int beforeGap = engine.TargetPowerWatts!.Value;
        clock.Advance(TimeSpan.FromMinutes(1));
        await engine.TickAsync(default);
        Check(engine.TargetPowerWatts == beforeGap, "stale samples cannot ramp the load");
        data = data with { Timestamp = clock.GetUtcNow() };
        await engine.TickAsync(default);
        Check(engine.TargetPowerWatts <= beforeGap + 20, "a long telemetry gap cannot cause an immediate jump to the cap");
        data = data with { Sample = sample with { CadenceRpm = 0 } };
        await engine.TickAsync(default);
        Check(engine.TargetPowerWatts == 20, "stopping pedalling immediately requests the minimum load");
        Check(await engine.SetTargetPowerAsync(150, default), "ERG still accepts a direct fixed watt target");
        config.Zwift.VirtualGear = 24;
        data = data with { Sample = sample with { CadenceRpm = 100 } };
        clock.Advance(TimeSpan.FromSeconds(1)); data = data with { Timestamp = clock.GetUtcNow() };
        int count = writes.Count; await engine.TickAsync(default);
        Check(engine.Mode == "erg" && engine.TargetPowerWatts == 150 && writes.Count == count && engine.VirtualSpeedKmh is null,
            "gears and 100 rpm never change ERG watt targets or retain simulation diagnostics");
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        private long ticks;
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long GetTimestamp() => ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan elapsed) { utcNow += elapsed; ticks += elapsed.Ticks; }
    }

    private static async Task FailedWritesCannotAccumulateRampAsync()
    {
        var clock = new TestClock();
        var config = new LoggerConfig(); config.Zwift.ControlEnabled = true;
        var sample = new DeviceSample(20, 95, null, 40, 0, TimeSpan.Zero);
        var data = new BridgeTelemetry(sample, clock.GetUtcNow(), true);
        var writes = new List<int>(); bool fail = false;
        var engine = new TrainerControl(config, () => data,
            _ => Task.FromResult(new DaumPowerLimits(20, 1000, 20)), (watts, _) =>
            {
                writes.Add(watts);
                if (fail) throw new IOException("missing hardware acknowledgement");
                return Task.FromResult((double)watts);
            }, clock);
        var hill = new FtmsSimulationParameters(0, 10, .004, .51);
        await engine.InitializeAsync(default); await engine.SetSimulationAsync(hill, default);
        clock.Advance(TimeSpan.FromSeconds(1)); data = data with { Timestamp = clock.GetUtcNow() }; fail = true;
        await engine.TickAsync(default);
        Check(writes[^1] == 40 && engine.Mode == "idle" && engine.TargetPowerWatts == 20 && engine.LastError is not null,
            "unconfirmed load stops simulation rather than ramping ahead of the confirmed bike load");
        int count = writes.Count;
        for (int i = 0; i < 15; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1)); data = data with { Timestamp = clock.GetUtcNow() };
            await engine.TickAsync(default);
        }
        Check(writes.Count == count, "failed simulation sends no automatic writes while the connection recovers");
        fail = false; Check(await engine.SetSimulationAsync(hill, default) && writes[^1] == 20,
            "a new simulation request restarts from confirmed load instead of a silently accumulated high target");
    }

    private static async Task CancelledStopCannotReactivateSimulationAsync(LoggerConfig config, DeviceSample sample, FtmsSimulationParameters simulation)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0;
        var engine = new TrainerControl(config, () => new(sample, DateTimeOffset.UtcNow, true),
            _ => Task.FromResult(new DaumPowerLimits(20, 1000, 75)), async (watts, _) =>
            {
                writes++; entered.TrySetResult(); await release.Task; return watts;
            });
        await engine.InitializeAsync(default);
        var setting = engine.SetSimulationAsync(simulation, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        try { await engine.StopAsync(cancel.Token); }
        catch (OperationCanceledException) { }
        Check(engine.Mode == "idle", "stop cancels virtual simulation before waiting for a blocked command");
        release.TrySetResult();
        Check(!await setting, "an obsolete in-flight request cannot re-enable simulation after stop");
        Check(engine.LastError is not null, "a late load acknowledgement cannot erase an unconfirmed minimum-load warning");
        await engine.TickAsync(default);
        Check(writes == 1 && engine.Mode == "idle", "no automatic commands follow a timed-out stop");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
    }
}
