using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Devices;

public interface ISensorDevice : IAsyncDisposable
{
    string Name { get; }

    bool IsConnected { get; }

    /// <summary>Why the device is not connected, e.g. "reconnecting: timeout". Null when connected.</summary>
    string? Status { get; }

    /// <summary>Charge in percent, for a device that reports it. Null when it does not, or has not yet.</summary>
    int? Battery => null;

    /// <summary>Null for a simulated device.</summary>
    DeviceId? Id => null;
}

/// <summary>A real device: it connects on request, then keeps itself connected until disposed.</summary>
public interface IConnectableDevice : ISensorDevice
{
    Task ConnectAsync(CancellationToken cancellationToken);
}

/// <summary><paramref name="WindResistance"/> is 0.5 × air density × CdA, in kg/m.</summary>
public readonly record struct RoadSimulation(double GradePercent, double Crr, double WindResistance, double RiderKg, double BikeKg);

public interface ITrainer : ISensorDevice
{
    event Action<IndoorBikeData>? DataReceived;

    /// <summary>The last control point error, shown on the dashboard. Null when the last request succeeded.</summary>
    string? LastError { get; }

    Task SetTargetPowerAsync(int watts, CancellationToken cancellationToken = default);

    /// <summary>Whether the trainer can simulate a road (FTMS Indoor Bike Simulation, or ANT+ FE-C simulation mode).</summary>
    bool SupportsSimulation => false;

    /// <summary>Leaves ERG and simulates <paramref name="road"/>. A later target power goes back to ERG.</summary>
    Task SetSimulationAsync(RoadSimulation road, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Loc.Format(CoreStrings.Device_NoSimulation, Name));

    /// <summary>Whether the trainer offers FTMS spin-down calibration (Target Setting Features bit 15).</summary>
    bool SupportsSpinDown => false;

    /// <summary>The steps of a spin-down, as the trainer reports them.</summary>
    event Action<SpinDownUpdate>? SpinDownUpdated
    {
        add { }
        remove { }
    }

    /// <summary>Starts a spin-down. Progress arrives through <see cref="SpinDownUpdated"/>.</summary>
    Task StartSpinDownAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Loc.Format(CoreStrings.Device_NoSpinDown, Name));
}

public interface IHeartRateMonitor : ISensorDevice
{
    event Action<int>? HeartRateReceived;
}

/// <summary>A shifter such as a Zwift Click. Raises the buttons that went down.</summary>
public interface IShifter : ISensorDevice
{
    event Action<ClickButtons>? ButtonsPressed;
}
