using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ErgoStravaLogger;

namespace ErgoStravaLogger.Tests;

public static class DeviceSettingsTests
{
    public static async Task RunAsync()
    {
        TestIpValidation();
        await TestSavedAddressOverridesEnvironmentAsync();
        await TestSettingsStoreContainsOnlyAddressAsync();
        await TestFailedSaveLeavesRuntimeAddressAsync();
        await TestDeviceChangeFinishesSessionAndResetsStatusAsync();
    }

    private static void TestIpValidation()
    {
        foreach (var address in new[] { "192.168.0.50", "10.0.0.1", "127.0.0.1" })
            Equal(address, DeviceSettingsStore.NormalizeIp(address), "IPv4 address remains canonical");
        Equal("192.168.0.50", DeviceSettingsStore.NormalizeIp(" 192.168.0.50 "), "outer whitespace is trimmed");
        foreach (var address in new[]
        {
            "", " ", "localhost", "ergo.local", "::1", "::ffff:192.168.0.50", "192.168.0.256", "192.168.0.-1",
            "127.1", "2130706433", "0x7f.0.0.1", "192.168.0.50:51955", "http://192.168.0.50", "192.168. 0.50", "192.168.0.50/24"
        })
            Throws<ArgumentException>(() => DeviceSettingsStore.NormalizeIp(address));
    }

    private static async Task TestSavedAddressOverridesEnvironmentAsync()
    {
        using var directory = new TemporaryDirectory();
        using var environment = new TemporaryEnvironment(new Dictionary<string, string?>
        {
            ["ERGO_IP"] = "192.168.0.80", ["OUTPUT_DIRECTORY"] = null, ["LISTEN_URL"] = null, ["PUBLIC_BASE_URL"] = null,
            ["STRAVA_CLIENT_ID"] = null, ["STRAVA_CLIENT_SECRET"] = null, ["STRAVA_REFRESH_TOKEN"] = null,
            ["STRAVA_ACCESS_TOKEN"] = null, ["STRAVA_ENABLED"] = null
        });
        var configPath = Path.Combine(directory.Path, "config.json");
        var output = Path.Combine(directory.Path, "data");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new LoggerConfig
        {
            ErgoIp = "192.168.0.50", OutputDirectory = "data", Strava = new() { TokenFile = "tokens.json" }
        }));
        Equal("192.168.0.80", LoggerConfig.Load(configPath).ErgoIp, "environment supplies initial device address");
        await DeviceSettingsStore.SaveAsync(output, "192.168.0.90");
        var restored = LoggerConfig.Load(configPath);
        Equal("192.168.0.90", restored.ErgoIp, "web settings survive reload and override the initial environment address");
        Equal(output, restored.OutputDirectory, "saved settings use the resolved output directory");
    }

    private static async Task TestSettingsStoreContainsOnlyAddressAsync()
    {
        using var directory = new TemporaryDirectory();
        await DeviceSettingsStore.SaveAsync(directory.Path, "192.168.0.60");
        await DeviceSettingsStore.SaveAsync(directory.Path, "192.168.0.61");
        var files = Directory.GetFiles(directory.Path);
        Equal(1, files.Length, "replacing settings leaves no temporary files");
        Equal("device-settings.json", Path.GetFileName(files[0]), "settings have a dedicated file");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(files[0]));
        var properties = json.RootElement.EnumerateObject().ToArray();
        Equal(1, properties.Length, "device settings must not serialize general configuration or secrets");
        Equal("ip", properties[0].Name.ToLowerInvariant(), "only the device IP is persisted");
        Equal("192.168.0.61", properties[0].Value.GetString(), "last successful save replaces the address");
    }

    private static async Task TestFailedSaveLeavesRuntimeAddressAsync()
    {
        using var directory = new TemporaryDirectory();
        var blockedOutput = Path.Combine(directory.Path, "not-a-directory");
        await File.WriteAllTextAsync(blockedOutput, "fixture");
        var config = Config(blockedOutput);
        using var strava = new StravaClient(config.Strava);
        var runtime = new LoggerRuntime(config, strava);
        await ThrowsAsync<IOException>(() => runtime.UpdateDeviceAddressAsync("192.168.0.90"));
        Equal("127.0.0.1", config.ErgoIp, "failed persistence does not change the live configuration");
        using var status = JsonSerializer.SerializeToDocument(runtime.DeviceStatus());
        Equal("127.0.0.1", status.RootElement.GetProperty("host").GetString(), "failed persistence keeps the reported device address");
        await ThrowsAsync<ArgumentException>(() => runtime.UpdateDeviceAddressAsync("ergo.local"));
        Equal("fixture", await File.ReadAllTextAsync(blockedOutput), "failed and invalid saves preserve existing files");
    }

    private static async Task TestDeviceChangeFinishesSessionAndResetsStatusAsync()
    {
        using var directory = new TemporaryDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var oldListener = new TcpListener(IPAddress.Loopback, 0);
        oldListener.Start();
        var port = ((IPEndPoint)oldListener.LocalEndpoint).Port;
        var newListener = new TcpListener(IPAddress.Parse("127.0.0.2"), port);
        newListener.Start();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldServer = ServeOldDeviceAsync(oldListener, disconnected, deadline.Token);
        var newServer = WaitForNewDeviceAsync(newListener, reconnected, deadline.Token);
        using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var config = Config(directory.Path);
        config.ErgoPort = port;
        config.LoggingIntervalMs = 200;
        config.ReconnectDelaySeconds = 1;
        config.TimeoutMs = 5000;
        config.SynchronizeOnConnect = false;
        config.MinimumTrainingSeconds = 0;
        using var strava = new StravaClient(config.Strava);
        var runtime = new LoggerRuntime(config, strava);
        var running = runtime.RunAsync(runtimeCancellation.Token);
        try
        {
            await WaitUntilAsync(() => JournalSampleCount(directory.Path) >= 2, deadline.Token);
            await runtime.DiagnoseAsync(deadline.Token);
            using (var diagnosed = JsonSerializer.SerializeToDocument(runtime.DeviceStatus()))
                Equal("1.380", diagnosed.RootElement.GetProperty("softwareVersion").GetString(), "fixture provides an identifiable device version");

            await runtime.UpdateDeviceAddressAsync("127.0.0.1", deadline.Token);
            Check(RecordingActive(runtime), "saving the same address keeps the current workout active");
            Equal(0, Directory.GetFiles(directory.Path, "*.tcx").Length, "saving the same address must not export the workout");

            await runtime.UpdateDeviceAddressAsync("127.0.0.2", deadline.Token);
            await disconnected.Task.WaitAsync(deadline.Token);
            await reconnected.Task.WaitAsync(deadline.Token);
            Equal(1, Directory.GetFiles(directory.Path, "*.tcx").Length, "changing the device exports the old workout");
            Check(!RecordingActive(runtime), "a workout from the old device must not continue on the new address");
            Check(runtime.SampleStatus() is null, "samples from the previous device are cleared");
            using var status = JsonSerializer.SerializeToDocument(runtime.DeviceStatus());
            Equal("127.0.0.2", status.RootElement.GetProperty("host").GetString(), "live status reports the new device address");
            Check(!status.RootElement.GetProperty("connected").GetBoolean(), "new device stays disconnected until a sample is received");
            foreach (var property in new[] { "lastSeen", "softwareVersion", "error" })
                Equal(JsonValueKind.Null, status.RootElement.GetProperty(property).ValueKind, "changing device clears " + property);
        }
        finally
        {
            runtimeCancellation.Cancel();
            deadline.Cancel();
            oldListener.Stop();
            newListener.Stop();
            await running;
            await IgnoreExpectedCancellationAsync(oldServer);
            await IgnoreExpectedCancellationAsync(newServer);
        }
    }

    private static async Task ServeOldDeviceAsync(TcpListener listener, TaskCompletionSource disconnected, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        var stream = client.GetStream();
        var samples = 0;
        try
        {
            while (true)
            {
                var firstByte = new byte[1];
                if (await stream.ReadAsync(firstByte, ct) == 0)
                {
                    disconnected.TrySetResult();
                    return;
                }
                var request = new byte[7];
                request[0] = firstByte[0];
                await stream.ReadExactlyAsync(request.AsMemory(1), ct);
                var command = Encoding.ASCII.GetString(request, 1, 3);
                Check(command is "X70" or "V70", "fixture only accepts read commands");
                var payload = command == "V70" ? "1.380" : string.Join('\x1d', new[]
                {
                    "10", "140", "7.5", "2.5", (++samples * 10).ToString(CultureInfo.InvariantCulture),
                    "80", "150", "10", "10", "10", "18", "1", "2", ""
                });
                var body = Encoding.ASCII.GetBytes(command + payload);
                var checksum = body.Sum(value => (int)value) % 100;
                var frame = new byte[] { DaumProtocol.Ack, 1 }.Concat(body)
                    .Concat(Encoding.ASCII.GetBytes(checksum.ToString("00", CultureInfo.InvariantCulture)))
                    .Append(DaumProtocol.Etb).ToArray();
                await stream.WriteAsync(frame, ct);
                var ack = new byte[1];
                await stream.ReadExactlyAsync(ack, ct);
                Equal(DaumProtocol.Ack, ack[0], "client acknowledges the fake device response");
            }
        }
        catch (IOException) when (!ct.IsCancellationRequested)
        {
            disconnected.TrySetResult();
        }
    }

    private static async Task WaitForNewDeviceAsync(TcpListener listener, TaskCompletionSource reconnected, CancellationToken ct)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        var query = new byte[7];
        await client.GetStream().ReadExactlyAsync(query, ct);
        reconnected.TrySetResult();
        // Hold the response so the test can inspect the state before the first sample from the new device.
        await Task.Delay(Timeout.Infinite, ct);
    }

    private static LoggerConfig Config(string output) => new()
    {
        ErgoIp = "127.0.0.1", OutputDirectory = output,
        Strava = new() { Enabled = false, TokenFile = Path.Combine(output, "fixture-tokens.json") }
    };

    private static int JournalSampleCount(string output)
    {
        var file = Directory.GetFiles(output, "*.jsonl").SingleOrDefault();
        if (file is null) return 0;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var count = 0;
        while (reader.ReadLine() is not null) count++;
        return count;
    }

    private static bool RecordingActive(LoggerRuntime runtime)
    {
        using var status = JsonSerializer.SerializeToDocument(runtime.RecordingStatus());
        return status.RootElement.GetProperty("active").GetBoolean();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(25, ct);
    }

    private static async Task IgnoreExpectedCancellationAsync(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException) { }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("Assertion failed: " + message);
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ".");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ".");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ergostrava-device-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class TemporaryEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> original;
        public TemporaryEnvironment(Dictionary<string, string?> replacements)
        {
            original = replacements.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            foreach (var pair in replacements) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        public void Dispose()
        {
            foreach (var pair in original) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
