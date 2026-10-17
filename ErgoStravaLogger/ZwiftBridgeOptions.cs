namespace ErgoStravaLogger;

public sealed class ZwiftBridgeOptions
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 36866;
    public string DeviceName { get; set; } = "Daum 8i Bridge";
    public string BindAddress { get; set; } = "0.0.0.0";
    public string Identity { get; set; } = "";
    public int StaleAfterSeconds { get; set; } = 5;
    public bool ControlEnabled { get; set; }
    public double RiderWeightKg { get; set; } = 75;
    public int MaxPowerWatts { get; set; } = 300;
    public int VirtualGear { get; set; } = 12;
}

public interface IBridgeDiscovery : IAsyncDisposable
{
    bool IsRunning { get; }
    string? LastError { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}

public sealed record ZwiftBridgeStatus(bool Enabled, bool Active, string DeviceName, int Port,
    int Clients, bool DataAvailable, string? LastError, bool Controllable = false,
    bool ControlEnabled = false, double RiderWeightKg = 75, int MaxPowerWatts = 300,
    string ControlMode = "idle", int? TargetPowerWatts = null, string? ControlError = null,
    int VirtualGear = 12, double? SimulationGradePercent = null, double? SimulationSpeedKmh = null,
    int? SimulationDesiredPowerWatts = null, bool SimulationLimited = false,
    int? CockpitGear = null, string? CockpitShiftError = null);
