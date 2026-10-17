using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ErgoStravaLogger;

namespace ErgoStravaLogger.Tests;

public static class DirConTests
{
    public static async Task RunAsync()
    {
        TestFrameVectors();
        TestMeasurements();
        await TestDiscoveryAndNotificationsAsync();
        await TestShutdownAndRestartAsync();
        await TestOversizedFrameAsync();
        await TestStartFailureAsync();
        await TestFtmsControlPointAsync();
        await TestReleaseDuringBlockedProcedureAsync();
        await TestTrainerControlTransportAsync();
    }

    private static void TestFrameVectors()
    {
        byte[] uuid = Convert.FromHexString("0000182600001000800000805F9B34FB");
        Bytes(uuid, DirConProtocol.UuidBytes(DirConProtocol.FitnessMachineService), "128-bit UUID uses network order");
        Equal(DirConProtocol.FitnessMachineService, DirConProtocol.ReadUuid(uuid), "UUID network-order decode");
        Bytes([1, 1, 7, 0, 0, 0], DirConProtocol.Encode(1, 7, 0, []), "discovery request header");
        byte[] longData = new byte[300];
        var encoded = DirConProtocol.Encode(3, 255, 4, longData);
        Bytes([1, 3, 255, 4, 1, 44], encoded.AsSpan(0, 6).ToArray(), "payload length is big endian");
        var decoded = DirConProtocol.Decode(encoded);
        Equal((byte)255, decoded.Sequence, "sequence preserved");
        Equal((byte)4, decoded.ResponseCode, "status preserved");
        Bytes(longData, decoded.Data, "payload preserved");
        Throws<InvalidDataException>(() => DirConProtocol.Decode([1, 1, 0, 0, 0, 1]));
        Throws<InvalidDataException>(() => DirConProtocol.Decode([1, 1, 0, 0, 0]));
        Throws<InvalidDataException>(() => DirConProtocol.Encode(1, 0, 0, new byte[1025]));
        Throws<InvalidDataException>(() => DirConProtocol.ReadUuid(new byte[15]));

        var cycling = DirConProtocol.Characteristics(DirConProtocol.CyclingPowerService)!;
        Equal(3, cycling.Count, "CPS includes mandatory sensor location");
        Equal(DirConProtocol.Notify, cycling.Single(c => c.Uuid == DirConProtocol.CyclingPowerMeasurement).Properties, "CPS measurement notify only");
        Equal(DirConProtocol.Read, cycling.Single(c => c.Uuid == DirConProtocol.CyclingPowerFeature).Properties, "CPS feature readable");
        foreach (var service in DirConProtocol.ServiceUuids)
            foreach (var characteristic in DirConProtocol.Characteristics(service)!)
                Equal(0, characteristic.Properties & DirConProtocol.Write, "no trainer control advertised");
        Equal<DirConCharacteristic?>(null, DirConProtocol.Characteristic(DirConProtocol.BluetoothUuid(0x2ad9)), "FTMS control point excluded");
    }

    private static void TestMeasurements()
    {
        var now = DateTimeOffset.UtcNow;
        var sample = new DeviceSample(250, 90, 150, 10, 123, TimeSpan.FromSeconds(10));
        var encoder = new DirConMeasurementEncoder();
        var first = encoder.Encode(new(sample, now, true), now, 0);
        Bytes([0x20, 0, 0xfa, 0, 0, 0, 0, 0], first.CyclingPower, "CPS flags and signed power little endian");
        Bytes([0x44, 2, 0x10, 0x0e, 0xb4, 0, 0xfa, 0, 150], first.IndoorBikeData, "FTMS 36 km/h, 90 RPM, 250 W and HR");
        Bytes([0, 150], first.HeartRate!, "HR no contact, energy or RR fields fabricated");

        var second = encoder.Encode(new(sample, now.AddSeconds(2), true), now.AddSeconds(2), 2);
        Bytes([0x20, 0, 0xfa, 0, 3, 0, 0, 8], second.CyclingPower, "90 RPM reconstructs three revolutions in two seconds");
        var idle = encoder.Encode(new(sample with { CadenceRpm = 0 }, now.AddSeconds(3), true), now.AddSeconds(3), 3);
        Bytes([0x20, 0, 0, 0, 3, 0, 0, 8], idle.CyclingPower, "configured idle target watts must not propel Zwift");
        Bytes([0x44, 2, 0, 0, 0, 0, 0, 0, 150], idle.IndoorBikeData, "idle sends zero speed, cadence, power but valid pulse");
        var disconnected = encoder.Encode(new(sample, now.AddSeconds(4), false), now.AddSeconds(4), 4);
        Bytes([0x44, 0, 0, 0, 0, 0, 0, 0], disconnected.IndoorBikeData, "disconnected clears all live measurement fields");
        Equal<byte[]?>(null, disconnected.HeartRate, "disconnected heartbeat is omitted");
        Equal<ushort>(3, BinaryPrimitives.ReadUInt16LittleEndian(disconnected.CyclingPower.AsSpan(4)), "disconnected does not advance cadence counters");
        var stale = encoder.Encode(new(sample, now, true), now.AddSeconds(10), 10);
        Bytes([0x44, 0, 0, 0, 0, 0, 0, 0], stale.IndoorBikeData, "stale measurements are cleared");
        var missing = encoder.Encode(new(null, null, true), now, 11);
        Equal<byte[]?>(null, missing.HeartRate, "missing pulse omitted");
        Bytes([0x44, 0, 0, 0, 0, 0, 0, 0], missing.IndoorBikeData, "missing sample encodes zero movement");
        var invalidPulse = encoder.Encode(new(sample with { HeartRateBpm = 300 }, now, true), now, 12);
        Equal(8, invalidPulse.IndoorBikeData.Length, "out-of-range pulse omitted from FTMS");
        Equal<byte[]?>(null, invalidPulse.HeartRate, "out-of-range pulse omitted from HR");
        var rollover = new DirConMeasurementEncoder();
        rollover.Encode(new(sample, now, true), now, 63);
        var afterRollover = rollover.Encode(new(sample, now.AddSeconds(2), true), now.AddSeconds(2), 65);
        Equal<ushort>(1024, BinaryPrimitives.ReadUInt16LittleEndian(afterRollover.CyclingPower.AsSpan(6)), "crank event time wraps at 64 seconds");
        Equal<ushort>(3, BinaryPrimitives.ReadUInt16LittleEndian(afterRollover.CyclingPower.AsSpan(4)), "event rollover preserves revolution delta");
    }

    private static async Task TestDiscoveryAndNotificationsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        BridgeTelemetry telemetry = new(new DeviceSample(212, 88, 150, 10.5, 0, TimeSpan.Zero), DateTimeOffset.UtcNow, true);
        await using var server = new DirConServer(Options(), () => Volatile.Read(ref telemetry));
        await server.StartAsync(deadline.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.BoundPort, deadline.Token);
        var stream = client.GetStream();
        byte[] discovery = DirConProtocol.Encode(1, 42, 0, []);
        // A TCP read boundary is neither a header boundary nor a frame boundary.
        await stream.WriteAsync(discovery.AsMemory(0, 2), deadline.Token);
        await stream.WriteAsync(discovery.AsMemory(2, 3), deadline.Token);
        await stream.WriteAsync(discovery.AsMemory(5), deadline.Token);
        var response = await ReceiveAsync(stream, deadline.Token);
        Equal((byte)42, response.Sequence, "fragmented discovery echoes sequence");
        Bytes(server.ServiceUuids.SelectMany(DirConProtocol.UuidBytes).ToArray(), response.Data, "all services discovered in network order");
        Equal(1, server.ClientCount, "connected client counted");

        var cpService = DirConProtocol.UuidBytes(DirConProtocol.CyclingPowerService);
        var cpFeature = DirConProtocol.UuidBytes(DirConProtocol.CyclingPowerFeature);
        var ftmsFeature = DirConProtocol.UuidBytes(DirConProtocol.FitnessMachineFeature);
        byte[] combined = DirConProtocol.Encode(2, 43, 0, cpService)
            .Concat(DirConProtocol.Encode(3, 44, 0, cpFeature))
            .Concat(DirConProtocol.Encode(3, 45, 0, ftmsFeature)).ToArray();
        await stream.WriteAsync(combined, deadline.Token);
        response = await ReceiveAsync(stream, deadline.Token);
        Equal((byte)43, response.Sequence, "coalesced discover characteristics response");
        Bytes(cpService.Concat(DirConProtocol.Characteristics(DirConProtocol.CyclingPowerService)!
            .SelectMany(c => DirConProtocol.UuidBytes(c.Uuid).Append(c.Properties))).ToArray(), response.Data, "characteristic properties use DirCon bit values");
        response = await ReceiveAsync(stream, deadline.Token);
        Bytes(cpFeature.Concat(new byte[] { 8, 0, 0, 0 }).ToArray(), response.Data, "CPS feature declares crank data only");
        response = await ReceiveAsync(stream, deadline.Token);
        Bytes(ftmsFeature.Concat(new byte[] { 2, 0x44, 0, 0, 0, 0, 0, 0 }).ToArray(), response.Data, "FTMS feature declares cadence, HR, power and zero controllable targets");

        await CheckResponseAsync(stream, 2, 46, DirConProtocol.UuidBytes(DirConProtocol.BluetoothUuid(0xeeee)), 3, deadline.Token);
        await CheckResponseAsync(stream, 3, 47, DirConProtocol.UuidBytes(DirConProtocol.BluetoothUuid(0xffff)), 4, deadline.Token);
        await CheckResponseAsync(stream, 3, 48, DirConProtocol.UuidBytes(DirConProtocol.CyclingPowerMeasurement), 5, deadline.Token);
        await CheckResponseAsync(stream, 4, 49, cpFeature.Concat(new byte[] { 1 }).ToArray(), 5, deadline.Token);
        await CheckResponseAsync(stream, 5, 50, cpFeature.Concat(new byte[] { 1 }).ToArray(), 5, deadline.Token);
        await CheckResponseAsync(stream, 5, 51, DirConProtocol.UuidBytes(DirConProtocol.IndoorBikeData).Concat(new byte[] { 2 }).ToArray(), 2, deadline.Token);
        await CheckResponseAsync(stream, 99, 52, [], 1, deadline.Token);
        await CheckResponseAsync(stream, 3, 53, new byte[15], 2, deadline.Token);

        await SubscribeAsync(stream, DirConProtocol.IndoorBikeData, 54, true, deadline.Token);
        var notification = await NotificationAsync(stream, DirConProtocol.IndoorBikeData, deadline.Token);
        Bytes([0x44, 2, 0xc4, 0x0e, 0xb0, 0, 0xd4, 0, 150], notification.Data.AsSpan(16).ToArray(), "actual Daum metrics reach TCP client");
        telemetry = telemetry with { Connected = false };
        notification = await NotificationAsync(stream, DirConProtocol.IndoorBikeData, deadline.Token);
        Bytes([0x44, 0, 0, 0, 0, 0, 0, 0], notification.Data.AsSpan(16).ToArray(), "bike connection loss reaches subscribed client as zero data");
        telemetry = telemetry with { Connected = true, Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1) };
        notification = await NotificationAsync(stream, DirConProtocol.IndoorBikeData, deadline.Token);
        Bytes([0x44, 0, 0, 0, 0, 0, 0, 0], notification.Data.AsSpan(16).ToArray(), "stale sample reaches TCP client as zero data");
        telemetry = new(null, null, false);
        notification = await NotificationAsync(stream, DirConProtocol.IndoorBikeData, deadline.Token);
        Bytes([0x44, 0, 0, 0, 0, 0, 0, 0], notification.Data.AsSpan(16).ToArray(), "missing sample reaches TCP client as zero data");
        telemetry = new(new DeviceSample(250, 0, null, 10, 0, TimeSpan.Zero), DateTimeOffset.UtcNow, true);
        notification = await NotificationAsync(stream, DirConProtocol.IndoorBikeData, deadline.Token);
        Bytes([0x44, 0, 0, 0, 0, 0, 0, 0], notification.Data.AsSpan(16).ToArray(), "configured watts with idle pedals cannot move Zwift");
        await SubscribeAsync(stream, DirConProtocol.IndoorBikeData, 55, false, deadline.Token);
        // The next request after unsubscribe must receive its response without an interleaved notification.
        await CheckResponseAsync(stream, 1, 56, [], 0, deadline.Token);
        await server.StopAsync(deadline.Token);
        Equal(0, server.ClientCount, "shutdown clears clients");
    }

    private static async Task TestShutdownAndRestartAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reserved = new TcpListener(IPAddress.Loopback, 0);
        reserved.Start();
        int port = ((IPEndPoint)reserved.LocalEndpoint).Port;
        reserved.Stop();
        var options = Options(port);
        await using var server = new DirConServer(options, () => new(null, null, false));
        await server.StartAsync(deadline.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        await CheckResponseAsync(client.GetStream(), 1, 1, [], 0, deadline.Token);
        // Even a peer stalled halfway through a header cannot prevent the switch from shutting down.
        await client.GetStream().WriteAsync(new byte[] { 1, 2 }, deadline.Token);
        await server.StopAsync(deadline.Token);
        Equal(false, server.IsRunning, "disabled bridge is stopped");
        Equal(0, server.BoundPort, "disabled bridge reports no bound port");
        await AssertPeerClosedAsync(client.GetStream(), deadline.Token, "switch off closes peer socket");
        var replacement = new TcpListener(IPAddress.Loopback, port);
        replacement.Start();
        replacement.Stop();
        await server.StartAsync(deadline.Token);
        Equal(port, server.BoundPort, "switch on reuses port");
        using var reconnected = new TcpClient();
        await reconnected.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
        await CheckResponseAsync(reconnected.GetStream(), 1, 2, [], 0, deadline.Token);
    }

    private static async Task TestOversizedFrameAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new DirConServer(Options(), () => new(null, null, false));
        await server.StartAsync(deadline.Token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.BoundPort, deadline.Token);
        await client.GetStream().WriteAsync(new byte[] { 1, 4, 7, 0, 0xff, 0xff }, deadline.Token);
        var response = await ReceiveAsync(client.GetStream(), deadline.Token);
        Equal((byte)2, response.ResponseCode, "oversized frames rejected before payload allocation");
        Equal((byte)7, response.Sequence, "oversized error echoes sequence");
        await AssertPeerClosedAsync(client.GetStream(), deadline.Token, "oversized frame closes client");
        using var healthy = new TcpClient();
        await healthy.ConnectAsync(IPAddress.Loopback, server.BoundPort, deadline.Token);
        await CheckResponseAsync(healthy.GetStream(), 1, 8, [], 0, deadline.Token);
    }

    private static async Task TestStartFailureAsync()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.ExclusiveAddressUse = true;
        occupied.Start();
        int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await using var server = new DirConServer(Options(port), () => new(null, null, false));
        try
        {
            await server.StartAsync();
            throw new Exception("An occupied port must fail startup.");
        }
        catch (SocketException)
        {
            Equal(false, server.IsRunning, "startup failure does not claim running");
            Equal(0, server.BoundPort, "startup failure releases endpoint");
            if (string.IsNullOrWhiteSpace(server.LastError)) throw new Exception("Startup failure must provide a diagnostic.");
        }
        finally { occupied.Stop(); }
        await server.StartAsync();
        Equal(true, server.IsRunning, "startup can recover after occupied port released");
        Equal<string?>(null, server.LastError, "successful recovery clears stale startup error");
    }

    private static async Task TestFtmsControlPointAsync()
    {
        var trainer = new FakeTrainerControl();
        var control = new FtmsControlPoint(trainer);
        Equal(FtmsControlPoint.InvalidParameter, await control.ProcessAsync(1, ReadOnlyMemory<byte>.Empty), "empty control command rejected");
        Equal(FtmsControlPoint.InvalidParameter, await control.ProcessAsync(1, new byte[] { 0, 1 }), "request control has no parameters");
        Equal(FtmsControlPoint.NotSupported, await control.ProcessAsync(1, new byte[] { 0x12 }), "unsupported procedure reported honestly");
        Equal(FtmsControlPoint.ControlNotPermitted, await control.ProcessAsync(1, new byte[] { 5, 100, 0 }), "targets require control lease");
        trainer.Available = false;
        Equal(FtmsControlPoint.OperationFailed, await control.ProcessAsync(1, new byte[] { 0 }), "offline trainer cannot grant control");
        trainer.Available = true;
        Equal(FtmsControlPoint.Success, await control.ProcessAsync(1, new byte[] { 0 }), "first client obtains control");
        Equal(FtmsControlPoint.ControlNotPermitted, await control.ProcessAsync(2, new byte[] { 0 }), "control is exclusive across sockets");
        Equal(FtmsControlPoint.ControlNotPermitted, await control.ProcessAsync(2, new byte[] { 5, 100, 0 }), "other socket cannot write targets");
        Equal(FtmsControlPoint.InvalidParameter, await control.ProcessAsync(1, new byte[] { 5, 0xff, 0xff }), "negative watts rejected");
        Equal(FtmsControlPoint.InvalidParameter, await control.ProcessAsync(1, new byte[] { 5, 0x2d, 1 }), "301 watts exceeds 300 watt cap");
        Equal(FtmsControlPoint.InvalidParameter, await control.ProcessAsync(1, new byte[] { 5, 100 }), "truncated watt field rejected");
        Equal(FtmsControlPoint.Success, await control.ProcessAsync(1, new byte[] { 5, 0x2c, 1 }), "upper power limit accepted");
        Equal(300, trainer.LastPower, "power decoder uses little endian");
        Equal(FtmsControlPoint.Success, await control.ProcessAsync(1, new byte[] { 5, 0xc5, 0 }), "intermediate targets reach hardware rounding layer");
        Equal(197, trainer.LastPower, "power target is not silently changed by FTMS layer");
        Equal(FtmsControlPoint.InvalidParameter, await control.ProcessAsync(1, new byte[] { 8, 0 }), "stop parameter must be stop or pause");
        Equal(FtmsControlPoint.Success, await control.ProcessAsync(1, new byte[] { 8, 2 }), "pause reduces load");
        Equal(FtmsControlPoint.Success, await control.ProcessAsync(1, new byte[] { 7 }), "resume retains control permission");
        trainer.Fail = true;
        Equal(FtmsControlPoint.OperationFailed, await control.ProcessAsync(1, new byte[] { 5, 100, 0 }), "physical failure must not report success");
        trainer.Throw = true;
        Equal(FtmsControlPoint.OperationFailed, await control.ProcessAsync(1, new byte[] { 5, 100, 0 }), "physical exception becomes FTMS failure");
        trainer.Fail = trainer.Throw = false;
        trainer.Available = false;
        Equal(FtmsControlPoint.OperationFailed, await control.ProcessAsync(1, new byte[] { 5, 100, 0 }), "lost connection blocks target writes");
        trainer.Available = true;
        Equal(FtmsControlPoint.Success, await control.ProcessAsync(1, new byte[] { 1 }), "reset virtual control and stop without resetting recording counters");
        Equal(FtmsControlPoint.ControlNotPermitted, await control.ProcessAsync(1, new byte[] { 5, 100, 0 }), "reset releases permission");
        Equal(FtmsControlPoint.Success, await control.ProcessAsync(2, new byte[] { 0 }), "second client can take released control");
        int stops = trainer.Stops;
        await control.ReleaseAsync(1);
        Equal(stops, trainer.Stops, "nonowner disconnect cannot stop owner's workout");
        await control.ReleaseAsync(2);
        Equal(stops + 1, trainer.Stops, "owner disconnect stops hardware once");
        await control.ReleaseAsync(2);
        Equal(stops + 1, trainer.Stops, "repeated release is harmless");
        trainer.Enabled = false;
        Equal(FtmsControlPoint.ControlNotPermitted, await control.ProcessAsync(1, new byte[] { 0 }), "disabled control cannot be granted");
    }

    private static async Task TestTrainerControlTransportAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var trainer = new FakeTrainerControl();
        BridgeTelemetry telemetry = new(new DeviceSample(100, 90, 140, 8, 0, TimeSpan.Zero), DateTimeOffset.UtcNow, true);
        await using var server = new DirConServer(Options(), () => telemetry, trainer);
        await server.StartAsync(deadline.Token);
        using var first = new TcpClient();
        using var second = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, server.BoundPort, deadline.Token);
        await second.ConnectAsync(IPAddress.Loopback, server.BoundPort, deadline.Token);
        var a = first.GetStream();
        var b = second.GetStream();
        byte[] cpUuid = DirConProtocol.UuidBytes(DirConProtocol.FitnessMachineControlPoint);
        byte[] ftmsUuid = DirConProtocol.UuidBytes(DirConProtocol.FitnessMachineService);
        await a.WriteAsync(DirConProtocol.Encode(2, 1, 0, ftmsUuid), deadline.Token);
        var response = await ResponseAsync(a, 1, deadline.Token);
        Bytes(ftmsUuid.Concat(DirConProtocol.Characteristics(DirConProtocol.FitnessMachineService, true)!
            .SelectMany(c => DirConProtocol.UuidBytes(c.Uuid).Append(c.Properties))).ToArray(), response.Data, "control characteristics discovered only when enabled");
        Equal((byte)6, DirConProtocol.Characteristic(DirConProtocol.FitnessMachineControlPoint, true)!.Properties,
            "BLE indication maps to reliable Direct Connect notify property");
        await a.WriteAsync(DirConProtocol.Encode(3, 2, 0, DirConProtocol.UuidBytes(DirConProtocol.FitnessMachineFeature)), deadline.Token);
        response = await ResponseAsync(a, 2, deadline.Token);
        Bytes([2, 0x44, 0, 0, 8, 0x20, 0, 0], response.Data.AsSpan(16).ToArray(), "power and simulation features declared");
        await a.WriteAsync(DirConProtocol.Encode(3, 3, 0, DirConProtocol.UuidBytes(DirConProtocol.SupportedPowerRange)), deadline.Token);
        response = await ResponseAsync(a, 3, deadline.Token);
        Bytes([25, 0, 0x2c, 1, 5, 0], response.Data.AsSpan(16).ToArray(), "readable range matches hardware bounds and step");
        await CheckResponseAsync(a, 4, 4, cpUuid.Append((byte)0).ToArray(), DirConProtocol.WriteFailed, deadline.Token);
        await SubscribeAsync(a, DirConProtocol.FitnessMachineControlPoint, 5, true, deadline.Token);
        await SubscribeAsync(a, DirConProtocol.FitnessMachineStatus, 6, true, deadline.Token);
        await SubscribeAsync(b, DirConProtocol.FitnessMachineControlPoint, 7, true, deadline.Token);
        await SubscribeAsync(b, DirConProtocol.FitnessMachineStatus, 8, true, deadline.Token);
        // A valid pulse must never turn into periodic control-point/status data.
        await Task.Delay(1100, deadline.Token);
        await ControlWriteAsync(a, 9, [0], FtmsControlPoint.Success, deadline.Token);
        await ControlWriteAsync(b, 10, [0], FtmsControlPoint.ControlNotPermitted, deadline.Token);
        await ControlWriteAsync(a, 11, [5, 0xc5, 0], FtmsControlPoint.Success, deadline.Token);
        var status = await NotificationAsync(a, DirConProtocol.FitnessMachineStatus, deadline.Token);
        Bytes([8, 0xc5, 0], status.Data.AsSpan(16).ToArray(), "power status emitted after successful command");
        status = await NotificationAsync(b, DirConProtocol.FitnessMachineStatus, deadline.Token);
        Bytes([8, 0xc5, 0], status.Data.AsSpan(16).ToArray(), "status reaches observers without granting control");
        await ControlWriteAsync(a, 12, [0x11, 0x18, 0xfc, 0x06, 0xff, 40, 51], FtmsControlPoint.Success, deadline.Token);
        Equal(new FtmsSimulationParameters(-1, -2.5, 0.004, 0.51), trainer.LastSimulation,
            "signed wind/grade and coefficient resolutions decoded accurately");
        await NotificationAsync(a, DirConProtocol.FitnessMachineStatus, deadline.Token);
        await NotificationAsync(b, DirConProtocol.FitnessMachineStatus, deadline.Token);
        await ControlWriteAsync(a, 13, [0x11, 0], FtmsControlPoint.InvalidParameter, deadline.Token);
        await ControlWriteAsync(a, 14, [0xfe], FtmsControlPoint.NotSupported, deadline.Token);
        trainer.Fail = true;
        await ControlWriteAsync(a, 15, [5, 100, 0], FtmsControlPoint.OperationFailed, deadline.Token);
        trainer.Fail = false;
        await SubscribeAsync(a, DirConProtocol.FitnessMachineControlPoint, 16, false, deadline.Token);
        // This follows unsubscribe on the same socket, proving release finished before its next request.
        await CheckResponseAsync(a, 1, 17, [], 0, deadline.Token);
        Equal(1, trainer.Stops, "control-point unsubscribe releases lease and stops hardware");
        await ControlWriteAsync(b, 18, [0], FtmsControlPoint.Success, deadline.Token);
        await server.StopAsync(deadline.Token);
        Equal(2, trainer.Stops, "server shutdown releases controller and reduces load");
    }

    private static async Task TestReleaseDuringBlockedProcedureAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var trainer = new FakeTrainerControl
        {
            SimulationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            SimulationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously),
            StopStarted = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var control = new FtmsControlPoint(trainer);
        Equal(FtmsControlPoint.Success, await control.ProcessAsync(1, new byte[] { 0 }, deadline.Token), "blocked-procedure owner obtains lease");
        var procedure = control.ProcessAsync(1, new byte[] { 0x11, 0, 0, 100, 0, 40, 30 }, deadline.Token);
        await trainer.SimulationStarted.Task.WaitAsync(deadline.Token);
        var release = control.ReleaseAsync(1);
        // Stop must be signalled while the command still occupies the FTMS procedure gate.
        using var promptDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        promptDeadline.CancelAfter(TimeSpan.FromSeconds(1));
        await trainer.StopStarted.Task.WaitAsync(promptDeadline.Token);
        Equal(false, procedure.IsCompleted, "release signals stop before blocked hardware procedure completes");
        await release.WaitAsync(promptDeadline.Token);
        Equal(1, trainer.Stops, "release immediately calls engine stop exactly once");
        trainer.SimulationCompletion.SetResult(true);
        Equal(FtmsControlPoint.ControlNotPermitted, await procedure, "late confirmation cannot revive revoked lease");
        Equal(FtmsControlPoint.Success, await control.ProcessAsync(2, new byte[] { 0 }, deadline.Token), "new owner acquires lease after blocked command finishes");
        Equal(FtmsControlPoint.ControlNotPermitted, await control.ProcessAsync(1, new byte[] { 5, 100, 0 }, deadline.Token), "disconnected owner stays revoked");
    }

    private static async Task ControlWriteAsync(NetworkStream stream, byte sequence, byte[] command, byte result, CancellationToken ct)
    {
        byte[] uuid = DirConProtocol.UuidBytes(DirConProtocol.FitnessMachineControlPoint);
        await stream.WriteAsync(DirConProtocol.Encode(4, sequence, 0, uuid.Concat(command).ToArray()), ct);
        var acknowledgement = await ReceiveAsync(stream, ct);
        Equal((byte)4, acknowledgement.MessageType, "transport acknowledgement precedes FTMS indication");
        Equal(sequence, acknowledgement.Sequence, "write acknowledgement preserves sequence");
        Equal((byte)0, acknowledgement.ResponseCode, "transport succeeds even when FTMS procedure rejects command");
        Bytes(uuid, acknowledgement.Data, "write acknowledgement UUID only");
        var indication = await ReceiveAsync(stream, ct);
        Equal((byte)6, indication.MessageType, "control result uses Direct Connect notification");
        Bytes(uuid.Concat(new byte[] { 0x80, command[0], result }).ToArray(), indication.Data, "FTMS result includes request opcode and honest outcome");
    }

    private sealed class FakeTrainerControl : ITrainerControl
    {
        public bool Enabled { get; set; } = true;
        public bool Available { get; set; } = true;
        public int MinimumPowerWatts => 25;
        public int MaximumPowerWatts => 300;
        public int PowerStepWatts => 5;
        public bool Fail { get; set; }
        public bool Throw { get; set; }
        public int LastPower { get; private set; }
        public FtmsSimulationParameters? LastSimulation { get; private set; }
        public int Stops { get; private set; }
        public TaskCompletionSource<bool>? SimulationStarted { get; init; }
        public TaskCompletionSource<bool>? SimulationCompletion { get; init; }
        public TaskCompletionSource<bool>? StopStarted { get; init; }
        public Task<bool> SetTargetPowerAsync(int watts, CancellationToken ct)
        {
            if (Throw) throw new IOException("synthetic Daum failure");
            LastPower = watts;
            return Task.FromResult(!Fail);
        }
        public Task<bool> SetSimulationAsync(FtmsSimulationParameters parameters, CancellationToken ct)
        {
            LastSimulation = parameters;
            SimulationStarted?.TrySetResult(true);
            return SimulationCompletion?.Task ?? Task.FromResult(!Fail);
        }
        public Task<bool> StopAsync(CancellationToken ct)
        {
            Stops++;
            StopStarted?.TrySetResult(true);
            return Task.FromResult(!Fail);
        }
    }

    private static ZwiftBridgeOptions Options(int port = 0) => new()
    {
        BindAddress = "127.0.0.1", Port = port, Identity = "020000000001", DeviceName = "Test Daum 8i", StaleAfterSeconds = 5
    };

    private static async Task CheckResponseAsync(NetworkStream stream, byte type, byte sequence, byte[] data, byte status, CancellationToken ct)
    {
        await stream.WriteAsync(DirConProtocol.Encode(type, sequence, 0, data), ct);
        var response = await ResponseAsync(stream, sequence, ct);
        Equal(type, response.MessageType, "response echoes message type");
        Equal(status, response.ResponseCode, "expected operation status");
    }

    private static async Task SubscribeAsync(NetworkStream stream, Guid uuid, byte sequence, bool enable, CancellationToken ct)
    {
        await stream.WriteAsync(DirConProtocol.Encode(5, sequence, 0, DirConProtocol.UuidBytes(uuid).Append(enable ? (byte)1 : (byte)0).ToArray()), ct);
        var response = await ResponseAsync(stream, sequence, ct);
        Equal((byte)5, response.MessageType, "subscribe acknowledgement type");
        Equal((byte)0, response.ResponseCode, "subscribe acknowledgement success");
        Bytes(DirConProtocol.UuidBytes(uuid), response.Data, "subscribe acknowledgement contains UUID only");
    }

    private static async Task<DirConFrame> ResponseAsync(NetworkStream stream, byte sequence, CancellationToken ct)
    {
        while (true)
        {
            var frame = await ReceiveAsync(stream, ct);
            if (frame.MessageType == 6) continue;
            Equal(sequence, frame.Sequence, "response echoes request sequence");
            return frame;
        }
    }

    private static async Task<DirConFrame> NotificationAsync(NetworkStream stream, Guid uuid, CancellationToken ct)
    {
        while (true)
        {
            var frame = await ReceiveAsync(stream, ct);
            if (frame.MessageType != 6) throw new Exception("Expected unsolicited measurement notification.");
            Equal((byte)0, frame.ResponseCode, "notification status is zero");
            if (DirConProtocol.ReadUuid(frame.Data.AsSpan(0, 16)) == uuid) return frame;
        }
    }

    private static async Task<DirConFrame> ReceiveAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] header = new byte[6];
        await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        byte[] frame = new byte[6 + length];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(6), ct);
        return DirConProtocol.Decode(frame);
    }

    private static async Task AssertPeerClosedAsync(NetworkStream stream, CancellationToken ct, string message)
    {
        try { Equal(0, await stream.ReadAsync(new byte[1], ct), message); }
        // Closing with unread bytes can produce a TCP reset on Windows instead of a FIN/EOF.
        catch (IOException ex) when (ex.InnerException is SocketException
            { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted }) { }
    }

    private static void Bytes(byte[] expected, byte[] actual, string message)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new Exception($"{message}: expected {Convert.ToHexString(expected)}, actual {Convert.ToHexString(actual)}");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: expected {expected}, actual {actual}");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
