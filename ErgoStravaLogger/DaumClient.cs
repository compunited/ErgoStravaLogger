using System.Net.Sockets;

namespace ErgoStravaLogger;

/// <summary>A persistent TCP connection for Daum premium telemetry and explicit watt or gear commands.</summary>
public sealed class DaumClient : IAsyncDisposable
{
    private const int MaximumAttempts = 5;
    private readonly LoggerConfig _config;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly byte[] _receiveBuffer = new byte[DaumProtocol.MaximumFrameLength];
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _received;
    private int _next;
    private bool _disposed;

    public DaumClient(LoggerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.ErgoIp)) throw new ArgumentException("An ergometer host is required.", nameof(config));
        if (config.ErgoPort is < 1 or > 65535) throw new ArgumentException("The TCP port must be between 1 and 65535.", nameof(config));
        if (config.TimeoutMs <= 0) throw new ArgumentException("The timeout must be positive.", nameof(config));
        if (config.SpeedUnit is not ("m/s" or "km/h")) throw new ArgumentException("The speed unit must be m/s or km/h.", nameof(config));
        _config = config;
    }

    public async Task<DeviceSample> ReadTrainingAsync(CancellationToken ct = default) =>
        DaumProtocol.ParseTraining(await QueryAsync("X70", DaumProtocol.EncodeQuery("X70"), ct).ConfigureAwait(false), _config.SpeedUnit);

    public async Task<string> ReadVersionAsync(CancellationToken ct = default) =>
        DaumProtocol.ParseVersion(await QueryAsync("V70", DaumProtocol.EncodeQuery("V70"), ct).ConfigureAwait(false));

    public async Task<DaumPowerLimits> ReadPowerLimitsAsync(CancellationToken ct = default) =>
        DaumProtocol.ParsePowerLimits(await QueryAsync("L70", DaumProtocol.EncodePowerLimitsQuery(), ct).ConfigureAwait(false));

    public async Task<double> SetTargetPowerAsync(int watts, CancellationToken ct = default) =>
        DaumProtocol.ParsePower(await QueryAsync("S23", DaumProtocol.EncodeSetPower(watts), ct).ConfigureAwait(false));

    public async Task<int> SetGearAsync(int gear, CancellationToken ct = default) =>
        DaumProtocol.ParseGear(await QueryAsync("M71", DaumProtocol.EncodeSetGear(gear), ct).ConfigureAwait(false));

    private async Task<byte[]> QueryAsync(string command, byte[] query, CancellationToken ct)
    {
        await _requests.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_config.TimeoutMs);
            try
            {
                await EnsureConnectedAsync(deadline.Token).ConfigureAwait(false);
                for (int queryAttempt = 0; queryAttempt < MaximumAttempts; queryAttempt++)
                {
                    await SendAsync(query, deadline.Token).ConfigureAwait(false);
                    byte acknowledgement = await ReadByteAsync(deadline.Token).ConfigureAwait(false);
                    if (acknowledgement == DaumProtocol.Nak)
                    {
                        if (queryAttempt + 1 == MaximumAttempts)
                            throw new DaumNegativeAcknowledgementException($"The ergometer rejected {command} five times.");
                        continue;
                    }
                    if (acknowledgement != DaumProtocol.Ack)
                        throw new InvalidDataException($"Expected Daum ACK, received 0x{acknowledgement:X2}.");

                    for (int responseAttempt = 0; responseAttempt < MaximumAttempts; responseAttempt++)
                    {
                        byte[] frame = await ReadFrameAsync(deadline.Token).ConfigureAwait(false);
                        byte[] payload;
                        try { payload = DaumProtocol.DecodeResponse(frame, command); }
                        catch (DaumChecksumException)
                        {
                            await SendAsync(new byte[] { DaumProtocol.Nak }, deadline.Token).ConfigureAwait(false);
                            if (responseAttempt + 1 == MaximumAttempts) throw;
                            continue;
                        }
                        await SendAsync(new byte[] { DaumProtocol.Ack }, deadline.Token).ConfigureAwait(false);
                        return payload;
                    }
                }
                throw new IOException("The Daum query could not be completed.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Disconnect();
                throw new TimeoutException($"The Daum {command} query exceeded {_config.TimeoutMs} ms.");
            }
            catch
            {
                Disconnect();
                throw;
            }
        }
        finally { _requests.Release(); }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is not null) return;
        TcpClient client = new() { NoDelay = true };
        try
        {
            await client.ConnectAsync(_config.ErgoIp, _config.ErgoPort, ct).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _client = client;
            _stream = client.GetStream();
            _received = _next = 0;
            if (_config.SynchronizeOnConnect)
            {
                // hpc_premium.cdl's connection/recovery routine sends four ACKs 30 ms apart.
                // This synchronization happens once per new connection, before the first query.
                for (int i = 0; i < 4; i++)
                {
                    await SendAsync(new byte[] { DaumProtocol.Ack }, ct).ConfigureAwait(false);
                    await Task.Delay(30, ct).ConfigureAwait(false);
                }
            }
        }
        catch { client.Dispose(); throw; }
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        List<byte> frame = new(DaumProtocol.MaximumFrameLength);
        byte first = await ReadByteAsync(ct).ConfigureAwait(false);
        if (first == DaumProtocol.Nak)
            throw new DaumNegativeAcknowledgementException("The ergometer sent NAK instead of a response frame.");
        if (first != DaumProtocol.Soh)
            throw new InvalidDataException($"Expected Daum SOH, received 0x{first:X2}.");
        frame.Add(first);
        while (frame.Count < DaumProtocol.MaximumFrameLength)
        {
            byte value = await ReadByteAsync(ct).ConfigureAwait(false);
            frame.Add(value);
            if (value == DaumProtocol.Etb && !IsRawGearByte(frame))
            {
                byte[] result = frame.ToArray();
                Trace("RX frame", result);
                return result;
            }
        }
        throw new InvalidDataException("The Daum response exceeds the maximum frame length.");
    }

    private static bool IsRawGearByte(List<byte> frame)
    {
        // The published X70 format has a raw gear byte, which may itself equal ETB.
        // Numeric ASCII statuses in the installed manufacturer's DLL use the same framing.
        if (frame.Count < 4 || frame[1] != 'X' || frame[2] != '7' || frame[3] != '0') return false;
        int separators = 0;
        for (int i = 4; i < frame.Count - 1; i++)
            if (frame[i] == DaumProtocol.GroupSeparator && ++separators == 10)
                return i == frame.Count - 2;
        return false;
    }

    private async Task<byte> ReadByteAsync(CancellationToken ct)
    {
        if (_next == _received)
        {
            _received = await _stream!.ReadAsync(_receiveBuffer, ct).ConfigureAwait(false);
            _next = 0;
            if (_received == 0) throw new EndOfStreamException("The ergometer closed the TCP connection.");
            Trace("RX bytes", _receiveBuffer.AsSpan(0, _received));
        }
        return _receiveBuffer[_next++];
    }

    private async Task SendAsync(byte[] bytes, CancellationToken ct)
    {
        Trace("TX", bytes);
        await _stream!.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private void Trace(string label, ReadOnlySpan<byte> bytes)
    {
        if (_config.TraceProtocol) Console.Error.WriteLine($"Daum {label}: {Convert.ToHexString(bytes)}");
    }

    public void Disconnect()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        _received = _next = 0;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        Disconnect();
        await _requests.WaitAsync().ConfigureAwait(false);
        try { Disconnect(); }
        finally { _requests.Release(); }
    }
}
