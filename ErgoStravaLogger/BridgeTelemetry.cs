namespace ErgoStravaLogger;

/// <summary>A single atomic snapshot of the already-polled ergometer connection.</summary>
public record BridgeTelemetry(DeviceSample? Sample, DateTimeOffset? Timestamp, bool Connected);
