using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ErgoStravaLogger;

namespace ErgoStravaLogger.Tests;

public static class AutomaticCockpitTests
{
    public static async Task RunAsync()
    {
        await StartsOnceAndRetainsGearAsync();
        await ExistingGearIsPreservedAsync();
        await PedallingDefersActivationAsync();
        await DisabledBridgeAndControlDoNotActivateAsync();
        await ErgDefersActivationAsync();
        await NativeResetReactivatesAsync();
        await TcpReconnectReactivatesAsync();
        await InitialLimitsFailureRecoversBridgeAsync();
        await FailureKeepsTelemetryAndBacksOffAsync();
        Console.WriteLine("Automatic cockpit shifting / idle guards / retry: passed");
    }

    private static async Task StartsOnceAndRetainsGearAsync()
    {
        await using var fixture = new Fixture(virtualGear: 7);
        await fixture.ActiveGearAsync(7);
        Check(fixture.State.Writes.ToArray().SequenceEqual(new[] { "M718", "S230020.00" }),
            "startup enables the saved virtual gear and confirms minimum load on the shared Daum connection");
        Check(fixture.Config.Zwift.VirtualGear == 7 && fixture.Runtime.ZwiftBridge.GetStatus().CockpitGear == 7,
            "automatic activation establishes a native baseline without changing the stored virtual gear");
        var samples = fixture.State.SampleCount;
        await fixture.MoreSamplesAsync(samples);
        Check(fixture.State.Writes.Count == 2, "an active native gear is not reactivated on each telemetry poll");
        Check(fixture.State.Connections == 1, "automatic activation shares the telemetry TCP connection");
    }

    private static async Task ExistingGearIsPreservedAsync()
    {
        await using var fixture = new Fixture(nativeGear: 19, virtualGear: 7);
        await fixture.MoreSamplesAsync(0);
        await fixture.ActiveGearAsync(19);
        Check(fixture.State.Writes.IsEmpty, "an already active cockpit gear is not overwritten at startup");
        Check(fixture.Config.Zwift.VirtualGear == 7 && fixture.Runtime.ZwiftBridge.GetStatus().CockpitGear == 19,
            "an existing native gear becomes the baseline while retaining the saved virtual gear");
    }

    private static async Task PedallingDefersActivationAsync()
    {
        await using var fixture = new Fixture(cadence: 95);
        await fixture.MoreSamplesAsync(0);
        Check(fixture.State.Writes.IsEmpty, "automatic activation never changes native mode while pedalling");
        fixture.State.Cadence = 0;
        await fixture.ActiveGearAsync(12);
        Check(fixture.State.GearWrites == 1, "the first stationary telemetry sample permits deferred activation");
    }

    private static async Task DisabledBridgeAndControlDoNotActivateAsync()
    {
        await using (var fixture = new Fixture(bridgeEnabled: false))
        {
            await fixture.MoreSamplesAsync(0);
            Check(fixture.State.Writes.IsEmpty, "disabled Zwift bridge leaves the cockpit unchanged");
            await fixture.Runtime.ZwiftBridge.SetEnabledAsync(true, fixture.Token);
            await fixture.ActiveGearAsync(12);
            Check(fixture.State.GearWrites == 1, "enabling the bridge permits automatic activation");
        }
        await using (var fixture = new Fixture(controlEnabled: false))
        {
            await fixture.MoreSamplesAsync(0);
            Check(fixture.State.Writes.IsEmpty, "disabled resistance control leaves the cockpit unchanged");
            await fixture.Runtime.ZwiftBridge.UpdateControlSettingsAsync(true, 88, 220, fixture.Token);
            await fixture.ActiveGearAsync(12);
            Check(fixture.State.GearWrites == 1, "enabling resistance control permits automatic activation");
        }
    }

    private static async Task ErgDefersActivationAsync()
    {
        await using var fixture = new Fixture(cadence: 95);
        await fixture.WaitAsync(() => fixture.Runtime.TrainerControl.Available);
        Check(await fixture.Runtime.TrainerControl.SetTargetPowerAsync(150, fixture.Token), "the fixture enters ERG mode");
        fixture.State.Cadence = 0;
        var samples = fixture.State.SampleCount;
        await fixture.MoreSamplesAsync(samples);
        Check(fixture.State.GearWrites == 0 && fixture.Runtime.TrainerControl.TargetPowerWatts == 150,
            "automatic activation preserves an ERG target even while the rider is stationary");
        await fixture.Runtime.TrainerControl.StopAsync(fixture.Token);
        await fixture.ActiveGearAsync(12);
        Check(fixture.State.GearWrites == 1, "leaving ERG permits activation without a manual cockpit request");
    }

    private static async Task NativeResetReactivatesAsync()
    {
        await using var fixture = new Fixture(virtualGear: 8);
        await fixture.ActiveGearAsync(8);
        fixture.State.Gear = 0;
        await fixture.WaitAsync(() => fixture.State.GearWrites == 2);
        await fixture.ActiveGearAsync(8);
        Check(fixture.Config.Zwift.VirtualGear == 8,
            "a cockpit reset reactivates the saved gear without treating the new native baseline as a shift");
        Check(fixture.State.Writes.ToArray().SequenceEqual(new[] { "M719", "S230020.00", "M719", "S230020.00" }),
            "each native-mode reset is followed by one activation and minimum-load confirmation");
    }

    private static async Task FailureKeepsTelemetryAndBacksOffAsync()
    {
        var clock = new AdjustableClock();
        await using var fixture = new Fixture(clock: clock, rejectGear: true);
        await fixture.WaitAsync(() => DeviceError(fixture.Runtime) is not null && fixture.State.GearWrites == 1);
        var samples = fixture.State.SampleCount;
        await fixture.MoreSamplesAsync(samples);
        Check(fixture.State.GearWrites == 1 && DeviceError(fixture.Runtime) is not null,
            "activation failure remains visible while telemetry and the recorder continue without rapid retries");
        Check(fixture.State.Writes.ToArray().Take(2).SequenceEqual(new[] { "M7113", "S230020.00" }),
            "an invalid gear reply still triggers a best-effort minimum-load restoration");
        clock.Advance(TimeSpan.FromSeconds(29));
        samples = fixture.State.SampleCount;
        await fixture.MoreSamplesAsync(samples);
        Check(fixture.State.GearWrites == 1, "failed automatic activation is not retried before 30 seconds");
        fixture.State.RejectGear = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.WaitAsync(() => fixture.State.GearWrites == 2 && DeviceError(fixture.Runtime) is null);
        await fixture.ActiveGearAsync(12);
        Check(DeviceError(fixture.Runtime) is null, "successful delayed activation clears its diagnostic error");
        Check(fixture.Runtime.ZwiftBridge.GetStatus().DataAvailable,
            "the retry clock does not age or invalidate real telemetry timestamps");
    }

    private static async Task TcpReconnectReactivatesAsync()
    {
        await using var fixture = new Fixture(virtualGear: 7);
        await fixture.ActiveGearAsync(7);
        Check(await fixture.Runtime.TrainerControl.SetSimulationAsync(new FtmsSimulationParameters(0, 0, .004, .3), fixture.Token),
            "the reconnect fixture enters simulation so a false cockpit delta would affect the virtual gear");
        var connections = fixture.State.Connections;
        Interlocked.Exchange(ref fixture.State.DisconnectOnNextSample, 1);
        await fixture.WaitAsync(() => fixture.State.Connections > connections && fixture.State.GearWrites == 2);
        await fixture.ActiveGearAsync(7);
        Check(fixture.Config.Zwift.VirtualGear == 7 && fixture.Runtime.TrainerControl.Mode == "simulation",
            "a real TCP disconnect and cockpit-mode reset establish a new baseline without shifting the virtual gear or ending simulation");
        Check(fixture.State.Writes.Where(write => write.StartsWith("M71", StringComparison.Ordinal)).SequenceEqual(new[] { "M718", "M718" }),
            "a newly connected Daum with native shifting off is automatically activated again");
        var writes = fixture.State.Writes.ToArray();
        var activation = Array.FindLastIndex(writes, write => write.StartsWith("M71", StringComparison.Ordinal));
        Check(activation >= 0 && writes.Length > activation + 1 && writes[activation + 1] == "S230020.00",
            "reactivation after reconnect confirms minimum load on the new telemetry connection");
        Check(DeviceError(fixture.Runtime) is null && fixture.Runtime.ZwiftBridge.GetStatus().DataAvailable,
            "telemetry and bridge availability recover after the disconnected socket");
    }

    private static async Task InitialLimitsFailureRecoversBridgeAsync()
    {
        await using var fixture = new Fixture(virtualGear: 9, initialInvalidLimits: 1);
        await fixture.ActiveGearAsync(9);
        Check(fixture.State.InvalidLimitsResponses == 1 && fixture.State.LimitsReads >= 2 && fixture.State.Connections >= 2,
            "startup encountered an invalid L70 reply and recovered limits on a new Daum connection");
        var bridge = fixture.Runtime.ZwiftBridge.GetStatus();
        Check(bridge.Active && bridge.Controllable && bridge.DataAvailable && bridge.LastError is null,
            "fresh telemetry and recovered power limits automatically start a bridge whose initial setup failed");
        Check(fixture.State.Writes.ToArray().SequenceEqual(new[] { "M7110", "S230020.00" }) && fixture.Config.Zwift.VirtualGear == 9,
            "bridge recovery activates the saved native gear once and confirms minimum load without a manual request");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "ergo-auto-cockpit-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly StravaClient strava;
        private readonly Task serving;
        private readonly Task running;
        public FixtureState State { get; }
        public LoggerConfig Config { get; }
        public LoggerRuntime Runtime { get; }
        public CancellationToken Token => cancellation.Token;

        public Fixture(int cadence = 0, int nativeGear = 0, int virtualGear = 12,
            bool bridgeEnabled = true, bool controlEnabled = true, bool rejectGear = false, TimeProvider? clock = null,
            int initialInvalidLimits = 0)
        {
            Directory.CreateDirectory(directory);
            listener.Start();
            State = new FixtureState { Cadence = cadence, Gear = nativeGear, RejectGear = rejectGear, InvalidLimitsRemaining = initialInvalidLimits };
            serving = ServeAsync(listener, State, Token);
            Config = new LoggerConfig
            {
                ErgoIp = "127.0.0.1", ErgoPort = ((IPEndPoint)listener.LocalEndpoint).Port,
                OutputDirectory = directory, LoggingIntervalMs = 200, TimeoutMs = 1000,
                ReconnectDelaySeconds = 1, SynchronizeOnConnect = false,
                Strava = new() { Enabled = false, TokenFile = Path.Combine(directory, "tokens.json") },
                Zwift = new()
                {
                    Enabled = bridgeEnabled, ControlEnabled = controlEnabled,
                    BindAddress = "127.0.0.1", Port = 0, VirtualGear = virtualGear, RiderWeightKg = 88, MaxPowerWatts = 220
                }
            };
            strava = new StravaClient(Config.Strava);
            Runtime = new LoggerRuntime(Config, strava, (_, _, _) => new FakeDiscovery(), clock);
            running = Runtime.RunAsync(Token);
        }

        public async Task WaitAsync(Func<bool> ready)
        {
            while (!ready())
            {
                if (running.IsFaulted) await running;
                if (serving.IsFaulted) await serving;
                await Task.Delay(20, Token);
            }
        }

        public Task MoreSamplesAsync(int previous) => WaitAsync(() => State.SampleCount >= previous + 4);

        public Task ActiveGearAsync(int gear) => WaitAsync(() => NativeGear(Runtime) == gear &&
            Runtime.ZwiftBridge.GetStatus().CockpitGear == gear);

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            listener.Stop();
            try { await running; }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
            try { await serving; }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            strava.Dispose();
            cancellation.Dispose();
            var fullPath = Path.GetFullPath(directory);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Check(fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase), "fixture cleanup stays inside the temporary directory");
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private sealed class FixtureState
    {
        public volatile int Cadence, Gear, Power = 20;
        public int SampleCount, Connections, GearWrites, DisconnectOnNextSample, InvalidLimitsRemaining, InvalidLimitsResponses, LimitsReads;
        public volatile bool RejectGear;
        public ConcurrentQueue<string> Writes { get; } = new();
    }

    private static async Task ServeAsync(TcpListener listener, FixtureState state, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            Interlocked.Increment(ref state.Connections);
            try { await ServeConnectionAsync(client.GetStream(), state, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or SocketException) { }
        }
    }

    private static async Task ServeConnectionAsync(NetworkStream stream, FixtureState state, CancellationToken ct)
    {
        var one = new byte[1];
        while (true)
        {
            var bytes = new List<byte>();
            do
            {
                if (await stream.ReadAsync(one, ct) == 0) return;
                bytes.Add(one[0]);
            } while (one[0] != DaumProtocol.Etb);
            string command = Encoding.ASCII.GetString(bytes.ToArray(), 1, 3);
            string value = Encoding.ASCII.GetString(DaumProtocol.DecodeResponse(bytes.ToArray(), command));
            string reply;
            switch (command)
            {
                case "X70":
                    if (Interlocked.Exchange(ref state.DisconnectOnNextSample, 0) == 1)
                    {
                        state.Gear = 0;
                        return; // Close the actual socket before replying; the runtime must reconnect.
                    }
                    Interlocked.Increment(ref state.SampleCount);
                    reply = string.Join('\x1d', new[] { "0", "0", "0", "0", "0", state.Cadence.ToString(CultureInfo.InvariantCulture),
                        state.Power.ToString(CultureInfo.InvariantCulture), "0", "0", "0", (state.Gear + 1).ToString(CultureInfo.InvariantCulture), "1", "0", "" });
                    break;
                case "L70":
                    Interlocked.Increment(ref state.LimitsReads);
                    if (state.InvalidLimitsRemaining > 0)
                    {
                        Interlocked.Decrement(ref state.InvalidLimitsRemaining);
                        Interlocked.Increment(ref state.InvalidLimitsResponses);
                        reply = "W\u001d1000\u001d20\u001d20"; // Inverted limits fail initial bridge setup.
                    }
                    else reply = "W\u001d20\u001d1000\u001d20";
                    break;
                case "M71":
                    state.Writes.Enqueue(command + value);
                    Interlocked.Increment(ref state.GearWrites);
                    if (state.RejectGear) reply = "0";
                    else { state.Gear = int.Parse(value, CultureInfo.InvariantCulture) - 1; reply = value; }
                    break;
                case "S23":
                    state.Writes.Enqueue(command + value);
                    state.Power = (int)double.Parse(value, CultureInfo.InvariantCulture);
                    reply = value;
                    break;
                default: throw new Exception("Unexpected automatic cockpit command: " + command);
            }
            var body = Encoding.ASCII.GetBytes(command + reply);
            var response = new byte[] { DaumProtocol.Ack, DaumProtocol.Soh }.Concat(body)
                .Concat(Encoding.ASCII.GetBytes(DaumProtocol.ComputeChecksum(body).ToString("00", CultureInfo.InvariantCulture))).Append(DaumProtocol.Etb).ToArray();
            await stream.WriteAsync(response, ct);
            if (await stream.ReadAsync(one, ct) == 0) return;
            Check(one[0] == DaumProtocol.Ack, "the client acknowledges each fake cockpit response");
        }
    }

    private sealed class FakeDiscovery : IBridgeDiscovery
    {
        public bool IsRunning { get; private set; }
        public string? LastError => null;
        public Task StartAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct = default) { IsRunning = false; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsRunning = false; return ValueTask.CompletedTask; }
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private long ticks;
        private readonly DateTimeOffset initial = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => initial.AddTicks(Interlocked.Read(ref ticks));
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }

    private static int? NativeGear(LoggerRuntime runtime)
    {
        using var document = JsonSerializer.SerializeToDocument(runtime.SampleStatus());
        return document.RootElement.ValueKind == JsonValueKind.Null || document.RootElement.GetProperty("NativeGear").ValueKind == JsonValueKind.Null
            ? null : document.RootElement.GetProperty("NativeGear").GetInt32();
    }

    private static string? DeviceError(LoggerRuntime runtime)
    {
        using var document = JsonSerializer.SerializeToDocument(runtime.DeviceStatus());
        return document.RootElement.GetProperty("error").GetString();
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
