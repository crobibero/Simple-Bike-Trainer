using System.Diagnostics;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleBikeTrainer.Desktop.Controls;
using SimpleBikeTrainer.Desktop.Services;
using SimpleBikeTrainer.Intervals;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Desktop.ViewModels;

/// <summary>Owns the config, the devices and the page on screen.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    public MainWindowViewModel(AppConfig config)
    {
        Config = config;
        Devices = new DeviceManager(config);
        Home = new HomeViewModel(this);
        Current = Home;
    }

    public AppConfig Config { get; }

    public DeviceManager Devices { get; }

    public HomeViewModel Home { get; }

    public Athlete Athlete { get; set; } = null!;

    /// <summary>Opens a file picker; set by the window, which owns the platform storage provider.</summary>
    public Func<Task<string?>>? PickFile { get; set; }

    [ObservableProperty]
    public partial ViewModelBase Current { get; set; }

    /// <summary>The help panel over the current page.</summary>
    [ObservableProperty]
    public partial bool IsHelpOpen { get; set; }

    /// <summary>Set once the app starts closing, while the ride stops and the devices disconnect.</summary>
    [ObservableProperty]
    public partial bool IsShuttingDown { get; private set; }

    private Task? _disposing;

    public string Title => AppInfo.Name;

    [RelayCommand]
    private void ShowHelp() => IsHelpOpen = true;

    [RelayCommand]
    private void CloseHelp() => IsHelpOpen = false;

    public IntervalsClient? CreateIntervals() =>
        Config.HasApiKey ? new IntervalsClient(Config.Intervals.ApiKey, Config.Intervals.AthleteId) : null;

    public void StartRide(Workout workout)
    {
        var devices = Devices.Devices ?? throw new InvalidOperationException("Connect the trainer first.");
        var options = Athlete.Options(Config, workout);
        var session = new RideSession(workout, options, devices.Trainer, devices.HeartRate, devices.Shifters);
        var ride = new RideViewModel(this, session, options.Ftp, Devices);
        Current = ride;
        ride.Start();
    }

    public void ShowSummary(RideResult result, IReadOnlyList<ChartPoint> points, ViewModelBase? back = null) =>
        Current = new SummaryViewModel(this, result, points, back);

    public void ShowHistory()
    {
        var history = new HistoryViewModel(this);
        Current = history;
        history.LoadCommand.Execute(null);
    }

    public void ShowCalibration() => Current = new CalibrationViewModel(this);

    public void ShowHome() => Current = Home;

    public void ShowSettings() => Current = new SettingsViewModel(this);

    /// <summary>Escape closes the help; otherwise keyboard shortcuts work on the ride page only.</summary>
    public bool HandleKey(Key key)
    {
        if (IsShuttingDown)
        {
            return true;
        }

        if (key == Key.F1 && !IsHelpOpen)
        {
            IsHelpOpen = true;
            return true;
        }

        if (IsHelpOpen)
        {
            IsHelpOpen = key is not (Key.Escape or Key.F1);
            return true;
        }

        return Current is RideViewModel ride && ride.HandleKey(key);
    }

    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser or file manager registered; nothing else to do.
        }
    }

    /// <summary>Stops the ride and disconnects the devices. It runs once; a second call waits for the first.</summary>
    public ValueTask DisposeAsync()
    {
        IsShuttingDown = true;
        return new ValueTask(_disposing ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        if (Current is RideViewModel ride)
        {
            await ride.StopAsync();
        }

        await Devices.DisposeAsync();
    }
}
