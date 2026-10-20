using SimpleBikeTrainer.Ble;

namespace SimpleBikeTrainer.Devices;

public abstract class DeviceConnection : IConnectableDevice
{
    private volatile int _battery = -1;

    protected DeviceConnection(string name, DeviceId id)
    {
        Name = name;
        Id = id;
    }

    public string Name { get; }

    public DeviceId Id { get; }

    public abstract bool IsConnected { get; }

    public abstract string? Status { get; }

    public virtual int? Battery => _battery < 0 ? null : _battery;

    public abstract Task ConnectAsync(CancellationToken cancellationToken);

    public abstract void ConnectInBackground();

    public abstract ValueTask DisposeAsync();

    protected void SetBattery(int percent) => _battery = Math.Clamp(percent, 0, 100);

    protected virtual Task OnStoppingAsync() => Task.CompletedTask;

    protected async Task TryStopAsync()
    {
        try
        {
            await OnStoppingAsync();
        }
        catch
        {
            // Best effort: the device may already be gone.
        }
    }
}
