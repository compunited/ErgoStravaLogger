using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ErgoStravaLogger;

namespace ErgoStravaLogger.Tests;

/// <summary>Exercises the real loopback TCP lifecycle with fake discovery and no LAN multicast.</summary>
public static class ZwiftBridgeTests
{
    public static async Task RunAsync()
    {
        await TestTogglePersistenceAndRestartAsync();
        await TestDiscoveryFailureRollsBackAsync();
        await TestSaveFailureRollsBackAsync();
        await TestLegacySettingsDefaultGearAsync();
        await TestVirtualGearPreservesActiveTrainerAsync();
        await TestVirtualGearSaveFailureRollsBackAsync();
        await TestCockpitGearDeltasAsync();
        await TestCockpitGearPersistenceRetryAsync();
    }

    private static async Task TestTogglePersistenceAndRestartAsync()
    {
        using var fixture = new FixtureDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var config = Config(fixture.Path);
        var sample = new DeviceSample(225, 85, 140, 8, 100, TimeSpan.FromSeconds(15));
        BridgeTelemetry telemetry = new(sample, DateTimeOffset.UtcNow, true);
        var discoveries = new List<FakeDiscovery>();
        await using var manager = new ZwiftBridgeManager(config, () => telemetry, (_, services, port) =>
        {
            Check(services.Contains(DirConProtocol.CyclingPowerService), "discovery must advertise the real trainer services");
            var discovery = new FakeDiscovery(port);
            discoveries.Add(discovery);
            return discovery;
        });
        await manager.InitializeAsync(deadline.Token);
        var initial = manager.GetStatus();
        Check(!initial.Enabled && !initial.Active && !initial.DataAvailable && initial.Clients == 0,
            "the bridge must start disabled without exposing clients or data");
        Equal(0, discoveries.Count, "disabled startup must not start discovery");
        Check(!File.Exists(SettingsPath(fixture.Path)), "disabled startup must not create settings");
        var identity = config.Zwift.Identity;
        Check(identity.Length == 12 && identity.All(Uri.IsHexDigit), "the trainer receives a valid generated identity");

        await manager.SetEnabledAsync(true, deadline.Token);
        var enabled = manager.GetStatus();
        Check(enabled.Enabled && enabled.Active && enabled.DataAvailable, "enabling starts TCP and discovery with fresh data");
        Check(!enabled.Controllable && enabled.LastError is null, "the bridge reports measurement mode without an invented control capability");
        Check(enabled.Port > 0, "discovery receives the actual dynamically allocated TCP port");
        Equal(enabled.Port, discoveries[0].Port, "advertised port must match the listening TCP socket");
        await AssertSavedSettingsAsync(fixture.Path, true, identity);
        await manager.SetEnabledAsync(true, deadline.Token);
        Equal(1, discoveries.Count, "enabling an active bridge must not create a second discovery service");
        Equal(1, discoveries[0].StartCount, "enabling an active bridge must not restart discovery");
        Equal(enabled.Port, manager.GetStatus().Port, "idempotent enable must preserve the listening port");
        Equal(identity, config.Zwift.Identity, "idempotent enable must preserve the trainer identity");

        telemetry = new(sample, DateTimeOffset.UtcNow.AddSeconds(-10), true);
        Check(!manager.GetStatus().DataAvailable, "stale samples must not appear available");
        telemetry = new(sample, DateTimeOffset.UtcNow.AddMinutes(1), true);
        Check(!manager.GetStatus().DataAvailable, "future samples must not appear available");
        telemetry = new(sample, DateTimeOffset.UtcNow, false);
        Check(!manager.GetStatus().DataAvailable, "disconnected samples must not appear available");
        telemetry = new(null, null, true);
        Check(!manager.GetStatus().DataAvailable, "missing samples must not appear available");
        telemetry = new(sample, DateTimeOffset.UtcNow, true);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, enabled.Port, deadline.Token);
            await WaitUntilAsync(() => manager.GetStatus().Clients == 1, deadline.Token);
            await manager.SetEnabledAsync(false, deadline.Token);
            await AssertDisconnectedAsync(client, deadline.Token);
        }
        var disabled = manager.GetStatus();
        Check(!disabled.Enabled && !disabled.Active && disabled.Clients == 0 && !disabled.DataAvailable,
            "disabling must stop the trainer and clear live client state");
        Check(!discoveries[0].IsRunning && discoveries[0].DisposeCount == 1, "disabling must dispose discovery");
        await AssertPortClosedAsync(enabled.Port, deadline.Token);
        AssertPortReleased(enabled.Port);
        await AssertSavedSettingsAsync(fixture.Path, false, identity);
        var restoredDisabled = Config(fixture.Path);
        ZwiftBridgeSettingsStore.Apply(restoredDisabled);
        Check(!restoredDisabled.Zwift.Enabled, "disabled setting survives reload");
        Equal(identity, restoredDisabled.Zwift.Identity, "disabled setting preserves the trainer identity");

        await manager.SetEnabledAsync(true, deadline.Token);
        Equal(2, discoveries.Count, "re-enabling after a stop creates one fresh discovery service");
        Equal(identity, config.Zwift.Identity, "re-enabling preserves the trainer identity");
        var savedBeforeStop = await File.ReadAllTextAsync(SettingsPath(fixture.Path), deadline.Token);
        await manager.StopAsync();
        Check(manager.GetStatus().Enabled && !manager.GetStatus().Active, "shutdown preserves the desired enabled state");
        Equal(savedBeforeStop, await File.ReadAllTextAsync(SettingsPath(fixture.Path), deadline.Token),
            "shutdown must not change persisted settings");

        var restartedConfig = Config(fixture.Path);
        ZwiftBridgeSettingsStore.Apply(restartedConfig);
        Check(restartedConfig.Zwift.Enabled, "enabled setting survives reload");
        Equal(identity, restartedConfig.Zwift.Identity, "identity survives reload");
        var restartedDiscovery = new List<FakeDiscovery>();
        await using var restarted = new ZwiftBridgeManager(restartedConfig,
            () => new BridgeTelemetry(sample, DateTimeOffset.UtcNow, true), (_, _, port) =>
            {
                var discovery = new FakeDiscovery(port);
                restartedDiscovery.Add(discovery);
                return discovery;
            });
        await restarted.InitializeAsync(deadline.Token);
        Check(restarted.GetStatus().Enabled && restarted.GetStatus().Active && restarted.GetStatus().DataAvailable,
            "saved enabled state starts a real trainer after restart");
        Equal(1, restartedDiscovery.Count, "restart starts one discovery instance");
        await restarted.StopAsync();
        await AssertSavedSettingsAsync(fixture.Path, true, identity);
        Equal(1, Directory.GetFiles(fixture.Path).Length, "settings updates must leave no temporary files");
    }

    private static async Task TestDiscoveryFailureRollsBackAsync()
    {
        using var fixture = new FixtureDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var config = Config(fixture.Path);
        FakeDiscovery? attempted = null;
        await using var manager = new ZwiftBridgeManager(config, () => new(null, null, false), (_, _, port) =>
            attempted = new FakeDiscovery(port, failStart: true));
        await manager.InitializeAsync(deadline.Token);
        await ThrowsAsync<IOException>(() => manager.SetEnabledAsync(true, deadline.Token));
        var failed = manager.GetStatus();
        Check(!failed.Enabled && !failed.Active && !failed.DataAvailable && failed.Clients == 0,
            "failed discovery must leave the bridge disabled and inactive");
        Check(failed.LastError is not null && failed.LastError.Contains("discovery-start-failed", StringComparison.Ordinal),
            "failed discovery must expose the useful startup error");
        Check(attempted is not null && attempted.DisposeCount == 1, "failed discovery must be disposed");
        Check(!File.Exists(SettingsPath(fixture.Path)), "failed enable must not persist an enabled setting");
        await AssertPortClosedAsync(attempted!.Port, deadline.Token);
        AssertPortReleased(attempted.Port);
    }

    private static async Task TestSaveFailureRollsBackAsync()
    {
        using var fixture = new FixtureDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var blockedDirectory = System.IO.Path.Combine(fixture.Path, "existing-file");
        await File.WriteAllTextAsync(blockedDirectory, "fixture-sentinel", deadline.Token);
        var config = Config(blockedDirectory);
        FakeDiscovery? attempted = null;
        await using var manager = new ZwiftBridgeManager(config, () => new(null, null, false), (_, _, port) =>
            attempted = new FakeDiscovery(port));
        await manager.InitializeAsync(deadline.Token);
        await ThrowsAsync<IOException>(() => manager.SetEnabledAsync(true, deadline.Token));
        var failed = manager.GetStatus();
        Check(!failed.Enabled && !failed.Active && !config.Zwift.Enabled, "failed persistence must roll back the running trainer and live setting");
        Check(failed.LastError is not null, "failed persistence must report an error");
        Check(attempted is not null && attempted.StartCount == 1 && attempted.DisposeCount == 1,
            "failed persistence must clean up the already-started discovery instance");
        Equal("fixture-sentinel", await File.ReadAllTextAsync(blockedDirectory, deadline.Token), "failed persistence must preserve existing files");
        await AssertPortClosedAsync(attempted!.Port, deadline.Token);
        AssertPortReleased(attempted.Port);
    }

    private static async Task TestLegacySettingsDefaultGearAsync()
    {
        using var fixture = new FixtureDirectory();
        const string identity = "020102030405";
        await File.WriteAllTextAsync(SettingsPath(fixture.Path), JsonSerializer.Serialize(new
        {
            Enabled = false, Identity = identity, ControlEnabled = false, RiderWeightKg = 88, MaxPowerWatts = 300
        }));
        var config = Config(fixture.Path);
        ZwiftBridgeSettingsStore.Apply(config);
        Equal(12, config.Zwift.VirtualGear, "settings saved before virtual gearing default to the neutral gear");
        Equal(identity, config.Zwift.Identity, "legacy settings preserve the existing trainer identity");
        Check(!config.Zwift.Enabled && !config.Zwift.ControlEnabled, "legacy settings preserve disabled bridge/control switches");
        Equal(88d, config.Zwift.RiderWeightKg, "legacy settings preserve rider weight");
        Equal(300, config.Zwift.MaxPowerWatts, "legacy settings preserve power limit");
        await using var manager = new ZwiftBridgeManager(config, () => new(null, null, false),
            (_, _, _) => throw new Exception("Disabled legacy settings must not start discovery."));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await manager.InitializeAsync(deadline.Token);
        Equal(12, manager.GetStatus().VirtualGear, "live status reports the migrated default gear");
        await manager.SetVirtualGearAsync(24, deadline.Token);
        var restored = Config(fixture.Path);
        ZwiftBridgeSettingsStore.Apply(restored);
        Equal(24, restored.Zwift.VirtualGear, "selected gear survives a settings reload");
        Equal(identity, restored.Zwift.Identity, "saving the migrated gear retains the identity");
        Check(!manager.GetStatus().Active && !restored.Zwift.Enabled && !restored.Zwift.ControlEnabled,
            "saving a gear in measurement mode must not start or enable resistance control");
    }

    private static async Task TestVirtualGearPreservesActiveTrainerAsync()
    {
        using var fixture = new FixtureDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var config = Config(fixture.Path);
        config.Zwift.ControlEnabled = true;
        config.Zwift.RiderWeightKg = 88;
        config.Zwift.MaxPowerWatts = 300;
        var sample = new DeviceSample(150, 85, 140, 8, 100, TimeSpan.FromSeconds(15));
        var writes = new List<int>();
        var controller = new TrainerControl(config, () => new(sample, DateTimeOffset.UtcNow, true),
            _ => Task.FromResult(new DaumPowerLimits(20, 1000, 75)),
            (watts, _) => { writes.Add(watts); return Task.FromResult<double>(watts); });
        var discoveries = new List<FakeDiscovery>();
        await using var manager = new ZwiftBridgeManager(config, () => new(sample, DateTimeOffset.UtcNow, true), (_, _, port) =>
        {
            var discovery = new FakeDiscovery(port);
            discoveries.Add(discovery);
            return discovery;
        }, controller);
        await manager.InitializeAsync(deadline.Token);
        await manager.SetEnabledAsync(true, deadline.Token);
        Check(await controller.SetTargetPowerAsync(150, deadline.Token), "fixture must establish a confirmed ERG target");
        using var client = new TcpClient();
        var initial = manager.GetStatus();
        await client.ConnectAsync(IPAddress.Loopback, initial.Port, deadline.Token);
        await WaitUntilAsync(() => manager.GetStatus().Clients == 1, deadline.Token);
        var identity = config.Zwift.Identity;
        Equal(1, writes.Count, "fixture writes only its initial ERG request");

        foreach (var gear in new[] { 1, 24 })
        {
            await manager.SetVirtualGearAsync(gear, deadline.Token);
            var status = manager.GetStatus();
            Equal(gear, status.VirtualGear, "live gear changes update the status");
            Check(status.Enabled && status.Active && status.Controllable && status.ControlEnabled && status.Clients == 1,
                "gear changes preserve the active controllable trainer and its connected client");
            Equal(initial.Port, status.Port, "gear changes retain the TCP listening port");
            Equal(identity, config.Zwift.Identity, "gear changes retain the advertised identity");
            Equal("erg", status.ControlMode, "gear changes must not stop an active ERG mode");
            Equal<int?>(150, status.TargetPowerWatts, "gear changes must not overwrite the confirmed ERG target");
            Equal(1, writes.Count, "changing gear never issues a direct resistance command");
            Equal(1, discoveries.Count, "changing gear must not recreate discovery");
            Equal(1, discoveries[0].StartCount, "changing gear must not restart discovery");
            Equal(0, discoveries[0].StopCount, "changing gear must not stop discovery");
            Equal(0, discoveries[0].DisposeCount, "changing gear must not dispose discovery");
            var restored = Config(fixture.Path);
            ZwiftBridgeSettingsStore.Apply(restored);
            Equal(gear, restored.Zwift.VirtualGear, "both gear boundaries persist");
            Check(restored.Zwift.Enabled && restored.Zwift.ControlEnabled, "gear persistence preserves both enabled switches");
            Equal(88d, restored.Zwift.RiderWeightKg, "gear persistence preserves weight");
            Equal(300, restored.Zwift.MaxPowerWatts, "gear persistence preserves the power cap");
            Equal(identity, restored.Zwift.Identity, "gear persistence preserves identity");
        }

        var savedSettings = await File.ReadAllTextAsync(SettingsPath(fixture.Path), deadline.Token);
        foreach (var invalid in new[] { 0, 25 })
            await ThrowsAsync<ArgumentException>(() => manager.SetVirtualGearAsync(invalid, deadline.Token));
        Equal(24, config.Zwift.VirtualGear, "invalid gear must leave the live selection unchanged");
        Equal(savedSettings, await File.ReadAllTextAsync(SettingsPath(fixture.Path), deadline.Token), "invalid gear must leave persisted preferences unchanged");
        Equal(1, manager.GetStatus().Clients, "invalid gear must retain connected clients");
        Equal(1, writes.Count, "invalid gear must not send device commands");
        Equal(0, discoveries[0].StopCount, "invalid gear must not stop discovery");
        await manager.StopAsync();
        await AssertDisconnectedAsync(client, deadline.Token);
    }

    private static async Task TestVirtualGearSaveFailureRollsBackAsync()
    {
        using var fixture = new FixtureDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var config = Config(fixture.Path);
        config.Zwift.RiderWeightKg = 88;
        config.Zwift.VirtualGear = 7;
        FakeDiscovery? discovery = null;
        await using var manager = new ZwiftBridgeManager(config, () => new(null, null, false), (_, _, port) => discovery = new FakeDiscovery(port));
        await manager.InitializeAsync(deadline.Token);
        await manager.SetEnabledAsync(true, deadline.Token);
        var initial = manager.GetStatus();
        var identity = config.Zwift.Identity;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, initial.Port, deadline.Token);
        await WaitUntilAsync(() => manager.GetStatus().Clients == 1, deadline.Token);
        var settingsPath = SettingsPath(fixture.Path);
        var savedSettings = await File.ReadAllTextAsync(settingsPath, deadline.Token);
        var backupPath = settingsPath + ".test-backup";
        File.Move(settingsPath, backupPath);
        Directory.CreateDirectory(settingsPath);
        try
        {
            await ThrowsPersistenceFailureAsync(() => manager.SetVirtualGearAsync(24, deadline.Token));
            Equal(7, config.Zwift.VirtualGear, "failed gear persistence rolls back the in-memory option");
            var status = manager.GetStatus();
            Equal(7, status.VirtualGear, "failed gear persistence rolls back the live status");
            Check(status.Active && status.Enabled && status.Clients == 1, "failed gear persistence preserves the running trainer and client");
            Equal(initial.Port, status.Port, "failed gear persistence preserves the listening port");
            Equal(identity, config.Zwift.Identity, "failed gear persistence preserves identity");
            Equal(88d, config.Zwift.RiderWeightKg, "failed gear persistence preserves weight");
            Check(!config.Zwift.ControlEnabled, "failed gear persistence preserves measurement mode");
            Equal(1, discovery!.StartCount, "failed gear persistence must not restart discovery");
            Equal(0, discovery.StopCount, "failed gear persistence must not stop discovery");
            Equal(0, discovery.DisposeCount, "failed gear persistence must not dispose discovery");
            Equal(savedSettings, await File.ReadAllTextAsync(backupPath, deadline.Token), "failed gear persistence preserves original settings");
            Equal(0, Directory.GetFileSystemEntries(settingsPath).Length, "failed gear persistence leaves the blocking directory intact");
            Equal(2, Directory.GetFileSystemEntries(fixture.Path).Length, "failed gear persistence leaves no temporary files");
        }
        finally
        {
            Directory.Delete(settingsPath);
            File.Move(backupPath, settingsPath);
        }
        await manager.StopAsync();
    }

    private static async Task TestCockpitGearDeltasAsync()
    {
        using var fixture = new FixtureDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var config = Config(fixture.Path);
        config.Zwift.ControlEnabled = true;
        config.Zwift.RiderWeightKg = 88;
        var sample = new DeviceSample(150, 85, 140, 8, 100, TimeSpan.FromSeconds(15));
        var writes = new List<int>();
        var controller = new TrainerControl(config, () => new(sample, DateTimeOffset.UtcNow, true),
            _ => Task.FromResult(new DaumPowerLimits(20, 1000, 75)),
            (watts, _) => { writes.Add(watts); return Task.FromResult<double>(watts); });
        var discoveries = new List<FakeDiscovery>();
        await using var manager = new ZwiftBridgeManager(config, () => new(sample, DateTimeOffset.UtcNow, true), (_, _, port) =>
        {
            var discovery = new FakeDiscovery(port);
            discoveries.Add(discovery);
            return discovery;
        }, controller);
        await manager.InitializeAsync(deadline.Token);
        await manager.SetEnabledAsync(true, deadline.Token);
        var simulation = new FtmsSimulationParameters(0, 2, 0.004, 0.3);
        Check(await controller.SetSimulationAsync(simulation, deadline.Token), "cockpit fixture must enter simulation mode");
        var initial = manager.GetStatus();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, initial.Port, deadline.Token);
        await WaitUntilAsync(() => manager.GetStatus().Clients == 1, deadline.Token);
        var writeCount = writes.Count;
        var target = controller.TargetPowerWatts;
        var identity = config.Zwift.Identity;

        await manager.ObserveCockpitGearAsync(19, deadline.Token);
        Equal(12, manager.GetStatus().VirtualGear, "first native gear establishes a baseline without overwriting the stored virtual gear");
        Equal<int?>(19, manager.GetStatus().CockpitGear, "status reports the observed native cockpit gear");
        await manager.ObserveCockpitGearAsync(21, deadline.Token);
        Equal(14, manager.GetStatus().VirtualGear, "positive native gear delta increments the virtual gear by the same amount");
        await manager.ObserveCockpitGearAsync(18, deadline.Token);
        Equal(11, manager.GetStatus().VirtualGear, "negative native gear delta decrements the virtual gear by the same amount");
        await manager.ObserveCockpitGearAsync(18, deadline.Token);
        Equal(11, manager.GetStatus().VirtualGear, "repeated native samples must not apply a delta twice");

        await manager.SetVirtualGearAsync(24, deadline.Token);
        await manager.ObserveCockpitGearAsync(19, deadline.Token);
        Equal(24, manager.GetStatus().VirtualGear, "positive native deltas clamp at virtual gear 24");
        await manager.ObserveCockpitGearAsync(18, deadline.Token);
        Equal(23, manager.GetStatus().VirtualGear, "a clamped high shift still updates its native baseline");
        await manager.SetVirtualGearAsync(1, deadline.Token);
        await manager.ObserveCockpitGearAsync(17, deadline.Token);
        Equal(1, manager.GetStatus().VirtualGear, "negative native deltas clamp at virtual gear 1");
        await manager.ObserveCockpitGearAsync(18, deadline.Token);
        Equal(2, manager.GetStatus().VirtualGear, "a clamped low shift still updates its native baseline");

        await manager.ObserveCockpitGearAsync(null, deadline.Token);
        Equal<int?>(null, manager.GetStatus().CockpitGear, "lost device data clears the displayed cockpit gear");
        await manager.ObserveCockpitGearAsync(28, deadline.Token);
        Equal(2, manager.GetStatus().VirtualGear, "first cockpit sample after reconnect must not create a jump");
        await manager.ObserveCockpitGearAsync(27, deadline.Token);
        Equal(1, manager.GetStatus().VirtualGear, "native gear 28 is a valid reconnect baseline");
        await manager.ObserveCockpitGearAsync(0, deadline.Token);
        await manager.ObserveCockpitGearAsync(1, deadline.Token);
        Equal(1, manager.GetStatus().VirtualGear, "invalid native gear zero clears the baseline before native gear one");
        await manager.ObserveCockpitGearAsync(2, deadline.Token);
        Equal(2, manager.GetStatus().VirtualGear, "native gear one is a valid baseline");
        await manager.ObserveCockpitGearAsync(29, deadline.Token);
        await manager.ObserveCockpitGearAsync(19, deadline.Token);
        Equal(2, manager.GetStatus().VirtualGear, "native gear above 28 clears the baseline before the next valid sample");
        Equal(writeCount, writes.Count, "observing cockpit shifts never directly writes hardware resistance");
        Equal(target, controller.TargetPowerWatts, "observing cockpit shifts leaves the confirmed load for the next simulation tick");
        Equal("simulation", controller.Mode, "observing cockpit shifts keeps simulation active");
        var status = manager.GetStatus();
        Check(status.Active && status.Clients == 1 && status.Port == initial.Port, "cockpit shifts preserve the active TCP port and client");
        Equal(identity, config.Zwift.Identity, "cockpit shifts preserve trainer identity");
        Equal(1, discoveries.Count, "cockpit shifts never recreate discovery");
        Equal(1, discoveries[0].StartCount, "cockpit shifts never restart discovery");
        Equal(0, discoveries[0].StopCount, "cockpit shifts never stop discovery");
        var restored = Config(fixture.Path);
        ZwiftBridgeSettingsStore.Apply(restored);
        Equal(2, restored.Zwift.VirtualGear, "cockpit-selected virtual gear persists");
        Check(restored.Zwift.Enabled && restored.Zwift.ControlEnabled && restored.Zwift.RiderWeightKg == 88,
            "cockpit shifts preserve the other persisted trainer preferences");

        Check(await controller.SetTargetPowerAsync(150, deadline.Token), "cockpit fixture must enter ERG mode");
        writeCount = writes.Count;
        await manager.ObserveCockpitGearAsync(21, deadline.Token);
        Equal(2, manager.GetStatus().VirtualGear, "ERG mode ignores native gear deltas");
        Equal(writeCount, writes.Count, "ERG cockpit observations do not change load");
        Check(await controller.SetSimulationAsync(simulation, deadline.Token), "fixture must resume simulation after ERG");
        await manager.ObserveCockpitGearAsync(22, deadline.Token);
        Equal(3, manager.GetStatus().VirtualGear, "ignored ERG shifts still update the native baseline");
        Check(await controller.StopAsync(deadline.Token), "fixture must enter idle control mode");
        writeCount = writes.Count;
        await manager.ObserveCockpitGearAsync(25, deadline.Token);
        Equal(3, manager.GetStatus().VirtualGear, "idle mode ignores native gear deltas");
        Equal(writeCount, writes.Count, "idle cockpit observations do not change load");
        Check(await controller.SetSimulationAsync(simulation, deadline.Token), "fixture must resume simulation after idle");
        await manager.ObserveCockpitGearAsync(26, deadline.Token);
        Equal(4, manager.GetStatus().VirtualGear, "ignored idle shifts still update the native baseline");

        config.Zwift.ControlEnabled = false;
        writeCount = writes.Count;
        await manager.ObserveCockpitGearAsync(27, deadline.Token);
        Equal(4, manager.GetStatus().VirtualGear, "disabled resistance control ignores cockpit shifts");
        Equal(writeCount, writes.Count, "disabled control observations do not write hardware");
        config.Zwift.ControlEnabled = true;
        await manager.ObserveCockpitGearAsync(28, deadline.Token);
        Equal(5, manager.GetStatus().VirtualGear, "ignored disabled-control shifts still update the native baseline");
        await manager.SetEnabledAsync(false, deadline.Token);
        writeCount = writes.Count;
        await manager.ObserveCockpitGearAsync(25, deadline.Token);
        Equal(5, manager.GetStatus().VirtualGear, "disabled bridge ignores cockpit shifts");
        Equal(writeCount, writes.Count, "disabled bridge observations do not write hardware");
        await manager.SetEnabledAsync(true, deadline.Token);
        await manager.ObserveCockpitGearAsync(26, deadline.Token);
        Equal(6, manager.GetStatus().VirtualGear, "ignored disabled-bridge shifts still update the native baseline");
        await manager.StopAsync();
    }

    private static async Task TestCockpitGearPersistenceRetryAsync()
    {
        using var fixture = new FixtureDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var config = Config(fixture.Path);
        config.Zwift.ControlEnabled = true;
        var sample = new DeviceSample(150, 85, 140, 8, 100, TimeSpan.FromSeconds(15));
        var writes = new List<int>();
        var controller = new TrainerControl(config, () => new(sample, DateTimeOffset.UtcNow, true),
            _ => Task.FromResult(new DaumPowerLimits(20, 1000, 75)),
            (watts, _) => { writes.Add(watts); return Task.FromResult<double>(watts); });
        FakeDiscovery? discovery = null;
        await using var manager = new ZwiftBridgeManager(config, () => new(sample, DateTimeOffset.UtcNow, true),
            (_, _, port) => discovery = new FakeDiscovery(port), controller);
        await manager.InitializeAsync(deadline.Token);
        await manager.SetEnabledAsync(true, deadline.Token);
        Check(await controller.SetSimulationAsync(new FtmsSimulationParameters(0, 2, 0.004, 0.3), deadline.Token),
            "persistence retry fixture must enter simulation mode");
        await manager.ObserveCockpitGearAsync(19, deadline.Token);
        var initial = manager.GetStatus();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, initial.Port, deadline.Token);
        await WaitUntilAsync(() => manager.GetStatus().Clients == 1, deadline.Token);
        var writeCount = writes.Count;
        var settingsPath = SettingsPath(fixture.Path);
        var savedSettings = await File.ReadAllTextAsync(settingsPath, deadline.Token);
        var backupPath = settingsPath + ".test-backup";
        File.Move(settingsPath, backupPath);
        Directory.CreateDirectory(settingsPath);
        try
        {
            await manager.ObserveCockpitGearAsync(20, deadline.Token);
            var failed = manager.GetStatus();
            Equal(12, failed.VirtualGear, "failed cockpit persistence must keep the live virtual gear unchanged");
            Equal<int?>(20, failed.CockpitGear, "failed cockpit persistence still reports the latest observed native gear");
            Check(!string.IsNullOrEmpty(failed.CockpitShiftError), "failed cockpit persistence must report a shift-specific error");
            Check(failed.Active && failed.Clients == 1 && failed.Port == initial.Port, "failed cockpit persistence preserves the TCP port and client");
            Equal(writeCount, writes.Count, "failed cockpit persistence must not write hardware");
            Equal(1, discovery!.StartCount, "failed cockpit persistence must not restart discovery");
            Equal(0, discovery.StopCount, "failed cockpit persistence must not stop discovery");
            Equal(0, discovery.DisposeCount, "failed cockpit persistence must not dispose discovery");
            Equal(savedSettings, await File.ReadAllTextAsync(backupPath, deadline.Token), "failed cockpit persistence preserves saved preferences");
            Equal(2, Directory.GetFileSystemEntries(fixture.Path).Length, "failed cockpit persistence leaves no temporary files");
        }
        finally
        {
            Directory.Delete(settingsPath);
            File.Move(backupPath, settingsPath);
        }
        await manager.ObserveCockpitGearAsync(20, deadline.Token);
        Equal(13, manager.GetStatus().VirtualGear, "the same native sample retries the delta after persistence recovers");
        Check(manager.GetStatus().CockpitShiftError is null, "successful cockpit retry clears its persistence error");
        await manager.ObserveCockpitGearAsync(20, deadline.Token);
        Equal(13, manager.GetStatus().VirtualGear, "successfully retried delta must not be applied twice");
        Equal(writeCount, writes.Count, "a successful observer retry still leaves hardware writes to the simulation tick");
        var restored = Config(fixture.Path);
        ZwiftBridgeSettingsStore.Apply(restored);
        Equal(13, restored.Zwift.VirtualGear, "successfully retried cockpit gear persists");

        File.Move(settingsPath, backupPath);
        Directory.CreateDirectory(settingsPath);
        try
        {
            await manager.ObserveCockpitGearAsync(21, deadline.Token);
            Check(manager.GetStatus().CockpitShiftError is not null, "second save failure must report a cockpit shift error");
        }
        finally
        {
            Directory.Delete(settingsPath);
            File.Move(backupPath, settingsPath);
        }
        await manager.SetVirtualGearAsync(7, deadline.Token);
        Check(manager.GetStatus().CockpitShiftError is null, "successful manual gear selection clears the cockpit shift error");
        Equal(7, manager.GetStatus().VirtualGear, "manual gear selection stays available after an observer save failure");
        Equal(writeCount, writes.Count, "manual error recovery never writes hardware resistance");
        await manager.StopAsync();
    }

    private static LoggerConfig Config(string output) => new()
    {
        OutputDirectory = output,
        Zwift = new() { Enabled = false, BindAddress = "127.0.0.1", Port = 0, StaleAfterSeconds = 5 },
        Strava = new() { Enabled = false }
    };

    private static string SettingsPath(string directory) => System.IO.Path.Combine(directory, "zwift-settings.json");

    private static async Task AssertSavedSettingsAsync(string directory, bool enabled, string identity)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(SettingsPath(directory)));
        var properties = document.RootElement.EnumerateObject().ToArray();
        var allowed = new HashSet<string> { "Enabled", "Identity", "ControlEnabled", "RiderWeightKg", "MaxPowerWatts", "VirtualGear" };
        Check(properties.All(property => allowed.Contains(property.Name)), "bridge settings contain only identity and bridge/control options");
        Equal(enabled, properties.Single(p => p.Name.Equals("enabled", StringComparison.OrdinalIgnoreCase)).Value.GetBoolean(), "saved enabled state");
        Equal(identity, properties.Single(p => p.Name.Equals("identity", StringComparison.OrdinalIgnoreCase)).Value.GetString(), "saved trainer identity");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(15, ct);
    }

    private static async Task AssertDisconnectedAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            Equal(0, await client.GetStream().ReadAsync(new byte[1], ct), "disabling must close connected TCP clients");
        }
        catch (Exception ex) when (ex is IOException or SocketException) { }
    }

    private static async Task AssertPortClosedAsync(int port, CancellationToken ct)
    {
        using var probe = new TcpClient();
        try { await probe.ConnectAsync(IPAddress.Loopback, port, ct); }
        catch (SocketException) { return; }
        throw new Exception("Stopped trainer still accepts TCP connections.");
    }

    private static void AssertPortReleased(int port)
    {
        var probe = new TcpListener(IPAddress.Loopback, port);
        // Reuse avoids platform TIME_WAIT differences for the deliberately disconnected accepted client.
        probe.ExclusiveAddressUse = false;
        probe.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try { probe.Start(); }
        finally { probe.Stop(); }
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ".");
    }

    private static async Task ThrowsPersistenceFailureAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        throw new Exception("Expected a file-system persistence failure.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("Assertion failed: " + message);
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");

    private sealed class FakeDiscovery(int port, bool failStart = false) : IBridgeDiscovery
    {
        public int Port { get; } = port;
        public bool IsRunning { get; private set; }
        public string? LastError { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public Task StartAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            StartCount++;
            if (failStart) { LastError = "discovery-start-failed"; throw new IOException(LastError); }
            IsRunning = true;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct = default)
        {
            StopCount++;
            IsRunning = false;
            return Task.CompletedTask;
        }
        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            await StopAsync();
        }
    }

    private sealed class FixtureDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "zwift-manager-test-" + Guid.NewGuid().ToString("N"));
        public FixtureDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
