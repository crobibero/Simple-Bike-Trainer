using AsyncAwaitBestPractices;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Devices;

/// <summary>
/// A pretend rider on a pretend trainer, for trying the app without hardware (<c>--simulate</c>). It pedals at
/// 85-90 rpm and holds whatever power the app asks for, with a little noise.
/// </summary>
public sealed class SimulatedTrainer : ITrainer
{
    private readonly ITimer _timer;
    private readonly TimeProvider _time;
    private readonly Random _random = new(42);
    private volatile int _target = 100;
    private double _power;

    /// <param name="timeProvider">Paces its readings, four a second.</param>
    public SimulatedTrainer(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        var period = TimeSpan.FromMilliseconds(250);
        _timer = _time.CreateTimer(_ => Emit(), null, period, period);
    }

    public event Action<IndoorBikeData>? DataReceived;

    public string Name => CoreStrings.Sim_Trainer;

    public bool IsConnected => true;

    public string? Status => null;

    public string? LastError => null;

    /// <summary>The rider stops pedaling while this is set, to try auto-pause.</summary>
    public bool Coasting { get; set; }

    public Task SetTargetPowerAsync(int watts, CancellationToken cancellationToken = default)
    {
        _target = watts;
        Road = null;
        return Task.CompletedTask;
    }

    public bool SupportsSimulation => true;

    /// <summary>The road being simulated; null in ERG.</summary>
    public RoadSimulation? Road { get; private set; }

    /// <summary>Holds the power the road costs at the flywheel speed of 85-90 rpm in a 34x17 gear, about 22 km/h.</summary>
    public Task SetSimulationAsync(RoadSimulation road, CancellationToken cancellationToken = default)
    {
        Road = road;
        const double speed = 87.5 / 60 * 2 * 2.136;
        var force = ((road.RiderKg + road.BikeKg) * 9.80665 * (road.Crr + (road.GradePercent / 100))) + (road.WindResistance * speed * speed);
        _target = (int)Math.Round(Math.Max(0, force * speed));
        return Task.CompletedTask;
    }

    public bool SupportsSpinDown => true;

    public event Action<SpinDownUpdate>? SpinDownUpdated;

    /// <summary>How long each simulated spin-down step takes.</summary>
    public TimeSpan SpinDownStepTime { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>A pretend spin-down: speed up, then coast, then success.</summary>
    public Task StartSpinDownAsync(CancellationToken cancellationToken = default)
    {
        PlaySpinDownAsync(cancellationToken).SafeFireAndForget<OperationCanceledException>(static _ => { });
        return Task.CompletedTask;
    }

    private async Task PlaySpinDownAsync(CancellationToken cancellationToken)
    {
        SpinDownUpdated?.Invoke(new SpinDownUpdate(SpinDownState.SpeedUp, 30, 33));
        await Task.Delay(SpinDownStepTime, _time, cancellationToken);
        SpinDownUpdated?.Invoke(new SpinDownUpdate(SpinDownState.StopPedaling));
        await Task.Delay(SpinDownStepTime, _time, cancellationToken);
        SpinDownUpdated?.Invoke(new SpinDownUpdate(SpinDownState.Success));
    }

    public ValueTask DisposeAsync()
    {
        _timer.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Emit()
    {
        if (Coasting)
        {
            DataReceived?.Invoke(new IndoorBikeData(0, 0, 0, null, null, null));
            return;
        }

        // ERG takes a second or two to settle on a new target.
        _power += (_target - _power) * 0.35;
        var power = (int)Math.Round(_power + (_random.NextDouble() * 8) - 4);
        var cadence = 85 + (_random.NextDouble() * 5);
        DataReceived?.Invoke(new IndoorBikeData(30, cadence, Math.Max(0, power), null, null, null));
    }
}

/// <summary>A heart rate that drifts toward a level set by the simulated trainer's power.</summary>
public sealed class SimulatedHeartRate : IHeartRateMonitor
{
    private readonly ITimer _timer;
    private double _hr = 70;
    private int _power;

    /// <param name="timeProvider">Paces its readings, one a second.</param>
    public SimulatedHeartRate(SimulatedTrainer trainer, TimeProvider? timeProvider = null)
    {
        trainer.DataReceived += d => _power = d.PowerWatts ?? 0;
        var period = TimeSpan.FromSeconds(1);
        _timer = (timeProvider ?? TimeProvider.System).CreateTimer(_ => Emit(), null, period, period);
    }

    public event Action<int>? HeartRateReceived;

    public string Name => CoreStrings.Sim_HeartRate;

    public bool IsConnected => true;

    public string? Status => null;

    public ValueTask DisposeAsync()
    {
        _timer.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Emit()
    {
        var steady = 70 + (_power * 0.45);
        _hr += (steady - _hr) * 0.05;
        HeartRateReceived?.Invoke((int)Math.Round(_hr));
    }
}
