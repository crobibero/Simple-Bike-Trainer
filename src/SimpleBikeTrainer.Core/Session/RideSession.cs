using System.Diagnostics;
using System.Threading.Channels;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Recording;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Session;

public enum RideCommand
{
    ShiftUp,
    ShiftDown,
    GradeUp,
    GradeDown,
    SkipStep,
    Lap,
    TogglePause,
    ToggleErg,
    EndRampTest,
    Finish,
}

public enum RideState
{
    WaitingToStart,
    Riding,
    Paused,
    AutoPaused,
}

public sealed record RideOptions(
    int Ftp,
    int WPrime,
    RiderModel Rider,
    string OutputPath,
    int[]? PowerZones = null,
    int[]? HrZones = null,
    double SpeedFactor = 1,
    int StartGear = 12);

public sealed record RideResult(
    Workout Workout,
    RideMetrics Metrics,
    string? FitPath,
    DateTime StartUtc,
    DateTime EndUtc,
    int LapCount,
    int? FtpEstimate)
{
    public IReadOnlyList<LapMetrics> Laps { get; init; } = [];
}

/// <summary>Everything the dashboard shows, published after every tick and command.</summary>
public sealed record RideSnapshot(
    RideState State,
    TimeSpan Elapsed,
    TimeSpan Moving,
    int Power,
    int Power3s,
    int? Cadence,
    int? HeartRate,
    double SpeedKmh,
    double DistanceKm,
    int? TargetWatts,
    CadenceTarget? CadenceTarget,
    bool ErgActive,
    bool ErgEnabled,
    int ErgOffset,
    int Gear,
    int GearCount,
    double Grade,
    double AltitudeMeters,
    int StepIndex,
    WorkoutStep? Step,
    WorkoutStep? Next,
    int StepElapsed,
    int StepRemaining,
    long WorkoutElapsed,
    bool WorkoutComplete,
    RideMetrics Metrics,
    LapMetrics Lap,
    string? Message);

/// <summary>
/// Runs one ride. Once a second it reads the sensors, records a sample, moves the workout on and sets the trainer's
/// target: the step's power in ERG, or in free ride the power that the virtual gear, cadence and grade cost.
/// </summary>
public sealed class RideSession
{
    private const int AutoPauseSeconds = 3;
    // An idle KICKR notifies only every 1-4 s.
    private static readonly TimeSpan SensorTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CheckpointEvery = TimeSpan.FromMinutes(5);

    private readonly Workout _workout;
    private readonly RideOptions _options;
    private readonly ITrainer _trainer;
    private readonly IHeartRateMonitor? _heartRate;
    private readonly IReadOnlyList<IShifter> _shifters;
    private readonly Channel<RideCommand> _commands = Channel.CreateUnbounded<RideCommand>();
    private readonly WorkoutPlayer _player;
    private readonly Gearbox _gearbox;
    private readonly RideMetrics _metrics;
    private readonly Lock _sensorLock = new();
    private readonly SemaphoreSlim _targetChanged = new(0);

    // Sensor values, written by BLE callbacks.
    private int _power;
    private double? _cadence;
    private int? _hr;
    private int? _trainerHr;
    private long _trainerSeen;
    private long _hrSeen;

    // Ride state, touched only by the ride loop.
    private FitRecorder? _recorder;
    private LapMetrics _lap;
    private RideState _state = RideState.WaitingToStart;
    private bool _manualPause;
    private bool _ergEnabled = true;
    private int _ergOffset;
    private double _grade;
    private double _altitude;
    private bool _courseDone;
    private int _zeroSeconds;
    private int _rampStruggleSeconds;
    private long _tick;
    private DateTime _clockStart;
    private DateTime? _rideStart;
    private int _lapCount;
    private readonly List<LapMetrics> _laps = [];
    private int? _target;
    private int? _ftpEstimate;
    private string? _message;
    private bool _finished;
    private DateTime _lastCheckpoint;
    private int _sendTarget = -1;

    public RideSession(Workout workout, RideOptions options, ITrainer trainer, IHeartRateMonitor? heartRate, IEnumerable<IShifter> shifters)
    {
        _workout = workout;
        _options = options;
        _trainer = trainer;
        _heartRate = heartRate;
        _player = new WorkoutPlayer(workout);
        _gearbox = new Gearbox(options.StartGear);
        _metrics = new RideMetrics(options.Ftp, options.WPrime, options.PowerZones, options.HrZones);
        _lap = new LapMetrics(DateTime.UtcNow, 0);
        if (workout.Course is { } course)
        {
            _altitude = course.ElevationAt(0);
            _grade = course.GradeAt(0);
        }

        trainer.DataReceived += OnTrainerData;
        if (heartRate is not null)
        {
            heartRate.HeartRateReceived += OnHeartRate;
        }

        _shifters = [.. shifters];
        foreach (var shifter in _shifters)
        {
            shifter.ButtonsPressed += OnButtons;
        }

        Snapshot = BuildSnapshot();
    }

    public event Action<RideSnapshot>? Updated;

    public RideSnapshot Snapshot { get; private set; }

    public Workout Workout => _workout;

    public void Send(RideCommand command) => _commands.Writer.TryWrite(command);

    public async Task<RideResult> RunAsync(CancellationToken cancellationToken)
    {
        _clockStart = DateTime.UtcNow;
        _lastCheckpoint = _clockStart;
        using var sender = new CancellationTokenSource();
        var sending = Task.Run(() => SendTargetsAsync(sender.Token), CancellationToken.None);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1 / Math.Max(_options.SpeedFactor, 0.01)));
        var tick = timer.WaitForNextTickAsync(cancellationToken).AsTask();
        var command = _commands.Reader.WaitToReadAsync(cancellationToken).AsTask();
        UpdateTarget();
        Publish();

        try
        {
            while (!_finished)
            {
                await Task.WhenAny(tick, command);

                // Commands first: one sent before this second ended (such as Finish) applies before the second is recorded.
                if (command.IsCompleted)
                {
                    await command;
                    while (_commands.Reader.TryRead(out var c))
                    {
                        Handle(c);
                    }

                    command = _commands.Reader.WaitToReadAsync(cancellationToken).AsTask();
                }

                if (tick.IsCompleted && !_finished)
                {
                    await tick;
                    Tick();
                    tick = timer.WaitForNextTickAsync(cancellationToken).AsTask();
                }

                UpdateTarget();
                Publish();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C finishes the ride and keeps what was recorded.
        }
        finally
        {
            await sender.CancelAsync();
            try
            {
                await sending;
            }
            catch (OperationCanceledException)
            {
            }

            await ReleaseTrainerAsync();
            Detach();
        }

        return Finish();
    }

    /// <summary>
    /// Drops the target to 0 W, so the trainer does not hold the last one while the app stays connected after the ride.
    /// </summary>
    private async Task ReleaseTrainerAsync()
    {
        try
        {
            await _trainer.SetTargetPowerAsync(0).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The trainer reports its own errors, and disconnecting hands control back anyway.
        }
    }

    private DateTime Now => _clockStart.AddSeconds(_tick);

    private void Tick()
    {
        _tick++;
        var (power, cadence, hr) = ReadSensors();
        UpdateState(power, cadence);

        if (_state == RideState.Riding)
        {
            RecordSecond(power, cadence, hr);
            if (_player.Tick())
            {
                CloseLap(manual: false);
                if (_player.IsFinished)
                {
                    _message = _workout.IsRampTest
                        ? RampTestMessage()
                        : CoreStrings.Session_WorkoutComplete;
                }
            }

            CheckRampTest(power, cadence);
        }

        if (_recorder is not null && Now - _lastCheckpoint >= CheckpointEvery)
        {
            _lastCheckpoint = Now;
            try
            {
                _recorder.Save(_options.OutputPath + ".partial", _metrics, Now);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A checkpoint is a convenience; the final save reports real errors.
            }
        }
    }

    private void UpdateState(int power, int? cadence)
    {
        var pedaling = power > 0 || cadence > 0;
        _zeroSeconds = pedaling ? 0 : _zeroSeconds + 1;
        var previous = _state;

        if (_state == RideState.WaitingToStart)
        {
            if (!pedaling || _manualPause)
            {
                return;
            }

            // This tick's sample covers the second that just ended, so the ride began a second ago.
            var start = Now.AddSeconds(-1);
            _rideStart = start;
            _recorder = new FitRecorder(start);
            _lap = new LapMetrics(start, 0);
            SaveInfo(start);
            _state = RideState.Riding;
            return;
        }

        _state = _manualPause ? RideState.Paused
            : _zeroSeconds >= AutoPauseSeconds ? RideState.AutoPaused
            : RideState.Riding;

        if (previous == RideState.Riding && _state != RideState.Riding)
        {
            _recorder!.AddTimerEvent(Now, start: false);
        }
        else if (previous != RideState.Riding && _state == RideState.Riding)
        {
            _recorder!.AddTimerEvent(Now, start: true);
        }
    }

    /// <summary>The ride's name and workout next to the FIT file, so an interrupted ride can be recovered with them.</summary>
    private void SaveInfo(DateTime start)
    {
        try
        {
            new RideInfo
            {
                Name = _workout.Name,
                EventId = _workout.EventId,
                Ftp = _options.Ftp,
                StartUtc = start,
                Workout = _workout,
            }.Save(_options.OutputPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The ride still records; it just loses its name if the app stops mid-ride.
        }
    }

    private void RecordSecond(int power, int? cadence, int? hr)
    {
        var grade = ErgActive ? 0 : _grade;
        var speed = Physics.SpeedForPower(power, grade, _options.Rider);
        var distance = _metrics.DistanceMeters + speed;

        // The altitude follows the route, or the grade of a free ride, so the FIT file has climbing.
        if (_workout.Course is { } course && !_courseDone)
        {
            _altitude = course.ElevationAt(distance);
            _grade = course.GradeAt(distance);
            if (distance >= course.DistanceMeters)
            {
                _courseDone = true;
                _message = CoreStrings.Session_RouteComplete;
            }
        }
        else
        {
            _altitude += speed * grade / 100;
        }

        var sample = new Sample(Now, power, cadence, hr, speed, distance, ErgActive ? _target : null, _altitude);
        _metrics.Add(sample);
        _lap.Add(sample);
        _recorder!.AddRecord(sample);
    }

    private void CheckRampTest(int power, int? cadence)
    {
        if (!_workout.IsRampTest || _player.Current is not { } step || !RampTest.IsRampStep(step))
        {
            _rampStruggleSeconds = 0;
            return;
        }

        // The test ends when cadence drops under 50 rpm, or power under 70% of target, for 5 seconds in a row.
        var target = step.Power!.WattsAt(_player.StepFraction, _options.Ftp);
        var struggling = cadence is < 50 || power < target * 0.7;
        _rampStruggleSeconds = struggling ? _rampStruggleSeconds + 1 : 0;
        if (_rampStruggleSeconds >= 5)
        {
            EndRampTest();
        }
    }

    private void EndRampTest()
    {
        if (!_workout.IsRampTest || _player.Current is not { } step || !RampTest.IsRampStep(step))
        {
            return;
        }

        CloseLap(manual: false);
        _player.JumpTo(_workout.Steps.Count - 1); // the cooldown
        _rampStruggleSeconds = 0;
        _message = RampTestMessage();
    }

    private string RampTestMessage()
    {
        if (!_metrics.Peaks.TryGetValue(60, out var best))
        {
            return CoreStrings.Session_RampTooShort;
        }

        _ftpEstimate = RampTest.EstimateFtp(best);
        return Loc.Format(CoreStrings.Session_RampDone, best, _ftpEstimate);
    }

    private void Handle(RideCommand command)
    {
        switch (command)
        {
            case RideCommand.ShiftUp or RideCommand.ShiftDown:
                var up = command == RideCommand.ShiftUp;
                if (ErgActive)
                {
                    // In ERG the shifter nudges the target, except in a ramp test, whose steps are the test.
                    if (!_workout.IsRampTest)
                    {
                        _ergOffset = Math.Clamp(_ergOffset + (up ? 5 : -5), -100, 100);
                    }
                }
                else
                {
                    _gearbox.Shift(up ? 1 : -1);
                }

                break;
            // On a route the road sets the grade.
            case RideCommand.GradeUp when _workout.Course is null || _courseDone:
                _grade = Math.Min(20, _grade + 0.5);
                break;
            case RideCommand.GradeDown when _workout.Course is null || _courseDone:
                _grade = Math.Max(-10, _grade - 0.5);
                break;
            case RideCommand.SkipStep:
                if (!_player.IsFinished && _state != RideState.WaitingToStart)
                {
                    CloseLap(manual: false);
                }

                _player.Skip();
                break;
            case RideCommand.Lap:
                if (_state != RideState.WaitingToStart)
                {
                    CloseLap(manual: true);
                }

                break;
            case RideCommand.TogglePause:
                _manualPause = !_manualPause;
                break;
            case RideCommand.ToggleErg:
                _ergEnabled = !_ergEnabled;
                break;
            case RideCommand.EndRampTest:
                EndRampTest();
                break;
            case RideCommand.Finish:
                _finished = true;
                break;
        }
    }

    private bool ErgActive => _ergEnabled && _player.Current is { IsFreeRide: false };

    private void UpdateTarget()
    {
        int target;
        if (ErgActive)
        {
            var step = _player.Current!;
            var offset = _workout.IsRampTest ? 0 : _ergOffset;
            target = Math.Max(0, step.Power!.WattsAt(_player.StepFraction, _workout.Ftp ?? _options.Ftp) + offset);
        }
        else
        {
            var cadence = ReadSensors().Cadence ?? 0;
            var speed = _gearbox.SpeedMs(cadence);
            target = (int)Math.Round(Math.Clamp(Physics.PowerForSpeed(speed, _grade, _options.Rider), 0, 1500));
        }

        _target = target;
        if (Interlocked.Exchange(ref _sendTarget, target) != target)
        {
            _targetChanged.Release();
        }
    }

    /// <summary>Sends the latest target. A write in flight is never queued behind: only the newest value goes out.</summary>
    private async Task SendTargetsAsync(CancellationToken ct)
    {
        var sent = -1;
        var lastSend = Stopwatch.GetTimestamp();
        while (!ct.IsCancellationRequested)
        {
            // Re-send every 10 s too, so a trainer that dropped a write still converges.
            await _targetChanged.WaitAsync(TimeSpan.FromSeconds(10), ct);
            var target = Volatile.Read(ref _sendTarget);
            if (target < 0 || (target == sent && Stopwatch.GetElapsedTime(lastSend) < TimeSpan.FromSeconds(10)))
            {
                continue;
            }

            try
            {
                await _trainer.SetTargetPowerAsync(target, ct);
                sent = target;
                lastSend = Stopwatch.GetTimestamp();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The trainer reports its own errors; keep riding.
            }
        }
    }

    private void CloseLap(bool manual)
    {
        if (_recorder is null || _lap.Seconds == 0)
        {
            _lap = new LapMetrics(Now, _lapCount);
            return;
        }

        _recorder.AddLap(_lap, manual);
        _laps.Add(_lap);
        _lapCount++;
        _lap = new LapMetrics(Now, _lapCount);
    }

    private RideResult Finish()
    {
        CloseLap(manual: false);
        string? path = null;
        var end = Now;
        if (_recorder is { RecordCount: > 0 })
        {
            path = _options.OutputPath;
            _recorder.Save(path, _metrics, end);
            File.Delete(path + ".partial");
        }

        if (_workout.IsRampTest && _ftpEstimate is null && _metrics.Peaks.ContainsKey(60))
        {
            RampTestMessage();
        }

        return new RideResult(_workout, _metrics, path, _rideStart ?? _clockStart, end, _lapCount, _ftpEstimate) { Laps = _laps };
    }

    /// <summary>Stops listening to the devices. A finished ride does this itself; call it for a ride that never ran.</summary>
    public void Detach()
    {
        _trainer.DataReceived -= OnTrainerData;
        if (_heartRate is not null)
        {
            _heartRate.HeartRateReceived -= OnHeartRate;
        }

        foreach (var shifter in _shifters)
        {
            shifter.ButtonsPressed -= OnButtons;
        }
    }

    private (int Power, int? Cadence, int? HeartRate) ReadSensors()
    {
        lock (_sensorLock)
        {
            var trainerFresh = _trainerSeen != 0 && Stopwatch.GetElapsedTime(_trainerSeen) < SensorTimeout;
            var hrFresh = _hrSeen != 0 && Stopwatch.GetElapsedTime(_hrSeen) < SensorTimeout;
            int? cadence = trainerFresh && _cadence is { } c ? (int)Math.Round(c) : null;
            return (trainerFresh ? _power : 0, cadence, hrFresh ? _hr : trainerFresh ? _trainerHr : null);
        }
    }

    private void OnTrainerData(IndoorBikeData data)
    {
        lock (_sensorLock)
        {
            _trainerSeen = Stopwatch.GetTimestamp();
            if (data.PowerWatts is { } p)
            {
                _power = Math.Max(0, p);
            }

            if (data.CadenceRpm is { } c)
            {
                // Light smoothing: the trainer sends about 4 readings a second.
                _cadence = _cadence is { } prev && c > 0 ? (prev * 0.5) + (c * 0.5) : c;
            }

            if (data.HeartRate is > 0)
            {
                _trainerHr = data.HeartRate;
            }
        }
    }

    private void OnHeartRate(int bpm)
    {
        lock (_sensorLock)
        {
            _hr = bpm;
            _hrSeen = Stopwatch.GetTimestamp();
        }
    }

    private void OnButtons(ClickButtons buttons)
    {
        // A Click V2's left puck (minus) is locked to the Zwift app, so B on the right puck also shifts down.
        if ((buttons & (ClickButtons.Plus | ClickButtons.Y)) != 0)
        {
            Send(RideCommand.ShiftUp);
        }

        if ((buttons & (ClickButtons.Minus | ClickButtons.B)) != 0)
        {
            Send(RideCommand.ShiftDown);
        }
    }

    private void Publish()
    {
        Snapshot = BuildSnapshot();
        Updated?.Invoke(Snapshot);
    }

    private RideSnapshot BuildSnapshot()
    {
        var (power, cadence, hr) = ReadSensors();
        var moving = TimeSpan.FromSeconds(_metrics.Seconds);
        var elapsed = _rideStart is { } start ? Now - start : TimeSpan.Zero;
        var step = _player.Current;
        return new RideSnapshot(
            _state,
            elapsed,
            moving,
            power,
            (int)Math.Round(_metrics.Seconds == 0 ? power : _metrics.RollingAverage(3)),
            cadence,
            hr,
            Physics.SpeedForPower(power, ErgActive ? 0 : _grade, _options.Rider) * 3.6,
            _metrics.DistanceMeters / 1000,
            _target,
            step?.Cadence,
            ErgActive,
            _ergEnabled,
            _ergOffset,
            _gearbox.Gear,
            _gearbox.GearCount,
            _grade,
            _altitude,
            _player.StepIndex,
            step,
            _player.Next,
            _player.StepElapsed,
            _player.StepRemaining,
            _player.WorkoutElapsed,
            _workout.Course is null ? _player.IsFinished : _courseDone,
            _metrics,
            _lap,
            _message);
    }
}
