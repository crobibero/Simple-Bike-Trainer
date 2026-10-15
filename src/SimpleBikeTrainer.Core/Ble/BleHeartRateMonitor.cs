using SimpleBikeTrainer.Devices;

namespace SimpleBikeTrainer.Ble;

/// <summary>A BLE heart rate strap, or a watch broadcasting heart rate (Forerunner: Broadcast Heart Rate).</summary>
public sealed class BleHeartRateMonitor(BlePeripheral peripheral, string name, DeviceId id) : BleConnection(peripheral, name, id), IHeartRateMonitor
{
    public event Action<int>? HeartRateReceived;

    protected override async Task SetupAsync(CancellationToken cancellationToken)
    {
        var measurement = await GetCharacteristicAsync(GattUuids.HeartRate, GattUuids.HeartRateMeasurement);
        await SubscribeAsync(measurement, bytes =>
        {
            if (HeartRateParser.Parse(bytes) is { } bpm and > 0)
            {
                HeartRateReceived?.Invoke(bpm);
            }
        });
    }
}
