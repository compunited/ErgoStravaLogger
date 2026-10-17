using System.Buffers.Binary;
using System.Text;

namespace ErgoStravaLogger;

public sealed record MdnsQuestion(string Name, ushort Type, ushort Class = 1, bool UnicastResponse = false);
public sealed record MdnsRecord(string Name, ushort Type, ushort Class, bool CacheFlush, uint Ttl, byte[] Data);

public sealed class MdnsMessage
{
    public ushort Id { get; init; }
    public ushort Flags { get; init; }
    public IReadOnlyList<MdnsQuestion> Questions { get; init; } = [];
    public IReadOnlyList<MdnsRecord> Answers { get; init; } = [];
    public IReadOnlyList<MdnsRecord> Authorities { get; init; } = [];
    public IReadOnlyList<MdnsRecord> Additionals { get; init; } = [];
}

/// <summary>Small bounded DNS wire codec. PTR/SRV names are normalized when decoding compression.</summary>
public static class MdnsCodec
{
    public const int MaximumPacketBytes = 9000;
    private const int MaximumEntries = 256;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Encode(MdnsMessage message)
    {
        int count = message.Questions.Count + message.Answers.Count + message.Authorities.Count + message.Additionals.Count;
        if (count > MaximumEntries) throw new InvalidDataException("Zu viele DNS-Einträge.");
        using var output = new MemoryStream();
        Write16(output, message.Id);
        Write16(output, message.Flags);
        Write16(output, checked((ushort)message.Questions.Count));
        Write16(output, checked((ushort)message.Answers.Count));
        Write16(output, checked((ushort)message.Authorities.Count));
        Write16(output, checked((ushort)message.Additionals.Count));
        foreach (MdnsQuestion question in message.Questions)
        {
            output.Write(EncodeName(question.Name));
            Write16(output, question.Type);
            Write16(output, (ushort)(question.Class | (question.UnicastResponse ? 0x8000 : 0)));
        }
        foreach (MdnsRecord record in message.Answers.Concat(message.Authorities).Concat(message.Additionals))
        {
            if (record.Data.Length > ushort.MaxValue) throw new InvalidDataException("DNS-RDATA ist zu groß.");
            output.Write(EncodeName(record.Name));
            Write16(output, record.Type);
            Write16(output, (ushort)(record.Class | (record.CacheFlush ? 0x8000 : 0)));
            Write32(output, record.Ttl);
            Write16(output, checked((ushort)record.Data.Length));
            output.Write(record.Data);
            if (output.Length > MaximumPacketBytes) throw new InvalidDataException("DNS-Paket ist zu groß.");
        }
        if (output.Length > MaximumPacketBytes) throw new InvalidDataException("DNS-Paket ist zu groß.");
        return output.ToArray();
    }

    public static MdnsMessage Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length is < 12 or > MaximumPacketBytes) throw new InvalidDataException("Ungültige DNS-Paketgröße.");
        ushort id = BinaryPrimitives.ReadUInt16BigEndian(packet);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        int questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        int authorities = BinaryPrimitives.ReadUInt16BigEndian(packet[8..]);
        int additionals = BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);
        if (questions + answers + authorities + additionals > MaximumEntries)
            throw new InvalidDataException("Zu viele DNS-Einträge.");
        int position = 12;
        var decodedQuestions = new List<MdnsQuestion>(questions);
        for (int index = 0; index < questions; index++)
        {
            string name = DecodeName(packet, ref position);
            ushort type = Read16(packet, ref position);
            ushort rawClass = Read16(packet, ref position);
            decodedQuestions.Add(new(name, type, (ushort)(rawClass & 0x7fff), (rawClass & 0x8000) != 0));
        }
        var decodedAnswers = ReadRecords(packet, ref position, answers);
        var decodedAuthorities = ReadRecords(packet, ref position, authorities);
        var decodedAdditionals = ReadRecords(packet, ref position, additionals);
        if (position != packet.Length) throw new InvalidDataException("Unerwartete Bytes hinter dem DNS-Paket.");
        return new MdnsMessage
        {
            Id = id, Flags = flags, Questions = decodedQuestions,
            Answers = decodedAnswers, Authorities = decodedAuthorities, Additionals = decodedAdditionals
        };
    }

    public static byte[] EncodeName(string name)
    {
        using var output = new MemoryStream();
        string value = name.TrimEnd('.');
        if (value.Length > 0)
        {
            foreach (string label in value.Split('.'))
            {
                byte[] bytes = Utf8.GetBytes(label);
                if (bytes.Length is < 1 or > 63) throw new InvalidDataException("Ungültiges DNS-Label.");
                output.WriteByte((byte)bytes.Length);
                output.Write(bytes);
            }
        }
        output.WriteByte(0);
        if (output.Length > 255) throw new InvalidDataException("DNS-Name ist zu lang.");
        return output.ToArray();
    }

    public static string DecodeName(ReadOnlySpan<byte> packet, ref int position)
    {
        int cursor = position;
        int endPosition = -1;
        int wireLength = 1;
        var visited = new HashSet<int>();
        var labels = new List<string>();
        for (int iteration = 0; iteration < 256; iteration++)
        {
            if ((uint)cursor >= (uint)packet.Length || !visited.Add(cursor))
                throw new InvalidDataException("Ungültiger DNS-Kompressionszeiger.");
            byte length = packet[cursor++];
            if ((length & 0xc0) == 0xc0)
            {
                if (cursor >= packet.Length) throw new InvalidDataException("Abgeschnittener DNS-Zeiger.");
                int target = ((length & 0x3f) << 8) | packet[cursor++];
                if (endPosition < 0) endPosition = cursor;
                cursor = target;
                continue;
            }
            if ((length & 0xc0) != 0) throw new InvalidDataException("Unbekannter DNS-Labeltyp.");
            if (length == 0)
            {
                position = endPosition < 0 ? cursor : endPosition;
                return string.Join('.', labels) + ".";
            }
            if (length > packet.Length - cursor) throw new InvalidDataException("Abgeschnittenes DNS-Label.");
            wireLength += length + 1;
            if (wireLength > 255) throw new InvalidDataException("DNS-Name ist zu lang.");
            try { labels.Add(Utf8.GetString(packet.Slice(cursor, length))); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Ungültiges UTF-8 im DNS-Namen.", ex); }
            cursor += length;
        }
        throw new InvalidDataException("Zu viele DNS-Kompressionszeiger.");
    }

    public static byte[] EncodeText(IEnumerable<string> entries)
    {
        using var output = new MemoryStream();
        foreach (string entry in entries)
        {
            byte[] bytes = Utf8.GetBytes(entry);
            if (bytes.Length > 255) throw new InvalidDataException("DNS-TXT-Eintrag ist zu lang.");
            output.WriteByte((byte)bytes.Length);
            output.Write(bytes);
        }
        return output.ToArray();
    }

    public static IReadOnlyList<string> DecodeText(ReadOnlySpan<byte> data)
    {
        var entries = new List<string>();
        int position = 0;
        while (position < data.Length)
        {
            int length = data[position++];
            if (length > data.Length - position) throw new InvalidDataException("Abgeschnittener DNS-TXT-Eintrag.");
            try { entries.Add(Utf8.GetString(data.Slice(position, length))); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("Ungültiges UTF-8 im TXT-Eintrag.", ex); }
            position += length;
        }
        return entries;
    }

    private static List<MdnsRecord> ReadRecords(ReadOnlySpan<byte> packet, ref int position, int count)
    {
        var records = new List<MdnsRecord>(count);
        for (int index = 0; index < count; index++)
        {
            string name = DecodeName(packet, ref position);
            ushort type = Read16(packet, ref position);
            ushort rawClass = Read16(packet, ref position);
            if (position > packet.Length - 4) throw new InvalidDataException("Abgeschnittener DNS-TTL.");
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(packet[position..]);
            position += 4;
            int length = Read16(packet, ref position);
            if (length > packet.Length - position) throw new InvalidDataException("Abgeschnittenes DNS-RDATA.");
            int end = position + length;
            byte[] data;
            if (type is 12 or 33)
            {
                int prefix = type == 33 ? 6 : 0;
                if (length <= prefix) throw new InvalidDataException("Abgeschnittenes DNS-Namens-RDATA.");
                int namePosition = position + prefix;
                string target = DecodeName(packet, ref namePosition);
                if (namePosition != end) throw new InvalidDataException("Ungültige DNS-RDATA-Länge.");
                data = packet.Slice(position, prefix).ToArray().Concat(EncodeName(target)).ToArray();
            }
            else
            {
                data = packet.Slice(position, length).ToArray();
                if (type == 1 && length != 4) throw new InvalidDataException("Ungültiger DNS-A-Record.");
                if (type == 16) _ = DecodeText(data);
            }
            position = end;
            records.Add(new(name, type, (ushort)(rawClass & 0x7fff), (rawClass & 0x8000) != 0, ttl, data));
        }
        return records;
    }

    private static ushort Read16(ReadOnlySpan<byte> packet, ref int position)
    {
        if (position > packet.Length - 2) throw new InvalidDataException("Abgeschnittenes DNS-Feld.");
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(packet[position..]);
        position += 2;
        return value;
    }

    private static void Write16(Stream output, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void Write32(Stream output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.Write(bytes);
    }
}
