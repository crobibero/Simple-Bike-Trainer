using SimpleBikeTrainer.Devices;

namespace SimpleBikeTrainer.Ble;

/// <summary>The devices a ride uses. The trainer is required; the others are there when connected.</summary>
public sealed class ConnectedDevices(ITrainer trainer, IHeartRateMonitor? heartRate, IReadOnlyList<IShifter> shifters) : IAsyncDisposable
{
    public ITrainer Trainer { get; } = trainer;

    public IHeartRateMonitor? HeartRate { get; } = heartRate;

    public IReadOnlyList<IShifter> Shifters { get; } = shifters;

    public ICyclingSensor? PowerMeter { get; init; }

    public ICyclingSensor? CadenceSensor { get; init; }

    public IReadOnlyList<ISensorDevice> All =>
        [Trainer, .. new ISensorDevice?[] { PowerMeter, CadenceSensor, HeartRate }.OfType<ISensorDevice>(), .. Shifters];

    public async ValueTask DisposeAsync()
    {
        foreach (var device in All.Reverse())
        {
            await device.DisposeAsync();
        }
    }
}

/// <summary>
/// The devices connected now: at most one of each kind, but any number of controllers. Unlike
/// <see cref="ConnectedDevices"/> it can be without a trainer, as while devices are being paired one at a time.
/// </summary>
public sealed class DeviceSet : IAsyncDisposable
{
    private readonly List<(DeviceKind Kind, ISensorDevice Device)> _devices = [];

    /// <summary>In <see cref="DeviceKind"/> order; controllers in the order they were added.</summary>
    public IReadOnlyList<(DeviceKind Kind, ISensorDevice Device)> Entries => [.. _devices.OrderBy(d => d.Kind)];

    public IReadOnlyList<ISensorDevice> All => [.. Entries.Select(e => e.Device)];

    public int Count => _devices.Count;

    public ITrainer? Trainer => Get(DeviceKind.Trainer) as ITrainer;

    public IHeartRateMonitor? HeartRate => Get(DeviceKind.HeartRate) as IHeartRateMonitor;

    public ICyclingSensor? PowerMeter => Get(DeviceKind.PowerMeter) as ICyclingSensor;

    public ICyclingSensor? CadenceSensor => Get(DeviceKind.CadenceSensor) as ICyclingSensor;

    public IReadOnlyList<IShifter> Shifters => [.. _devices.Where(d => d.Kind == DeviceKind.Shifter).Select(d => d.Device).OfType<IShifter>()];

    /// <summary>The device of a kind other than controllers.</summary>
    public ISensorDevice? Get(DeviceKind kind) => _devices.FirstOrDefault(d => d.Kind == kind).Device;

    public bool Contains(DeviceId? id) => id is not null && _devices.Any(d => d.Device.Id == id);

    /// <summary>
    /// Adds a device. One of a kind other than controllers replaces the one already there, which comes back still
    /// connected, for the caller to dispose.
    /// </summary>
    public ISensorDevice? Put(DeviceKind kind, ISensorDevice device)
    {
        ISensorDevice? replaced = null;
        var index = _devices.FindIndex(d => (d.Device.Id is { } id && id == device.Id) || (kind != DeviceKind.Shifter && d.Kind == kind));
        if (index >= 0)
        {
            replaced = _devices[index].Device;
            _devices.RemoveAt(index);
        }

        _devices.Add((kind, device));
        return replaced;
    }

    /// <summary>Takes a device out, still connected, for the caller to dispose. Null when it is not here.</summary>
    public ISensorDevice? Remove(DeviceId id)
    {
        var index = _devices.FindIndex(d => d.Device.Id == id);
        if (index < 0)
        {
            return null;
        }

        var device = _devices[index].Device;
        _devices.RemoveAt(index);
        return device;
    }

    /// <summary>The devices for a ride; null without a trainer. The set still owns them.</summary>
    public ConnectedDevices? ForRide() => Trainer is { } trainer
        ? new ConnectedDevices(trainer, HeartRate, Shifters) { PowerMeter = PowerMeter, CadenceSensor = CadenceSensor }
        : null;

    /// <summary>Empties the set without disconnecting anything, once another owner has the devices.</summary>
    public void Clear() => _devices.Clear();

    public async ValueTask DisposeAsync()
    {
        foreach (var device in All.Reverse())
        {
            await device.DisposeAsync();
        }

        _devices.Clear();
    }
}
