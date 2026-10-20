using SimpleBikeTrainer.Devices;

namespace SimpleBikeTrainer.Ble;

/// <summary>A Bluetooth power meter (Cycling Power Service): power, and cadence when it sends crank revolutions.</summary>
public sealed class BlePowerMeter(BlePeripheral peripheral, string name, DeviceId id) : BleConnection(peripheral, name, id), ICyclingSensor
{
    private readonly CrankCadence _cadence = new();

    public event Action<SensorReading>? ReadingReceived;

    protected override async Task SetupAsync(CancellationToken cancellationToken)
    {
        var measurement = await GetCharacteristicAsync(GattUuids.CyclingPower, GattUuids.CyclingPowerMeasurement);
        await SubscribeAsync(measurement, bytes =>
        {
            if (CyclingSensors.ParsePowerMeasurement(bytes) is not { } m)
            {
                return;
            }

            double? cadence = null;
            if (m.Crank is { } crank)
            {
                lock (_cadence)
                {
                    cadence = _cadence.Update(crank, Environment.TickCount64);
                }
            }

            ReadingReceived?.Invoke(new SensorReading(m.PowerWatts, cadence));
        });
    }
}

/// <summary>A Bluetooth cadence sensor, or speed and cadence sensor (Cycling Speed and Cadence Service).</summary>
public sealed class BleCadenceSensor(BlePeripheral peripheral, string name, DeviceId id) : BleConnection(peripheral, name, id), ICyclingSensor
{
    private readonly CrankCadence _cadence = new();

    public event Action<SensorReading>? ReadingReceived;

    protected override async Task SetupAsync(CancellationToken cancellationToken)
    {
        var measurement = await GetCharacteristicAsync(GattUuids.CyclingSpeedCadence, GattUuids.CscMeasurement);
        await SubscribeAsync(measurement, bytes =>
        {
            if (CyclingSensors.ParseCscMeasurement(bytes) is not { } crank)
            {
                return;
            }

            double? cadence;
            lock (_cadence)
            {
                cadence = _cadence.Update(crank, Environment.TickCount64);
            }

            ReadingReceived?.Invoke(new SensorReading(null, cadence));
        });
    }
}
