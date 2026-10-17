using System.Buffers.Binary;
using System.Net;
using ErgoStravaLogger;

namespace ErgoStravaLogger.Tests;

public static class MdnsTests
{
    private static readonly ZwiftBridgeOptions Options = new()
    {
        Identity = "02DA08001234", DeviceName = "Daum 8i Bridge", Port = 36866, BindAddress = "0.0.0.0"
    };
    private static readonly Guid[] Services =
    [
        Guid.Parse("00001818-0000-1000-8000-00805f9b34fb"),
        Guid.Parse("00001826-0000-1000-8000-00805f9b34fb"),
        Guid.Parse("0000180d-0000-1000-8000-00805f9b34fb"),
        Guid.Parse("0000180a-0000-1000-8000-00805f9b34fb")
    ];
    private static readonly IPAddress[] Addresses = [IPAddress.Parse("192.168.2.20")];

    public static Task RunAsync()
    {
        TestAdvertisementRecords();
        TestQueryResponses();
        TestGoodbyeAndLegacy();
        TestCompressedQueries();
        TestCompressedRecords();
        TestMalformedPackets();
        TestLimitsAndIdentity();
        return Task.CompletedTask;
    }

    private static void TestAdvertisementRecords()
    {
        IReadOnlyList<MdnsRecord> records = DirConDiscovery.CreateRecords(Options, Services, 36866, Addresses);
        MdnsMessage decoded = MdnsCodec.Decode(MdnsCodec.Encode(new MdnsMessage { Flags = 0x8400, Answers = records }));
        Equal((ushort)0x8400, decoded.Flags, "authoritative response");
        Equal(4, decoded.Answers.Count, "PTR, SRV, TXT, A count");
        MdnsRecord ptr = decoded.Answers.Single(record => record.Type == 12);
        Equal(DirConDiscovery.ServiceType, ptr.Name, "service type");
        Equal(DirConDiscovery.InstanceName(Options), DecodeName(ptr.Data), "PTR instance");
        True(!ptr.CacheFlush, "shared service PTR must not flush other trainers");
        MdnsRecord srv = decoded.Answers.Single(record => record.Type == 33);
        Equal((ushort)36866, BinaryPrimitives.ReadUInt16BigEndian(srv.Data.AsSpan(4)), "TCP port");
        Equal(DirConDiscovery.HostName(Options), DecodeName(srv.Data, 6), "SRV target host");
        True(srv.Data.Take(4).All(value => value == 0), "zero SRV priority and weight");
        MdnsRecord txt = decoded.Answers.Single(record => record.Type == 16);
        IReadOnlyList<string> text = MdnsCodec.DecodeText(txt.Data);
        True(text.Contains("ble-service-uuids=1818,1826,180D,180A"), "16-bit BLE service UUIDs");
        True(text.Contains("mac-address=02:DA:08:00:12:34"), "virtual stable MAC");
        True(text.Contains("serial-number=ERGO02DA08001234"), "stable serial identity");
        MdnsRecord a = decoded.Answers.Single(record => record.Type == 1);
        Bytes(Addresses[0].GetAddressBytes(), a.Data, "reachable IPv4 address");
        True(decoded.Answers.All(record => record.Ttl == 120 && record.Class == 1), "IN class and TTL");
        True(decoded.Answers.Where(record => record.Type != 12).All(record => record.CacheFlush), "unique record flush flags");
    }

    private static void TestQueryResponses()
    {
        foreach (var (name, type) in new[]
        {
            (DirConDiscovery.ServiceType, (ushort)12),
            (DirConDiscovery.InstanceName(Options), (ushort)33),
            (DirConDiscovery.InstanceName(Options), (ushort)16),
            (DirConDiscovery.HostName(Options), (ushort)1)
        })
        {
            MdnsMessage query = MdnsCodec.Decode(MdnsCodec.Encode(new MdnsMessage
            {
                Questions = [new(name.ToUpperInvariant(), type, 1, true)]
            }));
            True(query.Questions[0].UnicastResponse, "QU bit survives decode");
            MdnsMessage reply = DirConDiscovery.CreateResponse(Options, Services, 36866, Addresses, query)
                ?? throw new Exception("Matching mDNS query did not produce a response.");
            Equal(1, reply.Answers.Count, "only matching answer type");
            Equal(type, reply.Answers[0].Type, "matching answer type");
            Equal(0, reply.Questions.Count, "multicast/QU responses do not echo questions");
            Equal((ushort)0, reply.Id, "mDNS transaction ID is zero");
            Equal(3, reply.Additionals.Count, "service address and related records included");
        }
        MdnsMessage? meta = Reply(DirConDiscovery.MetaServiceType, 12);
        True(meta is not null, "DNS-SD service enumeration");
        Equal(DirConDiscovery.ServiceType, DecodeName(meta!.Answers[0].Data), "enumeration PTR");
        Equal(0, meta.Additionals.Count, "enumeration does not include unrelated instance records");
        Equal(2, Reply(DirConDiscovery.InstanceName(Options), 255)!.Answers.Count, "instance ANY query");
        True(Reply("_unrelated._tcp.local.", 12) is null, "unrelated service ignored");
        True(Reply(DirConDiscovery.ServiceType, 1) is null, "wrong RR type ignored");
        True(Reply(DirConDiscovery.ServiceType, 12, 3) is null, "wrong class ignored");
        True(DirConDiscovery.CreateResponse(Options, Services, 36866, Addresses,
            new MdnsMessage { Flags = 0x8000, Questions = [new(DirConDiscovery.ServiceType, 12)] }) is null,
            "never answer another response");
        True(DirConDiscovery.CreateResponse(Options, Services, 36866, Addresses,
            new MdnsMessage { Flags = 0x2800, Questions = [new(DirConDiscovery.ServiceType, 12)] }) is null,
            "unsupported DNS opcode ignored");
    }

    private static void TestGoodbyeAndLegacy()
    {
        IReadOnlyList<MdnsRecord> records = DirConDiscovery.CreateRecords(Options, Services, 41234, Addresses, 0);
        MdnsMessage decoded = MdnsCodec.Decode(MdnsCodec.Encode(new MdnsMessage { Flags = 0x8400, Answers = records }));
        True(decoded.Answers.All(record => record.Ttl == 0), "goodbye withdraws every record");
        MdnsMessage reply = DirConDiscovery.CreateResponse(Options, Services, 41234, Addresses,
            new MdnsMessage { Id = 12345, Questions = [new(DirConDiscovery.ServiceType, 12, 1, true)] }, legacy: true)!;
        Equal((ushort)12345, reply.Id, "legacy DNS transaction ID echoed");
        Equal(1, reply.Questions.Count, "legacy query echoed");
        True(!reply.Questions[0].UnicastResponse, "legacy query class has no QU bit");
        True(reply.Answers.Concat(reply.Additionals).All(record => !record.CacheFlush && record.Ttl == 10),
            "legacy responses use short TTL without cache flush");
        Equal((ushort)41234, BinaryPrimitives.ReadUInt16BigEndian(reply.Additionals.Single(record => record.Type == 33).Data.AsSpan(4)),
            "SRV advertises actual listener port");
    }

    private static void TestCompressedQueries()
    {
        byte[] plain = MdnsCodec.Encode(new MdnsMessage { Questions = [new(DirConDiscovery.ServiceType, 12)] });
        byte[] compressed = plain.Concat(new byte[] { 0xc0, 0x0c, 0x00, 0xff, 0x80, 0x01 }).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(compressed.AsSpan(4), 2);
        MdnsMessage query = MdnsCodec.Decode(compressed);
        Equal(2, query.Questions.Count, "two compressed questions");
        Equal(query.Questions[0].Name, query.Questions[1].Name, "pointer resolves first question");
        Equal((ushort)255, query.Questions[1].Type, "compressed question type");
        True(query.Questions[1].UnicastResponse, "compressed QU class");
    }

    private static void TestCompressedRecords()
    {
        byte[] question = MdnsCodec.Encode(new MdnsMessage { Questions = [new(DirConDiscovery.ServiceType, 12)] });
        byte[] packet = question.Concat(new byte[]
        {
            0xc0, 0x0c, 0, 12, 0, 1, 0, 0, 0, 120, 0, 2, 0xc0, 0x0c
        }).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0x8400);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), 1);
        MdnsMessage decoded = MdnsCodec.Decode(packet);
        Equal(DirConDiscovery.ServiceType, decoded.Answers[0].Name, "compressed record owner");
        Equal(DirConDiscovery.ServiceType, DecodeName(decoded.Answers[0].Data), "compressed PTR data normalized");
        MdnsMessage roundtrip = MdnsCodec.Decode(MdnsCodec.Encode(decoded));
        Bytes(decoded.Answers[0].Data, roundtrip.Answers[0].Data, "normalized pointer can be re-encoded independently");
    }

    private static void TestMalformedPackets()
    {
        Throws(() => MdnsCodec.Decode(new byte[11]));
        Throws(() => MdnsCodec.Decode(new byte[MdnsCodec.MaximumPacketBytes + 1]));
        byte[] query = MdnsCodec.Encode(new MdnsMessage { Questions = [new(DirConDiscovery.ServiceType, 12)] });
        Throws(() => MdnsCodec.Decode(query[..^1]));
        Throws(() => MdnsCodec.Decode(query.Concat(new byte[] { 0 }).ToArray()));
        byte[] loop = new byte[18];
        loop[5] = 1;
        loop[12] = 0xc0;
        loop[13] = 12;
        Throws(() => MdnsCodec.Decode(loop));
        loop[13] = 0xff;
        Throws(() => MdnsCodec.Decode(loop));
        loop[12] = 0x80;
        Throws(() => MdnsCodec.Decode(loop));
        byte[] excessive = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(excessive.AsSpan(4), 257);
        Throws(() => MdnsCodec.Decode(excessive));
        Throws(() => MdnsCodec.DecodeText(new byte[] { 2, (byte)'a' }));
        Throws(() => MdnsCodec.DecodeText(new byte[] { 1, 0xff }));
        byte[] wrongA = MdnsCodec.Encode(new MdnsMessage
        {
            Answers = [new("test.local.", 1, 1, true, 120, [1, 2, 3])]
        });
        Throws(() => MdnsCodec.Decode(wrongA));
        byte[] badUtf8 = new byte[18];
        badUtf8[5] = 1;
        badUtf8[12] = 1;
        badUtf8[13] = 0xff;
        Throws(() => MdnsCodec.Decode(badUtf8));
    }

    private static void TestLimitsAndIdentity()
    {
        Throws(() => MdnsCodec.EncodeName(new string('x', 64) + ".local."));
        Throws(() => MdnsCodec.EncodeName(string.Join('.', Enumerable.Repeat(new string('x', 63), 4))));
        Throws(() => MdnsCodec.EncodeText([new string('x', 256)]));
        Throws(() => MdnsCodec.Encode(new MdnsMessage
        {
            Answers = [new("test.local.", 16, 1, true, 120, new byte[9000])]
        }));
        Throws(() => MdnsCodec.Encode(new MdnsMessage
        {
            Questions = Enumerable.Repeat(new MdnsQuestion("test.local.", 255), 257).ToArray()
        }));
        Throws(() => DirConDiscovery.CreateRecords(Options, Services, 36866, [IPAddress.Any, IPAddress.IPv6Any]));
        Throws(() => DirConDiscovery.HostName(new ZwiftBridgeOptions { Identity = "not-a-mac" }));
        IReadOnlyList<MdnsRecord> records = DirConDiscovery.CreateRecords(Options, Services, 36866,
            [IPAddress.Any, Addresses[0], Addresses[0], IPAddress.Parse("224.0.0.251")]);
        Equal(1, records.Count(record => record.Type == 1), "unspecified/multicast/duplicate IPs never advertised");
        string name = DirConDiscovery.InstanceName(new ZwiftBridgeOptions
        {
            Identity = Options.Identity, DeviceName = string.Concat(Enumerable.Repeat("🚴", 40))
        });
        byte[] bytes = MdnsCodec.EncodeName(name);
        True(bytes[0] <= 63, "UTF-8 device name fits one DNS label");
        True(DirConDiscovery.HostName(Options).StartsWith("ergostrava-02da08001234.", StringComparison.Ordinal),
            "stable valid host name");
    }

    private static MdnsMessage? Reply(string name, ushort type, ushort dnsClass = 1) =>
        DirConDiscovery.CreateResponse(Options, Services, 36866, Addresses,
            new MdnsMessage { Questions = [new(name, type, dnsClass)] });

    private static string DecodeName(byte[] bytes, int offset = 0) => MdnsCodec.DecodeName(bytes, ref offset);
    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{label}: expected {expected}, got {actual}");
    }
    private static void True(bool value, string label)
    {
        if (!value) throw new Exception(label);
    }
    private static void Bytes(byte[] expected, byte[] actual, string label) => True(expected.AsSpan().SequenceEqual(actual), label);
    private static void Throws(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected InvalidDataException for malformed DNS data.");
    }
}
