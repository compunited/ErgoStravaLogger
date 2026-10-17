using System.Security.Cryptography;
using System.Text.Json;

namespace ErgoStravaLogger;

public static class ZwiftBridgeSettingsStore
{
    private const string FileName = "zwift-settings.json";

    public static void Apply(LoggerConfig config)
    {
        var path = Path.Combine(config.OutputDirectory, FileName);
        if (File.Exists(path))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(path))
                    ?? throw new JsonException("Leere Zwift-Einstellungen.");
                config.Zwift.Enabled = saved.Enabled;
                config.Zwift.Identity = saved.Identity;
                if (saved.ControlEnabled.HasValue) config.Zwift.ControlEnabled = saved.ControlEnabled.Value;
                if (saved.RiderWeightKg.HasValue) config.Zwift.RiderWeightKg = saved.RiderWeightKg.Value;
                if (saved.MaxPowerWatts.HasValue) config.Zwift.MaxPowerWatts = saved.MaxPowerWatts.Value;
                if (saved.VirtualGear.HasValue) config.Zwift.VirtualGear = saved.VirtualGear.Value;
            }
            catch (JsonException ex) { throw new InvalidDataException("Ungültige zwift-settings.json.", ex); }
        }
        EnsureIdentity(config.Zwift);
    }

    public static void EnsureIdentity(ZwiftBridgeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Identity))
        {
            var bytes = RandomNumberGenerator.GetBytes(6);
            bytes[0] = (byte)((bytes[0] & 0xfe) | 0x02);
            options.Identity = Convert.ToHexString(bytes);
        }
        if (options.Identity.Length != 12 || !options.Identity.All(Uri.IsHexDigit))
            throw new InvalidDataException("Die Zwift-Gerätekennung muss aus zwölf Hex-Zeichen bestehen.");
        options.Identity = options.Identity.ToUpperInvariant();
    }

    public static async Task SaveAsync(string directory, bool enabled, string identity, CancellationToken ct = default)
        => await SaveCoreAsync(directory, new SavedSettings(enabled, identity), ct);

    public static async Task SaveAsync(string directory, ZwiftBridgeOptions options, CancellationToken ct = default)
        => await SaveCoreAsync(directory, new SavedSettings(options.Enabled, options.Identity,
            options.ControlEnabled, options.RiderWeightKg, options.MaxPowerWatts, options.VirtualGear), ct);

    public static async Task SaveGearAsync(string directory, ZwiftBridgeOptions options, int virtualGear, CancellationToken ct = default)
        => await SaveCoreAsync(directory, new SavedSettings(options.Enabled, options.Identity,
            options.ControlEnabled, options.RiderWeightKg, options.MaxPowerWatts, virtualGear), ct);

    private static async Task SaveCoreAsync(string directory, SavedSettings settings, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, settings, cancellationToken: ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record SavedSettings(bool Enabled, string Identity, bool? ControlEnabled = null,
        double? RiderWeightKg = null, int? MaxPowerWatts = null, int? VirtualGear = null);
}
