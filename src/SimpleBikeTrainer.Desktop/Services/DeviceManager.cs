using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleBikeTrainer.AntPlus;
using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Desktop.Services;

public partial class DeviceRow : ObservableObject
{
    public required string Kind { get; init; }

    public required ISensorDevice Device { get; init; }

    /// <summary>The name the rider gave the device in Settings, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name))]
    public partial string? Alias { get; set; }

    public string Name => Alias ?? Device.Name;

    [ObservableProperty]
    public partial bool Connected { get; set; }

    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    public void Refresh()
    {
        Connected = Device.IsConnected;
        Detail = Device switch
        {
            _ when !Device.IsConnected => Device.Status ?? Strings.Device_Disconnected,
            ITrainer { LastError: { } e } => e,
            { Battery: { } b } => Loc.Format(Strings.Device_Battery, b),
            _ => Strings.Device_Connected,
        };
    }
}

/// <summary>A saved device that is not connected now, so the rider can see what Connect looks for.</summary>
public sealed class SavedDeviceRow
{
    public required string Kind { get; init; }

    public required string Name { get; init; }

    public required DeviceId Id { get; init; }
}

/// <summary>A device found by a scan, for the rider to tick.</summary>
public sealed partial class CandidateRow : ObservableObject
{
    public required DiscoveredDevice Found { get; set; }

    public DeviceKind Kind => Found.Kind;

    public string KindLabel => LabelOf(Kind);

    public string Name => Found.Name;

    public DeviceId Id => Found.Id;

    public static string LabelOf(DeviceKind kind) => kind switch
    {
        DeviceKind.Trainer => Strings.Device_Trainer,
        DeviceKind.HeartRate => Strings.Device_HeartRate,
        DeviceKind.Shifter => Strings.Device_Controller,
        _ => Strings.Device_Other,
    };

    /// <summary>Rssi is null for a device BlueZ only remembers, from some ANT+ sticks, and for every Wi-Fi trainer.</summary>
    public string Signal => Found.Rssi is { } rssi ? Loc.Format(Strings.Device_Rssi, rssi)
        : Id.Transport == DeviceTransport.Wifi ? Strings.Device_OnNetwork
        : Id.Transport == DeviceTransport.AntPlus ? Strings.Device_InRange
        : Strings.Device_OutOfRange;

    [ObservableProperty]
    public partial bool Selected { get; set; }

    public void Update(DiscoveredDevice found)
    {
        Found = found;
        OnPropertyChanged(nameof(Signal));
    }
}

public sealed partial class DeviceManager : ObservableRecipient, IAsyncDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly AppConfig _config;
    private readonly List<string> _warnings = [];
    private DeviceScanner? _scanner;
    private CancellationTokenSource? _cancel;

    public DeviceManager(AppConfig config)
    {
        _config = config;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshRows());
        _timer.Start();
        SavedChanged();
    }

    /// <summary>The connected devices.</summary>
    public ObservableCollection<DeviceRow> Rows { get; } = [];

    /// <summary>The saved devices that are not connected: all of them before Connect, and any that were not found after.</summary>
    public ObservableCollection<SavedDeviceRow> Saved { get; } = [];

    /// <summary>What the current scan found, trainers first.</summary>
    public ObservableCollection<CandidateRow> Candidates { get; } = [];

    public ConnectedDevices? Devices { get; private set; }

    /// <summary>Also sent as a message, so Start waits until a device being added is connected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect), nameof(IsBusy), nameof(CanScan))]
    [NotifyPropertyChangedRecipients]
    public partial bool IsConnecting { get; set; }

    /// <summary>Also sent as a message, so Start waits until the scan ends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect), nameof(IsBusy), nameof(CanScan))]
    [NotifyPropertyChangedRecipients]
    public partial bool IsScanning { get; set; }

    /// <summary>Also sent as a <see cref="CommunityToolkit.Mvvm.Messaging.Messages.PropertyChangedMessage{T}"/>, so pages can follow it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect), nameof(CanScan), nameof(ScanLabel), nameof(ScanTip))]
    [NotifyPropertyChangedRecipients]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial bool HasTrainerPicked { get; set; }

    /// <summary>Connect selected works: something is ticked, and a trainer among them unless one is connected already.</summary>
    [ObservableProperty]
    public partial bool CanConnectPicked { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = Strings.Devices_NotConnected;

    /// <summary>Use a pretend trainer and heart rate instead of Bluetooth.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectLabel), nameof(CanScan))]
    public partial bool Simulate { get; set; }

    /// <summary>Saved devices exist, so Connect skips the picker.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectLabel), nameof(CanScan))]
    public partial bool HasSaved { get; set; }

    public bool CanConnect => !IsConnecting && !IsScanning && !IsConnected;

    /// <summary>A scan can start: to pick other devices, or to add one to those connected.</summary>
    public bool CanScan => !IsBusy && !Simulate && (IsConnected || HasSaved);

    public string ScanLabel => IsConnected ? Strings.Devices_Add : Strings.Home_ChooseDevices;

    public string ScanTip => IsConnected ? Strings.Devices_AddTip : Strings.Home_ChooseDevicesTip;

    /// <summary>Scanning or connecting, which Cancel stops.</summary>
    public bool IsBusy => IsConnecting || IsScanning;

    public string ConnectLabel => Simulate || HasSaved ? Strings.Devices_Connect : Strings.Devices_Find;

    /// <summary>Connects the saved devices, or opens the picker when there are none.</summary>
    public async Task ConnectAsync(AppConfig config)
    {
        SavedChanged();
        if (Simulate)
        {
            var trainer = new SimulatedTrainer();
            Connected(new ConnectedDevices(trainer, new SimulatedHeartRate(trainer), []), Strings.Devices_Simulated);
            return;
        }

        if (!HasSaved)
        {
            await ScanAsync(config);
            return;
        }

        await RunAsync(async ct =>
        {
            var devices = await DeviceConnector.ConnectAsync(config, wantHeartRate: true, wantShifters: true, Progress, ct);
            Connected(devices, ConnectedStatus(devices));
        });
    }

    /// <summary>Starts a scan; found devices appear in <see cref="Candidates"/> until the rider connects or cancels.</summary>
    public async Task ScanAsync(AppConfig config)
    {
        if (IsBusy)
        {
            return;
        }

        Candidates.Clear();
        HasTrainerPicked = false;
        CanConnectPicked = false;
        IsScanning = true;
        Status = IsConnected ? Strings.Devices_ScanningToAdd : Strings.Devices_Scanning;
        try
        {
            _scanner = await DeviceScanner.CreateAsync(config.Devices);
            _scanner.DeviceFound += found => Dispatcher.UIThread.Post(() => AddCandidate(found, config.Devices));
            await _scanner.StartAsync();

            // E.g. Bluetooth is off while an ANT+ stick scans, or a stick is plugged in that would not open.
            if (_scanner.Warnings.Count > 0)
            {
                Status = string.Join(" ", [Status, .. _scanner.Warnings]);
            }

            foreach (var found in _scanner.Found)
            {
                AddCandidate(found, config.Devices);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await StopScanAsync();
            Status = e.Message;
        }
    }

    /// <summary>
    /// Connects the ticked devices and saves them, so the next Connect goes straight to them. With devices connected
    /// already, the ticked ones join them, and those stay connected.
    /// </summary>
    public async Task ConnectChosenAsync(AppConfig config)
    {
        var picked = Candidates.Where(c => c.Selected).Select(c => c.Found).ToList();
        if (Devices is { } current)
        {
            if (picked.Count == 0)
            {
                return;
            }

            await StopScanAsync();
            await RunAsync(async ct =>
            {
                var devices = await DeviceConnector.AddAsync(config, current, picked, Progress, ct);
                Candidates.Clear();
                Connected(devices, Strings.Devices_ConnectedStatus);
            });
            return;
        }

        if (picked.FirstOrDefault(d => d.Kind == DeviceKind.Trainer) is not { } trainer)
        {
            Status = Strings.Devices_PickTrainer;
            return;
        }

        await StopScanAsync();
        await RunAsync(async ct =>
        {
            var devices = await DeviceConnector.ConnectChosenAsync(
                config,
                trainer,
                picked.FirstOrDefault(d => d.Kind == DeviceKind.HeartRate),
                [.. picked.Where(d => d.Kind == DeviceKind.Shifter)],
                Progress,
                ct);
            Candidates.Clear();
            Connected(devices, ConnectedStatus(devices));
        });
    }

    /// <summary>Removes a saved device, so Connect no longer looks for it. A connected one stays connected until Disconnect.</summary>
    public void Forget(DeviceId id)
    {
        var saved = _config.Devices;
        saved.Forget(id);
        if (saved.Saved.Count == 0)
        {
            saved.Picked = false;
        }

        _config.Save();
        SavedChanged();
    }

    /// <summary>Shows the saved devices again, after a connect, a disconnect, or Settings renaming or forgetting them.</summary>
    public void SavedChanged()
    {
        var saved = _config.Devices;
        HasSaved = saved.Trainer is not null;
        foreach (var row in Rows)
        {
            row.Alias = AliasOf(row.Device);
        }

        var connected = Devices?.All.Select(d => d.Id).OfType<DeviceId>().ToHashSet() ?? [];
        Saved.Clear();
        foreach (var device in saved.Saved.OrderBy(d => d.Kind).Where(d => !connected.Contains(d.Id)))
        {
            Saved.Add(new SavedDeviceRow { Kind = CandidateRow.LabelOf(device.Kind), Name = saved.NameOf(device.Id), Id = device.Id });
        }
    }

    /// <summary>Stops a scan or a connect in progress.</summary>
    public async Task CancelAsync()
    {
        if (_cancel is { } cancel)
        {
            await cancel.CancelAsync();
        }

        if (IsScanning)
        {
            await StopScanAsync();
            Candidates.Clear();
            Status = Strings.Devices_NotConnected;
        }
    }

    public async Task DisconnectAsync()
    {
        if (Devices is { } devices)
        {
            Devices = null;
            Rows.Clear();
            IsConnected = false;
            Status = Strings.Devices_NotConnected;
            SavedChanged();
            await devices.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        await CancelAsync();
        await DisconnectAsync();
    }

    /// <summary>Adds or updates a scan result. Saved devices start ticked; picking a trainer or monitor unticks the other ones.</summary>
    internal void AddCandidate(DiscoveredDevice found, DevicesConfig saved)
    {
        // A connected device is already in use; the picker offers the others.
        if (found.Kind == DeviceKind.Other || Devices?.All.Any(d => d.Id == found.Id) == true)
        {
            return;
        }

        if (Candidates.FirstOrDefault(c => c.Id == found.Id) is { } existing)
        {
            existing.Update(found);
            return;
        }

        var row = new CandidateRow
        {
            Found = found,
            Selected = found.Kind switch
            {
                DeviceKind.Trainer => saved.Trainer == found.Id,
                DeviceKind.HeartRate => saved.HeartRate == found.Id,
                DeviceKind.Shifter => saved.Shifters.Contains(found.Id),
                _ => false,
            },
        };
        row.PropertyChanged += OnCandidateChanged;

        var index = 0;
        while (index < Candidates.Count && Candidates[index].Kind <= found.Kind)
        {
            index++;
        }

        Candidates.Insert(index, row);
        UpdatePicked();
    }

    private void OnCandidateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CandidateRow.Selected) || sender is not CandidateRow row)
        {
            return;
        }

        // One trainer and one heart rate monitor; any number of controllers.
        if (row.Selected && row.Kind is DeviceKind.Trainer or DeviceKind.HeartRate)
        {
            foreach (var other in Candidates.Where(c => c != row && c.Kind == row.Kind && c.Selected))
            {
                other.Selected = false;
            }
        }

        UpdatePicked();
    }

    private void UpdatePicked()
    {
        HasTrainerPicked = Candidates.Any(c => c.Selected && c.Kind == DeviceKind.Trainer);
        CanConnectPicked = Devices is null ? HasTrainerPicked : Candidates.Any(c => c.Selected);
    }

    private async Task RunAsync(Func<CancellationToken, Task> connect)
    {
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        lock (_warnings)
        {
            _warnings.Clear();
        }

        IsConnecting = true;
        try
        {
            await connect(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Status = Strings.Devices_Canceled;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Status = e.Message;
        }
        finally
        {
            _cancel = null;
            IsConnecting = false;
        }
    }

    private async Task StopScanAsync()
    {
        if (_scanner is { } scanner)
        {
            _scanner = null;
            await scanner.DisposeAsync();
        }

        IsScanning = false;
    }

    private void Connected(ConnectedDevices devices, string status)
    {
        Devices = devices;
        Rows.Clear();
        Rows.Add(new DeviceRow { Kind = Strings.Device_Trainer, Device = devices.Trainer });
        if (devices.HeartRate is { } hr)
        {
            Rows.Add(new DeviceRow { Kind = Strings.Device_HeartRate, Device = hr });
        }

        foreach (var shifter in devices.Shifters)
        {
            Rows.Add(new DeviceRow { Kind = Strings.Device_Controller, Device = shifter });
        }

        RefreshRows();
        IsConnected = true;
        SavedChanged();

        // A device that would not connect says why, instead of a plain Connected.
        lock (_warnings)
        {
            Status = _warnings.Count > 0 ? string.Join(" ", _warnings) : status;
            _warnings.Clear();
        }
    }

    private string? AliasOf(ISensorDevice device) =>
        device.Id is { } id && _config.Devices.Find(id) is { Alias.Length: > 0 } saved ? saved.Alias : null;

    private static string ConnectedStatus(ConnectedDevices devices) =>
        devices.Shifters.Count == 0 ? Strings.Devices_NoClick : Strings.Devices_ConnectedStatus;

    private void Progress(string message, bool warning)
    {
        if (warning)
        {
            lock (_warnings)
            {
                _warnings.Add(message);
            }
        }

        Dispatcher.UIThread.Post(() => Status = message);
    }

    private void RefreshRows()
    {
        foreach (var row in Rows)
        {
            row.Refresh();
        }
    }
}
