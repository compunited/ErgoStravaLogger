using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ErgoStravaLogger;

namespace ErgoStravaLogger.Tests;

public static class ProtocolTests
{
    public static async Task RunAsync()
    {
        TestCodecAndUnits();
        TestPowerCodecAndLimits();
        TestGearCodec();
        await TestFragmentationAndPersistentConnectionAsync();
        await TestPowerControlAndLimitsAsync();
        await TestPowerControlSerializesWithTelemetryAsync();
        await TestRejectedPowerResponseAsync();
        await TestGearControlAsync();
        await TestGearControlSerializesWithTelemetryAsync();
        await TestRejectedGearResponseAsync();
        await TestChecksumRetransmissionAsync();
        await TestNegativeAcknowledgementsAsync();
        await TestTimeoutAndReconnectAsync();
        await TestCancellationAsync();
        await TestConnectionSynchronizationAsync();
        await TestWrongEchoAsync();
        Console.WriteLine("Protocol tests passed.");
    }

    private static void TestCodecAndUnits()
    {
        Bytes(new byte[] { 1, 0x56, 0x30, 0x30, 0x38, 0x32, 0x17 }, DaumProtocol.EncodeQuery("V00"), "V00 request");
        Bytes(new byte[] { 1, 0x56, 0x37, 0x30, 0x38, 0x39, 0x17 }, DaumProtocol.EncodeQuery("V70"), "V70 request");
        Bytes(new byte[] { 1, 0x58, 0x37, 0x30, 0x39, 0x31, 0x17 }, DaumProtocol.EncodeQuery("X70"), "X70 request");
        Throws<ArgumentException>(() => DaumProtocol.EncodeQuery("S23"));
        byte[] version = new byte[] { 1, 0x56, 0x37, 0x30, 0x31, 0x2e, 0x33, 0x38, 0x30, 0x33, 0x39, 0x17 };
        Equal("1.380", DaumProtocol.ParseVersion(DaumProtocol.DecodeResponse(version, "V70")), "version");
        byte[] broken = (byte[])version.Clone();
        broken[^2] = (byte)'8';
        Throws<DaumChecksumException>(() => DaumProtocol.DecodeResponse(broken, "V70"));
        broken[^2] = 0x06;
        Throws<DaumChecksumException>(() => DaumProtocol.DecodeResponse(broken, "V70"));
        Throws<InvalidDataException>(() => DaumProtocol.DecodeResponse(version, "X70"));
        Throws<InvalidDataException>(() => DaumProtocol.DecodeResponse(new byte[] { 0x15 }, "V70"));

        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            byte[] payload = TrainingPayload();
            DeviceSample sample = DaumProtocol.ParseTraining(payload);
            Equal(212, sample.PowerWatts, "power");
            Equal(88, sample.CadenceRpm, "rounded cadence");
            Equal<int?>(150, sample.HeartRateBpm, "heart rate");
            Near(10.5, sample.SpeedMetersPerSecond, "DLL m/s default");
            Near(10.5 / 3.6, DaumProtocol.ParseTraining(payload, "km/h").SpeedMetersPerSecond, "document km/h override");
            Near(12345, sample.DistanceMeters, "distance in meters");
            Equal(TimeSpan.FromHours(1), sample.TrainingTime, "training time");
            Equal<int?>(17, sample.NativeGear, "numeric X70 gear is transmitted as gear plus one");
            Equal<int?>(22, DaumProtocol.ParseTraining(TrainingPayload(rawGear: DaumProtocol.Etb)).NativeGear, "raw ETB status byte is native gear 22");
            Equal<int?>(28, DaumProtocol.ParseTraining(TrainingPayload(rawGear: DaumProtocol.GroupSeparator)).NativeGear, "raw GS status byte is native gear 28");
            foreach (byte rawGear in new byte[] { 0, 1, 2, 29 })
                Equal<int?>(rawGear >= 2 ? rawGear - 1 : null,
                    DaumProtocol.ParseTraining(TrainingPayload(rawGear: rawGear)).NativeGear, "raw gear boundaries and disabled shifting");
            foreach (var gear in new[] { "0", "1", "2", "29", "30", "255" })
                Equal<int?>(gear == "2" ? 1 : gear == "29" ? 28 : null,
                    DaumProtocol.ParseTraining(TrainingPayload(gearStatus: gear)).NativeGear, "ASCII gear boundaries retain unsupported readings as null");
            Near(10.5, DaumProtocol.ParseTraining(TrainingPayload(trailingSeparator: false)).SpeedMetersPerSecond, "optional final GS");
            Equal<int?>(null, DaumProtocol.ParseTraining(TrainingPayload(pulse: "0")).HeartRateBpm, "zero pulse convention");
            Equal(212, DaumProtocol.ParseTraining(TrainingPayload(cadenceStatus: "0")).PowerWatts, "numeric cadence-status zero supported by deployed implementations");
            Near(10.5, DaumProtocol.ParseTraining(TrainingPayload(rawGear: DaumProtocol.Etb)).SpeedMetersPerSecond, "raw ETB gear");
            Near(10.5, DaumProtocol.ParseTraining(TrainingPayload(rawGear: DaumProtocol.GroupSeparator)).SpeedMetersPerSecond, "raw GS gear");
            Throws<InvalidDataException>(() => DaumProtocol.ParseTraining(TrainingPayload(speed: "10,5")));
            Throws<InvalidDataException>(() => DaumProtocol.ParseTraining(TrainingPayload(speed: "NaN")));
            Throws<InvalidDataException>(() => DaumProtocol.ParseTraining(TrainingPayload(speed: "-1")));
            Throws<InvalidDataException>(() => DaumProtocol.ParseTraining(Encoding.ASCII.GetBytes("60\u001d100")));
            Throws<ArgumentException>(() => DaumProtocol.ParseTraining(payload, "mph"));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    private static async Task TestFragmentationAndPersistentConnectionAsync()
    {
        await ExchangeAsync(async (stream, ct) =>
        {
            await ExpectQueryAsync(stream, "V70", ct);
            await SendFragmentedResponseAsync(stream, Frame("V70", Encoding.ASCII.GetBytes("1.380")), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
            foreach (byte gear in new byte[] { DaumProtocol.Etb, DaumProtocol.GroupSeparator })
            {
                await ExpectQueryAsync(stream, "X70", ct);
                await SendFragmentedResponseAsync(stream, Frame("X70", TrainingPayload(rawGear: gear)), ct);
                await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
            }
        }, async client =>
        {
            Equal("1.380", await client.ReadVersionAsync(), "fragmented version");
            Near(10.5, (await client.ReadTrainingAsync()).SpeedMetersPerSecond, "fragmented raw ETB sample");
            Equal(212, (await client.ReadTrainingAsync()).PowerWatts, "same-connection raw GS sample");
        });
    }

    private static void TestPowerCodecAndLimits()
    {
        Bytes(new byte[] { 1, 0x4c, 0x37, 0x30, 0x57, 0x36, 0x36, 0x17 },
            DaumProtocol.EncodePowerLimitsQuery(), "L70 W request has no separator");
        Bytes(new byte[] { 1, 0x53, 0x32, 0x33, 0x30, 0x31, 0x35, 0x30, 0x2e, 0x30, 0x30, 0x32, 0x34, 0x17 },
            DaumProtocol.EncodeSetPower(150), "manufacturer S23 150 W frame");
        Throws<ArgumentOutOfRangeException>(() => DaumProtocol.EncodeSetPower(-1));
        Throws<ArgumentOutOfRangeException>(() => DaumProtocol.EncodeSetPower(1001));
        Equal("0000.00", Encoding.ASCII.GetString(DaumProtocol.DecodeResponse(DaumProtocol.EncodeSetPower(0), "S23")), "codec zero boundary");
        Equal("1000.00", Encoding.ASCII.GetString(DaumProtocol.DecodeResponse(DaumProtocol.EncodeSetPower(1000), "S23")), "codec maximum boundary");

        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Near(149.5, DaumProtocol.ParsePower(Encoding.ASCII.GetBytes("149.50")), "applied watt response keeps decimals");
            Equal(new DaumPowerLimits(20, 1000, 50), DaumProtocol.ParsePowerLimits(Encoding.ASCII.GetBytes("W\u001d20.00\u001d1000.00\u001d50.00")), "active watt limits");
            Equal(new DaumPowerLimits(25, 300, 100), DaumProtocol.ParsePowerLimits(Encoding.ASCII.GetBytes("W\u001d25.00\u001d300.00\u001d100.00\u001d")), "optional trailing separator");
            foreach (string invalid in new[] { "", "150,00", "NaN", "Infinity", "-1", "1001", "150\n" })
                Throws<InvalidDataException>(() => DaumProtocol.ParsePower(Encoding.ASCII.GetBytes(invalid)));
            foreach (string invalid in new[] { "W", "E\u001d20\u001d300\u001d50", "W\u001d20\u001d300\u001d50\u001d0",
                "W\u001d300\u001d20\u001d50", "W\u001d20\u001d300\u001d400", "W\u001d-1\u001d300\u001d50",
                "W\u001d20\u001d1001\u001d50", "W\u001d20\u001dNaN\u001d50", "W\u001d20,0\u001d300\u001d50" })
                Throws<InvalidDataException>(() => DaumProtocol.ParsePowerLimits(Encoding.ASCII.GetBytes(invalid)));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    private static async Task TestPowerControlAndLimitsAsync()
    {
        await ExchangeAsync(async (stream, ct) =>
        {
            await ExpectFrameAsync(stream, new byte[] { 1, 0x4c, 0x37, 0x30, 0x57, 0x36, 0x36, 0x17 }, ct);
            await SendFragmentedResponseAsync(stream, Frame("L70", Encoding.ASCII.GetBytes("W\u001d20.00\u001d300.00\u001d50.00")), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);

            byte[] target = new byte[] { 1, 0x53, 0x32, 0x33, 0x30, 0x31, 0x35, 0x30, 0x2e, 0x30, 0x30, 0x32, 0x34, 0x17 };
            await ExpectFrameAsync(stream, target, ct);
            await stream.WriteAsync(new byte[] { DaumProtocol.Nak }, ct);
            await ExpectFrameAsync(stream, target, ct);
            await SendFragmentedResponseAsync(stream, Frame("S23", Encoding.ASCII.GetBytes("149.50")), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
            await ExpectQueryAsync(stream, "X70", ct);
            await SendFragmentedResponseAsync(stream, Frame("X70", TrainingPayload()), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
        }, async client =>
        {
            await ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetTargetPowerAsync(-1));
            Equal(new DaumPowerLimits(20, 300, 50), await client.ReadPowerLimitsAsync(), "fragmented limits response");
            Near(149.5, await client.SetTargetPowerAsync(150), "reported applied power after NAK retry");
            Equal(212, (await client.ReadTrainingAsync()).PowerWatts, "telemetry continues on shared connection");
        });
    }

    private static void TestGearCodec()
    {
        Bytes(new byte[] { 1, 0x4d, 0x37, 0x31, 0x32, 0x33, 0x31, 0x17 },
            DaumProtocol.EncodeSetGear(1), "M71 lowest native gear uses one ASCII digit, without padding");
        Bytes(new byte[] { 1, 0x4d, 0x37, 0x31, 0x31, 0x33, 0x38, 0x31, 0x17 },
            DaumProtocol.EncodeSetGear(12), "M71 native gear 12 frame and checksum");
        Bytes(new byte[] { 1, 0x4d, 0x37, 0x31, 0x32, 0x39, 0x38, 0x38, 0x17 },
            DaumProtocol.EncodeSetGear(28), "M71 highest native gear frame");
        foreach (int invalid in new[] { int.MinValue, -1, 0, 29, int.MaxValue })
            Throws<ArgumentOutOfRangeException>(() => DaumProtocol.EncodeSetGear(invalid));
        foreach (int gear in new[] { 1, 12, 28 })
            Equal(gear, DaumProtocol.ParseGear(DaumProtocol.DecodeResponse(DaumProtocol.EncodeSetGear(gear), "M71")),
                "M71 encode/decode normalizes the gear-plus-one wire value to the native gear");
        foreach (int encodedGear in new[] { 2, 13, 29 })
        {
            int nativeGear = DaumProtocol.ParseGear(Encoding.ASCII.GetBytes(encodedGear.ToString(CultureInfo.InvariantCulture)));
            Equal(encodedGear - 1, nativeGear, "M71 active wire boundaries map to native gears 1..28");
            Equal<int?>(nativeGear, DaumProtocol.ParseTraining(TrainingPayload(gearStatus: encodedGear.ToString(CultureInfo.InvariantCulture))).NativeGear,
                "identical M71/X70 wire values represent the same native gear");
        }
        foreach (string invalid in new[] { "", "0", "1", "30", "-1", "+1", "1.0", "1,0", " 2", "2 ", "2\n", "NaN", "2147483648" })
            Throws<InvalidDataException>(() => DaumProtocol.ParseGear(Encoding.ASCII.GetBytes(invalid)));
        Throws<InvalidDataException>(() => DaumProtocol.ParseGear(new byte[] { 12 }));
        Throws<InvalidDataException>(() => DaumProtocol.DecodeResponse(DaumProtocol.EncodeSetGear(12), "S23"));
    }

    private static async Task TestGearControlAsync()
    {
        await ExchangeAsync(async (stream, ct) =>
        {
            await ExpectQueryAsync(stream, "V70", ct);
            await SendFragmentedResponseAsync(stream, Frame("V70", Encoding.ASCII.GetBytes("2.025")), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
            byte[] request = new byte[] { 1, 0x4d, 0x37, 0x31, 0x31, 0x33, 0x38, 0x31, 0x17 };
            await ExpectFrameAsync(stream, request, ct);
            await stream.WriteAsync(new byte[] { DaumProtocol.Nak }, ct);
            await ExpectFrameAsync(stream, request, ct);
            await SendFragmentedResponseAsync(stream, Frame("M71", Encoding.ASCII.GetBytes("12")), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
            await ExpectQueryAsync(stream, "X70", ct);
            await SendFragmentedResponseAsync(stream, Frame("X70", TrainingPayload(gearStatus: "12")), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
            await ExpectFrameAsync(stream, DaumProtocol.EncodeSetGear(28), ct);
            await SendFragmentedResponseAsync(stream, Frame("M71", Encoding.ASCII.GetBytes("29")), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
            await ExpectQueryAsync(stream, "X70", ct);
            await SendFragmentedResponseAsync(stream, Frame("X70", TrainingPayload(gearStatus: "29")), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
        }, async client =>
        {
            await ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetGearAsync(0));
            await ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetGearAsync(29));
            Equal("2.025", await client.ReadVersionAsync(), "existing connection before the gear command");
            Equal(11, await client.SetGearAsync(12), "M71 returns the actual echoed gear after NAK, rather than assuming the request succeeded");
            Equal<int?>(11, (await client.ReadTrainingAsync()).NativeGear, "fragmented telemetry resumes on the same connection after M71");
            Equal(28, await client.SetGearAsync(28), "M71 wire 29 confirms native gear 28");
            Equal<int?>(28, (await client.ReadTrainingAsync()).NativeGear, "M71 and X70 apply the same offset to native gear 28");
        });
    }

    private static async Task TestGearControlSerializesWithTelemetryAsync()
    {
        TaskCompletionSource receivedGear = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource queuedTelemetry = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await ExchangeAsync(async (stream, ct) =>
        {
            await ExpectFrameAsync(stream, DaumProtocol.EncodeSetGear(12), ct);
            receivedGear.SetResult();
            await queuedTelemetry.Task.WaitAsync(ct);
            await Task.Delay(30, ct);
            Check(!stream.DataAvailable, "telemetry waits until the gear transaction completes");
            await SendFragmentedResponseAsync(stream, Frame("M71", Encoding.ASCII.GetBytes("13")), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
            await ExpectQueryAsync(stream, "X70", ct);
            await SendFragmentedResponseAsync(stream, Frame("X70", TrainingPayload(gearStatus: "13")), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
            await ExpectFrameAsync(stream, DaumProtocol.EncodeSetPower(20), ct);
            await SendFragmentedResponseAsync(stream, Frame("S23", Encoding.ASCII.GetBytes("20.00")), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
        }, async client =>
        {
            Task<int> gear = client.SetGearAsync(12);
            await receivedGear.Task;
            Task<DeviceSample> telemetry = client.ReadTrainingAsync();
            queuedTelemetry.SetResult();
            Equal(12, await gear, "serialized native gear target");
            Equal<int?>(12, (await telemetry).NativeGear, "serialized native gear telemetry");
            Near(20, await client.SetTargetPowerAsync(20), "watt restoration uses the same connection after gear activation");
        });
    }

    private static async Task TestRejectedGearResponseAsync()
    {
        foreach (string invalid in new[] { "0", "1", "30", "NaN" })
            await ExchangeAsync(async (stream, ct) =>
            {
                await ExpectFrameAsync(stream, DaumProtocol.EncodeSetGear(12), ct);
                await SendFragmentedResponseAsync(stream, Frame("M71", Encoding.ASCII.GetBytes(invalid)), ct);
                await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
            }, client => ThrowsAsync<InvalidDataException>(() => client.SetGearAsync(12)));
    }

    private static async Task TestPowerControlSerializesWithTelemetryAsync()
    {
        TaskCompletionSource receivedTarget = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource queuedTelemetry = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await ExchangeAsync(async (stream, ct) =>
        {
            await ExpectFrameAsync(stream, DaumProtocol.EncodeSetPower(100), ct);
            receivedTarget.SetResult();
            await queuedTelemetry.Task.WaitAsync(ct);
            await Task.Delay(30, ct);
            Check(!stream.DataAvailable, "telemetry waits until the power transaction completes");
            await SendFragmentedResponseAsync(stream, Frame("S23", Encoding.ASCII.GetBytes("100.00")), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
            await ExpectQueryAsync(stream, "X70", ct);
            await SendFragmentedResponseAsync(stream, Frame("X70", TrainingPayload()), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
        }, async client =>
        {
            Task<double> power = client.SetTargetPowerAsync(100);
            await receivedTarget.Task;
            Task<DeviceSample> telemetry = client.ReadTrainingAsync();
            queuedTelemetry.SetResult();
            Near(100, await power, "serialized target power");
            Equal(212, (await telemetry).PowerWatts, "serialized telemetry");
        });
    }

    private static async Task TestRejectedPowerResponseAsync()
    {
        await ExchangeAsync(async (stream, ct) =>
        {
            await ExpectFrameAsync(stream, DaumProtocol.EncodeSetPower(100), ct);
            await SendFragmentedResponseAsync(stream, Frame("S23", Encoding.ASCII.GetBytes("NaN")), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
        }, client => ThrowsAsync<InvalidDataException>(() => client.SetTargetPowerAsync(100)));
    }

    private static async Task TestChecksumRetransmissionAsync()
    {
        await ExchangeAsync(async (stream, ct) =>
        {
            await ExpectQueryAsync(stream, "X70", ct);
            byte[] good = Frame("X70", TrainingPayload());
            byte[] bad = (byte[])good.Clone();
            bad[^2] = bad[^2] == '9' ? (byte)'0' : (byte)(bad[^2] + 1);
            await stream.WriteAsync(new byte[] { DaumProtocol.Ack }, ct);
            await stream.WriteAsync(bad, ct);
            await ExpectByteAsync(stream, DaumProtocol.Nak, ct);
            await stream.WriteAsync(good, ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
        }, async client => Equal(212, (await client.ReadTrainingAsync()).PowerWatts, "checksum retransmission"));
    }

    private static async Task TestNegativeAcknowledgementsAsync()
    {
        await ExchangeAsync(async (stream, ct) =>
        {
            for (int i = 0; i < 5; i++)
            {
                await ExpectQueryAsync(stream, "V70", ct);
                await stream.WriteAsync(new byte[] { DaumProtocol.Nak }, ct);
            }
            Equal(0, await stream.ReadAsync(new byte[1], ct), "connection closes after NAK exhaustion");
        }, client => ThrowsAsync<DaumNegativeAcknowledgementException>(() => client.ReadVersionAsync()));
    }

    private static async Task TestTimeoutAndReconnectAsync()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(8));
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task server = Task.Run(async () =>
            {
                using (TcpClient first = await listener.AcceptTcpClientAsync(deadline.Token))
                {
                    NetworkStream stream = first.GetStream();
                    await ExpectQueryAsync(stream, "V70", deadline.Token);
                    Equal(0, await stream.ReadAsync(new byte[1], deadline.Token), "timeout closes connection");
                }
                using TcpClient second = await listener.AcceptTcpClientAsync(deadline.Token);
                NetworkStream secondStream = second.GetStream();
                await ExpectQueryAsync(secondStream, "V70", deadline.Token);
                await SendFragmentedResponseAsync(secondStream, Frame("V70", Encoding.ASCII.GetBytes("1.380")), deadline.Token);
                await ExpectByteAsync(secondStream, DaumProtocol.Ack, deadline.Token);
            }, deadline.Token);
            await using DaumClient client = new(Config(port, timeoutMs: 250));
            Stopwatch clock = Stopwatch.StartNew();
            await ThrowsAsync<TimeoutException>(() => client.ReadVersionAsync());
            Check(clock.Elapsed < TimeSpan.FromSeconds(3), "IO timeout is bounded");
            Equal("1.380", await client.ReadVersionAsync(), "reconnect after timeout");
            await server.WaitAsync(deadline.Token);
        }
        finally { listener.Stop(); }
    }

    private static async Task TestCancellationAsync()
    {
        await ExchangeAsync(async (stream, ct) =>
        {
            await ExpectQueryAsync(stream, "V70", ct);
            Equal(0, await stream.ReadAsync(new byte[1], ct), "cancel closes partial exchange");
        }, async client =>
        {
            using CancellationTokenSource cancellation = new(150);
            await ThrowsAsync<OperationCanceledException>(() => client.ReadVersionAsync(cancellation.Token));
        });
    }

    private static async Task TestConnectionSynchronizationAsync()
    {
        await ExchangeAsync(async (stream, ct) =>
        {
            for (int i = 0; i < 4; i++) await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
            await ExpectQueryAsync(stream, "V70", ct);
            await SendFragmentedResponseAsync(stream, Frame("V70", Encoding.ASCII.GetBytes("1.380")), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
            await ExpectQueryAsync(stream, "V70", ct); // No repeated connection synchronization.
            await SendFragmentedResponseAsync(stream, Frame("V70", Encoding.ASCII.GetBytes("1.380")), ct);
            await ExpectByteAsync(stream, DaumProtocol.Ack, ct);
        }, async client =>
        {
            Equal("1.380", await client.ReadVersionAsync(), "synchronized version");
            Equal("1.380", await client.ReadVersionAsync(), "second query without sync");
        }, synchronize: true);
    }

    private static async Task TestWrongEchoAsync()
    {
        await ExchangeAsync(async (stream, ct) =>
        {
            await ExpectQueryAsync(stream, "V70", ct);
            await SendFragmentedResponseAsync(stream, Frame("V00", Encoding.ASCII.GetBytes("1")), ct);
            Equal(0, await stream.ReadAsync(new byte[1], ct), "wrong echo closes connection without ACK");
        }, client => ThrowsAsync<InvalidDataException>(() => client.ReadVersionAsync()));
    }

    private static async Task ExchangeAsync(Func<NetworkStream, CancellationToken, Task> device,
        Func<DaumClient, Task> useClient, bool synchronize = false)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(8));
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task server = Task.Run(async () =>
            {
                using TcpClient accepted = await listener.AcceptTcpClientAsync(deadline.Token);
                await device(accepted.GetStream(), deadline.Token);
            }, deadline.Token);
            await using DaumClient client = new(Config(port, synchronize: synchronize));
            try { await useClient(client).WaitAsync(deadline.Token); }
            finally { client.Disconnect(); }
            await server.WaitAsync(deadline.Token);
        }
        finally { listener.Stop(); }
    }

    private static LoggerConfig Config(int port, int timeoutMs = 3000, bool synchronize = false) => new()
    {
        ErgoIp = "127.0.0.1", ErgoPort = port, TimeoutMs = timeoutMs,
        TraceProtocol = false, SynchronizeOnConnect = synchronize, SpeedUnit = "m/s"
    };

    private static async Task ExpectQueryAsync(NetworkStream stream, string command, CancellationToken ct)
    {
        byte[] received = new byte[7];
        await stream.ReadExactlyAsync(received, ct);
        Bytes(DaumProtocol.EncodeQuery(command), received, $"device received {command}");
    }

    private static async Task ExpectFrameAsync(NetworkStream stream, byte[] expected, CancellationToken ct)
    {
        byte[] received = new byte[expected.Length];
        await stream.ReadExactlyAsync(received, ct);
        Bytes(expected, received, "device received power control frame");
    }

    private static async Task ExpectByteAsync(NetworkStream stream, byte expected, CancellationToken ct)
    {
        byte[] received = new byte[1];
        await stream.ReadExactlyAsync(received, ct);
        Equal(expected, received[0], "protocol control byte");
    }

    private static async Task SendFragmentedResponseAsync(NetworkStream stream, byte[] frame, CancellationToken ct)
    {
        // ACK and the frame prefix share a write; the remaining frame is split across writes.
        await stream.WriteAsync(new byte[] { DaumProtocol.Ack, frame[0], frame[1] }, ct);
        for (int i = 2; i < frame.Length; i++)
        {
            await Task.Delay(1, ct);
            await stream.WriteAsync(frame.AsMemory(i, 1), ct);
        }
    }

    private static byte[] TrainingPayload(string pulse = "150", string speed = "10.5",
        bool trailingSeparator = true, byte? rawGear = null, string cadenceStatus = "2", string gearStatus = "18")
    {
        string prefix = string.Join('\x1d', new[] { "3600", pulse, speed, "2.5", "12345", "87.6", "212", "734.1", "800.2", "12.4" }) + '\x1d';
        byte[] first = Encoding.ASCII.GetBytes(prefix);
        byte[] statuses = rawGear.HasValue
            ? new byte[] { rawGear.Value, 0x1d, 1, 0x1d, 2 }
            : Encoding.ASCII.GetBytes(gearStatus + "\u001d1\u001d" + cadenceStatus + (trailingSeparator ? "\u001d" : ""));
        return first.Concat(statuses).ToArray();
    }

    private static byte[] Frame(string command, byte[] payload)
    {
        byte[] data = Encoding.ASCII.GetBytes(command).Concat(payload).ToArray();
        int checksum = data.Sum(value => (int)value) % 100;
        return new byte[] { 1 }.Concat(data)
            .Concat(Encoding.ASCII.GetBytes(checksum.ToString("00", CultureInfo.InvariantCulture)))
            .Append((byte)0x17).ToArray();
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception($"Assertion failed: {message}");
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}, got {actual}");

    private static void Near(double expected, double actual, string message) =>
        Check(Math.Abs(expected - actual) < 1e-9, $"{message}: expected {expected}, got {actual}");

    private static void Bytes(byte[] expected, byte[] actual, string message) =>
        Check(expected.SequenceEqual(actual), $"{message}: expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}");

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
