using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ErgoStravaLogger;

/// <summary>BLE-over-TCP endpoint for network fitness clients. Lifecycle is owned by ZwiftBridge.</summary>
public sealed class DirConServer(ZwiftBridgeOptions options, Func<BridgeTelemetry> readTelemetry,
    ITrainerControl? trainerControl = null) : IAsyncDisposable
{
    private const int MaximumClients = 8;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<long, ClientSession> _clients = new();
    private readonly FtmsControlPoint? _controlPoint = trainerControl is null ? null : new(trainerControl);
    private CancellationTokenSource? _cancellation;
    private TcpListener? _listener;
    private Task _acceptTask = Task.CompletedTask;
    private Task _notificationTask = Task.CompletedTask;
    private long _nextClientId;
    private volatile bool _running;
    private volatile int _boundPort;
    private volatile string? _lastError;
    private bool _disposed;

    public bool IsRunning => _running;
    public int ClientCount => _clients.Count;
    public int BoundPort => _boundPort;
    public string? LastError => _lastError;
    public IReadOnlyList<Guid> ServiceUuids => DirConProtocol.ServiceUuids;
    private bool ControlEnabled => trainerControl?.Enabled == true;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) return;
            await StopCoreAsync();
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!IPAddress.TryParse(options.BindAddress, out var address))
                    throw new ArgumentException("Zwift bridge BindAddress must be an IP address.");
                if (options.Port is < 0 or > 65535) throw new ArgumentException("Zwift bridge port is invalid.");
                _listener = new TcpListener(address, options.Port);
                _listener.ExclusiveAddressUse = true;
                _listener.Start(MaximumClients);
                _boundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _cancellation = new CancellationTokenSource();
                _lastError = null;
                _running = true;
                _acceptTask = AcceptClientsAsync(_listener, _cancellation.Token);
                _notificationTask = NotifyClientsAsync(_cancellation.Token);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                await StopCoreAsync();
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        _running = false;
        _cancellation?.Cancel();
        _listener?.Stop();
        foreach (var client in _clients.Values) client.Close();
        await Task.WhenAll(_acceptTask, _notificationTask);
        // AcceptClientsAsync is finished, so no new client or completion task can appear now.
        var sessions = _clients.Values.ToArray();
        foreach (var client in sessions) client.Close();
        await Task.WhenAll(sessions.Select(client => client.Completion));
        _clients.Clear();
        _cancellation?.Dispose();
        _cancellation = null;
        _listener = null;
        _boundPort = 0;
        _acceptTask = _notificationTask = Task.CompletedTask;
    }

    private async Task AcceptClientsAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient socket = await listener.AcceptTcpClientAsync(ct);
                if (ct.IsCancellationRequested || _clients.Count >= MaximumClients)
                {
                    socket.Dispose();
                    continue;
                }
                socket.NoDelay = true;
                socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                long id = Interlocked.Increment(ref _nextClientId);
                var client = new ClientSession(this, id, socket, ct, () => _clients.TryRemove(id, out _));
                _clients[id] = client;
                client.Start();
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _running = false;
            _cancellation?.Cancel();
            listener.Stop();
            foreach (var client in _clients.Values) client.Close();
        }
    }

    private async Task NotifyClientsAsync(CancellationToken ct)
    {
        var encoder = new DirConMeasurementEncoder(options.StaleAfterSeconds);
        var clock = Stopwatch.StartNew();
        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await ticker.WaitForNextTickAsync(ct))
            {
                BridgeTelemetry telemetry;
                try { telemetry = readTelemetry(); }
                catch (Exception ex)
                {
                    _lastError = $"Ergometer telemetry unavailable: {ex.Message}";
                    telemetry = new BridgeTelemetry(null, null, false);
                }
                var measurements = encoder.Encode(telemetry, DateTimeOffset.UtcNow, clock.Elapsed.TotalSeconds);
                await Task.WhenAll(_clients.Values.Select(client => client.NotifyAsync(measurements)));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _running = false;
            _cancellation?.Cancel();
            _listener?.Stop();
            foreach (var client in _clients.Values) client.Close();
        }
    }

    private byte[] ReadValue(Guid uuid)
    {
        if (uuid == DirConProtocol.CyclingPowerFeature) return [8, 0, 0, 0]; // Crank revolutions supported.
        if (uuid == DirConProtocol.FitnessMachineFeature)
            return ControlEnabled ? [2, 0x44, 0, 0, 8, 0x20, 0, 0] : [2, 0x44, 0, 0, 0, 0, 0, 0];
        if (uuid == DirConProtocol.SupportedPowerRange && ControlEnabled)
        {
            byte[] range = new byte[6];
            BinaryPrimitives.WriteInt16LittleEndian(range, checked((short)trainerControl!.MinimumPowerWatts));
            BinaryPrimitives.WriteInt16LittleEndian(range.AsSpan(2), checked((short)trainerControl.MaximumPowerWatts));
            BinaryPrimitives.WriteUInt16LittleEndian(range.AsSpan(4), checked((ushort)trainerControl.PowerStepWatts));
            return range;
        }
        if (uuid == DirConProtocol.SensorLocation) return [0]; // Other.
        if (uuid == DirConProtocol.Appearance) return [0x80, 0x04]; // Generic cycling (1152).
        if (uuid == DirConProtocol.DeviceName) return Encoding.UTF8.GetBytes(options.DeviceName);
        if (uuid == DirConProtocol.ManufacturerName) return Encoding.UTF8.GetBytes("ErgoStravaLogger");
        if (uuid == DirConProtocol.ModelNumber) return Encoding.UTF8.GetBytes("Daum premium 8i bridge");
        if (uuid == DirConProtocol.SerialNumber) return Encoding.UTF8.GetBytes("ERGO" + options.Identity);
        if (uuid == DirConProtocol.SoftwareRevision) return "1.0"u8.ToArray();
        throw new InvalidOperationException("No readable value exists for this characteristic.");
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopCoreAsync();
        }
        finally { _lifecycle.Release(); }
    }

    private sealed class ClientSession(DirConServer owner, long id, TcpClient socket, CancellationToken serverCancellation, Action onClosed)
    {
        private readonly NetworkStream _stream = socket.GetStream();
        private readonly CancellationTokenSource _cancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
        private readonly SemaphoreSlim _writer = new(1, 1);
        private readonly HashSet<Guid> _subscriptions = [];
        private int _subscriptionCount;
        private byte _notificationSequence;
        public Task Completion { get; private set; } = Task.CompletedTask;
        public void Start() => Completion = RunAsync();
        public void Close()
        {
            // The CTS remains alive until RunAsync finishes; concurrent shutdown/notification calls are harmless.
            try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
            socket.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var frame = await ReadFrameAsync(_cancellation.Token);
                    if (frame is null) break;
                    await HandleAsync(frame, _cancellation.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { }
            catch (Exception ex) { owner._lastError = $"Zwift client: {ex.Message}"; }
            finally
            {
                Close();
                if (owner._controlPoint is not null) await owner._controlPoint.ReleaseAsync(id);
                onClosed();
                _cancellation.Dispose();
                // Do not dispose _writer: a notification already holding a reference may still be finishing.
            }
        }

        private async Task<DirConFrame?> ReadFrameAsync(CancellationToken ct)
        {
            byte[] header = new byte[DirConProtocol.HeaderLength];
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // A subscribed fitness client can legitimately send no requests for an entire ride.
            if (Volatile.Read(ref _subscriptionCount) == 0) idle.CancelAfter(TimeSpan.FromSeconds(60));
            if (await _stream.ReadAsync(header.AsMemory(0, 1), idle.Token) == 0) return null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await _stream.ReadExactlyAsync(header.AsMemory(1), deadline.Token);
            int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            if (length > DirConProtocol.MaximumDataLength)
            {
                await WriteAsync(DirConProtocol.Encode(header[1], header[2], DirConProtocol.GenericError, []), ct);
                throw new InvalidDataException("DirCon payload exceeds the supported frame limit.");
            }
            byte[] body = new byte[length];
            await _stream.ReadExactlyAsync(body, deadline.Token);
            return new DirConFrame(header[0], header[1], header[2], header[3], body);
        }

        private async Task HandleAsync(DirConFrame request, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            byte[]? controlResponse = null;
            byte[]? machineStatus = null;
            bool releaseControl = false;
            await _writer.WaitAsync(deadline.Token);
            bool writerHeld = true;
            try
            {
                byte status = DirConProtocol.Success;
                byte[] payload = [];
                if (request.Version != DirConProtocol.Version || request.ResponseCode != 0)
                    status = DirConProtocol.GenericError;
                else if (request.MessageType == 1)
                {
                    if (request.Data.Length != 0) status = DirConProtocol.GenericError;
                    else payload = owner.ServiceUuids.SelectMany(DirConProtocol.UuidBytes).ToArray();
                }
                else if (request.MessageType is >= 2 and <= 5)
                {
                    bool validLength = request.MessageType switch
                    {
                        2 or 3 => request.Data.Length == 16,
                        4 => request.Data.Length >= 16,
                        5 => request.Data.Length == 17,
                        _ => false
                    };
                    if (!validLength) status = DirConProtocol.GenericError;
                    else
                    {
                        Guid uuid = DirConProtocol.ReadUuid(request.Data.AsSpan(0, 16));
                        payload = request.Data.AsSpan(0, 16).ToArray();
                        if (request.MessageType == 2)
                        {
                            var characteristics = DirConProtocol.Characteristics(uuid, owner.ControlEnabled);
                            if (characteristics is null) status = DirConProtocol.ServiceNotFound;
                            else payload = payload.Concat(characteristics.SelectMany(c => DirConProtocol.UuidBytes(c.Uuid).Append(c.Properties))).ToArray();
                        }
                        else
                        {
                            var characteristic = DirConProtocol.Characteristic(uuid, owner.ControlEnabled);
                            if (characteristic is null) status = DirConProtocol.CharacteristicNotFound;
                            else if (request.MessageType == 3)
                            {
                                if ((characteristic.Properties & DirConProtocol.Read) == 0) status = DirConProtocol.OperationNotSupported;
                                else payload = payload.Concat(owner.ReadValue(uuid)).ToArray();
                            }
                            else if (request.MessageType == 4)
                            {
                                if ((characteristic.Properties & DirConProtocol.Write) == 0 || owner._controlPoint is null)
                                    status = DirConProtocol.OperationNotSupported;
                                else if (!_subscriptions.Contains(DirConProtocol.FitnessMachineControlPoint))
                                    status = DirConProtocol.WriteFailed;
                                else if (request.Data.Length == 16) status = DirConProtocol.WriteFailed;
                                else
                                {
                                    var command = request.Data.AsMemory(16);
                                    // Keep measurement notifications flowing while the real trainer acknowledges a command.
                                    _writer.Release();
                                    writerHeld = false;
                                    byte result = await owner._controlPoint.ProcessAsync(id, command, deadline.Token);
                                    await _writer.WaitAsync(deadline.Token);
                                    writerHeld = true;
                                    controlResponse = [0x80, command.Span[0], result];
                                    if (result == FtmsControlPoint.Success)
                                        machineStatus = command.Span[0] switch
                                        {
                                            1 => [1],
                                            5 => new byte[] { 8 }.Concat(command.Span[1..].ToArray()).ToArray(),
                                            7 => [4],
                                            8 => [2, command.Span[1]],
                                            0x11 => new byte[] { 0x12 }.Concat(command.Span[1..].ToArray()).ToArray(),
                                            _ => null
                                        };
                                }
                            }
                            else if ((characteristic.Properties & DirConProtocol.Notify) == 0) status = DirConProtocol.OperationNotSupported;
                            else if (request.Data[16] > 1) status = DirConProtocol.GenericError;
                            else
                            {
                                if (request.Data[16] == 1) _subscriptions.Add(uuid);
                                else
                                {
                                    _subscriptions.Remove(uuid);
                                    releaseControl = uuid == DirConProtocol.FitnessMachineControlPoint;
                                }
                                Volatile.Write(ref _subscriptionCount, _subscriptions.Count);
                            }
                        }
                    }
                }
                else status = DirConProtocol.InvalidMessageType;
                // Holding the same lock as notifications ensures subscribe/unsubscribe acknowledgements precede later data.
                await _stream.WriteAsync(DirConProtocol.Encode(request.MessageType, request.Sequence, status, payload), deadline.Token);
                if (controlResponse is not null)
                    await SendNotificationCoreAsync(DirConProtocol.FitnessMachineControlPoint, controlResponse, deadline.Token);
            }
            finally { if (writerHeld) _writer.Release(); }
            if (releaseControl && owner._controlPoint is not null) await owner._controlPoint.ReleaseAsync(id);
            if (machineStatus is not null)
                await Task.WhenAll(owner._clients.Values.Select(client =>
                    client.NotifyValueAsync(DirConProtocol.FitnessMachineStatus, machineStatus)));
        }

        public async Task NotifyAsync(BridgeMeasurements measurements)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                await _writer.WaitAsync(deadline.Token);
                try
                {
                    foreach (Guid uuid in _subscriptions)
                    {
                        byte[]? value = uuid == DirConProtocol.CyclingPowerMeasurement ? measurements.CyclingPower :
                            uuid == DirConProtocol.IndoorBikeData ? measurements.IndoorBikeData :
                            uuid == DirConProtocol.HeartRateMeasurement ? measurements.HeartRate : null;
                        if (value is null) continue; // No invented/stale heartbeat when the bike has no valid pulse.
                        await SendNotificationCoreAsync(uuid, value, deadline.Token);
                    }
                }
                finally { _writer.Release(); }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { Close(); }
        }

        private Task SendNotificationCoreAsync(Guid uuid, byte[] value, CancellationToken ct) => _stream.WriteAsync(
            DirConProtocol.Encode(6, _notificationSequence++, 0, DirConProtocol.UuidBytes(uuid).Concat(value).ToArray()), ct).AsTask();

        private async Task NotifyValueAsync(Guid uuid, byte[] value)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                await _writer.WaitAsync(deadline.Token);
                try
                {
                    if (_subscriptions.Contains(uuid)) await SendNotificationCoreAsync(uuid, value, deadline.Token);
                }
                finally { _writer.Release(); }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { Close(); }
        }

        private async Task WriteAsync(byte[] frame, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await _writer.WaitAsync(deadline.Token);
            try { await _stream.WriteAsync(frame, deadline.Token); }
            finally { _writer.Release(); }
        }
    }
}
