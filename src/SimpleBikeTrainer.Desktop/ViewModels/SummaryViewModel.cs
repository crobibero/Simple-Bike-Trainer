using System.Collections.ObjectModel;
using System.Globalization;
using AsyncAwaitBestPractices;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleBikeTrainer.Desktop.Controls;
using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Uploads;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Desktop.ViewModels;

public sealed record LapRow(string Number, string Time, string AvgPower, string MaxPower, string AvgHr, string AvgCadence, string OnTarget);

public sealed partial class UploadRow : ObservableObject
{
    public required UploadTarget Target { get; init; }

    public required string Name { get; init; }

    public required bool Available { get; init; }

    [ObservableProperty]
    public partial bool Selected { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUrl))]
    public partial string? Url { get; set; }

    [ObservableProperty]
    public partial IBrush StatusBrush { get; set; } = Brushes.Gray;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasUrl => Url is not null;
}

/// <summary>The end-of-ride page: totals, laps, uploads, and the FTP from a ramp test.</summary>
public sealed partial class SummaryViewModel : ViewModelBase
{
    private static IBrush Good => Palette.Good;
    private static IBrush Bad => Palette.Bad;
    private static IBrush Muted => Palette.Muted;

    private readonly MainWindowViewModel _main;
    private readonly RideResult _result;

    private readonly ViewModelBase? _back;

    /// <param name="back">The page Done returns to; home when null.</param>
    public SummaryViewModel(MainWindowViewModel main, RideResult result, IReadOnlyList<ChartPoint> points, ViewModelBase? back = null)
    {
        _main = main;
        _back = back;
        _result = result;
        Points = points;
        var m = result.Metrics;
        var units = main.Config.Units;
        Title = result.Workout.Name;
        Subtitle = Loc.Format(Strings.Summary_Subtitle, result.StartUtc.ToLocalTime().ToString(Strings.Format_LongDateTime, CultureInfo.CurrentCulture), Format.Clock(m.Seconds));
        FitPath = result.FitPath;
        Ftp = m.Ftp;

        Headline =
        [
            new(Strings.Common_DistanceCaps, units.FormatDistance(m.DistanceMeters, "0.0")),
            new(Strings.Common_AvgPowerCaps, Loc.Format(Strings.Fmt_Watts, m.AveragePower)),
            new(Strings.Summary_NpCaps, Loc.Format(Strings.Fmt_Watts, m.NormalizedPower), Tips.Np),
            new(Strings.Summary_TssCaps, m.TrainingStressScore.ToString("0", CultureInfo.CurrentCulture), Tips.Tss),
            new(Strings.Summary_AvgHrCaps, m.AverageHeartRate is { } hr ? Loc.Format(Strings.Fmt_Bpm, hr) : "-"),
            m.ElevationGain >= 1
                ? new(Strings.Common_ClimbingCaps, units.FormatElevation(m.ElevationGain), Strings.Tip_Climbing)
                : new(Strings.Summary_WorkCaps, Loc.Format(Strings.Fmt_Kj, m.KiloJoules), Tips.Work),
        ];

        Stats =
        [
            new(Strings.Stat_ElapsedMoving, $"{Format.Clock(result.EndUtc - result.StartUtc)} / {Format.Clock(m.Seconds)}"),
            new(Strings.Stat_MaxPower, Loc.Format(Strings.Fmt_Watts, m.MaxPower)),
            new(Strings.Stat_Work, Loc.Format(Strings.Fmt_Kj, m.KiloJoules), Tips.Work),
            new(Strings.Stat_Climbing, units.FormatElevation(m.ElevationGain), Strings.Tip_Climbing),
            new(Strings.Stat_IntensityFactor, Loc.Format(Strings.Summary_IfValue, m.IntensityFactor, m.Ftp), Tips.If),
            new(Strings.Stat_Energy, Loc.Format(Strings.Summary_Energy, m.Kilocalories)),
            new(Strings.Summary_WPrimeLow, m.Seconds > 0 ? Loc.Format(Strings.Summary_WPrimeValue, m.MinWBalance / 1000, m.WPrime / 1000.0) : "-", Tips.WPrimeLow),
            new(Strings.Stat_MaxHr, m.MaxHeartRate is { } mhr ? Loc.Format(Strings.Fmt_Bpm, mhr) : "-"),
            new(Strings.Common_Cadence, m.AverageCadence is { } c ? Loc.Format(Strings.Summary_CadenceValue, c, m.MaxCadence) : "-"),
            new(Strings.Stat_Speed, Loc.Format(Strings.Summary_SpeedValue, units.Speed(m.AverageSpeedMs), units.FormatSpeed(m.MaxSpeedMs))),
            .. RideMetrics.PeakWindows.Where(m.Peaks.ContainsKey).Select(w => new StatRow(Loc.Format(Strings.Summary_Best, w < 60 ? Loc.Format(Strings.Common_SecondsShort, w) : Loc.Format(Strings.Common_Minutes, w / 60)), Loc.Format(Strings.Fmt_Watts, m.Peaks[w]))),
        ];

        Laps =
        [
            .. result.Laps.Select(l => new LapRow(
                $"{l.Index + 1}",
                Format.Clock(l.Seconds),
                $"{l.AveragePower:0}",
                $"{l.MaxPower}",
                l.AverageHeartRate is { } lhr ? $"{lhr:0}" : "-",
                l.AverageCadence is { } lc ? $"{lc:0}" : "-",
                l.Compliance is { } on ? $"{on:P0}" : "-")),
        ];

        if (result.FtpEstimate is { } ftp)
        {
            FtpEstimate = ftp;
            FtpText = Loc.Format(Strings.Summary_RampFtp, ftp, m.Peaks.GetValueOrDefault(60));
            CanSetFtp = main.Config.HasApiKey;
        }

        var uploads = main.CreateUploads();
        var defaults = uploads.DefaultTargets();
        foreach (var (target, name, hint) in new[]
        {
            (UploadTarget.Intervals, Strings.Service_Intervals, Strings.Summary_NeedsKey),
            (UploadTarget.Garmin, Strings.Service_Garmin, Strings.Summary_NeedsGarmin),
        })
        {
            var available = uploads.IsAvailable(target);
            Uploads.Add(new UploadRow
            {
                Target = target,
                Name = name,
                Available = available && FitPath is not null,
                Selected = available && defaults.Contains(target),
                Status = FitPath is null ? Strings.Summary_NothingRecorded : available ? string.Empty : hint,
                StatusBrush = Muted,
            });
        }

        if (main.Config.Uploads.Automatic && FitPath is not null && Uploads.Any(u => u.Selected))
        {
            UploadAsync().SafeFireAndForget();
        }
    }

    public string Title { get; }

    public string Subtitle { get; }

    public string? FitPath { get; }

    public bool HasFit => FitPath is not null;

    public int Ftp { get; }

    public Workout Workout => _result.Workout;

    public bool IsRoute => Workout.Course is not null;

    public IReadOnlyList<ChartPoint> Points { get; }

    public double ChartEnd => Points.Count > 0 ? Points[^1].X : -1;

    public IReadOnlyList<StatRow> Headline { get; }

    public IReadOnlyList<StatRow> Stats { get; }

    public IReadOnlyList<LapRow> Laps { get; }

    public bool HasLaps => Laps.Count > 1;

    public ObservableCollection<UploadRow> Uploads { get; } = [];

    public int? FtpEstimate { get; }

    public bool HasFtpEstimate => FtpEstimate is not null;

    public string? FtpText { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UploadCommand))]
    public partial bool IsUploading { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetFtpCommand))]
    public partial bool CanSetFtp { get; set; }

    [ObservableProperty]
    public partial string? FtpStatus { get; set; }

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task UploadAsync()
    {
        IsUploading = true;
        try
        {
            await UploadRowsAsync();
        }
        finally
        {
            IsUploading = false;
        }
    }

    private async Task UploadRowsAsync()
    {
        var uploads = _main.CreateUploads();
        var rows = Uploads.Where(u => u.Selected && u.Available).ToList();

        // The destinations are independent, so they go up together.
        await Task.WhenAll(rows.Select(async row =>
        {
            row.IsBusy = true;
            row.Status = Strings.Summary_Uploading;
            row.StatusBrush = Muted;
            var outcome = await uploads.UploadAsync(row.Target, FitPath!, Title, _result.Workout.EventId, _result.Workout.Description, CancellationToken.None);
            row.IsBusy = false;
            row.Url = outcome.Url;
            row.Status = outcome.State switch
            {
                UploadState.Uploaded => Strings.Summary_Uploaded,
                UploadState.Duplicate => Strings.Summary_Duplicate,
                _ => outcome.Error ?? Strings.Summary_Failed,
            };
            row.StatusBrush = outcome.Succeeded ? Good : Bad;
            if (outcome.Succeeded)
            {
                row.Selected = false;
            }
        }));
    }

    private bool CanUpload() => !IsUploading && FitPath is not null;

    [RelayCommand(CanExecute = nameof(CanSetFtp))]
    private async Task SetFtpAsync()
    {
        using var intervals = _main.CreateIntervals();
        if (intervals is null || FtpEstimate is not { } ftp)
        {
            return;
        }

        try
        {
            await intervals.SetFtpAsync(ftp, alsoIndoor: _main.Athlete.Settings?.IndoorFtp is > 0);
            FtpStatus = Loc.Format(Strings.Summary_FtpSet, ftp);
            CanSetFtp = false;
        }
        catch (Exception e) when (e is Intervals.IntervalsException or HttpRequestException)
        {
            FtpStatus = e.Message;
        }
    }

    [RelayCommand]
    private static void OpenUrl(string? url)
    {
        if (url is not null)
        {
            MainWindowViewModel.Open(url);
        }
    }

    public static string FitButtonText => OperatingSystem.IsAndroid() ? Strings.Summary_ShareFit : Strings.Summary_ShowFit;

    [RelayCommand]
    private void OpenFolder()
    {
        if (FitPath is not null)
        {
#if ANDROID
            Platforms.AndroidApp.ShareFile(FitPath);
#else
            MainWindowViewModel.Open(Path.GetDirectoryName(FitPath)!);
#endif
        }
    }

    public override bool GoBack()
    {
        DoneCommand.Execute(null);
        return true;
    }

    [RelayCommand]
    private async Task DoneAsync()
    {
        if (_back is HistoryViewModel history)
        {
            _main.Current = history;
            await history.LoadCommand.ExecuteAsync(null);
            return;
        }

        _main.ShowHome();
        if (FtpStatus is not null)
        {
            await _main.Home.RefreshCommand.ExecuteAsync(null);
        }
    }
}
