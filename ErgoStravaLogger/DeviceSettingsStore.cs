using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ErgoStravaLogger;

/// <summary>Stores device settings in the writable data volume, including for read-only Docker configs.</summary>
public static class DeviceSettingsStore
{
    private const string FileName = "device-settings.json";

    public static string NormalizeIp(string ip)
    {
        var parts = (ip ?? "").Trim().Split('.');
        if (parts.Length != 4) throw InvalidIp();
        var bytes = new byte[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length is < 1 or > 3 || parts[index].Any(character => character is < '0' or > '9') ||
                !byte.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out bytes[index]))
                throw InvalidIp();
        }
        return new IPAddress(bytes).ToString();
    }

    public static void Apply(LoggerConfig config)
    {
        var path = Path.Combine(config.OutputDirectory, FileName);
        if (!File.Exists(path)) return;
        try
        {
            var settings = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(path))
                ?? throw new JsonException("Leere Geräteeinstellungen.");
            config.ErgoIp = NormalizeIp(settings.Ip);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidDataException("Ungültige Ergometer-IP in device-settings.json.", ex);
        }
    }

    public static async Task SaveAsync(string outputDirectory, string ip, CancellationToken ct = default)
    {
        var normalized = NormalizeIp(ip);
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, FileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, new SavedSettings(normalized), cancellationToken: ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static ArgumentException InvalidIp() => new("Bitte eine gültige IPv4-Adresse eingeben, zum Beispiel 192.168.0.50.", "ip");
    private sealed record SavedSettings(string Ip);
}
