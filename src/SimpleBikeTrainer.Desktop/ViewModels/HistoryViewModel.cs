using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleBikeTrainer.Desktop.Controls;
using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Recording;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Desktop.ViewModels;

/// <summary>One saved ride in the history list.</summary>
public sealed record HistoryRow(
    RideSummary Ride,
    string Name,
    string When,
    string Duration,
    string Distance,
    string Power,
    string Tss,
    string Climbing,
    string Uploads,
    bool IsUploaded);

/// <summary>Every saved ride, newest first, with this week's totals. Opening one shows its summary.</summary>
public sealed partial class HistoryViewModel(MainWindowViewModel main) : ViewModelBase
{
    public ObservableCollection<HistoryRow> Rides { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty] public partial string WeekText { get; set; } = string.Empty;

    [ObservableProperty] public partial string TotalText { get; set; } = string.Empty;

    [ObservableProperty] public partial string? Status { get; set; }

    public string Folder => main.Config.RidesDirectory;

    public bool IsEmpty => !IsLoading && Rides.Count == 0;

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        var units = main.Config.Units;

        // Reading every FIT file can take a moment with many rides.
        var rides = await Task.Run(() => new RideLibrary(main.Config).List());
        Rides.Clear();
        foreach (var r in rides)
        {
            var uploads = r.Uploads.Keys.Select(k => k switch { "Intervals" => Strings.Service_Intervals, "Garmin" => Strings.Service_Garmin, _ => k }).ToList();
            Rides.Add(new HistoryRow(
                r,
                r.Name,
                r.StartUtc.ToLocalTime().ToString(Strings.Format_DateTime, CultureInfo.CurrentCulture),
                Format.Clock(r.MovingSeconds),
                units.FormatDistance(r.DistanceMeters, "0.0"),
                Loc.Format(Strings.Fmt_Watts, r.AveragePower),
                r.TrainingStressScore.ToString("0", CultureInfo.CurrentCulture),
                r.ElevationGain >= 1 ? units.FormatElevation(r.ElevationGain) : "-",
                uploads.Count > 0 ? string.Join(" · ", uploads) : Strings.History_NotUploaded,
                uploads.Count > 0));
        }

        // Monday to Sunday, like Intervals.icu's week.
        var today = DateTime.Today;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var week = rides.Where(r => r.StartUtc.ToLocalTime() >= monday).ToList();
        WeekText = Totals(week, units);
        TotalText = rides.Count == 0 ? string.Empty : Loc.Format(Strings.History_AllTime, Totals(rides, units));
        IsLoading = false;
    }

    [RelayCommand]
    private async Task OpenAsync(HistoryRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            var athlete = main.Athlete;
            var loaded = await Task.Run(() => RideLibrary.Load(row.Ride.FitPath, athlete.WPrime, athlete.PowerZones, athlete.HrZones));
            main.ShowSummary(loaded.Result, ChartPoints.FromSamples(loaded.Samples, loaded.Result.Workout), this);
        }
        catch (Exception e) when (e is IOException or Dynastream.Fit.FitException)
        {
            Status = Loc.Format(Strings.Common_CouldNotRead, Path.GetFileName(row.Ride.FitPath), e.Message);
        }
    }

    [RelayCommand]
    private void OpenFolder() => MainWindowViewModel.Open(Folder);

    public override bool GoBack()
    {
        Back();
        return true;
    }

    [RelayCommand]
    private void Back() => main.ShowHome();

    private static string Totals(IReadOnlyCollection<RideSummary> rides, UnitSystem units) =>
        rides.Count == 0
            ? Strings.History_NoRides
            : string.Join("  ·  ", new[]
            {
                rides.Count == 1 ? Strings.History_OneRide : Loc.Format(Strings.History_Rides, rides.Count),
                Format.Clock(rides.Sum(r => r.MovingSeconds)),
                units.FormatDistance(rides.Sum(r => r.DistanceMeters), "0"),
                Loc.Format(Strings.Fmt_Tss, rides.Sum(r => r.TrainingStressScore)),
                rides.Sum(r => r.ElevationGain) >= 1 ? Loc.Format(Strings.History_Climbing, units.FormatElevation(rides.Sum(r => r.ElevationGain))) : null,
            }.OfType<string>());
}
