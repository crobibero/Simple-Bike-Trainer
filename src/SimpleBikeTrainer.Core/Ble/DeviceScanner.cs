using SimpleBikeTrainer.AntPlus;
using SimpleBikeTrainer.Dircon;

namespace SimpleBikeTrainer.Ble;

/// <summary>Any one of Bluetooth, the ANT+ stick and the network is enough; what kept another out is in <see cref="Warnings"/>.</summary>
public sealed class DeviceScanner : IDeviceScanner
{
    private readonly IReadOnlyList<IDeviceScanner> _scanners;

    private DeviceScanner(IReadOnlyList<IDeviceScanner> scanners, IReadOnlyList<string> warnings)
    {
        _scanners = scanners;
        Warnings = warnings;
        foreach (var scanner in scanners)
        {
            scanner.DeviceFound += OnFound;
        }
    }

    public event Action<DiscoveredDevice>? DeviceFound;

    /// <summary>Why Bluetooth, or an ANT+ stick that is plugged in, is not part of the scan.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<DiscoveredDevice> Found => [.. _scanners.SelectMany(s => s.Found)];

    /// <summary>Throws Bluetooth's error when there is neither Bluetooth nor a stick, and no Wi-Fi trainer was typed in.</summary>
    public static async Task<DeviceScanner> CreateAsync(DevicesConfig devices, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        BleScanner? ble = null;
        Exception? bleError = null;
        try
        {
            ble = await BleScanner.CreateAsync(devices.Adapter);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            bleError = e;
        }

        AntScanner? ant = null;
        try
        {
            ant = await AntScanner.CreateAsync(cancellationToken);
        }
        catch (AntException e)
        {
            warnings.Add(e.Message);
        }
        catch
        {
            if (ble is not null)
            {
                await ble.DisposeAsync();
            }

            throw;
        }

        // A Wi-Fi trainer found by mDNS alone is not worth hiding Bluetooth's error for, unless the rider set one up.
        if (ble is null && ant is null && devices.WifiTrainers.Count == 0)
        {
            throw bleError!;
        }

        if (bleError is not null)
        {
            warnings.Insert(0, bleError.Message);
        }

        IDeviceScanner?[] scanners = [ble, ant, new DirconScanner(devices.WifiTrainers)];
        return new DeviceScanner([.. scanners.OfType<IDeviceScanner>()], warnings);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (var scanner in _scanners)
        {
            await scanner.StartAsync(cancellationToken);
        }
    }

    public async Task StopAsync()
    {
        foreach (var scanner in _scanners)
        {
            await scanner.StopAsync();
        }
    }

    public async Task WaitForAsync(Func<IReadOnlyList<DiscoveredDevice>, bool> done, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !done(Found))
        {
            await Task.Delay(250, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scanner in _scanners)
        {
            scanner.DeviceFound -= OnFound;
            await scanner.DisposeAsync();
        }
    }

    private void OnFound(DiscoveredDevice device) => DeviceFound?.Invoke(device);
}
