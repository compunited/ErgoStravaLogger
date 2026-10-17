using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ErgoStravaLogger;

namespace ErgoStravaLogger.Tests;

public static class CockpitActivationTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ergo-cockpit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var state = new FixtureState();
        var serving = ServeAsync(listener, state, cancellation.Token);
        var config = new LoggerConfig
        {
            ErgoIp = "127.0.0.1", ErgoPort = ((IPEndPoint)listener.LocalEndpoint).Port,
            OutputDirectory = directory, LoggingIntervalMs = 200, SynchronizeOnConnect = false,
            Strava = new() { Enabled = false, TokenFile = Path.Combine(directory, "tokens.json") },
            Zwift = new() { ControlEnabled = true }
        };
        using var strava = new StravaClient(config.Strava);
        var runtime = new LoggerRuntime(config, strava);
        var running = runtime.RunAsync(cancellation.Token);
        try
        {
            await WaitUntilAsync(() => runtime.TrainerControl.Available, cancellation.Token);
            config.Zwift.ControlEnabled = false;
            await RejectedAsync(() => runtime.ActivateCockpitShiftingAsync(cancellation.Token));
            Check(state.Writes.IsEmpty, "disabled control cannot activate a native gear");
            config.Zwift.ControlEnabled = true;
            state.Cadence = 95;
            await WaitUntilAsync(() => Cadence(runtime) == 95, cancellation.Token);
            await RejectedAsync(() => runtime.ActivateCockpitShiftingAsync(cancellation.Token));
            Check(state.Writes.IsEmpty, "activation while pedalling performs no writes");
            state.Cadence = 0;
            await WaitUntilAsync(() => Cadence(runtime) == 0, cancellation.Token);
            Check(await runtime.TrainerControl.SetTargetPowerAsync(150, cancellation.Token), "fixture accepts ERG control");
            int count = state.Writes.Count;
            await RejectedAsync(() => runtime.ActivateCockpitShiftingAsync(cancellation.Token));
            Check(state.Writes.Count == count, "activation does not override an ERG target");
            await runtime.TrainerControl.StopAsync(cancellation.Token);
            while (state.Writes.TryDequeue(out _)) { }
            Check(await runtime.ActivateCockpitShiftingAsync(cancellation.Token) == 12, "the cockpit confirms the requested native gear");
            Check(state.Writes.ToArray().SequenceEqual(new[] { "M7113", "S230020.00" }),
                "activation sends only the native gear and restores the bounded 20 W minimum on the shared connection");
            await WaitUntilAsync(() => NativeGear(runtime) == 12, cancellation.Token);
            count = state.Writes.Count;
            Check(await runtime.ActivateCockpitShiftingAsync(cancellation.Token) == 12 && state.Writes.Count == count,
                "an already active cockpit gear is read without writing or resetting the training");
            state.Gear = 0;
            await WaitUntilAsync(() => NativeGear(runtime) is null, cancellation.Token);
            state.GearReply = 0;
            while (state.Writes.TryDequeue(out _)) { }
            bool failed = false;
            try { await runtime.ActivateCockpitShiftingAsync(cancellation.Token); }
            catch (InvalidDataException) { failed = true; }
            Check(failed && state.Writes.ToArray().SequenceEqual(new[] { "M7113", "S230020.00" }),
                "an invalid gear acknowledgement still restores the minimum load before reporting failure");
            Console.WriteLine("Cockpit activation / shared connection / minimum load: passed");
        }
        finally
        {
            cancellation.Cancel(); listener.Stop();
            await running;
            try { await serving; }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FixtureState
    {
        public volatile int Cadence;
        public int Gear, Power = 20;
        public int? GearReply;
        public ConcurrentQueue<string> Writes { get; } = new();
    }

    private static async Task ServeAsync(TcpListener listener, FixtureState state, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        var stream = client.GetStream(); var one = new byte[1];
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
                    reply = string.Join('\x1d', new[] { "0", "0", "0", "0", "0", state.Cadence.ToString(CultureInfo.InvariantCulture),
                        state.Power.ToString(CultureInfo.InvariantCulture), "0", "0", "0", (state.Gear + 1).ToString(CultureInfo.InvariantCulture), "1", "0", "" });
                    break;
                case "L70": reply = "W\u001d20\u001d1000\u001d20"; break;
                case "M71": state.Gear = int.Parse(value, CultureInfo.InvariantCulture) - 1; state.Writes.Enqueue(command + value); reply = (state.GearReply ?? (state.Gear + 1)).ToString(CultureInfo.InvariantCulture); break;
                case "S23": state.Power = (int)double.Parse(value, CultureInfo.InvariantCulture); state.Writes.Enqueue(command + value); reply = value; break;
                default: throw new Exception("Unexpected cockpit command: " + command);
            }
            var body = Encoding.ASCII.GetBytes(command + reply);
            var response = new byte[] { DaumProtocol.Ack, DaumProtocol.Soh }.Concat(body)
                .Concat(Encoding.ASCII.GetBytes(DaumProtocol.ComputeChecksum(body).ToString("00", CultureInfo.InvariantCulture))).Append(DaumProtocol.Etb).ToArray();
            await stream.WriteAsync(response, ct); await stream.ReadExactlyAsync(one, ct);
            Check(one[0] == DaumProtocol.Ack, "the client acknowledges each cockpit response");
        }
    }

    private static int? NativeGear(LoggerRuntime runtime)
    {
        using var sample = JsonSerializer.SerializeToDocument(runtime.SampleStatus());
        return sample.RootElement.ValueKind == JsonValueKind.Null || sample.RootElement.GetProperty("NativeGear").ValueKind == JsonValueKind.Null
            ? null : sample.RootElement.GetProperty("NativeGear").GetInt32();
    }
    private static int Cadence(LoggerRuntime runtime)
    {
        using var sample = JsonSerializer.SerializeToDocument(runtime.SampleStatus());
        return sample.RootElement.ValueKind == JsonValueKind.Null ? -1 : sample.RootElement.GetProperty("CadenceRpm").GetInt32();
    }
    private static async Task WaitUntilAsync(Func<bool> ready, CancellationToken ct) { while (!ready()) await Task.Delay(20, ct); }
    private static async Task RejectedAsync(Func<Task<int>> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Cockpit activation should have been rejected.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
