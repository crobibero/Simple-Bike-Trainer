using Linux.Bluetooth;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Ble;

/// <summary>A KICKR CORE 2, or any FTMS trainer, driven in ERG mode through the Fitness Machine Control Point.</summary>
public sealed class FtmsTrainer(Device device, string name, string address) : BleConnection(device, name, address), ITrainer
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    // BlueZ fails a GATT write that overlaps another one, so control point writes go one at a time.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private GattCharacteristic? _controlPoint;
    private int? _lastTarget;

    public event Action<IndoorBikeData>? DataReceived;

    /// <summary>Raised for each control point indication, for <c>simple-bike-trainer-cli probe --control</c>.</summary>
    public event Action<ControlPointResponse>? ControlPointResponded;

    public string? LastError { get; private set; }

    public bool SupportsSpinDown { get; private set; }

    public event Action<SpinDownUpdate>? SpinDownUpdated;

    public async Task StartSpinDownAsync(CancellationToken cancellationToken = default)
    {
        if (!SupportsSpinDown)
        {
            throw new NotSupportedException(Loc.Format(CoreStrings.Ftms_NoSpinDown, Name));
        }

        await SendAsync(Ftms.StartSpinDown(), cancellationToken);
    }

    public async Task SetTargetPowerAsync(int watts, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return;
        }

        await SendAsync(Ftms.SetTargetPower(watts), cancellationToken);
        _lastTarget = watts;
    }

    protected override async Task SetupAsync(CancellationToken cancellationToken)
    {
        var bikeData = await GetCharacteristicAsync(GattUuids.FitnessMachine, GattUuids.IndoorBikeData);
        _controlPoint = await GetCharacteristicAsync(GattUuids.FitnessMachine, GattUuids.FitnessMachineControlPoint);

        var feature = await GetCharacteristicAsync(GattUuids.FitnessMachine, GattUuids.FitnessMachineFeature);
        SupportsSpinDown = Ftms.SupportsSpinDown(await feature.ReadValueAsync(new Dictionary<string, object>()));
        if (SupportsSpinDown)
        {
            var status = await GetCharacteristicAsync(GattUuids.FitnessMachine, GattUuids.FitnessMachineStatus);
            await SubscribeAsync(status, bytes =>
            {
                if (Ftms.ParseSpinDownStatus(bytes) is { } update)
                {
                    SpinDownUpdated?.Invoke(update);
                }
            });
        }

        // The control point answers with indications, and some trainers refuse writes until they are on.
        await SubscribeAsync(_controlPoint, OnControlPointResponse);
        await SubscribeAsync(bikeData, bytes => DataReceived?.Invoke(Ftms.ParseIndoorBikeData(bytes)));

        await SendAsync(Ftms.RequestControl(), cancellationToken);
        await SendAsync(Ftms.Start(), cancellationToken);

        // After a reconnect, restore the target the ride was holding.
        if (_lastTarget is { } target)
        {
            await SendAsync(Ftms.SetTargetPower(target), cancellationToken);
        }
    }

    protected override async Task OnStoppingAsync()
    {
        if (_controlPoint is not null)
        {
            // Reset hands control back so another app can take the trainer.
            await SendAsync(Ftms.Reset(), CancellationToken.None);
        }
    }

    private async Task SendAsync(byte[] command, CancellationToken cancellationToken)
    {
        if (_controlPoint is null)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await WriteAsync(_controlPoint, command, withResponse: true).WaitAsync(WriteTimeout, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LastError = Loc.Format(CoreStrings.Ftms_WriteFailed, e.Message);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void OnControlPointResponse(byte[] bytes)
    {
        if (Ftms.ParseControlPointResponse(bytes) is not { } response)
        {
            return;
        }

        LastError = response.Succeeded ? null : Loc.Format(CoreStrings.Ftms_OpFailed, response.RequestOpCode, response.ResultName);
        ControlPointResponded?.Invoke(response);
        if (Ftms.ParseSpinDownResponse(bytes) is { } spinDown)
        {
            SpinDownUpdated?.Invoke(spinDown);
        }
    }
}
