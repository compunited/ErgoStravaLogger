using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ErgoStravaLogger;

/// <summary>Independent IPv4 mDNS/DNS-SD responder for the WFTNP Direct Connect service.</summary>
public sealed class DirConDiscovery : IBridgeDiscovery
{
    public const string ServiceType = "_wahoo-fitness-tnp._tcp.local.";
    public const string MetaServiceType = "_services._dns-sd._udp.local.";
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint MulticastEndpoint = new(MulticastAddress, 5353);
    private const uint RecordTtl = 120;
    private readonly ZwiftBridgeOptions _options;
    private readonly IReadOnlyList<Guid> _serviceUuids;
    private readonly int _tcpPort;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _send = new(1, 1);
    private Socket? _socket;
    private CancellationTokenSource? _lifetime;
    private Task? _receiveTask;
    private Task? _announceTask;
    private IReadOnlyList<LocalInterface> _interfaces = [];
    private volatile bool _running;
    private volatile bool _probing;
    private volatile string? _lastError;
    private bool _disposed;

    public DirConDiscovery(ZwiftBridgeOptions options, IReadOnlyList<Guid> serviceUuids, int tcpPort)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceUuids);
        if (tcpPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(tcpPort));
        _options = options;
        _serviceUuids = serviceUuids.ToArray();
        _tcpPort = tcpPort;
        // Validate DNS fields even when the service is not yet enabled.
        _ = CreateRecords(options, serviceUuids, tcpPort, [IPAddress.Loopback]);
    }

    public bool IsRunning => _running && _lifetime?.IsCancellationRequested == false;
    public string? LastError => _lastError;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) return;
            await CleanupAsync();
            _lastError = null;
            _interfaces = SelectInterfaces(_options.BindAddress);
            if (_interfaces.Count == 0)
                throw new InvalidOperationException("Keine aktive IPv4-Schnittstelle für Zwift-mDNS gefunden.");
            _lifetime = new CancellationTokenSource();
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket = socket;
            socket.ExclusiveAddressUse = false;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, 5353));
            foreach (LocalInterface network in _interfaces)
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(MulticastAddress, network.Addresses[0].Address));
            _probing = true;
            _receiveTask = ReceiveAsync(socket, _lifetime.Token);
            await Task.Delay(Random.Shared.Next(0, 251), ct);
            for (int probe = 0; probe < 3; probe++)
            {
                foreach (LocalInterface network in _interfaces)
                {
                    IReadOnlyList<MdnsRecord> records = RecordsFor(network);
                    byte[] request = MdnsCodec.Encode(new MdnsMessage
                    {
                        Questions = [new(InstanceName(_options), 255), new(HostName(_options), 255)],
                        Authorities = records.Where(record => record.CacheFlush)
                            .Select(record => record with { CacheFlush = false }).ToArray()
                    });
                    await SendAsync(socket, network, request, MulticastEndpoint, ct);
                }
                await Task.Delay(250, ct);
                if (_lastError is not null) throw new InvalidOperationException(_lastError);
            }
            _probing = false;
            _running = true;
            await AnnounceAsync(socket, RecordTtl, ct);
            if (_lastError is not null) throw new InvalidOperationException(_lastError);
            _announceTask = AnnouncePeriodicallyAsync(socket, _lifetime.Token);
        }
        catch (Exception ex)
        {
            _lastError ??= ex is OperationCanceledException ? "Zwift-mDNS-Start wurde abgebrochen." : ex.Message;
            await CleanupAsync();
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try { await StopCoreAsync(ct); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        var announceGoodbye = _running;
        _running = false;
        _probing = false;
        _lifetime?.Cancel();
        // Finish in-flight answers/announcements before withdrawing the records.
        if (_receiveTask is not null) await ObserveShutdownAsync(_receiveTask);
        if (_announceTask is not null) await ObserveShutdownAsync(_announceTask);
        if (_socket is { } socket && announceGoodbye)
        {
            using var goodbye = CancellationTokenSource.CreateLinkedTokenSource(ct);
            goodbye.CancelAfter(TimeSpan.FromSeconds(2));
            try { await AnnounceAsync(socket, 0, goodbye.Token); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { }
        }
        await CleanupAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopCoreAsync(CancellationToken.None);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task CleanupAsync()
    {
        _running = false;
        _probing = false;
        _lifetime?.Cancel();
        _socket?.Dispose();
        if (_receiveTask is not null) await ObserveShutdownAsync(_receiveTask);
        if (_announceTask is not null) await ObserveShutdownAsync(_announceTask);
        _receiveTask = _announceTask = null;
        _socket = null;
        _lifetime?.Dispose();
        _lifetime = null;
    }

    private static async Task ObserveShutdownAsync(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ReceiveAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[MdnsCodec.MaximumPacketBytes + 1];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SocketReceiveMessageFromResult received = await socket.ReceiveMessageFromAsync(
                    buffer.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
                if ((received.SocketFlags & SocketFlags.Truncated) != 0 || received.ReceivedBytes > MdnsCodec.MaximumPacketBytes)
                    continue;
                if (received.RemoteEndPoint is not IPEndPoint remote) continue;
                LocalInterface? network = _interfaces.FirstOrDefault(item => item.Index == received.PacketInformation.Interface)
                    ?? _interfaces.FirstOrDefault(item => item.Addresses.Any(address => InSubnet(remote.Address, address)));
                // mDNS has link-local scope; do not answer queries from a routed external address.
                if (network is null || !network.Addresses.Any(address => InSubnet(remote.Address, address))) continue;
                MdnsMessage request;
                try { request = MdnsCodec.Decode(buffer.AsSpan(0, received.ReceivedBytes)); }
                catch (InvalidDataException) { continue; }
                if ((request.Flags & 0x780f) != 0) continue; // Only standard, successful DNS messages.
                if ((request.Flags & 0x8000) != 0)
                {
                    if (HasConflict(request))
                        throw new InvalidOperationException("Zwift-mDNS-Namenskonflikt: eine andere Bridge verwendet dieselbe Identität.");
                    continue;
                }
                if (_probing || !_running) continue;
                bool legacy = remote.Port != 5353;
                if (legacy)
                {
                    MdnsMessage? reply = CreateResponse(_options, _serviceUuids, _tcpPort,
                        network.Addresses.Select(address => address.Address).ToArray(), request, legacy: true);
                    if (reply is not null) await SendAsync(socket, network, MdnsCodec.Encode(reply), remote, ct);
                }
                else
                {
                    foreach (bool unicast in new[] { true, false })
                    {
                        var questions = request.Questions.Where(question => question.UnicastResponse == unicast).ToArray();
                        if (questions.Length == 0) continue;
                        MdnsMessage? reply = CreateResponse(_options, _serviceUuids, _tcpPort,
                            network.Addresses.Select(address => address.Address).ToArray(),
                            new MdnsMessage { Questions = questions });
                        if (reply is null) continue;
                        if (!unicast) await Task.Delay(Random.Shared.Next(20, 121), ct);
                        await SendAsync(socket, network, MdnsCodec.Encode(reply), unicast ? remote : MulticastEndpoint, ct);
                    }
                }
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _running = false;
            _lifetime?.Cancel();
        }
    }

    private bool HasConflict(MdnsMessage message)
    {
        // A multihomed host legitimately owns multiple A values. Compare against the complete local set.
        IReadOnlyList<MdnsRecord> local = CreateRecords(_options, _serviceUuids, _tcpPort,
            _interfaces.SelectMany(network => network.Addresses).Select(address => address.Address).ToArray());
        foreach (MdnsRecord record in message.Answers.Concat(message.Authorities).Concat(message.Additionals))
        {
            if (record.Ttl == 0 || record.Class != 1 || record.Type is not (1 or 16 or 33)) continue;
            MdnsRecord[] owned = local.Where(item => item.Type == record.Type && NameEquals(item.Name, record.Name)).ToArray();
            if (owned.Length > 0 && !owned.Any(item => item.Data.AsSpan().SequenceEqual(record.Data))) return true;
        }
        return false;
    }

    private async Task AnnouncePeriodicallyAsync(Socket socket, CancellationToken ct)
    {
        try
        {
            // A second initial announcement helps clients that missed the first multicast packet.
            await Task.Delay(1000, ct);
            await AnnounceAsync(socket, RecordTtl, ct);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            while (await timer.WaitForNextTickAsync(ct)) await AnnounceAsync(socket, RecordTtl, ct);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _running = false;
            _lifetime?.Cancel();
        }
    }

    private async Task AnnounceAsync(Socket socket, uint ttl, CancellationToken ct)
    {
        foreach (LocalInterface network in _interfaces)
        {
            byte[] packet = MdnsCodec.Encode(new MdnsMessage { Flags = 0x8400, Answers = RecordsFor(network, ttl) });
            await SendAsync(socket, network, packet, MulticastEndpoint, ct);
        }
    }

    private IReadOnlyList<MdnsRecord> RecordsFor(LocalInterface network, uint ttl = RecordTtl) =>
        CreateRecords(_options, _serviceUuids, _tcpPort, network.Addresses.Select(address => address.Address).ToArray(), ttl);

    private async Task SendAsync(Socket socket, LocalInterface network, byte[] packet, IPEndPoint destination, CancellationToken ct)
    {
        await _send.WaitAsync(ct);
        try
        {
            // One shared receiver can coexist with Avahi/Bonjour. Select the outbound interface for each packet.
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                network.Addresses[0].Address.GetAddressBytes());
            await socket.SendToAsync(packet.AsMemory(), SocketFlags.None, destination, ct);
        }
        finally { _send.Release(); }
    }

    public static IReadOnlyList<MdnsRecord> CreateRecords(ZwiftBridgeOptions options, IReadOnlyList<Guid> serviceUuids,
        int tcpPort, IReadOnlyList<IPAddress> addresses, uint ttl = RecordTtl)
    {
        if (tcpPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(tcpPort));
        string identity = NormalizeIdentity(options.Identity);
        string host = HostName(options);
        string instance = InstanceName(options);
        byte[] target = MdnsCodec.EncodeName(host);
        byte[] srv = new byte[6 + target.Length];
        BinaryPrimitives.WriteUInt16BigEndian(srv.AsSpan(4), checked((ushort)tcpPort));
        target.CopyTo(srv, 6);
        string[] shortUuids = serviceUuids.Select(uuid => uuid.ToString("N"))
            .Where(uuid => uuid.StartsWith("0000", StringComparison.OrdinalIgnoreCase)
                && uuid.EndsWith("00001000800000805f9b34fb", StringComparison.OrdinalIgnoreCase))
            .Select(uuid => uuid.Substring(4, 4).ToUpperInvariant()).Distinct().ToArray();
        byte[] text = MdnsCodec.EncodeText([
            "ble-service-uuids=" + string.Join(',', shortUuids),
            "mac-address=" + string.Join(':', Enumerable.Range(0, 6).Select(index => identity.Substring(index * 2, 2))),
            "serial-number=ERGO" + identity
        ]);
        var records = new List<MdnsRecord>
        {
            new(ServiceType, 12, 1, false, ttl, MdnsCodec.EncodeName(instance)),
            new(instance, 33, 1, true, ttl, srv),
            new(instance, 16, 1, true, ttl, text)
        };
        foreach (IPAddress address in addresses.Distinct())
        {
            if (address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.Broadcast) || address.GetAddressBytes()[0] is >= 224 or 0)
                continue;
            records.Add(new(host, 1, 1, true, ttl, address.GetAddressBytes()));
        }
        if (records.Count == 3) throw new InvalidDataException("Keine gültige IPv4-Adresse für Zwift-mDNS.");
        return records;
    }

    public static MdnsMessage? CreateResponse(ZwiftBridgeOptions options, IReadOnlyList<Guid> serviceUuids,
        int tcpPort, IReadOnlyList<IPAddress> addresses, MdnsMessage request, bool legacy = false)
    {
        if ((request.Flags & 0xf80f) != 0) return null;
        IReadOnlyList<MdnsRecord> records = CreateRecords(options, serviceUuids, tcpPort, addresses, legacy ? 10u : RecordTtl);
        var answers = new List<MdnsRecord>();
        var questions = new List<MdnsQuestion>();
        foreach (MdnsQuestion question in request.Questions)
        {
            if (question.Class is not (1 or 255)) continue;
            MdnsRecord[] matching;
            if (NameEquals(question.Name, MetaServiceType) && question.Type is 12 or 255)
                matching = [new(MetaServiceType, 12, 1, false, legacy ? 10u : RecordTtl, MdnsCodec.EncodeName(ServiceType))];
            else matching = records.Where(record => NameEquals(question.Name, record.Name)
                && (question.Type == 255 || question.Type == record.Type)).ToArray();
            if (matching.Length == 0) continue;
            questions.Add(question);
            foreach (MdnsRecord record in matching)
                if (!answers.Any(existing => SameRecord(existing, record))) answers.Add(record);
        }
        if (answers.Count == 0) return null;
        MdnsRecord[] additional = answers.All(record => NameEquals(record.Name, MetaServiceType)) ? []
            : records.Where(record => !answers.Any(answer => SameRecord(answer, record))).ToArray();
        return new MdnsMessage
        {
            Id = legacy ? request.Id : (ushort)0,
            Flags = 0x8400,
            Questions = legacy ? questions.Select(question => question with { UnicastResponse = false }).ToArray() : [],
            Answers = answers.Select(record => legacy ? record with { CacheFlush = false } : record).ToArray(),
            Additionals = additional.Select(record => legacy ? record with { CacheFlush = false } : record).ToArray()
        };
    }

    public static string HostName(ZwiftBridgeOptions options) => "ergostrava-" + NormalizeIdentity(options.Identity).ToLowerInvariant() + ".local.";

    public static string InstanceName(ZwiftBridgeOptions options)
    {
        string name = options.DeviceName.Trim().Replace('.', '-').Replace('\\', '-');
        if (name.Length == 0) throw new InvalidDataException("Zwift-Gerätename darf nicht leer sein.");
        string suffix = " " + NormalizeIdentity(options.Identity)[^4..];
        while (Encoding.UTF8.GetByteCount(name + suffix) > 63) name = name[..^1];
        // Avoid truncating inside a supplementary Unicode character.
        if (name.Length > 0 && char.IsHighSurrogate(name[^1])) name = name[..^1];
        string instance = name + suffix + "." + ServiceType;
        _ = MdnsCodec.EncodeName(instance);
        return instance;
    }

    private static string NormalizeIdentity(string identity)
    {
        if (identity is null || identity.Length != 12 || identity.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Zwift-Identität muss aus 12 Hex-Zeichen bestehen.");
        return identity.ToUpperInvariant();
    }

    private static bool NameEquals(string left, string right) =>
        string.Equals(left.TrimEnd('.'), right.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static bool SameRecord(MdnsRecord left, MdnsRecord right) =>
        left.Type == right.Type && NameEquals(left.Name, right.Name) && left.Data.AsSpan().SequenceEqual(right.Data);

    private static IReadOnlyList<LocalInterface> SelectInterfaces(string bindAddress)
    {
        if (!IPAddress.TryParse(bindAddress, out IPAddress? bind) || bind.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("Zwift-BindAddress muss eine IPv4-Adresse sein.");
        var selected = new List<LocalInterface>();
        var loopback = new List<LocalInterface>();
        foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties properties = network.GetIPProperties();
            var addresses = properties.UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork
                    && !address.Address.Equals(IPAddress.Any)
                    && (bind.Equals(IPAddress.Any) || bind.Equals(address.Address)))
                .Select(address => new LocalAddress(address.Address, address.IPv4Mask)).ToArray();
            if (addresses.Length == 0) continue;
            int index = properties.GetIPv4Properties()?.Index ?? 0;
            var item = new LocalInterface(index, addresses);
            if (network.NetworkInterfaceType == NetworkInterfaceType.Loopback || addresses.All(address => IPAddress.IsLoopback(address.Address)))
                loopback.Add(item);
            else if (network.SupportsMulticast) selected.Add(item);
        }
        return selected.Count > 0 ? selected : loopback;
    }

    private static bool InSubnet(IPAddress address, LocalAddress local)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] peer = address.GetAddressBytes();
        byte[] own = local.Address.GetAddressBytes();
        byte[] mask = local.Mask.GetAddressBytes();
        if (mask.All(value => value == 0)) return address.Equals(local.Address);
        for (int index = 0; index < 4; index++)
            if ((peer[index] & mask[index]) != (own[index] & mask[index])) return false;
        return true;
    }

    private sealed record LocalAddress(IPAddress Address, IPAddress Mask);
    private sealed record LocalInterface(int Index, IReadOnlyList<LocalAddress> Addresses);
}
