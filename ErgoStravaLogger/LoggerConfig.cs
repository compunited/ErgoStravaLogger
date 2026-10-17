using System.Text.Json;

namespace ErgoStravaLogger;

public sealed class LoggerConfig
{
    public string ErgoIp { get; set; } = "192.168.0.50";
    public int ErgoPort { get; set; } = 51955;
    public int LoggingIntervalMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 5000;
    public int ReconnectDelaySeconds { get; set; } = 5;
    public int PauseSeconds { get; set; } = 15;
    public int IdleSeconds { get; set; } = 0;
    public int MinimumTrainingSeconds { get; set; } = 30;
    public string OutputDirectory { get; set; } = "data";
    public bool TraceProtocol { get; set; }
    public bool SynchronizeOnConnect { get; set; } = true;
    public string SpeedUnit { get; set; } = "m/s";
    public bool RecordingEnabled { get; set; } = true;
    public string ListenUrl { get; set; } = "http://0.0.0.0:8080";
    public string PublicBaseUrl { get; set; } = "";
    public StravaOptions Strava { get; set; } = new();
    public ZwiftBridgeOptions Zwift { get; set; } = new();

    public static LoggerConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<LoggerConfig>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        }) ?? throw new InvalidDataException("Die Konfiguration ist leer.");
        // Secrets can be supplied by Docker env_file without putting them in the source tree.
        if (config.Strava is null) throw new InvalidDataException("Strava muss gesetzt sein.");
        if (config.Zwift is null) throw new InvalidDataException("Zwift muss gesetzt sein.");
        config.ErgoIp = Env("ERGO_IP", config.ErgoIp);
        config.ListenUrl = Env("LISTEN_URL", config.ListenUrl);
        config.PublicBaseUrl = Env("PUBLIC_BASE_URL", config.PublicBaseUrl).TrimEnd('/');
        config.OutputDirectory = Env("OUTPUT_DIRECTORY", config.OutputDirectory);
        config.Strava.ClientId = Env("STRAVA_CLIENT_ID", config.Strava.ClientId);
        config.Strava.ClientSecret = Env("STRAVA_CLIENT_SECRET", config.Strava.ClientSecret);
        config.Strava.RefreshToken = Env("STRAVA_REFRESH_TOKEN", config.Strava.RefreshToken);
        config.Strava.AccessToken = Env("STRAVA_ACCESS_TOKEN", config.Strava.AccessToken);
        if (bool.TryParse(Environment.GetEnvironmentVariable("STRAVA_ENABLED"), out var enabled))
            config.Strava.Enabled = enabled;

        if (string.IsNullOrWhiteSpace(config.ErgoIp) || config.ErgoPort is < 1 or > 65535)
            throw new InvalidDataException("ErgoIp und ErgoPort müssen gültig sein.");
        if (config.LoggingIntervalMs is < 200 or > 60000 || config.TimeoutMs is < 100 or > 60000)
            throw new InvalidDataException("LoggingIntervalMs: 200–60000, TimeoutMs: 100–60000.");
        if (config.ReconnectDelaySeconds < 1 || config.PauseSeconds < 1 || config.IdleSeconds < 0 ||
            (config.IdleSeconds > 0 && config.IdleSeconds < config.PauseSeconds) || config.MinimumTrainingSeconds < 0)
            throw new InvalidDataException("Ungültige Zeitangaben in der Konfiguration.");
        if (string.IsNullOrWhiteSpace(config.OutputDirectory) || config.Strava is null)
            throw new InvalidDataException("OutputDirectory und Strava müssen gesetzt sein.");
        if (config.SpeedUnit is not ("m/s" or "km/h"))
            throw new InvalidDataException("SpeedUnit muss m/s oder km/h sein.");
        if (config.Zwift.Port is < 1 or > 65535 || config.Zwift.StaleAfterSeconds is < 1 or > 60 ||
            !double.IsFinite(config.Zwift.RiderWeightKg) || config.Zwift.RiderWeightKg is < 30 or > 250 ||
            config.Zwift.MaxPowerWatts is < 20 or > 1000 || config.Zwift.VirtualGear is < 1 or > 24 ||
            string.IsNullOrWhiteSpace(config.Zwift.DeviceName) || config.Zwift.DeviceName.Any(char.IsControl) ||
            System.Text.Encoding.UTF8.GetByteCount(config.Zwift.DeviceName) > 40 ||
            !System.Net.IPAddress.TryParse(config.Zwift.BindAddress, out var bridgeAddress) ||
            bridgeAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidDataException("Ungültige Zwift-Bridge-Konfiguration (IPv4, Port, Gerätename oder Zeitlimit).");
        // Relative paths refer to the selected config, independent of the working directory.
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        config.OutputDirectory = Path.GetFullPath(config.OutputDirectory, configDirectory);
        config.Strava.TokenFile = Path.GetFullPath(config.Strava.TokenFile, config.OutputDirectory);
        // A choice saved in the UI survives restarts and takes precedence over startup defaults/env.
        DeviceSettingsStore.Apply(config);
        ZwiftBridgeSettingsStore.Apply(config);
        if (!double.IsFinite(config.Zwift.RiderWeightKg) || config.Zwift.RiderWeightKg is < 30 or > 250 ||
            config.Zwift.MaxPowerWatts is < 20 or > 1000 || config.Zwift.VirtualGear is < 1 or > 24)
            throw new InvalidDataException("Ungültiges Fahrergewicht, Leistungsgrenze oder virtueller Gang in zwift-settings.json.");
        return config;
    }

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;
}
