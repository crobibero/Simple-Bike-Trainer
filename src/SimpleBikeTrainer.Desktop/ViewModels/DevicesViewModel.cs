using System.Collections.ObjectModel;
using System.ComponentModel;
using AsyncAwaitBestPractices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Desktop.Services;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Desktop.ViewModels;

/// <summary>A paired device: whether it is connected and what it reports, a name to edit, and Forget.</summary>
public sealed partial class PairedDeviceRow : ObservableObject
{
    public required DeviceKind DeviceKind { get; init; }

    public string Kind => CandidateRow.LabelOf(DeviceKind);

    public required DeviceId Id { get; init; }

    /// <summary>What the device calls itself, shown when the name box is empty, which goes back to it.</summary>
    public required string AdvertisedName { get; init; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>The device while it is connected, or trying to; null while not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsTrying), nameof(CanConnect))]
    public partial DeviceRow? Live { get; set; }

    /// <summary>Among the connected devices, whether its link is up or it is still trying.</summary>
    public bool IsConnected => Live is not null;

    /// <summary>Among the connected devices, but its link is down: it keeps trying.</summary>
    public bool IsTrying => Live is { Connected: false };

    public bool IsLinked => Live is { Connected: true };

    partial void OnLiveChanged(DeviceRow? oldValue, DeviceRow? newValue)
    {
        oldValue?.PropertyChanged -= OnLivePropertyChanged;
        newValue?.PropertyChanged += OnLivePropertyChanged;
        OnPropertyChanged(nameof(IsLinked));
    }

    private void OnLivePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceRow.Connected))
        {
            OnPropertyChanged(nameof(IsTrying));
            OnPropertyChanged(nameof(IsLinked));
        }
    }

    /// <summary>Connect was pressed: the scan is looking for it, or it is connecting.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    public partial bool IsConnecting { get; set; }

    public bool CanConnect => !IsConnected && !IsConnecting;

    /// <summary>Forget was pressed once; a second press forgets the device.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForgetLabel))]
    public partial bool ConfirmForget { get; set; }

    public string ForgetLabel => ConfirmForget ? Strings.Settings_ConfirmForget : Strings.Settings_Forget;
}

public sealed record SourceOption(MetricSource Source, string Label);

/// <summary>Where one reading comes from, and which device gives it now.</summary>
public sealed partial class MetricChoice : ObservableObject
{
    public required Metric Metric { get; init; }

    public required string Label { get; init; }

    public required IReadOnlyList<SourceOption> Options { get; init; }

    [ObservableProperty]
    public partial SourceOption Selected { get; set; } = null!;

    /// <summary>Such as "KICKR CORE 6107: 182 W"; or that nothing reports it.</summary>
    [ObservableProperty]
    public partial string Now { get; set; } = string.Empty;
}

/// <summary>
/// The Devices page. Pairing and forgetting take effect at once; names and reading sources wait for Save, and Cancel
/// drops them.
/// </summary>
public sealed partial class DevicesViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private readonly AppConfig _config;
    private readonly ViewModelBase _back;
    private readonly DispatcherTimer _timer;

    public DevicesViewModel(MainWindowViewModel main, ViewModelBase back)
    {
        _main = main;
        _config = main.Config;
        _back = back;
        Metrics =
        [
            Choice(Metric.Power, Strings.Devices_Power),
            Choice(Metric.Cadence, Strings.Devices_Cadence),
            Choice(Metric.HeartRate, Strings.Devices_HeartRate),
        ];
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshNow());
    }

    public DeviceManager Devices => _main.Devices;

    public ObservableCollection<PairedDeviceRow> Paired { get; } = [];

    [ObservableProperty]
    public partial bool HasPaired { get; set; }

    public IReadOnlyList<MetricChoice> Metrics { get; }

    /// <summary>Forget all was pressed once; a second press forgets every device.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForgetAllLabel))]
    public partial bool ConfirmForgetAll { get; set; }

    public string ForgetAllLabel => ConfirmForgetAll ? Strings.Settings_ConfirmForgetAll : Strings.Settings_ForgetDevices;

    /// <summary>Connect all: a trainer is paired and a paired device is not connected, or the pretend devices are not.</summary>
    public bool CanConnectAll => !Devices.IsConnecting && (Devices.Simulate ? !Devices.IsConnected : Devices.HasSaved && Devices.Saved.Count > 0);

    public string ScanLabel => Devices.IsScanning ? Strings.Devices_StopScan : Strings.Devices_Scan;

    /// <summary>Saves the names and reading sources, and goes back.</summary>
    [RelayCommand]
    private void Save()
    {
        foreach (var row in Paired.Where(r => r.Name != _config.Devices.NameOf(r.Id)))
        {
            _config.Devices.Rename(row.Id, row.Name);
        }

        foreach (var choice in Metrics)
        {
            _config.Devices.Metrics[choice.Metric] = choice.Selected.Source;
        }

        _config.Save();
        Devices.SavedChanged();
        _main.Current = _back;
    }

    /// <summary>Goes back, dropping the names and reading sources not saved.</summary>
    [RelayCommand]
    private void Cancel() => _main.Current = _back;

    public override bool GoBack()
    {
        Cancel();
        return true;
    }

    [RelayCommand]
    private Task ScanAsync() => Devices.IsScanning ? Devices.StopScanAsync() : Devices.ScanAsync(_config);

    [RelayCommand]
    private Task PairAsync(CandidateRow row) => Devices.PairAsync(row, _config);

    [RelayCommand]
    private Task ConnectAllAsync() => Devices.ConnectAsync(_config);

    [RelayCommand]
    private Task CancelConnectAsync() => Devices.CancelAsync();

    [RelayCommand]
    private Task DisconnectAsync() => Devices.DisconnectAsync();

    /// <summary>Spin-down calibration of the connected trainer; Back there comes back here.</summary>
    [RelayCommand]
    private void Calibrate() => _main.ShowCalibration(this);

    /// <summary>Connects this one paired device, whatever else is connected.</summary>
    [RelayCommand]
    private async Task ConnectDeviceAsync(PairedDeviceRow device)
    {
        device.IsConnecting = true;
        try
        {
            await Devices.ConnectPairedAsync(device.Id, _config);
        }
        finally
        {
            device.IsConnecting = false;
        }
    }

    [RelayCommand]
    private Task DisconnectDeviceAsync(PairedDeviceRow device) => Devices.DisconnectOneAsync(device.Id);

    /// <summary>Forgets one device on the second press; the first only asks.</summary>
    [RelayCommand]
    private async Task ForgetDeviceAsync(PairedDeviceRow device)
    {
        if (!device.ConfirmForget)
        {
            device.ConfirmForget = true;
            return;
        }

        await Devices.ForgetAsync(device.Id);
    }

    [RelayCommand]
    private static void KeepDevice(PairedDeviceRow device) => device.ConfirmForget = false;

    /// <summary>Forgets every device on the second press; the first only asks.</summary>
    [RelayCommand]
    private async Task ForgetAllAsync()
    {
        if (!ConfirmForgetAll)
        {
            ConfirmForgetAll = true;
            return;
        }

        ConfirmForgetAll = false;
        await Devices.ForgetAllAsync();
    }

    [RelayCommand]
    private void KeepAll() => ConfirmForgetAll = false;

    public override void OnShown()
    {
        Devices.Changed += Rebuild;
        Devices.PropertyChanged += OnDevicesChanged;
        Rebuild();
        _timer.Start();

        // A pretend trainer needs no scan.
        if (!Devices.Simulate)
        {
            Devices.ScanAsync(_config).SafeFireAndForget();
        }
    }

    public override void OnLeaving()
    {
        _timer.Stop();
        Devices.Changed -= Rebuild;
        Devices.PropertyChanged -= OnDevicesChanged;
        Devices.StopScanAsync().SafeFireAndForget();
    }

    private MetricChoice Choice(Metric metric, string label)
    {
        var options = MetricSources.Choices(metric).Select(s => new SourceOption(s, SourceLabel(s))).ToList();
        var choice = new MetricChoice
        {
            Metric = metric,
            Label = label,
            Options = options,
            Selected = options.FirstOrDefault(o => o.Source == _config.Devices.Metrics[metric]) ?? options[0],
        };
        choice.PropertyChanged += OnChoiceChanged;
        return choice;
    }

    private static string SourceLabel(MetricSource source) => source switch
    {
        MetricSource.Auto => Strings.Devices_SourceAuto,
        MetricSource.Trainer => Strings.Device_Trainer,
        MetricSource.PowerMeter => Strings.Device_PowerMeter,
        MetricSource.CadenceSensor => Strings.Device_CadenceSensor,
        MetricSource.HeartRateMonitor => Strings.Devices_SourceMonitor,
        _ => Strings.Devices_SourceOff,
    };

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MetricChoice.Selected))
        {
            RefreshNow();
        }
    }

    /// <summary>Shows each reading's source as it is now, the way a ride picks it.</summary>
    private void RefreshNow()
    {
        foreach (var choice in Metrics)
        {
            DeviceRow? Row(MetricSource source) =>
                MetricSources.KindOf(source) is { } kind ? Devices.Rows.FirstOrDefault(r => r.DeviceKind == kind) : null;

            var from = MetricSources.Resolve(choice.Metric, choice.Selected.Source, s => Row(s)?.Value(choice.Metric));
            choice.Now = choice.Selected.Source == MetricSource.Off ? Strings.Devices_NotRecorded
                : from is { } f ? Loc.Format(Strings.Devices_From, Row(f.Source)!.Name, Format(choice.Metric, f.Value))
                : Strings.Devices_NoReading;
        }
    }

    private static string Format(Metric metric, double value) => metric switch
    {
        Metric.Power => Loc.Format(Strings.Device_Watts, value),
        Metric.Cadence => Loc.Format(Strings.Device_Rpm, Math.Round(value)),
        _ => Loc.Format(Strings.Device_Bpm, value),
    };

    /// <summary>Shows the paired devices again, keeping each row, and the name being typed in it, where it can.</summary>
    private void Rebuild()
    {
        var saved = _config.Devices;
        var wanted = saved.Saved.OrderBy(d => d.Kind).ToList();
        foreach (var gone in Paired.Where(p => !wanted.Any(w => w.Id == p.Id)).ToList())
        {
            Paired.Remove(gone);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var device = wanted[i];
            var row = Paired.FirstOrDefault(p => p.Id == device.Id);
            if (row is null)
            {
                row = new PairedDeviceRow
                {
                    DeviceKind = device.Kind,
                    Id = device.Id,
                    AdvertisedName = saved.AdvertisedName(device.Id),
                    Name = saved.NameOf(device.Id),
                };
                Paired.Insert(Math.Min(i, Paired.Count), row);
            }
            else if (Paired.IndexOf(row) != i)
            {
                Paired.Move(Paired.IndexOf(row), i);
            }

            row.Live = Devices.Rows.FirstOrDefault(r => r.Device.Id == device.Id);
        }

        HasPaired = Paired.Count > 0;
        OnPropertyChanged(nameof(CanConnectAll));
        RefreshNow();
    }

    private void OnDevicesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceManager.IsConnecting) or nameof(DeviceManager.HasSaved) or nameof(DeviceManager.IsConnected) or nameof(DeviceManager.Simulate))
        {
            OnPropertyChanged(nameof(CanConnectAll));
        }

        if (e.PropertyName == nameof(DeviceManager.Simulate))
        {
            (Devices.Simulate ? Devices.StopScanAsync() : Devices.ScanAsync(_config)).SafeFireAndForget();
        }

        if (e.PropertyName == nameof(DeviceManager.IsScanning))
        {
            OnPropertyChanged(nameof(ScanLabel));
        }
    }
}
