using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Ble;

/// <summary>A KICKR CORE 2, or any FTMS trainer, driven in ERG mode through the Fitness Machine Control Point.</summary>
public sealed class FtmsTrainer(BlePeripheral peripheral, string name, DeviceId id) : BleConnection(peripheral, name, id), ITrainer
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    // BlueZ fails a GATT write that overlaps another one, so control point writes go one at a time.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private BleCharacteristic? _controlPoint;
    // The last target power or simulation sent, to restore after a reconnect.
    private byte[]? _lastControl;

    public event Action<IndoorBikeData>? DataReceived;

    /// <summary>Raised for each control point indication, for <c>simple-bike-trainer-cli probe --control</c>.</summary>
    public event Action<ControlPointResponse>? ControlPointResponded;

    public string? LastError { get; private set; }

    public bool SupportsSpinDown { get; private set; }

    public bool SupportsSimulation { get; private set; }

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

        await SendControlAsync(Ftms.SetTargetPower(watts), cancellationToken);
    }

    /// <summary>FTMS has no weight field: the trainer uses its own, often the one set in its maker's app.</summary>
    public async Task SetSimulationAsync(RoadSimulation road, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return;
        }

        await SendControlAsync(Ftms.SetSimulation(road.GradePercent, road.Crr, road.WindResistance), cancellationToken);
    }

    protected override async Task SetupAsync(CancellationToken cancellationToken)
    {
        var bikeData = await GetCharacteristicAsync(GattUuids.FitnessMachine, GattUuids.IndoorBikeData);
        _controlPoint = await GetCharacteristicAsync(GattUuids.FitnessMachine, GattUuids.FitnessMachineControlPoint);

        var feature = await GetCharacteristicAsync(GattUuids.FitnessMachine, GattUuids.FitnessMachineFeature);
        var features = await feature.ReadAsync();
        SupportsSpinDown = Ftms.SupportsSpinDown(features);
        SupportsSimulation = Ftms.SupportsSimulation(features);
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

        if (_lastControl is { } control)
        {
            await SendAsync(control, cancellationToken);
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

    private async Task SendControlAsync(byte[] command, CancellationToken cancellationToken)
    {
        await SendAsync(command, cancellationToken);
        _lastControl = command;
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
