using System.Globalization;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Ble;

public enum DeviceTransport
{
    Bluetooth,
    AntPlus,
    Wifi,
}

/// <summary>Addresses compare ignoring case, as systems spell Bluetooth addresses in either.</summary>
public sealed class DeviceId(DeviceTransport transport, string address) : IEquatable<DeviceId>
{
    public DeviceTransport Transport { get; } = transport;

    /// <summary>On macOS, the identifier CoreBluetooth assigns the device: it does not expose the MAC.</summary>
    public string Address { get; } = address;

    public string TransportLabel => Transport switch
    {
        DeviceTransport.AntPlus => CoreStrings.Transport_AntPlus,
        DeviceTransport.Wifi => CoreStrings.Transport_Wifi,
        _ => CoreStrings.Transport_Bluetooth,
    };

    public static DeviceId Bluetooth(string address) => new(DeviceTransport.Bluetooth, address);

    public static DeviceId AntPlus(int deviceNumber) => new(DeviceTransport.AntPlus, deviceNumber.ToString(CultureInfo.InvariantCulture));

    /// <summary>A Wi-Fi trainer, by its MAC address or by "host:port".</summary>
    public static DeviceId Wifi(string address) => new(DeviceTransport.Wifi, address);

    public static bool operator ==(DeviceId? left, DeviceId? right) => left?.Equals(right) ?? right is null;

    public static bool operator !=(DeviceId? left, DeviceId? right) => !(left == right);

    public override string ToString() => $"{TransportLabel} {Address}";

    public bool Equals(DeviceId? other) =>
        other is not null && Transport == other.Transport && string.Equals(Address, other.Address, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => Equals(obj as DeviceId);

    public override int GetHashCode() => HashCode.Combine(Transport, StringComparer.OrdinalIgnoreCase.GetHashCode(Address));
}
