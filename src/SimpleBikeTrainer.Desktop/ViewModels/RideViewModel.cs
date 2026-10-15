using System.Collections.ObjectModel;
using AsyncAwaitBestPractices;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleBikeTrainer.Desktop.Controls;
using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Desktop.Services;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Desktop.ViewModels;

public sealed record ZoneRow(string Name, string Time, double Fraction, IBrush Brush);

/// <summary>A labeled number. <see cref="Tip"/> explains it on hover.</summary>
public sealed record StatRow(string Label, string Value, string? Tip = null);

/// <summary>Hover text for the numbers on the ride and summary pages. The help page has the longer story.</summary>
public static class Tips
{
    public static string Np => Strings.Tip_Np;
    public static string If => Strings.Tip_If;
    public static string Tss => Strings.Tip_Tss;
    public static string WPrime => Strings.Tip_WPrime;
    public static string WPrimeLow => Strings.Tip_WPrimeLow;
    public static string Work => Strings.Tip_Work;
    public static string OnTarget => Strings.Tip_OnTarget;
    public static string Peaks => Strings.Tip_Peaks;
}

/// <summary>The live ride page.</summary>
public sealed partial class RideViewModel : ViewModelBase
{
    private static IBrush Good => Palette.Good;
    private static IBrush Warn => Palette.Warn;
    private static IBrush Bad => Palette.Bad;
    private static IBrush Plain => Palette.Text;
    private static IBrush Dim => Palette.Muted;
    private static IBrush Heart => Palette.Heart;
    private static string[] ZoneNames => [Strings.Zone_1, Strings.Zone_2, Strings.Zone_3, Strings.Zone_4, Strings.Zone_5, Strings.Zone_6, Strings.Zone_7];

    private readonly MainWindowViewModel _main;
    private readonly RideSession _session;
    private readonly List<ChartPoint> _points = [];
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;
    private int _lastRecorded;
    private long? _completedAt;
    private (double X, int Seconds)? _cooldownFrom; // where a ramp test's chart was, and the time ridden, at its cooldown
    private DispatcherTimer? _finishReset;
    private readonly bool _soundCues;
    private (int Step, int Second) _lastCue = (-1, -1);
    private int _lastStep = -1;
    private IAsyncDisposable? _awake;
    private readonly int[]? _hrZones;
    private readonly IReadOnlyList<string>? _hrZoneNames;

    public RideViewModel(MainWindowViewModel main, RideSession session, int ftp, DeviceManager devices)
    {
        _main = main;
        _session = session;
        Ftp = ftp;
        Devices = devices;
        Workout = session.Workout;
        Title = Workout.Name;
        Units = main.Config.Units;
        IsRampTest = Workout.IsRampTest;
        _soundCues = main.Config.SoundCues;
        UseCountdown = main.Config.StartCountdown;
        _hrZones = main.Athlete?.HrZones;
        _hrZoneNames = main.Athlete?.Settings?.HrZoneNames;
        Apply(RideFrame.From(session.Snapshot));
    }

    public Workout Workout { get; }

    public int Ftp { get; }

    public DeviceManager Devices { get; }

    public string Title { get; }

    public UnitSystem Units { get; }

    public string SpeedUnit => Units.SpeedUnit();

    public bool IsRampTest { get; }

    public bool IsRoute => Workout.Course is not null;

    public IReadOnlyList<ChartPoint> Points => _points;

    public ObservableCollection<ZoneRow> Zones { get; } = [];

    public ObservableCollection<StatRow> RideStats { get; } = [];

    public ObservableCollection<StatRow> LapStats { get; } = [];

    [ObservableProperty] public partial int ChartVersion { get; set; }

    [ObservableProperty] public partial double Position { get; set; }

    [ObservableProperty] public partial string State { get; set; } = string.Empty;

    [ObservableProperty] public partial IBrush StateBrush { get; set; } = Warn;

    [ObservableProperty] public partial string Elapsed { get; set; } = "0:00";

    [ObservableProperty] public partial string Moving { get; set; } = "0:00";

    [ObservableProperty] public partial string Power { get; set; } = "0";

    [ObservableProperty] public partial string PowerInstant { get; set; } = string.Empty;

    [ObservableProperty] public partial IBrush PowerBrush { get; set; } = Plain;

    [ObservableProperty] public partial string Target { get; set; } = "-";

    [ObservableProperty] public partial string TargetMode { get; set; } = string.Empty;

    [ObservableProperty] public partial string Cadence { get; set; } = "-";

    [ObservableProperty] public partial string CadenceTarget { get; set; } = string.Empty;

    [ObservableProperty] public partial IBrush CadenceBrush { get; set; } = Plain;

    [ObservableProperty] public partial string HeartRate { get; set; } = "-";

    [ObservableProperty] public partial IBrush HeartRateBrush { get; set; } = Heart;

    [ObservableProperty] public partial string HeartRateZone { get; set; } = string.Empty;

    [ObservableProperty] public partial string Grade { get; set; } = "0.0";

    [ObservableProperty] public partial string GradeDetail { get; set; } = string.Empty;

    [ObservableProperty] public partial IBrush GradeBrush { get; set; } = Plain;

    [ObservableProperty] public partial string Speed { get; set; } = "0.0";

    [ObservableProperty] public partial string Distance { get; set; } = "0.00";

    [ObservableProperty] public partial string GearLabel { get; set; } = Strings.Ride_GearCaps;

    [ObservableProperty] public partial string Gear { get; set; } = string.Empty;

    [ObservableProperty] public partial string GearDetail { get; set; } = string.Empty;

    [ObservableProperty] public partial string StepLabel { get; set; } = string.Empty;

    [ObservableProperty] public partial string StepTarget { get; set; } = string.Empty;

    [ObservableProperty] public partial string StepRemaining { get; set; } = string.Empty;

    [ObservableProperty] public partial double StepProgress { get; set; }

    [ObservableProperty] public partial string NextStep { get; set; } = string.Empty;

    [ObservableProperty] public partial string WorkoutProgress { get; set; } = string.Empty;

    [ObservableProperty] public partial string? Message { get; set; }

    [ObservableProperty] public partial string ErgButton { get; set; } = Strings.Ride_ErgOff;

    [ObservableProperty] public partial string PauseButton { get; set; } = Strings.Ride_Pause;

    [ObservableProperty] public partial string FinishButton { get; set; } = Strings.Ride_Finish;

    [ObservableProperty] public partial bool ShowsWBalance { get; set; }

    [ObservableProperty] public partial double WBalanceFraction { get; set; } = 1;

    /// <summary>The banner over the chart in the last 5 seconds of a step and the first 3 of the next.</summary>
    [ObservableProperty] public partial bool CueVisible { get; set; }

    [ObservableProperty] public partial string CueCount { get; set; } = string.Empty;

    [ObservableProperty] public partial string CueTitle { get; set; } = string.Empty;

    [ObservableProperty] public partial string CueDetail { get; set; } = string.Empty;

    /// <summary>Whether the desktop agreed to keep the screen on, for the tooltip on the clock.</summary>
    [ObservableProperty] public partial string AwakeStatus { get; set; } = Strings.Ride_KeepingAwake;

    /// <summary>The big 3, 2, 1, GO over the page before the ride starts; null once it is done.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCountingDown))]
    public partial string? Countdown { get; set; }

    public bool IsCountingDown => Countdown is not null;

    /// <summary>GO is showing: the ride has started, so the countdown can no longer be canceled.</summary>
    private bool _go;

    private bool CanCancelStart => IsCountingDown && !_go;

    /// <summary>The session stopped without a summary to show, such as when the final save failed.</summary>
    private bool _ended;

    /// <summary>What comes first, under the countdown.</summary>
    public string CountdownDetail =>
        Workout.Course is { } course ? Loc.Format(Strings.Ride_CountdownRoute, Units.FormatDistance(course.DistanceMeters, "0.0"), Units.FormatElevation(course.ElevationGain))
        : _session.Snapshot.Step is { DurationSeconds: < int.MaxValue } step ? Loc.Format(Strings.Ride_CountdownFirst, step.Label, Format.Target(step))
        : Strings.Ride_CountdownFree;

    /// <summary>How long each number of the countdown shows. Tests shorten it.</summary>
    public TimeSpan CountdownStep { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Off in Settings, the ride starts at once.</summary>
    public bool UseCountdown { get; init; }

    public void Start()
    {
        _session.Updated += OnUpdated;
        _run = UseCountdown ? CountDownThenRideAsync() : Task.Run(() => RunAsync());
        KeepScreenOnAsync().SafeFireAndForget();
    }

    /// <summary>3, 2, 1 with a beep each, then GO as the session starts. Finish or Escape during it goes back home.</summary>
    private async Task CountDownThenRideAsync()
    {
        State = Strings.Ride_GetReady;
        try
        {
            for (var n = 3; n >= 1; n--)
            {
                Countdown = n.ToString(System.Globalization.CultureInfo.CurrentCulture);
                if (_soundCues)
                {
                    Sounds.Tick();
                }

                await Task.Delay(CountdownStep, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _session.Updated -= OnUpdated;
            _session.Detach();
            Countdown = null;
            await ReleaseScreenAsync();
            return;
        }

        _go = true;
        Countdown = Strings.Ride_Go;
        if (_soundCues)
        {
            Sounds.Go();
        }

        var ride = Task.Run(() => RunAsync());
        await Task.WhenAny(ride, Task.Delay(CountdownStep));
        Countdown = null;
        await ride;
    }

    /// <summary>Leaves the countdown before anything is recorded.</summary>
    [RelayCommand]
    private async Task CancelStartAsync()
    {
        if (!CanCancelStart)
        {
            return;
        }

        await StopAsync();
        _main.ShowHome();
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            await _run;
        }

        await ReleaseScreenAsync();
    }

    private async Task KeepScreenOnAsync()
    {
#if ANDROID
        var awake = await Platforms.RideLock.AcquireAsync(Loc.Format(Strings.Ride_AwakeReason, Title));
#else
        var awake = await SimpleBikeTrainer.Platform.KeepAwake.AcquireAsync(Loc.Format(Strings.Ride_AwakeReason, Title));
#endif
        if (_run is { IsCompleted: true } || _stop.IsCancellationRequested)
        {
            await awake.DisposeAsync();
            return;
        }

        _awake = awake;
        Dispatcher.UIThread.Post(() => AwakeStatus = awake.IsActive ? Strings.Ride_Awake : Loc.Format(Strings.Ride_AwakeFailed, awake.How));
    }

    private async Task ReleaseScreenAsync()
    {
        if (Interlocked.Exchange(ref _awake, null) is { } awake)
        {
            await awake.DisposeAsync();
        }
    }

    /// <summary>A ride ends only with Finish, so the back gesture does nothing.</summary>
    public override bool GoBack() => true;

    public override bool HandleKey(Key key)
    {
        RideCommand? command = key switch
        {
            Key.Up or Key.Add or Key.OemPlus => RideCommand.ShiftUp,
            Key.Down or Key.Subtract or Key.OemMinus => RideCommand.ShiftDown,
            Key.Right => RideCommand.GradeUp,
            Key.Left => RideCommand.GradeDown,
            Key.S => RideCommand.SkipStep,
            Key.L => RideCommand.Lap,
            Key.P or Key.Space => RideCommand.TogglePause,
            Key.E => RideCommand.ToggleErg,
            Key.X => RideCommand.EndRampTest,
            _ => null,
        };

        if (CanCancelStart)
        {
            if (key is Key.Q or Key.Escape)
            {
                CancelStartCommand.Execute(null);
            }

            return key is Key.Q or Key.Escape;
        }

        if (key is Key.Q or Key.Escape)
        {
            Finish();
            return true;
        }

        if (command is { } c)
        {
            _session.Send(c);
            return true;
        }

        return false;
    }

    [RelayCommand] private void ShiftUp() => _session.Send(RideCommand.ShiftUp);

    [RelayCommand] private void ShiftDown() => _session.Send(RideCommand.ShiftDown);

    [RelayCommand] private void GradeUp() => _session.Send(RideCommand.GradeUp);

    [RelayCommand] private void GradeDown() => _session.Send(RideCommand.GradeDown);

    [RelayCommand] private void ToggleErg() => _session.Send(RideCommand.ToggleErg);

    [RelayCommand] private void Skip() => _session.Send(RideCommand.SkipStep);

    [RelayCommand] private void Lap() => _session.Send(RideCommand.Lap);

    [RelayCommand] private void Pause() => _session.Send(RideCommand.TogglePause);

    [RelayCommand] private void EndRamp() => _session.Send(RideCommand.EndRampTest);

    /// <summary>Two presses within 3 seconds, so a stray tap cannot end a ride.</summary>
    [RelayCommand]
    private void Finish()
    {
        if (CanCancelStart)
        {
            CancelStartCommand.Execute(null);
            return;
        }

        if (_ended)
        {
            _main.ShowHome();
            return;
        }

        if (_finishReset is not null)
        {
            _finishReset.Stop();
            _finishReset = null;
            _session.Send(RideCommand.Finish);
            FinishButton = Strings.Ride_Saving;
            return;
        }

        FinishButton = Strings.Ride_PressAgain;
        _finishReset = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Normal, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            _finishReset = null;
            FinishButton = Strings.Ride_Finish;
        });
        _finishReset.Start();
    }

    private async Task RunAsync()
    {
        RideResult result;
        try
        {
            result = await _session.RunAsync(_stop.Token);
        }
        catch (Exception e)
        {
            // Finish becomes Back, since the session has ended and the page has nothing left to do.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _ended = true;
                FinishButton = Strings.Common_Back;
                Message = Loc.Format(e is IOException or UnauthorizedAccessException ? Strings.Ride_SaveFailed : Strings.Ride_Failed, e.Message);
            });
            return;
        }
        finally
        {
            _session.Updated -= OnUpdated;
            await ReleaseScreenAsync();
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_stop.IsCancellationRequested)
            {
                _main.ShowSummary(result, _points);
            }
        });
    }

    private void OnUpdated(RideSnapshot snapshot)
    {
        var frame = RideFrame.From(snapshot);
        Dispatcher.UIThread.Post(() => Apply(frame));
    }

    private void Apply(RideFrame f)
    {
        var s = f.Snapshot;
        State = s.State switch
        {
            RideState.WaitingToStart => Strings.Ride_PedalToStart,
            RideState.Paused => Strings.Ride_Paused,
            RideState.AutoPaused => Strings.Ride_AutoPaused,
            _ => s.WorkoutComplete && Workout.Steps.Count > 0 ? Strings.Ride_WorkoutDone : Strings.Ride_Riding,
        };
        StateBrush = s.State == RideState.Riding ? Good : Warn;
        Elapsed = Format.Clock(s.Elapsed);
        Moving = Format.Clock(s.Moving);

        Power = s.Power3s.ToString();
        PowerInstant = Loc.Format(Strings.Ride_PowerNow, s.Power);
        PowerBrush = s.ErgActive && s.TargetWatts is > 0 and var t ? Deviation(s.Power3s, t, Math.Max(10, t * 0.05), Math.Max(25, t * 0.12)) : Plain;
        Target = s.TargetWatts?.ToString() ?? "-";
        TargetMode = s.ErgActive ? (s.ErgOffset != 0 ? Loc.Format(Strings.Ride_ErgOffset, s.ErgOffset) : Strings.Common_Erg)
            : s.Simulating ? (s.VirtualGears ? Strings.Ride_SimulationVirtualGears : Strings.Ride_Simulation)
            : Strings.Ride_VirtualGears;

        Cadence = s.Cadence?.ToString() ?? "-";
        CadenceTarget = s.CadenceTarget is { } ct ? Loc.Format(Strings.Ride_CadenceTarget, ct) : string.Empty;
        CadenceBrush = s.CadenceTarget is { } target && s.Cadence is { } c
            ? c >= target.Low - 3 && c <= target.High + 3 ? Good : Math.Abs(c - target.Mid) <= 8 ? Warn : Bad
            : Plain;
        HeartRate = s.HeartRate?.ToString() ?? "-";
        if (s.HeartRate is > 0 and var bpm && _hrZones is { Length: > 0 } zones)
        {
            var zone = HeartRateZones.IndexOf(zones, bpm);
            HeartRateBrush = HeartRateZoneBrush(zone, zones.Length);
            HeartRateZone = HeartRateZones.Name(zone, zones.Length, _hrZoneNames);
        }
        else
        {
            HeartRateBrush = Heart;
            HeartRateZone = _hrZones is null ? Strings.Ride_SetMaxHr : string.Empty;
        }

        Grade = s.Grade.ToString("+0.0;-0.0;0.0");
        GradeBrush = s.ErgActive ? Dim : IsRoute ? GradeBrushes.GetOrAdd(WorkoutChart.GradeColor(s.Grade), c => new SolidColorBrush(c)) : Plain;
        GradeDetail = s.ErgActive ? Strings.Ride_GradeErg : IsRoute && !s.WorkoutComplete ? Strings.Ride_GradeRoute : Strings.Ride_GradeKeys;
        Speed = Units.Speed(s.SpeedKmh / 3.6).ToString("0.0");
        Distance = Units.FormatDistance(s.DistanceKm * 1000);
        if (s.ErgActive)
        {
            GearLabel = Strings.Ride_ModeCaps;
            Gear = Strings.Common_Erg;
            GearDetail = Strings.Ride_ErgNudge;
        }
        else if (!s.VirtualGears)
        {
            GearLabel = Strings.Ride_ModeCaps;
            Gear = Strings.Ride_SimCaps;
            GearDetail = Strings.Ride_ShiftOnBike;
        }
        else
        {
            GearLabel = Strings.Ride_GearCaps;
            Gear = s.Gear.ToString(System.Globalization.CultureInfo.CurrentCulture);
            GearDetail = Loc.Format(Strings.Ride_GearOf, s.GearCount);
        }

        ErgButton = s.ErgEnabled ? Strings.Ride_ErgOff : Strings.Ride_ErgOn;
        PauseButton = s.State == RideState.Paused ? Strings.Ride_Resume : Strings.Ride_Pause;

        if (Workout.Course is { } course)
        {
            ApplyRoute(f, course);
        }
        else if (s.Step is { } step && step.DurationSeconds != int.MaxValue)
        {
            StepLabel = step.Label;
            StepTarget = Format.Target(step);
            StepRemaining = Format.Clock(s.StepRemaining);
            StepProgress = step.DurationSeconds == 0 ? 0 : (double)s.StepElapsed / step.DurationSeconds;
            NextStep = s.Next is { } next ? Loc.Format(Strings.Ride_Next, next.Label, Format.Clock(next.DurationSeconds), Format.Target(next)) : Strings.Ride_LastStep;
            WorkoutProgress = IsRampTest ? Loc.Format(Strings.Ride_StepN, s.StepIndex + 1) : Loc.Format(Strings.Ride_StepOf, s.StepIndex + 1, Workout.Steps.Count, Format.Clock(Math.Min(s.WorkoutElapsed, Workout.TotalSeconds)), Format.Clock(Workout.TotalSeconds));
        }
        else
        {
            StepLabel = s.WorkoutComplete && Workout.Steps.Count > 0 && Workout.Steps[0].DurationSeconds != int.MaxValue ? Strings.Ride_WorkoutComplete : Strings.Ride_FreeRide;
            StepTarget = Strings.Ride_ShiftHint;
            StepRemaining = Moving;
            StepProgress = 0;
            NextStep = string.Empty;
            WorkoutProgress = string.Empty;
        }

        Message = s.Message;
        UpdateCue(s);
        UpdateChart(s, f.Seconds);
        UpdateStats(f);
    }

    /// <summary>On a route the step card shows the road: the grade now, distance left, and the next stretch.</summary>
    private void ApplyRoute(RideFrame f, Workouts.Course course)
    {
        var s = f.Snapshot;
        var done = s.DistanceKm * 1000;
        var climbed = f.ElevationGain;
        if (s.WorkoutComplete)
        {
            StepLabel = Strings.Ride_RouteComplete;
            StepTarget = Strings.Ride_RouteDone;
            StepRemaining = Moving;
            StepProgress = 1;
            NextStep = string.Empty;
        }
        else
        {
            StepLabel = s.Grade switch
            {
                >= 1 => Loc.Format(Strings.Ride_Climbing, s.Grade),
                <= -1 => Loc.Format(Strings.Ride_Descending, -s.Grade),
                _ => Strings.Ride_Flat,
            };
            StepTarget = Strings.Ride_ShiftCadence;
            StepRemaining = Units.FormatDistance(course.DistanceMeters - done, "0.0");
            StepProgress = done / course.DistanceMeters;
            var ahead = Units.DistanceToMeters(1);
            var end = Math.Min(course.DistanceMeters, done + ahead);
            var rise = course.ElevationAt(end) - course.ElevationAt(done);
            NextStep = end > done
                ? Loc.Format(Strings.Ride_NextDistance, Units.DistanceUnit(), rise / (end - done) * 100, Units.FormatElevation(course.GainBetween(done, end)))
                : string.Empty;
        }

        WorkoutProgress = Loc.Format(Strings.Ride_RouteProgress, Units.FormatDistance(Math.Min(done, course.DistanceMeters), "0.0"), Units.FormatDistance(course.DistanceMeters, "0.0"), Units.FormatElevation(climbed), Units.FormatElevation(course.ElevationGain));
    }

    /// <summary>
    /// Counts down the last 5 seconds of each workout step in a banner, with a beep at 3, 2 and 1 and a longer one as
    /// the next step starts. Free rides and routes have no steps to count down.
    /// </summary>
    private void UpdateCue(RideSnapshot s)
    {
        var timed = s.Step is { DurationSeconds: < int.MaxValue } && Workout.Course is null;
        if (!timed || s.State != RideState.Riding)
        {
            CueVisible = false;
            _lastStep = s.StepIndex;
            return;
        }

        if (s.StepIndex != _lastStep)
        {
            if (_lastStep >= 0 && _soundCues)
            {
                Sounds.Go();
            }

            _lastStep = s.StepIndex;
        }

        if (s.StepElapsed < 3 && s.StepIndex > 0)
        {
            CueVisible = true;
            CueCount = Strings.Ride_Go;
            CueTitle = s.Step!.Label;
            CueDetail = Format.Target(s.Step);
        }
        else if (s.StepRemaining is > 0 and <= 5)
        {
            CueVisible = true;
            CueCount = s.StepRemaining.ToString(System.Globalization.CultureInfo.CurrentCulture);
            CueTitle = s.Next is { } next ? Loc.Format(Strings.Ride_CueNext, next.Label) : Strings.Ride_CueLast;
            CueDetail = s.Next is { } n ? Format.Target(n) : Strings.Ride_CueContinue;
            if (s.StepRemaining <= 3 && _lastCue != (s.StepIndex, s.StepRemaining))
            {
                _lastCue = (s.StepIndex, s.StepRemaining);
                if (_soundCues)
                {
                    Sounds.Tick();
                }
            }
        }
        else
        {
            CueVisible = false;
        }
    }

    private void UpdateChart(RideSnapshot s, int seconds)
    {
        var finite = Workout.Steps.Count > 0 && Workout.Steps.All(st => st.DurationSeconds < int.MaxValue);
        double x;
        if (IsRoute)
        {
            // A route's chart runs along the road.
            x = s.DistanceKm * 1000;
        }
        else if (Workout.IsRampTest)
        {
            // Ending the ramp jumps the workout to its far-right cooldown, so from there the trace continues in time ridden.
            if (s.StepIndex >= Workout.Steps.Count - 1)
            {
                _cooldownFrom ??= (Position, seconds);
                x = _cooldownFrom.Value.X + (seconds - _cooldownFrom.Value.Seconds);
            }
            else
            {
                x = s.WorkoutElapsed;
            }
        }
        else if (!finite)
        {
            x = seconds;
        }
        else if (s.WorkoutComplete)
        {
            // Past the end of the workout, the trace continues to the right.
            _completedAt ??= seconds;
            x = Workout.TotalSeconds + (seconds - _completedAt.Value);
        }
        else
        {
            x = s.WorkoutElapsed;
        }

        Position = x;
        if (seconds > _lastRecorded)
        {
            _lastRecorded = seconds;
            _points.Add(new ChartPoint(x, s.Power, s.HeartRate, s.Grade));
            ChartVersion++;
        }
    }

    private void UpdateStats(RideFrame f)
    {
        var s = f.Snapshot;
        Set(RideStats,
        [
            new(Strings.Stat_Distance, Units.FormatDistance(s.DistanceKm * 1000)),
            new(Strings.Stat_Climbing, Units.FormatElevation(f.ElevationGain), Strings.Tip_Climbing),
            new(Strings.Stat_AvgMaxPower, Loc.Format(Strings.Ride_PowerPair, f.AveragePower, f.MaxPower)),
            new(Strings.Stat_Np, Loc.Format(Strings.Fmt_Watts, f.NormalizedPower), Tips.Np),
            new(Strings.Stat_IntensityFactor, f.IntensityFactor.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture), Tips.If),
            new(Strings.Stat_Tss, f.Tss.ToString("0", System.Globalization.CultureInfo.CurrentCulture), Tips.Tss),
            new(Strings.Stat_Work, Loc.Format(Strings.Ride_WorkValue, f.KiloJoules, f.Kilocalories), Tips.Work),
            new(Strings.Stat_AvgMaxHr, f.AverageHr is { } hr ? Loc.Format(Strings.Ride_HrPair, hr, f.MaxHr) : "-"),
            new(Strings.Stat_AvgCadence, f.AverageCadence is { } cad ? Loc.Format(Strings.Fmt_Rpm, cad) : "-"),
            new(Strings.Stat_AvgSpeed, Units.FormatSpeed(f.AverageSpeedMs)),
            new(Strings.Common_Best, string.Join(" / ", RideMetrics.PeakWindows.Select(w => f.Peaks.TryGetValue(w, out var p) ? p.ToString("0", System.Globalization.CultureInfo.CurrentCulture) : "-")), Tips.Peaks),
            new(Strings.Stat_WPrime, Loc.Format(Strings.Ride_WPrimeLeft, f.WBalance / 1000, f.WPrime / 1000.0), Tips.WPrime),
        ]);
        Set(LapStats,
        [
            new(Strings.Common_Lap, (f.LapIndex + 1).ToString(System.Globalization.CultureInfo.CurrentCulture)),
            new(Strings.Common_Time, Format.Clock(f.LapSeconds)),
            new(Strings.Stat_AvgMaxPower, Loc.Format(Strings.Ride_PowerPair, f.LapAveragePower, f.LapMaxPower)),
            new(Strings.Common_AvgHr, f.LapAverageHr is { } lhr ? Loc.Format(Strings.Fmt_Bpm, lhr) : "-"),
            new(Strings.Stat_AvgCadence, f.LapAverageCadence is { } lc ? Loc.Format(Strings.Fmt_Rpm, lc) : "-"),
            new(Strings.Common_OnTarget, f.LapCompliance is { } on ? on.ToString("P0", System.Globalization.CultureInfo.CurrentCulture) : "-", Tips.OnTarget),
        ]);

        ShowsWBalance = f.Seconds > 0;
        WBalanceFraction = Math.Clamp(f.WBalance / f.WPrime, 0, 1);

        var total = Math.Max(1, f.Seconds);
        var zones = f.PowerZones.Select((secs, i) => new ZoneRow(
            ZoneNames[Math.Min(i, ZoneNames.Length - 1)],
            Format.Clock(secs),
            (double)secs / total,
            ZoneBrushes[Math.Min(i, ZoneBrushes.Length - 1)])).ToList();
        Set(Zones, zones);
    }

    private static readonly IBrush[] ZoneBrushes =
        [.. new[] { 0.45, 0.65, 0.83, 0.98, 1.13, 1.35, 1.6 }.Select(f => (IBrush)new SolidColorBrush(WorkoutChart.ZoneColor(f)))];

    /// <summary>Updates rows in place, so the lists do not flicker every second.</summary>
    private static void Set<T>(ObservableCollection<T> target, IReadOnlyList<T> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i < target.Count)
            {
                if (!Equals(target[i], values[i]))
                {
                    target[i] = values[i];
                }
            }
            else
            {
                target.Add(values[i]);
            }
        }

        while (target.Count > values.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    /// <summary>Heart rate zones in the power zone colors: gray for the easiest, purple for the hardest of seven.</summary>
    internal static IBrush HeartRateZoneBrush(int zone, int count)
    {
        double[] fractions = count switch
        {
            <= 5 => [0.45, 0.65, 0.83, 1.13, 1.35],
            6 => [0.45, 0.65, 0.83, 0.98, 1.13, 1.35],
            _ => [0.45, 0.65, 0.83, 0.98, 1.13, 1.35, 1.6],
        };
        return HeartRateZoneBrushes.GetOrAdd(fractions[Math.Clamp(zone, 0, fractions.Length - 1)], f => new SolidColorBrush(WorkoutChart.ZoneColor(f)));
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<double, IBrush> HeartRateZoneBrushes = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Color, IBrush> GradeBrushes = new();

    private static IBrush Deviation(double value, double target, double ok, double warn)
    {
        var d = Math.Abs(value - target);
        return d <= ok ? Good : d <= warn ? Warn : Bad;
    }
}
