namespace SimpleBikeTrainer.Devices;

/// <summary>A reading the rider chooses a device for.</summary>
public enum Metric
{
    Power,
    Cadence,
    HeartRate,
}

/// <summary>Which device a ride takes a reading from.</summary>
public enum MetricSource
{
    /// <summary>The first device in <see cref="MetricSources.AutoOrder"/> that has a fresh reading.</summary>
    Auto,
    Trainer,
    PowerMeter,
    CadenceSensor,
    HeartRateMonitor,

    /// <summary>No reading at all. Only heart rate offers it: gears and auto-pause need power and cadence.</summary>
    Off,
}

public static class MetricSources
{
    /// <summary>The sources the rider can pick for <paramref name="metric"/>, Auto first.</summary>
    public static IReadOnlyList<MetricSource> Choices(Metric metric) => metric switch
    {
        Metric.Power => [MetricSource.Auto, MetricSource.Trainer, MetricSource.PowerMeter],
        Metric.Cadence => [MetricSource.Auto, MetricSource.Trainer, MetricSource.PowerMeter, MetricSource.CadenceSensor],
        _ => [MetricSource.Auto, MetricSource.HeartRateMonitor, MetricSource.Trainer, MetricSource.Off],
    };

    /// <summary>What Auto tries, best first: a dedicated sensor before the trainer's own reading.</summary>
    public static IReadOnlyList<MetricSource> AutoOrder(Metric metric) => metric switch
    {
        Metric.Power => [MetricSource.PowerMeter, MetricSource.Trainer],
        Metric.Cadence => [MetricSource.CadenceSensor, MetricSource.PowerMeter, MetricSource.Trainer],
        _ => [MetricSource.HeartRateMonitor, MetricSource.Trainer],
    };

    /// <summary>The devices to take <paramref name="metric"/> from, in order. A choice that cannot give it, such as Off, gives none.</summary>
    public static IReadOnlyList<MetricSource> Order(Metric metric, MetricSource chosen) =>
        chosen == MetricSource.Auto ? AutoOrder(metric)
        : AutoOrder(metric).Contains(chosen) ? [chosen]
        : [];

    /// <summary>The first source in <see cref="Order"/> that <paramref name="read"/> has a value for; null when none has.</summary>
    public static (MetricSource Source, T Value)? Resolve<T>(Metric metric, MetricSource chosen, Func<MetricSource, T?> read)
        where T : struct
    {
        foreach (var source in Order(metric, chosen))
        {
            if (read(source) is { } value)
            {
                return (source, value);
            }
        }

        return null;
    }

    /// <summary>The device kind that is the source, for finding it among the connected devices.</summary>
    public static Ble.DeviceKind? KindOf(MetricSource source) => source switch
    {
        MetricSource.Trainer => Ble.DeviceKind.Trainer,
        MetricSource.PowerMeter => Ble.DeviceKind.PowerMeter,
        MetricSource.CadenceSensor => Ble.DeviceKind.CadenceSensor,
        MetricSource.HeartRateMonitor => Ble.DeviceKind.HeartRate,
        _ => null,
    };
}
