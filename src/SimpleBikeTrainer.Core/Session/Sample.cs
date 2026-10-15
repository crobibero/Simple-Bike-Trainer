namespace SimpleBikeTrainer.Session;

/// <summary>One recorded second of riding.</summary>
public readonly record struct Sample(
    DateTime Time,
    int Power,
    int? Cadence,
    int? HeartRate,
    double SpeedMs,
    double DistanceMeters,
    int? TargetWatts,
    double? AltitudeMeters = null);
