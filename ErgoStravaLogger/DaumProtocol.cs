using System.Globalization;
using System.Text;

namespace ErgoStravaLogger;

public record DeviceSample(
    int PowerWatts,
    int CadenceRpm,
    int? HeartRateBpm,
    double SpeedMetersPerSecond,
    double DistanceMeters,
    TimeSpan TrainingTime,
    int? NativeGear = null);

public record DaumPowerLimits(double MinimumWatts, double MaximumWatts, double DefaultWatts);

/// <summary>Pure codec for the Daum premium ASCII serial/TCP protocol.</summary>
public static class DaumProtocol
{
    public const byte Soh = 0x01;
    public const byte Etb = 0x17;
    public const byte Ack = 0x06;
    public const byte Nak = 0x15;
    public const byte GroupSeparator = 0x1d;
    public const int MaximumFrameLength = 1024;

    /// <summary>Build a read-only V00, V70, or X70 request, including its checksum.</summary>
    public static byte[] EncodeQuery(string command)
    {
        if (command is not ("V00" or "V70" or "X70"))
            throw new ArgumentException("Only the documented V00, V70, and X70 read queries are supported.", nameof(command));
        return EncodeFrame(command, "");
    }

    /// <summary>Query the active watt limits. L70 takes a single ASCII W, without a GS.</summary>
    public static byte[] EncodePowerLimitsQuery() => EncodeFrame("L70", "W");

    /// <summary>Build an S23 watt command in the installed manufacturer's four-digit format.</summary>
    public static byte[] EncodeSetPower(int watts)
    {
        if (watts is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(watts), "The premium 8i power command must be between 0 and 1000 W.");
        return EncodeFrame("S23", watts.ToString("D4", CultureInfo.InvariantCulture) + ".00");
    }

    /// <summary>Build an M71 command: native gear 1..28 is transmitted as gear+1 (2..29), without padding.</summary>
    public static byte[] EncodeSetGear(int gear)
    {
        if (gear is < 1 or > 28)
            throw new ArgumentOutOfRangeException(nameof(gear), "The premium 8i native gear must be between 1 and 28.");
        return EncodeFrame("M71", (gear + 1).ToString(CultureInfo.InvariantCulture));
    }

    private static byte[] EncodeFrame(string command, string payload)
    {
        byte[] frame = new byte[7 + payload.Length];
        frame[0] = Soh;
        Encoding.ASCII.GetBytes(command, frame.AsSpan(1, 3));
        Encoding.ASCII.GetBytes(payload, frame.AsSpan(4, payload.Length));
        WriteChecksum(frame.AsSpan(1, 3 + payload.Length), frame.AsSpan(frame.Length - 3, 2));
        frame[^1] = Etb;
        return frame;
    }

    /// <summary>Validate framing, checksum, and echoed command and return the response data.</summary>
    public static byte[] DecodeResponse(ReadOnlySpan<byte> frame, string expectedCommand)
    {
        if (expectedCommand.Length != 3 || expectedCommand.Any(c => c > 0x7f))
            throw new ArgumentException("The expected command must contain three ASCII characters.", nameof(expectedCommand));
        if (frame.Length < 7 || frame.Length > MaximumFrameLength || frame[0] != Soh || frame[^1] != Etb)
            throw new InvalidDataException("Invalid Daum frame boundaries or length.");
        if (frame[^3] is < (byte)'0' or > (byte)'9' || frame[^2] is < (byte)'0' or > (byte)'9')
            throw new DaumChecksumException("The Daum checksum must contain two decimal ASCII digits.");
        int received = (frame[^3] - '0') * 10 + frame[^2] - '0';
        if (ComputeChecksum(frame[1..^3]) != received)
            throw new DaumChecksumException("The Daum response checksum does not match.");
        for (int i = 0; i < 3; i++)
            if (frame[i + 1] != expectedCommand[i])
                throw new InvalidDataException($"The Daum response does not echo {expectedCommand}.");
        return frame[4..^3].ToArray();
    }

    public static string ParseVersion(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload.ContainsAnyExceptInRange((byte)0x20, (byte)0x7e))
            throw new InvalidDataException("The Daum version response is not printable ASCII text.");
        return Encoding.ASCII.GetString(payload).Trim();
    }

    /// <summary>Read the actual watt value echoed by S23, which may differ from the request.</summary>
    public static double ParsePower(ReadOnlySpan<byte> payload)
    {
        double watts = ParseControlFloat(payload, "power");
        if (watts is < 0 or > 1000)
            throw new InvalidDataException("The Daum power response is outside the premium 8i range.");
        return watts;
    }

    /// <summary>Read the native gear from the M71 gear+1 echo, matching the X70 wire representation.</summary>
    public static int ParseGear(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload.ContainsAnyExceptInRange((byte)'0', (byte)'9') ||
            !int.TryParse(Encoding.ASCII.GetString(payload), NumberStyles.None, CultureInfo.InvariantCulture, out int encodedGear) ||
            encodedGear is < 2 or > 29)
            throw new InvalidDataException("The Daum gear response must contain an active gear+1 value between 2 and 29.");
        return encodedGear - 1;
    }

    public static DaumPowerLimits ParsePowerLimits(ReadOnlySpan<byte> payload)
    {
        if (!payload.IsEmpty && payload[^1] == GroupSeparator) payload = payload[..^1];
        string[] fields = Encoding.ASCII.GetString(payload).Split((char)GroupSeparator);
        if (fields.Length != 4 || fields[0] != "W")
            throw new InvalidDataException("The Daum watt limits must contain W, minimum, maximum, and default.");
        double minimum = ParseControlFloat(Encoding.ASCII.GetBytes(fields[1]), "minimum power");
        double maximum = ParseControlFloat(Encoding.ASCII.GetBytes(fields[2]), "maximum power");
        double initial = ParseControlFloat(Encoding.ASCII.GetBytes(fields[3]), "default power");
        if (minimum < 0 || maximum <= 0 || maximum > 1000 || minimum > maximum || initial < minimum || initial > maximum)
            throw new InvalidDataException("The Daum watt limits are inconsistent or outside the premium 8i range.");
        return new DaumPowerLimits(minimum, maximum, initial);
    }

    private static double ParseControlFloat(ReadOnlySpan<byte> value, string name)
    {
        if (value.IsEmpty || value.ContainsAnyExceptInRange((byte)0x20, (byte)0x7e) ||
            !double.TryParse(Encoding.ASCII.GetString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ||
            !double.IsFinite(result))
            throw new InvalidDataException($"Invalid Daum {name} response.");
        return result;
    }

    /// <summary>
    /// Parse X70 in the installed manufacturer application's numeric format, accepting an optional
    /// trailing GS. The three raw status bytes described by SP-KOMM-PM1 are also accepted.
    /// </summary>
    public static DeviceSample ParseTraining(ReadOnlySpan<byte> payload, string speedUnit = "m/s")
    {
        if (speedUnit is not ("m/s" or "km/h"))
            throw new ArgumentException("The Daum speed unit must be m/s or km/h.", nameof(speedUnit));
        List<byte[]> fields = new(10);
        int offset = 0;
        for (int i = 0; i < 10; i++)
        {
            int delimiter = payload[offset..].IndexOf(GroupSeparator);
            if (delimiter < 0)
                throw new InvalidDataException("X70 must contain thirteen GS-separated fields.");
            fields.Add(payload.Slice(offset, delimiter).ToArray());
            offset += delimiter + 1;
        }
        var nativeGear = ParseNativeGear(payload[offset..]);

        uint seconds = ParseUnsigned(fields[0], "training time");
        uint pulse = ParseUnsigned(fields[1], "heart rate");
        double speed = ParseFloat(fields[2], "speed", nonNegative: true);
        _ = ParseFloat(fields[3], "slope", nonNegative: false);
        uint distance = ParseUnsigned(fields[4], "distance");
        double cadence = ParseFloat(fields[5], "cadence", nonNegative: true);
        uint power = ParseUnsigned(fields[6], "power");
        _ = ParseFloat(fields[7], "physical energy", nonNegative: true);
        _ = ParseFloat(fields[8], "real energy", nonNegative: true);
        _ = ParseFloat(fields[9], "torque", nonNegative: true);
        if (power > int.MaxValue || pulse > int.MaxValue || cadence > int.MaxValue - 0.5)
            throw new InvalidDataException("An X70 value exceeds the supported integer range.");
        return new DeviceSample((int)power, (int)Math.Round(cadence, MidpointRounding.AwayFromZero),
            pulse == 0 ? null : (int)pulse, speedUnit == "km/h" ? speed / 3.6 : speed, distance, TimeSpan.FromSeconds(seconds), nativeGear);
    }

    public static int ComputeChecksum(ReadOnlySpan<byte> headerAndData)
    {
        int sum = 0;
        foreach (byte value in headerAndData) sum = (sum + value) % 100;
        return sum;
    }

    private static void WriteChecksum(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int value = ComputeChecksum(source);
        destination[0] = (byte)('0' + value / 10);
        destination[1] = (byte)('0' + value % 10);
    }

    private static int? ParseNativeGear(ReadOnlySpan<byte> statuses)
    {
        // SP-KOMM-PM1 specifies gear+1, device-on, and cadence-status+1 as raw bytes.
        // A gear byte can itself be GS or ETB, so these bytes cannot be naively split.
        ReadOnlySpan<byte> raw = statuses;
        if (raw.Length == 6 && raw[^1] == GroupSeparator) raw = raw[..^1];
        if (raw.Length == 5 && raw[1] == GroupSeparator && raw[3] == GroupSeparator &&
            raw[0] <= 29 && raw[2] <= 1 && raw[4] <= 3)
            return raw[0] is >= 2 and <= 29 ? raw[0] - 1 : null;

        // The installed hpc_premium.cdl parses these same three values as ASCII uints.
        if (!statuses.IsEmpty && statuses[^1] == GroupSeparator) statuses = statuses[..^1];
        int first = statuses.IndexOf(GroupSeparator);
        if (first < 0) throw new InvalidDataException("Missing X70 status fields.");
        int secondRelative = statuses[(first + 1)..].IndexOf(GroupSeparator);
        if (secondRelative < 0) throw new InvalidDataException("Missing X70 cadence status.");
        int second = first + 1 + secondRelative;
        uint gear = ParseUnsigned(statuses[..first], "gear status");
        uint on = ParseUnsigned(statuses[(first + 1)..second], "device status");
        uint cadence = ParseUnsigned(statuses[(second + 1)..], "cadence status");
        // Independent premium implementations use cadence-status 0 for OK, whereas
        // SP-KOMM-PM1 documents status+1. These unused enums must not reject real samples.
        if (gear > byte.MaxValue || on > byte.MaxValue || cadence > byte.MaxValue)
            throw new InvalidDataException("X70 contains an invalid status value.");
        // X70 transmits gear+1: 1 means gear shifting is off; 2..29 represent native gears 1..28.
        return gear is >= 2 and <= 29 ? (int)gear - 1 : null;
    }

    private static uint ParseUnsigned(ReadOnlySpan<byte> value, string name)
    {
        if (value.IsEmpty || value.ContainsAnyExceptInRange((byte)'0', (byte)'9') ||
            !uint.TryParse(Encoding.ASCII.GetString(value), NumberStyles.None, CultureInfo.InvariantCulture, out uint result))
            throw new InvalidDataException($"Invalid X70 {name}.");
        return result;
    }

    private static double ParseFloat(ReadOnlySpan<byte> value, string name, bool nonNegative)
    {
        if (value.IsEmpty || value.ContainsAnyExceptInRange((byte)0x20, (byte)0x7e) ||
            !double.TryParse(Encoding.ASCII.GetString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ||
            !double.IsFinite(result) || (nonNegative && result < 0))
            throw new InvalidDataException($"Invalid X70 {name}.");
        return result;
    }
}

public sealed class DaumChecksumException(string message) : IOException(message);

public sealed class DaumNegativeAcknowledgementException(string message) : IOException(message);
