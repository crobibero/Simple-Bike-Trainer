using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Desktop.ViewModels;

/// <summary>
/// Spin-down calibration over FTMS: the rider speeds up past the trainer's target speed, stops pedaling, and the
/// trainer measures how the flywheel slows to correct its power readings.
/// </summary>
public sealed partial class CalibrationViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly ITrainer? _trainer;
    private readonly UnitSystem _units;
    private double? _low;
    private double? _high;

    public CalibrationViewModel(MainWindowViewModel main)
    {
        _main = main;
        _trainer = main.Devices.Devices?.Trainer;
        _units = main.Config.Units;
        TrainerName = _trainer?.Name ?? Strings.Cal_NoTrainer;
        IsSupported = _trainer?.SupportsSpinDown == true;
        Instruction = _trainer is null
            ? Strings.Cal_ConnectFirst
            : IsSupported
                ? Strings.Cal_Instructions
                : Loc.Format(Strings.Cal_Unsupported, TrainerName);
        if (_trainer is not null)
        {
            _trainer.DataReceived += OnData;
            _trainer.SpinDownUpdated += OnUpdate;
        }
    }

    public string TrainerName { get; }

    public bool IsSupported { get; }

    public string SpeedUnit => _units.SpeedUnit();

    [ObservableProperty] public partial string Instruction { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial string Speed { get; set; } = "0.0";

    [ObservableProperty] public partial string Target { get; set; } = string.Empty;

    [ObservableProperty] public partial string Step { get; set; } = string.Empty;

    [ObservableProperty] public partial bool? Succeeded { get; set; }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsRunning = true;
        Succeeded = null;
        Target = string.Empty;
        Step = Strings.Cal_Asking;
        try
        {
            await _trainer!.StartSpinDownAsync();
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or IOException)
        {
            Fail(e.Message);
        }
    }

    private bool CanStart() => IsSupported && !IsRunning;

    [RelayCommand]
    private void Back()
    {
        if (_trainer is not null)
        {
            _trainer.DataReceived -= OnData;
            _trainer.SpinDownUpdated -= OnUpdate;
        }

        _main.ShowHome();
    }

    private void OnData(IndoorBikeData data)
    {
        if (data.SpeedKmh is { } kmh)
        {
            Dispatcher.UIThread.Post(() => Speed = _units.Speed(kmh / 3.6).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture));
        }
    }

    private void OnUpdate(SpinDownUpdate update) => Dispatcher.UIThread.Post(() =>
    {
        switch (update.State)
        {
            case SpinDownState.SpeedUp:
                _low = update.TargetLowKmh ?? _low;
                _high = update.TargetHighKmh ?? _high;
                Step = Strings.Cal_SpeedUp;
                Target = _high is { } high
                    ? Loc.Format(Strings.Cal_TargetAbove, _units.FormatSpeed(high / 3.6)) + (_low is { } low ? Loc.Format(Strings.Cal_AtLeast, _units.FormatSpeed(low / 3.6)) : string.Empty)
                    : Strings.Cal_TargetUnknown;
                break;
            case SpinDownState.StopPedaling:
                Step = Strings.Cal_Stop;
                Target = string.Empty;
                break;
            case SpinDownState.Success:
                Step = Strings.Cal_Done;
                Succeeded = true;
                IsRunning = false;
                break;
            case SpinDownState.Error:
                Fail(Strings.Cal_Failed);
                break;
        }
    });

    private void Fail(string message)
    {
        Step = message;
        Succeeded = false;
        IsRunning = false;
    }
}
