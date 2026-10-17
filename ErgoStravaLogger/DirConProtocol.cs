using System.Buffers.Binary;

namespace ErgoStravaLogger;

public record DirConFrame(byte Version, byte MessageType, byte Sequence, byte ResponseCode, byte[] Data);
public record DirConCharacteristic(Guid Uuid, byte Properties);
public record BridgeMeasurements(byte[] CyclingPower, byte[] IndoorBikeData, byte[]? HeartRate);

/// <summary>
/// Independently implemented WFTNP framing. UUIDs use network order; characteristic values use BLE little endian.
/// Wire reference: https://github.com/elfrances/wahoo-fitness-tnp (protocol description, no implementation copied).
/// </summary>
public static class DirConProtocol
{
    public const byte Version = 1;
    public const int HeaderLength = 6;
    public const int MaximumDataLength = 1024;
    public const byte Read = 1, Write = 2, Notify = 4;
    public const byte Success = 0, InvalidMessageType = 1, GenericError = 2,
        ServiceNotFound = 3, CharacteristicNotFound = 4, OperationNotSupported = 5, WriteFailed = 6;

    public static readonly Guid CyclingPowerService = BluetoothUuid(0x1818);
    public static readonly Guid FitnessMachineService = BluetoothUuid(0x1826);
    public static readonly Guid HeartRateService = BluetoothUuid(0x180d);
    public static readonly Guid DeviceInformationService = BluetoothUuid(0x180a);
    public static readonly Guid GenericAccessService = BluetoothUuid(0x1800);
    public static readonly Guid CyclingPowerMeasurement = BluetoothUuid(0x2a63);
    public static readonly Guid CyclingPowerFeature = BluetoothUuid(0x2a65);
    public static readonly Guid SensorLocation = BluetoothUuid(0x2a5d);
    public static readonly Guid FitnessMachineFeature = BluetoothUuid(0x2acc);
    public static readonly Guid IndoorBikeData = BluetoothUuid(0x2ad2);
    public static readonly Guid FitnessMachineControlPoint = BluetoothUuid(0x2ad9);
    public static readonly Guid FitnessMachineStatus = BluetoothUuid(0x2ada);
    public static readonly Guid SupportedPowerRange = BluetoothUuid(0x2ad8);
    public static readonly Guid HeartRateMeasurement = BluetoothUuid(0x2a37);
    public static readonly Guid DeviceName = BluetoothUuid(0x2a00);
    public static readonly Guid Appearance = BluetoothUuid(0x2a01);
    public static readonly Guid ManufacturerName = BluetoothUuid(0x2a29);
    public static readonly Guid ModelNumber = BluetoothUuid(0x2a24);
    public static readonly Guid SerialNumber = BluetoothUuid(0x2a25);
    public static readonly Guid SoftwareRevision = BluetoothUuid(0x2a28);

    private static readonly IReadOnlyDictionary<Guid, IReadOnlyList<DirConCharacteristic>> Services =
        new Dictionary<Guid, IReadOnlyList<DirConCharacteristic>>
        {
            [CyclingPowerService] = Array.AsReadOnly(new[]
            {
                new DirConCharacteristic(CyclingPowerMeasurement, Notify),
                new DirConCharacteristic(CyclingPowerFeature, Read),
                new DirConCharacteristic(SensorLocation, Read)
            }),
            [FitnessMachineService] = Array.AsReadOnly(new[]
            {
                new DirConCharacteristic(FitnessMachineFeature, Read), new DirConCharacteristic(IndoorBikeData, Notify)
            }),
            [HeartRateService] = Array.AsReadOnly(new[] { new DirConCharacteristic(HeartRateMeasurement, Notify) }),
            [DeviceInformationService] = Array.AsReadOnly(new[]
            {
                new DirConCharacteristic(ManufacturerName, Read), new DirConCharacteristic(ModelNumber, Read),
                new DirConCharacteristic(SerialNumber, Read), new DirConCharacteristic(SoftwareRevision, Read)
            }),
            [GenericAccessService] = Array.AsReadOnly(new[]
            {
                new DirConCharacteristic(DeviceName, Read), new DirConCharacteristic(Appearance, Read)
            })
        };

    public static IReadOnlyList<Guid> ServiceUuids { get; } = Array.AsReadOnly(Services.Keys.ToArray());
    private static readonly IReadOnlyList<DirConCharacteristic> ControllableFitnessCharacteristics = Array.AsReadOnly(
        Services[FitnessMachineService].Concat(new[]
        {
            // BLE indications map to NOTIFY in the reliable TCP transport.
            new DirConCharacteristic(FitnessMachineControlPoint, Write | Notify),
            new DirConCharacteristic(FitnessMachineStatus, Notify),
            new DirConCharacteristic(SupportedPowerRange, Read)
        }).ToArray());
    public static IReadOnlyList<DirConCharacteristic>? Characteristics(Guid service) => Services.GetValueOrDefault(service);
    public static IReadOnlyList<DirConCharacteristic>? Characteristics(Guid service, bool controlEnabled) =>
        controlEnabled && service == FitnessMachineService ? ControllableFitnessCharacteristics : Characteristics(service);
    public static DirConCharacteristic? Characteristic(Guid uuid) => Services.Values.SelectMany(c => c).FirstOrDefault(c => c.Uuid == uuid);
    public static DirConCharacteristic? Characteristic(Guid uuid, bool controlEnabled) => controlEnabled
        ? ControllableFitnessCharacteristics.FirstOrDefault(c => c.Uuid == uuid) ?? Characteristic(uuid) : Characteristic(uuid);
    public static Guid BluetoothUuid(ushort uuid) => Guid.Parse($"0000{uuid:x4}-0000-1000-8000-00805f9b34fb");
    public static byte[] UuidBytes(Guid uuid) => uuid.ToByteArray(bigEndian: true);
    public static Guid ReadUuid(ReadOnlySpan<byte> data) => data.Length == 16
        ? new Guid(data, bigEndian: true) : throw new InvalidDataException("A DirCon UUID must contain 16 bytes.");

    public static byte[] Encode(byte messageType, byte sequence, byte responseCode, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaximumDataLength) throw new InvalidDataException("DirCon payload exceeds the supported frame limit.");
        byte[] result = new byte[HeaderLength + data.Length];
        result[0] = Version;
        result[1] = messageType;
        result[2] = sequence;
        result[3] = responseCode;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4, 2), (ushort)data.Length);
        data.CopyTo(result.AsSpan(HeaderLength));
        return result;
    }

    public static DirConFrame Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength) throw new InvalidDataException("Incomplete DirCon header.");
        int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4, 2));
        if (length > MaximumDataLength || data.Length != HeaderLength + length)
            throw new InvalidDataException("Invalid DirCon frame length.");
        return new DirConFrame(data[0], data[1], data[2], data[3], data[HeaderLength..].ToArray());
    }
}

/// <summary>
/// Encodes standard CPS, FTMS and HR characteristic values. Kurbel events are reconstructed from measured RPM,
/// since the Daum does not expose revolution counters. Time is monotonic and the BLE 16-bit counters wrap.
/// References: Bluetooth CPS 1.1 §3.2.1.6, FTMS 1.0 §4.3 / §4.9, HRS 1.0 §3.1.
/// </summary>
public sealed class DirConMeasurementEncoder(int staleAfterSeconds = 5)
{
    private double? _previousSeconds;
    private double _fractionalRevolutions;
    private ushort _crankRevolutions;
    private ushort _lastCrankEvent;
    private int _previousCadence;

    public BridgeMeasurements Encode(BridgeTelemetry telemetry, DateTimeOffset utcNow, double monotonicSeconds)
    {
        var sample = telemetry.Sample;
        bool fresh = telemetry.Connected && sample is not null && telemetry.Timestamp is { } timestamp &&
            utcNow >= timestamp && utcNow - timestamp <= TimeSpan.FromSeconds(Math.Max(1, staleAfterSeconds));
        int cadence = fresh ? Math.Clamp(sample!.CadenceRpm, 0, ushort.MaxValue / 2) : 0;
        // Daum can report its configured target wattage even when nobody is pedalling.
        int power = cadence > 0 ? Math.Clamp(sample!.PowerWatts, 0, short.MaxValue) : 0;
        double speed = cadence > 0 && double.IsFinite(sample!.SpeedMetersPerSecond)
            ? Math.Clamp(sample.SpeedMetersPerSecond * 360, 0, ushort.MaxValue) : 0;
        int? heartRate = fresh && sample!.HeartRateBpm is > 0 and <= byte.MaxValue ? sample.HeartRateBpm : null;

        if (_previousSeconds is { } previous && cadence > 0 && _previousCadence > 0)
        {
            double elapsed = Math.Clamp(monotonicSeconds - previous, 0, Math.Max(1, staleAfterSeconds));
            double revolutions = _fractionalRevolutions + elapsed * cadence / 60d;
            int whole = (int)Math.Floor(revolutions);
            _fractionalRevolutions = revolutions - whole;
            if (whole > 0)
            {
                _crankRevolutions = unchecked((ushort)(_crankRevolutions + whole));
                double lastEventSeconds = monotonicSeconds - _fractionalRevolutions * 60d / cadence;
                _lastCrankEvent = unchecked((ushort)(long)Math.Round(Math.Max(0, lastEventSeconds) * 1024));
            }
        }
        if (cadence == 0) _fractionalRevolutions = 0;
        _previousSeconds = monotonicSeconds;
        _previousCadence = cadence;

        byte[] cyclingPower = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(cyclingPower, 1 << 5); // Crank revolution pair present.
        BinaryPrimitives.WriteInt16LittleEndian(cyclingPower.AsSpan(2), (short)power);
        BinaryPrimitives.WriteUInt16LittleEndian(cyclingPower.AsSpan(4), _crankRevolutions);
        BinaryPrimitives.WriteUInt16LittleEndian(cyclingPower.AsSpan(6), _lastCrankEvent);

        byte[] indoorBike = new byte[heartRate.HasValue ? 9 : 8];
        // More Data = 0, so mandatory instantaneous speed is present; cadence, power and optional HR follow.
        BinaryPrimitives.WriteUInt16LittleEndian(indoorBike, (ushort)((1 << 2) | (1 << 6) | (heartRate.HasValue ? 1 << 9 : 0)));
        BinaryPrimitives.WriteUInt16LittleEndian(indoorBike.AsSpan(2), (ushort)Math.Round(speed));
        BinaryPrimitives.WriteUInt16LittleEndian(indoorBike.AsSpan(4), (ushort)(cadence * 2)); // 0.5 RPM.
        BinaryPrimitives.WriteInt16LittleEndian(indoorBike.AsSpan(6), (short)power);
        if (heartRate.HasValue) indoorBike[8] = (byte)heartRate.Value;
        return new BridgeMeasurements(cyclingPower, indoorBike,
            heartRate.HasValue ? new byte[] { 0, (byte)heartRate.Value } : null);
    }
}
