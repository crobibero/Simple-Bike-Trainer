using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Desktop.Services;

/// <summary>A connected device, with what it reports now.</summary>
public sealed partial class DeviceRow : ObservableObject
{
    // Matches RideSession.SensorTimeout: an idle KICKR notifies only every 1-4 s.
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(5);

    private readonly Lock _lock = new();
    private long _seen;
    private int? _power;
    private double? _cadence;
    private int? _heartRate;

    public DeviceRow(DeviceKind kind, ISensorDevice device)
    {
        DeviceKind = kind;
        Device = device;
        switch (device)
        {
            case ITrainer trainer:
                trainer.DataReceived += OnTrainerData;
                break;
            case IHeartRateMonitor monitor:
                monitor.HeartRateReceived += OnHeartRate;
                break;
            case ICyclingSensor sensor:
                sensor.ReadingReceived += OnReading;
                break;
        }
    }

    public DeviceKind DeviceKind { get; }

    public string Kind => CandidateRow.LabelOf(DeviceKind);

    public ISensorDevice Device { get; }

    /// <summary>The name the rider gave the device on the Devices page, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name))]
    public partial string? Alias { get; set; }

    public string Name => Alias ?? Device.Name;

    [ObservableProperty]
    public partial bool Connected { get; set; }

    /// <summary>Its readings and battery while connected, such as "182 W · 88 rpm"; otherwise why it is not.</summary>
    [ObservableProperty]
    public partial string Detail { get; set; } = string.Empty;

    /// <summary>The device's own reading of <paramref name="metric"/>, while fresh.</summary>
    public double? Value(Metric metric)
    {
        lock (_lock)
        {
            if (_seen == 0 || Stopwatch.GetElapsedTime(_seen) >= FreshFor)
            {
                return null;
            }

            return metric switch
            {
                Metric.Power => _power,
                Metric.Cadence => _cadence,
                _ => _heartRate,
            };
        }
    }

    public void Refresh()
    {
        Connected = Device.IsConnected;
        List<string> parts = [];
        if (Value(Metric.Power) is { } power)
        {
            parts.Add(Loc.Format(Strings.Device_Watts, power));
        }

        if (Value(Metric.Cadence) is { } cadence)
        {
            parts.Add(Loc.Format(Strings.Device_Rpm, Math.Round(cadence)));
        }

        if (Value(Metric.HeartRate) is { } bpm)
        {
            parts.Add(Loc.Format(Strings.Device_Bpm, bpm));
        }

        if (Device.Battery is { } battery)
        {
            parts.Add(Loc.Format(Strings.Device_Battery, battery));
        }

        Detail = Device switch
        {
            _ when !Device.IsConnected => Device.Status ?? Strings.Device_Disconnected,
            ITrainer { LastError: { } e } => e,
            _ when parts.Count > 0 => string.Join("  ·  ", parts),
            _ => Strings.Device_Connected,
        };
    }

    /// <summary>Stops listening, once the device has left the list.</summary>
    public void Detach()
    {
        switch (Device)
        {
            case ITrainer trainer:
                trainer.DataReceived -= OnTrainerData;
                break;
            case IHeartRateMonitor monitor:
                monitor.HeartRateReceived -= OnHeartRate;
                break;
            case ICyclingSensor sensor:
                sensor.ReadingReceived -= OnReading;
                break;
        }
    }

    private void OnTrainerData(IndoorBikeData data) => Update(data.PowerWatts, data.CadenceRpm, data.HeartRate is > 0 ? data.HeartRate : null);

    private void OnHeartRate(int bpm) => Update(null, null, bpm);

    private void OnReading(SensorReading reading) => Update(reading.PowerWatts, reading.CadenceRpm, null);

    private void Update(int? power, double? cadence, int? heartRate)
    {
        lock (_lock)
        {
            _seen = Stopwatch.GetTimestamp();
            _power = power ?? _power;
            _cadence = cadence ?? _cadence;
            _heartRate = heartRate ?? _heartRate;
        }
    }
}

/// <summary>A saved device that is not connected now, so the rider can see what Connect looks for.</summary>
public sealed class SavedDeviceRow
{
    public required string Kind { get; init; }

    public required string Name { get; init; }

    public required DeviceId Id { get; init; }
}

/// <summary>A device found by a scan, to pair.</summary>
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
        DeviceKind.PowerMeter => Strings.Device_PowerMeter,
        DeviceKind.CadenceSensor => Strings.Device_CadenceSensor,
        DeviceKind.HeartRate => Strings.Device_HeartRate,
        DeviceKind.Shifter => Strings.Device_Controller,
        _ => Strings.Device_Other,
    };

    /// <summary>Rssi is null for a device BlueZ only remembers, from some ANT+ sticks, and for every Wi-Fi trainer.</summary>
    public string Signal => Found.Rssi is { } rssi ? Loc.Format(Strings.Device_Rssi, rssi)
        : Id.Transport == DeviceTransport.Wifi ? Strings.Device_OnNetwork
        : Id.Transport == DeviceTransport.AntPlus ? Strings.Device_InRange
        : Strings.Device_OutOfRange;

    /// <summary>Paired already but not connected, so the button only connects it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairLabel))]
    public partial bool IsPaired { get; set; }

    [ObservableProperty]
    public partial bool IsPairing { get; set; }

    public string PairLabel => IsPaired ? Strings.Devices_ConnectOne : Strings.Devices_PairOne;

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
    private readonly DeviceSet _set = new();
    private IDeviceScanner? _scanner;
    private Task _stopping = Task.CompletedTask;
    private bool _wantScan;
    private CancellationTokenSource? _cancel;

    public DeviceManager(AppConfig config, bool testMode = false)
    {
        _config = config;
        CanSimulate = testMode;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshRows());
        _timer.Start();
        SavedChanged();
    }

    /// <summary>How long Connect on one paired device waits for the scan to find it; tests shorten it.</summary>
    internal TimeSpan LookTime { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Opens the radios for the Devices page's scan; tests put in a scanner of their own.</summary>
    internal Func<DevicesConfig, Task<IDeviceScanner>> OpenScanner { get; set; } = async devices => await DeviceScanner.CreateAsync(devices);

    /// <summary>Raised after devices connect, disconnect, are paired or forgotten, for pages that show them.</summary>
    public event Action? Changed;

    /// <summary>The connected devices, trainer first.</summary>
    public ObservableCollection<DeviceRow> Rows { get; } = [];

    /// <summary>The saved devices that are not connected: all of them before Connect, and any that were not found after.</summary>
    public ObservableCollection<SavedDeviceRow> Saved { get; } = [];

    /// <summary>What the scan on the Devices page found, but for the connected devices, trainers first.</summary>
    public ObservableCollection<CandidateRow> Candidates { get; } = [];

    /// <summary>What a ride uses; null without a trainer.</summary>
    public ConnectedDevices? Devices => _set.ForRide();

    /// <summary>Also sent as a message, so Start waits until a device being added is connected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanDisconnect))]
    [NotifyPropertyChangedRecipients]
    public partial bool IsConnecting { get; set; }

    /// <summary>Also sent as a message, so Start waits until the scan ends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanDisconnect))]
    [NotifyPropertyChangedRecipients]
    public partial bool IsScanning { get; set; }

    /// <summary>A trainer is connected, so a ride can start. Also sent as a message, so pages can follow it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedRecipients]
    public partial bool IsConnected { get; set; }

    /// <summary>Any device is connected, so Disconnect has something to do.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDisconnect))]
    public partial bool HasConnected { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = Strings.Devices_NotConnected;

    /// <summary>Offers <see cref="Simulate"/>; only when launched with <c>--test</c>.</summary>
    public bool CanSimulate { get; }

    /// <summary>Use a pretend trainer and heart rate instead of Bluetooth.</summary>
    [ObservableProperty]
    public partial bool Simulate { get; set; }

    /// <summary>A trainer is paired, so Connect has something to look for.</summary>
    [ObservableProperty]
    public partial bool HasSaved { get; set; }

    /// <summary>The Devices page scans the whole time it is open, so only a connect in progress holds this back.</summary>
    public bool CanDisconnect => !IsConnecting && HasConnected;

    public bool IsBusy => IsConnecting || IsScanning;

    /// <summary>
    /// Connects the paired devices that are not connected yet, joining any that are. One that is found but will not
    /// connect keeps trying in the background.
    /// </summary>
    public async Task ConnectAsync(AppConfig config)
    {
        SavedChanged();
        if (Simulate)
        {
            await DisconnectAsync();
            var trainer = new SimulatedTrainer();
            _set.Put(DeviceKind.Trainer, trainer);
            _set.Put(DeviceKind.HeartRate, new SimulatedHeartRate(trainer));
            DevicesChanged();
            Status = Strings.Devices_Simulated;
            return;
        }

        if (!HasSaved || IsConnecting)
        {
            return;
        }

        await WithScanPausedAsync(config, () => RunAsync(
            ct => DeviceConnector.ConnectSavedAsync(config, _set, wantHeartRate: true, wantShifters: true, Progress, ct),
            () => _set.Shifters.Count == 0 ? Strings.Devices_NoClick : Strings.Devices_ConnectedStatus));
    }

    /// <summary>Scans until <see cref="StopScanAsync"/>; what it finds appears in <see cref="Candidates"/>.</summary>
    public async Task ScanAsync(AppConfig config)
    {
        _wantScan = true;
        await StartScannerAsync(config);
    }

    public async Task StopScanAsync()
    {
        _wantScan = false;
        await StopScannerAsync();

        // A scan started again while this one stopped keeps its list.
        if (_wantScan)
        {
            return;
        }

        Candidates.Clear();
        if (!IsConnecting)
        {
            Status = IsConnected ? Strings.Devices_ConnectedStatus : Strings.Devices_NotConnected;
        }
    }

    /// <summary>
    /// Connects one device from the scan and saves it, in place of the paired one of its kind; controllers add up. It
    /// stays connected, alongside the others. One that will not connect is not saved, and the status says what to try.
    /// </summary>
    public async Task PairAsync(CandidateRow row, AppConfig config)
    {
        if (IsConnecting)
        {
            return;
        }

        var paired = row.IsPaired;
        row.IsPairing = true;
        try
        {
            await WithScanPausedAsync(config, () => RunAsync(
                async ct =>
                {
                    if ((await DeviceConnector.AddAsync(config, _set, [row.Found], Progress, ct)).Count == 0)
                    {
                        throw new PairException(PairHint(row.Kind));
                    }

                    Candidates.Remove(row);
                },
                () => Loc.Format(paired ? Strings.Devices_ConnectedOne : Strings.Devices_Paired, row.Name)));
        }
        finally
        {
            row.IsPairing = false;
        }
    }

    /// <summary>
    /// Connects one paired device alongside the others. It needs the scan to have found it, so it scans for it first,
    /// for up to <see cref="LookTime"/>; one not found says what to try.
    /// </summary>
    public async Task ConnectPairedAsync(DeviceId id, AppConfig config)
    {
        if (IsConnecting || _set.Contains(id) || config.Devices.Find(id) is not { } saved)
        {
            return;
        }

        var found = Candidates.FirstOrDefault(c => c.Id == id);
        if (found is null)
        {
            var name = config.Devices.NameOf(id);
            var scanning = _scanner is not null;
            await StartScannerAsync(config);
            using var cancel = new CancellationTokenSource();
            using var look = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            look.CancelAfter(LookTime);
            _cancel = cancel;
            IsConnecting = true;
            Status = Loc.Format(Strings.Devices_LookingFor, name);
            try
            {
                while ((found = Candidates.FirstOrDefault(c => c.Id == id)) is null)
                {
                    await Task.Delay(250, look.Token);
                }
            }
            catch (OperationCanceledException)
            {
                Status = cancel.IsCancellationRequested ? Strings.Devices_Canceled : Loc.Format(Strings.Devices_NotFound, name, PairHint(saved.Kind));
            }
            finally
            {
                _cancel = null;
                IsConnecting = false;
                if (!scanning && !_wantScan)
                {
                    await StopScannerAsync();
                }
            }

            if (found is null)
            {
                return;
            }
        }

        await PairAsync(found, config);
    }

    /// <summary>Disconnects one device and leaves the others connected; it stays paired.</summary>
    public async Task DisconnectOneAsync(DeviceId id)
    {
        if (_set.Remove(id) is not { } device)
        {
            return;
        }

        DevicesChanged();
        Status = Loc.Format(Strings.Devices_DisconnectedOne, device.Name);
        await device.DisposeAsync();
    }

    /// <summary>Removes a saved device, so Connect no longer looks for it, and disconnects it.</summary>
    public async Task ForgetAsync(DeviceId id)
    {
        _config.Devices.Forget(id);
        if (_config.Devices.Saved.Count == 0)
        {
            _config.Devices.Picked = false;
        }

        _config.Save();
        var device = _set.Remove(id);
        DevicesChanged();
        if (device is not null)
        {
            await device.DisposeAsync();
        }
    }

    /// <summary>Forgets every device, and disconnects them; where readings come from is kept.</summary>
    public async Task ForgetAllAsync()
    {
        _config.Devices = _config.Devices.WithoutDevices();
        _config.Save();
        await DisconnectAsync();
        SavedChanged();
    }

    /// <summary>Shows the saved devices again, after a connect, a disconnect, or a device renamed or forgotten.</summary>
    public void SavedChanged()
    {
        var saved = _config.Devices;
        HasSaved = saved.Trainer is not null;
        foreach (var row in Rows)
        {
            row.Alias = row.Device.Id is { } id && saved.Find(id) is { Alias.Length: > 0 } s ? s.Alias : null;
        }

        Saved.Clear();
        foreach (var device in saved.Saved.OrderBy(d => d.Kind).Where(d => !_set.Contains(d.Id)))
        {
            Saved.Add(new SavedDeviceRow { Kind = CandidateRow.LabelOf(device.Kind), Name = saved.NameOf(device.Id), Id = device.Id });
        }

        foreach (var candidate in Candidates)
        {
            candidate.IsPaired = saved.Find(candidate.Id) is not null;
        }

        Changed?.Invoke();
    }

    /// <summary>Stops a connect in progress.</summary>
    public async Task CancelAsync()
    {
        if (_cancel is { } cancel)
        {
            await cancel.CancelAsync();
        }
    }

    public async Task DisconnectAsync()
    {
        if (_set.Count == 0)
        {
            return;
        }

        var devices = _set.All;
        _set.Clear();
        DevicesChanged();
        Status = Strings.Devices_NotConnected;
        foreach (var device in devices.Reverse())
        {
            await device.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        await CancelAsync();
        await StopScanAsync();
        await DisconnectAsync();
    }

    /// <summary>Adds or updates a scan result; connected devices are left out, as they are in use.</summary>
    internal void AddCandidate(DiscoveredDevice found, DevicesConfig saved)
    {
        if (found.Kind == DeviceKind.Other || _set.Contains(found.Id))
        {
            return;
        }

        if (Candidates.FirstOrDefault(c => c.Id == found.Id) is { } existing)
        {
            existing.Update(found);
            return;
        }

        var index = 0;
        while (index < Candidates.Count && Candidates[index].Kind <= found.Kind)
        {
            index++;
        }

        Candidates.Insert(index, new CandidateRow { Found = found, IsPaired = saved.Find(found.Id) is not null });
    }

    /// <summary>What to try when a device will not pair, for the kind of device it is.</summary>
    private static string PairHint(DeviceKind kind) => kind switch
    {
        DeviceKind.HeartRate => Strings.Devices_PairHintHeartRate,
        DeviceKind.Trainer => Strings.Devices_PairHintTrainer,
        DeviceKind.Shifter => Strings.Devices_PairHintController,
        _ => Strings.Devices_PairHintSensor,
    };

    private async Task StartScannerAsync(AppConfig config)
    {
        // The radio is free once the last scan has let go of it, e.g. on coming back to the Devices page.
        await _stopping.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        if (_scanner is not null || IsConnecting)
        {
            return;
        }

        IsScanning = true;
        Status = Strings.Devices_Scanning;
        try
        {
            var scanner = _scanner = await OpenScanner(config.Devices);
            scanner.DeviceFound += found => Dispatcher.UIThread.Post(() =>
            {
                if (_scanner == scanner)
                {
                    AddCandidate(found, config.Devices);
                }
            });
            await scanner.StartAsync();

            // E.g. Bluetooth is off while an ANT+ stick scans, or a stick is plugged in that would not open.
            if (scanner is DeviceScanner { Warnings.Count: > 0 } radios)
            {
                Status = string.Join(" ", [Status, .. radios.Warnings]);
            }

            foreach (var found in scanner.Found)
            {
                AddCandidate(found, config.Devices);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await StopScannerAsync();
            Status = e.Message;
        }
    }

    /// <summary>Stops the radio but keeps the list, for a connect that needs the radio to itself.</summary>
    private async Task StopScannerAsync()
    {
        if (_scanner is { } scanner)
        {
            _scanner = null;
            _stopping = scanner.DisposeAsync().AsTask();
            await _stopping;
        }

        IsScanning = false;
    }

    /// <summary>BlueZ can fail a connection made while it scans, so the Devices page's scan stops for it and goes on after.</summary>
    private async Task WithScanPausedAsync(AppConfig config, Func<Task> connect)
    {
        var scanning = _scanner is not null;
        await StopScannerAsync();
        try
        {
            await connect();
        }
        finally
        {
            if (scanning && _wantScan)
            {
                var status = Status;
                await StartScannerAsync(config);
                Status = status;
            }
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> connect, Func<string> succeeded)
    {
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        lock (_warnings)
        {
            _warnings.Clear();
        }

        IsConnecting = true;
        string? error = null;
        try
        {
            await connect(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            error = Strings.Devices_Canceled;
        }
        catch (Exception e)
        {
            error = e.Message;
        }
        finally
        {
            _cancel = null;
            IsConnecting = false;
        }

        // Even a connect that failed can have connected some devices, such as a heart rate monitor without a trainer.
        DevicesChanged();

        // A device that would not connect says why, before the outcome.
        lock (_warnings)
        {
            List<string> parts = [.. _warnings];
            parts.Add(error ?? (parts.Count > 0 ? string.Empty : succeeded()));
            Status = string.Join(" ", parts.Where(p => p.Length > 0));
            _warnings.Clear();
        }
    }

    private void DevicesChanged()
    {
        foreach (var row in Rows)
        {
            row.Detach();
        }

        Rows.Clear();
        foreach (var (kind, device) in _set.Entries)
        {
            Rows.Add(new DeviceRow(kind, device));
        }

        foreach (var candidate in Candidates.Where(c => _set.Contains(c.Id)).ToList())
        {
            Candidates.Remove(candidate);
        }

        IsConnected = _set.Trainer is not null;
        HasConnected = _set.Count > 0;
        OnPropertyChanged(nameof(Devices));
        SavedChanged();
        RefreshRows();
    }

    private void Progress(string message, bool warning)
    {
        if (warning)
        {
            lock (_warnings)
            {
                _warnings.Add(message);
            }
        }

        // In order on the UI thread, where the connect runs, so a late message cannot hide the outcome.
        if (Dispatcher.UIThread.CheckAccess())
        {
            Status = message;
        }
        else
        {
            Dispatcher.UIThread.Post(() => Status = message);
        }
    }

    private void RefreshRows()
    {
        foreach (var row in Rows)
        {
            row.Refresh();
        }
    }

    /// <summary>A pairing that failed, with what to try; the device's own error is already among the warnings.</summary>
    private sealed class PairException(string message) : Exception(message);
}
