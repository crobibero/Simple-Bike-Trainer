using SimpleBikeTrainer.Devices;

namespace SimpleBikeTrainer.Ble;

public enum DeviceKind
{
    Other,
    Trainer,
    HeartRate,
    Shifter,
}

public sealed record DiscoveredDevice(IDeviceLink Link, DeviceId Id, string Name, DeviceKind Kind, short? Rssi);

public interface IDeviceLink
{
    /// <summary>Does not connect: the result's ConnectAsync does.</summary>
    IConnectableDevice Open(DiscoveredDevice found);
}
