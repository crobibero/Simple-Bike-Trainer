using SimpleBikeTrainer.Devices;

namespace SimpleBikeTrainer.Ble;

/// <summary>
/// A Zwift Click (v1 or V2 puck). After the "RideOn" handshake it sends unencrypted button notifications.
/// A Click V2's left puck stays locked to the Zwift app, so only its right puck may work here.
/// </summary>
public sealed class ZwiftClick(BlePeripheral peripheral, string name, DeviceId id) : BleConnection(peripheral, name, id), IShifter
{
    private readonly ZwiftClickDecoder _decoder = new();

    public event Action<ClickButtons>? ButtonsPressed;

    /// <summary>The level the Click's own messages report, or the Battery Service's before the first one.</summary>
    public override int? Battery => _decoder.Battery ?? base.Battery;

    protected override async Task SetupAsync(CancellationToken cancellationToken)
    {
        var service = await HasServiceAsync(GattUuids.ZwiftRideService) ? GattUuids.ZwiftRideService : GattUuids.ZwiftService;
        var async = await GetCharacteristicAsync(service, GattUuids.ZwiftAsync);
        var syncTx = await GetCharacteristicAsync(service, GattUuids.ZwiftSyncTx);
        var syncRx = await GetCharacteristicAsync(service, GattUuids.ZwiftSyncRx);

        await SubscribeAsync(async, OnMessage);
        await SubscribeAsync(syncTx, OnMessage);

        await WriteAsync(syncRx, ZwiftClickDecoder.RideOn, withResponse: !syncRx.CanWriteWithoutResponse);
    }

    private void OnMessage(byte[] bytes)
    {
        ClickButtons pressed;
        lock (_decoder)
        {
            pressed = _decoder.Decode(bytes);
        }

        if (pressed != ClickButtons.None)
        {
            ButtonsPressed?.Invoke(pressed);
        }
    }
}
