using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging.Messages;
using CommunityToolkit.Mvvm.Messaging;
using SimpleBikeTrainer.Desktop.Controls;
using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Desktop.Services;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Desktop.ViewModels;

public enum CardKind
{
    Planned,
    FreeRide,
    RampTest,
    File,
    Route,
}

public sealed record StepRow(string Label, string Duration, string Target, Avalonia.Media.IBrush Swatch);

/// <summary>One choice on the home page: a planned ride, free ride, the ramp test, a workout file, or a generated route.</summary>
public sealed partial class WorkoutCard : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfile))]
    public partial Workout Workout { get; set; } = Workout.FreeRide();

    public CardKind Kind { get; init; }

    public bool IsRoute => Kind == CardKind.Route;

    public bool IsToday { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<StepRow> Steps { get; init; } = [];

    public bool HasProfile => Workout.Course is not null || Workout.Steps.Count > 1 || Workout.Steps.FirstOrDefault()?.DurationSeconds < int.MaxValue;
}

/// <summary>An unfinished ride offered for recovery on the home page.</summary>
public sealed partial class InterruptedRow(SimpleBikeTrainer.Recording.InterruptedRide ride, string text) : ObservableObject
{
    public SimpleBikeTrainer.Recording.InterruptedRide Ride { get; } = ride;

    [ObservableProperty]
    public partial string Text { get; set; } = text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscardLabel))]
    public partial bool ConfirmDiscard { get; set; }

    public string DiscardLabel => ConfirmDiscard ? Strings.Home_ConfirmDiscard : Strings.Home_Discard;
}

public sealed partial class HomeViewModel : ViewModelBase, IRecipient<PropertyChangedMessage<bool>>
{
    private readonly MainWindowViewModel main;

    public HomeViewModel(MainWindowViewModel main)
    {
        this.main = main;
        WeakReferenceMessenger.Default.Register(this);
    }

    /// <summary>Start ride needs a connected trainer, and no scan or connect in progress.</summary>
    public void Receive(PropertyChangedMessage<bool> message)
    {
        if (message.Sender == main.Devices && message.PropertyName is nameof(DeviceManager.IsConnected) or nameof(DeviceManager.IsConnecting) or nameof(DeviceManager.IsScanning))
        {
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Every card: <see cref="PlannedCards"/> then <see cref="OtherCards"/>.</summary>
    public IReadOnlyList<WorkoutCard> Cards => [.. PlannedCards, .. OtherCards];

    /// <summary>Rides planned in Intervals.icu for the next 7 days.</summary>
    public ObservableCollection<WorkoutCard> PlannedCards { get; } = [];

    /// <summary>Free ride, a generated route, the ramp test, and opened workout files.</summary>
    public ObservableCollection<WorkoutCard> OtherCards { get; } = [];

    public DeviceManager Devices => main.Devices;

    public int Ftp => main.Athlete?.Ftp ?? main.Config.Rider.Ftp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial WorkoutCard? Selected { get; set; }

    /// <summary>The selection in the planned list; picking one there clears the other list's.</summary>
    [ObservableProperty]
    public partial WorkoutCard? SelectedPlanned { get; set; }

    [ObservableProperty]
    public partial WorkoutCard? SelectedOther { get; set; }

    /// <summary>Route length, in km or mi.</summary>
    [ObservableProperty]
    public partial decimal? RouteDistance { get; set; }

    /// <summary>Route climbing, in m or ft.</summary>
    [ObservableProperty]
    public partial decimal? RouteClimb { get; set; }

    [ObservableProperty]
    public partial string RouteStats { get; set; } = string.Empty;

    public string DistanceLabel => Loc.Format(Strings.Home_DistanceLabel, main.Config.Units.DistanceUnit());

    public string ClimbLabel => Loc.Format(Strings.Home_ClimbLabel, main.Config.Units.ElevationUnit());

    public string AppName => AppInfo.Name;

    private int _routeSeed = Random.Shared.Next();
    private UnitSystem _routeUnits;

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial string AthleteText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Why planned rides did not load, for the error card; null when they did.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    public partial string? LoadError { get; set; }

    /// <summary>The underlying error, shown on hover.</summary>
    [ObservableProperty]
    public partial string? LoadErrorDetail { get; set; }

    /// <summary>The fix is in Settings (the API key or athlete ID).</summary>
    [ObservableProperty]
    public partial bool LoadErrorNeedsSettings { get; set; }

    public bool HasLoadError => LoadError is not null;

    /// <summary>Rides cut off by a crash or power loss, with only their last 5-minute checkpoint saved.</summary>
    public ObservableCollection<InterruptedRow> Interrupted { get; } = [];

    public Task InitializeAsync() => RefreshAsync();

    // Counts refreshes, so one that a newer refresh overtook stops at its next await instead of adding its cards too.
    private int _refreshes;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var refresh = ++_refreshes;
        IsLoading = true;
        Status = null;
        LoadError = null;
        var previous = Selected?.Workout.EventId;
        Selected = null;
        PlannedCards.Clear();
        OtherCards.Clear();

        await FindInterruptedAsync();
        if (refresh != _refreshes)
        {
            return;
        }

        // The other rides are ready at once, so a free ride does not wait for Intervals.icu.
        main.Athlete = Athlete.FromConfig(main.Config);
        AddBuiltIns();
        var untouched = Selected;
        try
        {
            using var intervals = main.CreateIntervals();
            if (intervals is null)
            {
                Status = Strings.Home_NoApiKey;
                return;
            }

            var athlete = await Athlete.LoadAsync(main.Config, intervals, CancellationToken.None);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var planned = await intervals.GetPlannedRidesAsync(today, today.AddDays(6));
            if (refresh != _refreshes)
            {
                return;
            }

            main.Athlete = athlete;
            AddPlanned(planned);
            if (PlannedCards.Count == 0)
            {
                Status = Strings.Home_NothingPlanned;
            }

            // Intervals.icu's FTP and weight replace the config's.
            UpdateAthlete();
            GenerateRoute();
        }
        catch (Exception e) when (e is Intervals.IntervalsException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or FormatException)
        {
            if (refresh == _refreshes)
            {
                (LoadError, LoadErrorNeedsSettings) = DescribeLoadError(e, main.Config.Intervals.AthleteId);
                LoadErrorDetail = e.Message;
            }
        }
        finally
        {
            if (refresh == _refreshes)
            {
                IsLoading = false;

                // Pick the planned ride again, or today's, unless the rider chose something while it loaded.
                if (Selected == untouched)
                {
                    Selected = PlannedCards.FirstOrDefault(c => previous is not null && c.Workout.EventId == previous)
                        ?? PlannedCards.FirstOrDefault(c => c.IsToday) ?? Selected;
                }
            }
        }
    }

    /// <summary>What went wrong loading planned rides, in words a rider can act on, and whether Settings can fix it.</summary>
    internal static (string Message, bool Settings) DescribeLoadError(Exception e, string athleteId) => e switch
    {
        Intervals.IntervalsException { Status: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } =>
            (Strings.LoadError_Key, true),
        Intervals.IntervalsException { Status: System.Net.HttpStatusCode.NotFound } =>
            (Loc.Format(Strings.LoadError_Athlete, athleteId), true),
        Intervals.IntervalsException { Status: System.Net.HttpStatusCode.TooManyRequests } =>
            (Strings.LoadError_RateLimit, false),
        Intervals.IntervalsException { Status: >= System.Net.HttpStatusCode.InternalServerError and var status } =>
            (Loc.Format(Strings.LoadError_Server, (int)status!), false),
        TaskCanceledException =>
            (Strings.LoadError_Timeout, false),
        HttpRequestException =>
            (Strings.LoadError_Offline, false),
        System.Text.Json.JsonException or FormatException =>
            (Strings.LoadError_Unreadable, false),
        _ => (e.Message, false),
    };

    /// <summary>Adds Intervals.icu's planned rides as cards.</summary>
    internal void AddPlanned(IEnumerable<Intervals.PlannedRide> rides)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        foreach (var ride in rides)
        {
            var workout = RideSetup.ForPlanned(ride.Workout);
            PlannedCards.Add(Card(workout, ride.Date == today ? Strings.Home_Today : ride.Date.ToString(Strings.Format_ShortDate, CultureInfo.CurrentCulture), CardKind.Planned, ride.Date == today, ride.Workout.Description));
        }
    }

    internal void AddBuiltIns()
    {
        OtherCards.Add(Card(Workout.FreeRide(), Strings.Home_FreeRideSubtitle, CardKind.FreeRide));
        var units = main.Config.Units;
        if (_routeUnits != units && RouteDistance is { } d && RouteClimb is { } c)
        {
            // The units setting changed: keep the same route size in the new units.
            RouteDistance = Math.Round((decimal)units.Distance(_routeUnits.DistanceToMeters((double)d)), 1);
            RouteClimb = Math.Round((decimal)units.Elevation(_routeUnits.ElevationToMeters((double)c)));
        }

        _routeUnits = units;
        RouteDistance ??= units == UnitSystem.Imperial ? 20 : 30;
        RouteClimb ??= units == UnitSystem.Imperial ? 1200 : 400;
        OnPropertyChanged(nameof(DistanceLabel));
        OnPropertyChanged(nameof(ClimbLabel));
        OtherCards.Add(new WorkoutCard { Kind = CardKind.Route });
        GenerateRoute();
        OtherCards.Add(Card(RampTest.Create(), Strings.Home_RampSubtitle, CardKind.RampTest));
        UpdateAthlete();
        Selected ??= OtherCards.FirstOrDefault();
    }

    private void UpdateAthlete()
    {
        AthleteText = Loc.Format(Strings.Home_Athlete, main.Athlete.Ftp, main.Athlete.WPrime / 1000.0, main.Config.Units.FormatWeight(main.Athlete.WeightKg));
        OnPropertyChanged(nameof(Ftp));
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        if (main.PickFile is null || await main.PickFile() is not { } path)
        {
            return;
        }

        try
        {
            var card = Card(RideSetup.LoadFile(path), Path.GetFileName(path), CardKind.File);
            OtherCards.Insert(0, card);
            Selected = card;
            Status = null;
        }
        catch (Exception e) when (e is FormatException or IOException or System.Text.Json.JsonException)
        {
            Status = Loc.Format(Strings.Common_CouldNotRead, Path.GetFileName(path), e.Message);
        }
    }

    [RelayCommand]
    private Task ConnectAsync() => Devices.ConnectAsync(main.Config);

    [RelayCommand]
    private Task ScanAsync() => Devices.ScanAsync(main.Config);

    [RelayCommand]
    private Task ConnectChosenAsync() => Devices.ConnectChosenAsync(main.Config);

    [RelayCommand]
    private Task CancelDevicesAsync() => Devices.CancelAsync();

    [RelayCommand]
    private Task DisconnectAsync() => Devices.DisconnectAsync();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (Selected is { } card)
        {
            main.StartRide(card.Workout);
        }
    }

    private bool CanStart() => Selected is not null && Devices.IsConnected && !Devices.IsBusy;

    [RelayCommand]
    private void Settings() => main.ShowSettings();

    [RelayCommand]
    private void History() => main.ShowHistory();

    [RelayCommand]
    private void Calibrate() => main.ShowCalibration();

    internal async Task FindInterruptedAsync()
    {
        var library = new SimpleBikeTrainer.Recording.RideLibrary(main.Config);
        var found = await Task.Run(library.FindInterrupted);
        Interrupted.Clear();
        foreach (var ride in found)
        {
            Interrupted.Add(new InterruptedRow(ride,
                Loc.Format(Strings.Home_Interrupted, ride.Name, ride.StartUtc.ToLocalTime().ToString(Strings.Format_DateTime, CultureInfo.CurrentCulture), Format.Clock(ride.MovingSeconds))));
        }
    }

    /// <summary>Keeps the checkpoint as a finished ride and opens its summary, where it can be uploaded.</summary>
    [RelayCommand]
    private async Task RecoverAsync(InterruptedRow row)
    {
        try
        {
            var athlete = main.Athlete;
            var loaded = await Task.Run(() => new SimpleBikeTrainer.Recording.RideLibrary(main.Config).Recover(row.Ride, athlete.WPrime));
            Interrupted.Remove(row);
            main.ShowSummary(loaded.Result, ChartPoints.FromSamples(loaded.Samples, loaded.Result.Workout));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Dynastream.Fit.FitException)
        {
            row.Text = Loc.Format(Strings.Home_RecoverFailed, e.Message);
        }
    }

    /// <summary>Deletes the checkpoint. Two presses, like Finish, so a stray click cannot lose a ride.</summary>
    [RelayCommand]
    private void Discard(InterruptedRow row)
    {
        if (!row.ConfirmDiscard)
        {
            row.ConfirmDiscard = true;
            return;
        }

        SimpleBikeTrainer.Recording.RideLibrary.Discard(row.Ride);
        Interrupted.Remove(row);
    }

    /// <summary>A different route of the same length and climbing.</summary>
    [RelayCommand]
    private void NewRoute()
    {
        _routeSeed = Random.Shared.Next();
        GenerateRoute();
    }

    partial void OnRouteDistanceChanged(decimal? value) => GenerateRoute();

    partial void OnRouteClimbChanged(decimal? value) => GenerateRoute();

    /// <summary>Rebuilds the route card from the distance, climbing and seed.</summary>
    private void GenerateRoute()
    {
        if (OtherCards.FirstOrDefault(c => c.IsRoute) is not { } card || RouteDistance is not { } distance || RouteClimb is not { } climb)
        {
            return;
        }

        var units = main.Config.Units;
        var workout = RideSetup.Route(units, (double)distance, (double)climb, _routeSeed);
        var course = workout.Course!;
        card.Workout = workout;
        card.Title = Strings.Home_GenerateRoute;
        card.Subtitle = Loc.Format(Strings.Home_RouteSubtitle, units.FormatDistance(course.DistanceMeters, "0.#"), units.FormatElevation(course.ElevationGain));

        // How long it takes at a steady 70% of FTP, from the same physics the ride uses.
        var rider = main.Athlete.Rider(main.Config);
        var power = main.Athlete.Ftp * 0.7;
        double seconds = 0;
        for (var i = 1; i < course.Points.Count; i++)
        {
            var (a, b) = (course.Points[i - 1], course.Points[i]);
            var length = b.DistanceMeters - a.DistanceMeters;
            seconds += length / Math.Max(0.5, Physics.SpeedForPower(power, (b.ElevationMeters - a.ElevationMeters) / length * 100, rider));
        }

        var asked = units.ElevationToMeters((double)climb);
        RouteStats = Loc.Format(Strings.Home_RouteStats, course.MaxGrade, Format.Clock((long)seconds), power)
            + (course.ElevationGain < asked - 1 ? Loc.Format(Strings.Home_RouteCapped, CourseGenerator.MaxGrade, units.FormatElevation(course.ElevationGain)) : string.Empty);
    }

    partial void OnSelectedChanged(WorkoutCard? value)
    {
        SelectedPlanned = value?.Kind == CardKind.Planned ? value : null;
        SelectedOther = value is { Kind: not CardKind.Planned } ? value : null;
    }

    partial void OnSelectedPlannedChanged(WorkoutCard? value)
    {
        if (value is not null)
        {
            Selected = value;
        }
    }

    partial void OnSelectedOtherChanged(WorkoutCard? value)
    {
        if (value is not null)
        {
            Selected = value;
        }
    }

    private WorkoutCard Card(Workout workout, string subtitle, CardKind kind, bool today = false, string? description = null)
    {
        var length = workout.IsRampTest ? Strings.Home_RampLength
            : workout.Steps.Count == 0 || workout.Steps[0].DurationSeconds == int.MaxValue ? Strings.Home_OpenEnded
            : Loc.Format(Strings.Common_Minutes, workout.TotalSeconds / 60);
        var ftp = workout.Ftp ?? main.Athlete.Ftp;
        var steps = workout.IsRampTest
            ? workout.Steps.Take(4).Select(s => Row(s, ftp)).Append(new StepRow(Strings.Home_RampMore, string.Empty, string.Empty, Avalonia.Media.Brushes.Transparent)).ToList()
            : workout.Steps.Where(s => s.DurationSeconds < int.MaxValue).Select(s => Row(s, ftp)).ToList();
        return new WorkoutCard
        {
            Title = workout.Name,
            Kind = kind,
            Subtitle = $"{subtitle}  ·  {length}",
            Workout = workout,
            IsToday = today,
            Description = description,
            Steps = steps,
        };
    }

    private static StepRow Row(WorkoutStep step, int ftp)
    {
        var minutes = step.DurationSeconds % 60 == 0 ? Loc.Format(Strings.Common_Minutes, step.DurationSeconds / 60) : $"{step.DurationSeconds / 60}:{step.DurationSeconds % 60:00}";
        var target = step.Power?.ToString() ?? Strings.Common_FreeRide;
        if (step.Power is { } power && power.Unit == PowerUnit.Watts)
        {
            target += $"  ({power.WattsAt(0, ftp) * 100 / Math.Max(1, ftp)}%)";
        }

        if (step.Cadence is { } c)
        {
            target += $"  ·  {c}";
        }

        var color = step.Power is { } pw ? Controls.WorkoutChart.ZoneColor(pw.WattsAt(0.5, ftp) / (double)ftp) : Avalonia.Media.Color.Parse("#555");
        return new StepRow(step.Label, minutes, target, new Avalonia.Media.SolidColorBrush(color));
    }
}
