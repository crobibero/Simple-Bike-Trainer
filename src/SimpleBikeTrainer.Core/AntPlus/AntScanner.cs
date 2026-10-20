using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.AntPlus;

/// <summary>Scans on a background channel, which leaves the stick's other channels, and their devices, alone.</summary>
public sealed class AntScanner : IDeviceScanner
{
    /// <summary>Devices send about 4 times a second; one report a second is enough for the list.</summary>
    private static readonly long ReportEveryMs = 1000;

    private readonly AntStick _stick;
    private readonly Dictionary<DeviceId, (DiscoveredDevice Device, long Reported)> _found = [];
    private readonly Lock _lock = new();
    private AntChannel? _channel;
    private bool _released;

    private AntScanner(AntStick stick)
    {
        _stick = stick;
    }

    public event Action<DiscoveredDevice>? DeviceFound;

    public IReadOnlyList<DiscoveredDevice> Found
    {
        get
        {
            lock (_lock)
            {
                return [.. _found.Values.Select(f => f.Device)];
            }
        }
    }

    /// <summary>A scanner on the ANT+ stick; null when none is plugged in.</summary>
    public static async Task<AntScanner?> CreateAsync(CancellationToken cancellationToken = default) =>
        await AntStick.AcquireAsync(cancellationToken) is { } stick ? new AntScanner(stick) : null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_channel is null)
        {
            _channel = await _stick.OpenChannelAsync(new AntChannelConfig(0, 0, 0, AntPlusPages.FitnessEquipmentPeriod, BackgroundScan: true), cancellationToken);
            _channel.DataReceived += OnData;
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _channel, null) is { } channel)
        {
            channel.DataReceived -= OnData;
            await channel.CloseAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (!_released)
        {
            _released = true;
            await _stick.ReleaseAsync();
        }
    }

    /// <summary>The kind and name of a device heard on the scan channel; null for ones this app does not use.</summary>
    internal static DiscoveredDevice? Identify(AntReceived received)
    {
        if (received.DeviceNumber is not { } number)
        {
            return null;
        }

        // The top bit of the device type is the pairing bit.
        var type = (byte)(received.DeviceType & 0x7F);
        var (kind, name) = type switch
        {
            AntPlusPages.FitnessEquipmentType => (DeviceKind.Trainer, CoreStrings.Ant_TrainerName),
            AntPlusPages.HeartRateType => (DeviceKind.HeartRate, CoreStrings.Ant_HeartRateName),
            AntPlusPages.BikePowerType => (DeviceKind.PowerMeter, CoreStrings.Ant_PowerName),
            AntPlusPages.BikeCadenceType or AntPlusPages.BikeSpeedCadenceType => (DeviceKind.CadenceSensor, CoreStrings.Ant_CadenceName),
            _ => (DeviceKind.Other, string.Empty),
        };

        return kind == DeviceKind.Other
            ? null
            : new DiscoveredDevice(new AntDeviceLink(number, type, received.TransmissionType), DeviceId.AntPlus(number), Loc.Format(name, number), kind, received.Rssi);
    }

    private void OnData(AntReceived received)
    {
        if (Identify(received) is not { } device)
        {
            return;
        }

        lock (_lock)
        {
            var now = Environment.TickCount64;
            if (_found.TryGetValue(device.Id, out var seen) && now - seen.Reported < ReportEveryMs)
            {
                return;
            }

            _found[device.Id] = (device, now);
        }

        DeviceFound?.Invoke(device);
    }
}
