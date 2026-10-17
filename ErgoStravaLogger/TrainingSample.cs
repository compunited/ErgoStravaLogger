namespace ErgoStravaLogger;

public sealed record TrainingSample(
    DateTimeOffset Timestamp,
    int PowerWatts,
    int CadenceRpm,
    int? HeartRateBpm,
    double SpeedMetersPerSecond,
    double DistanceMeters,
    bool StartsNewTrack = false);
