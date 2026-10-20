using SimpleBikeTrainer.Devices;

namespace SimpleBikeTrainer.Ble;

/// <summary>Stored by name in the config, so the order here is only the order lists show the kinds in.</summary>
public enum DeviceKind
{
    Other,
    Trainer,
    PowerMeter,

    /// <summary>A cadence sensor, or a combined speed and cadence sensor, of which the app uses the cadence.</summary>
    CadenceSensor,
    HeartRate,
    Shifter,
}

public sealed record DiscoveredDevice(IDeviceLink Link, DeviceId Id, string Name, DeviceKind Kind, short? Rssi);

public interface IDeviceLink
{
    /// <summary>Does not connect: the result's ConnectAsync does.</summary>
    IConnectableDevice Open(DiscoveredDevice found);
}
