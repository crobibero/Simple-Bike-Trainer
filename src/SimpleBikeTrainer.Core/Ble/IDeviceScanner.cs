namespace SimpleBikeTrainer.Ble;

public interface IDeviceScanner : IAsyncDisposable
{
    event Action<DiscoveredDevice>? DeviceFound;

    IReadOnlyList<DiscoveredDevice> Found { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}
