using System.Globalization;
using AsyncAwaitBestPractices;
using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.AntPlus;

public sealed class AntDeviceLink(int deviceNumber, byte deviceType, byte transmissionType) : IDeviceLink
{
    public int DeviceNumber { get; } = deviceNumber;

    public byte DeviceType { get; } = deviceType;

    public byte TransmissionType { get; } = transmissionType;

    public IConnectableDevice Open(DiscoveredDevice found) => found.Kind switch
    {
        DeviceKind.Trainer => new AntFecTrainer(this, found.Name, found.Id),
        DeviceKind.HeartRate => new AntHeartRateMonitor(this, found.Name, found.Id),
        DeviceKind.PowerMeter => new AntPowerMeter(this, found.Name, found.Id),
        DeviceKind.CadenceSensor => new AntCadenceSensor(this, found.Name, found.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(found)),
    };
}

/// <summary>ANT+ has no connection as such: the device counts as connected while data arrives.</summary>
public abstract class AntConnection : DeviceConnection
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>No data this long counts as disconnected, as does no data yet (a <c>_lastData</c> of 0).</summary>
    private static readonly long QuietMs = 3000;

    private readonly TaskCompletionSource _firstData = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private AntStick? _stick;
    private AntChannel? _channel;
    private Task? _opening;
    private volatile string? _openError;
    private long _lastData;
    private volatile bool _lost;

    protected AntConnection(AntDeviceLink link, string name, DeviceId id)
        : base(name, id)
    {
        Link = link;
    }

    public override bool IsConnected => _channel is not null && Environment.TickCount64 - Interlocked.Read(ref _lastData) < QuietMs;

    public override string? Status => IsConnected ? null
        : _openError is { } error ? error
        : _stick is { IsAvailable: false } ? CoreStrings.Ant_StickLost
        : _firstData.Task.IsCompleted ? CoreStrings.Ble_Disconnected
        : CoreStrings.Ble_Connecting;

    protected AntDeviceLink Link { get; }

    protected abstract ushort Period { get; }

    public override async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken);
        try
        {
            await _firstData.Task.WaitAsync(ConnectTimeout, cancellationToken);
        }
        catch (Exception e)
        {
            await CloseAsync();
            if (e is TimeoutException)
            {
                throw new InvalidOperationException(Loc.Format(CoreStrings.Ant_NoData, Name));
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public override void ConnectInBackground()
    {
        // The channel searches for the device with no time limit, so it only needs to be open.
        if (_channel is null && _opening is not { IsCompleted: false })
        {
            _opening = Task.Run(KeepOpeningAsync);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_opening is { } opening)
        {
            try
            {
                await opening;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (IsConnected)
        {
            await TryStopAsync();
        }

        await CloseAsync();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    protected abstract void OnPage(byte[] page);

    /// <summary>Runs when data arrives again after the device went quiet, e.g. to restore the trainer's target.</summary>
    protected virtual void OnRegained()
    {
    }

    /// <summary>Sends a page as acknowledged data. False when the device did not acknowledge it.</summary>
    protected Task<bool> SendAsync(byte[] page, CancellationToken cancellationToken) =>
        _channel is { } channel ? channel.SendAcknowledgedAsync(page, cancellationToken) : Task.FromResult(false);

    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        _stick = await AntStick.AcquireAsync(cancellationToken) ?? throw new InvalidOperationException(CoreStrings.Ant_NoStick);
        _stick.Available += OnStickAvailable;
        try
        {
            _channel = await _stick.OpenChannelAsync(new AntChannelConfig(Link.DeviceNumber, Link.DeviceType, Link.TransmissionType, Period), cancellationToken);
            _channel.DataReceived += OnData;
            _channel.SignalLost += OnSignalLost;
        }
        catch
        {
            await CloseAsync();
            throw;
        }
    }

    /// <summary>Tries again while the stick is missing or will not open, e.g. until it is plugged back in.</summary>
    private async Task KeepOpeningAsync()
    {
        while (true)
        {
            try
            {
                await OpenAsync(_cts.Token);
                _openError = null;
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _openError = e.Message;
            }

            await Task.Delay(AntStick.RetryDelay, _cts.Token);
        }
    }

    private void OnData(AntReceived received)
    {
        var wasQuiet = _lost || Environment.TickCount64 - Interlocked.Read(ref _lastData) >= QuietMs;
        Interlocked.Exchange(ref _lastData, Environment.TickCount64);
        _lost = false;
        OnPage(received.Page);
        if (!_firstData.TrySetResult() && wasQuiet)
        {
            OnRegained();
        }
    }

    private void OnSignalLost()
    {
        _lost = true;
        Interlocked.Exchange(ref _lastData, 0);
    }

    private void OnStickAvailable(bool available)
    {
        if (!available)
        {
            OnSignalLost();
        }
    }

    private async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _channel, null) is { } channel)
        {
            channel.DataReceived -= OnData;
            channel.SignalLost -= OnSignalLost;
            await channel.CloseAsync();
        }

        if (Interlocked.Exchange(ref _stick, null) is { } stick)
        {
            stick.Available -= OnStickAvailable;
            await stick.ReleaseAsync();
        }
    }
}

/// <summary>ERG with data page 49; simulation with pages 50, 51 and 55.</summary>
public sealed class AntFecTrainer(AntDeviceLink link, string name, DeviceId id) : AntConnection(link, name, id), ITrainer
{
    private readonly FecDecoder _decoder = new();
    private readonly SemaphoreSlim _controlLock = new(1, 1);
    private int? _lastTarget;
    private RoadSimulation? _lastRoad;

    // The wind and weights the trainer has, sent only when they change or after the trainer was quiet.
    private byte[]? _sentWind;
    private byte[]? _sentUser;

    public event Action<IndoorBikeData>? DataReceived;

    public string? LastError { get; private set; }

    protected override ushort Period => AntPlusPages.FitnessEquipmentPeriod;

    /// <summary>Nearly every FE-C trainer has simulation mode; the profile's capabilities page is only sent on request.</summary>
    public bool SupportsSimulation => true;

    public async Task SetTargetPowerAsync(int watts, CancellationToken cancellationToken = default)
    {
        // Remembered while the trainer is quiet, and sent once it is heard again.
        (_lastTarget, _lastRoad) = (watts, null);
        if (IsConnected)
        {
            await SendControlAsync(cancellationToken);
        }
    }

    public async Task SetSimulationAsync(RoadSimulation road, CancellationToken cancellationToken = default)
    {
        (_lastTarget, _lastRoad) = (null, road);
        if (IsConnected)
        {
            await SendControlAsync(cancellationToken);
        }
    }

    protected override void OnPage(byte[] page)
    {
        IndoorBikeData? data;
        lock (_decoder)
        {
            data = _decoder.Decode(page);
        }

        if (data is { } d)
        {
            DataReceived?.Invoke(d);
        }
    }

    protected override void OnRegained()
    {
        // The trainer may have lost its settings while it was away.
        (_sentWind, _sentUser) = (null, null);
        if (_lastTarget is not null || _lastRoad is not null)
        {
            SendControlAsync(CancellationToken.None).SafeFireAndForget();
        }
    }

    /// <summary>A trainer left in ERG fights the next app or ride; zero resistance frees it. Only if this took control.</summary>
    protected override async Task OnStoppingAsync()
    {
        if (_lastTarget is not null || _lastRoad is not null)
        {
            await SendAsync(AntPlusPages.BasicResistance(0), CancellationToken.None);
        }
    }

    private async Task SendControlAsync(CancellationToken cancellationToken)
    {
        await _controlLock.WaitAsync(cancellationToken);
        try
        {
            if (_lastRoad is { } road)
            {
                var user = AntPlusPages.UserConfiguration(road.RiderKg, road.BikeKg);
                if (_sentUser is null || !_sentUser.AsSpan().SequenceEqual(user))
                {
                    _sentUser = await SendOrReportAsync(user, cancellationToken) ? user : null;
                }

                var wind = AntPlusPages.WindResistance(road.WindResistance);
                if (_sentWind is null || !_sentWind.AsSpan().SequenceEqual(wind))
                {
                    _sentWind = await SendOrReportAsync(wind, cancellationToken) ? wind : null;
                }

                await SendOrReportAsync(AntPlusPages.TrackResistance(road.GradePercent, road.Crr), cancellationToken);
            }
            else if (_lastTarget is { } watts)
            {
                await SendOrReportAsync(AntPlusPages.TargetPower(watts), cancellationToken);
            }
        }
        finally
        {
            _controlLock.Release();
        }
    }

    private async Task<bool> SendOrReportAsync(byte[] page, CancellationToken cancellationToken)
    {
        try
        {
            var acknowledged = await SendAsync(page, cancellationToken);
            LastError = acknowledged ? null : Loc.Format(CoreStrings.Ant_NotAcknowledged, Name);
            return acknowledged;
        }
        catch (AntException e)
        {
            LastError = e.Message;
            return false;
        }
    }
}

public sealed class AntHeartRateMonitor(AntDeviceLink link, string name, DeviceId id) : AntConnection(link, name, id), IHeartRateMonitor
{
    public event Action<int>? HeartRateReceived;

    protected override ushort Period => AntPlusPages.HeartRatePeriod;

    protected override void OnPage(byte[] page)
    {
        if (AntPlusPages.HeartRateBattery(page) is { } battery)
        {
            SetBattery(battery);
        }

        if (AntPlusPages.HeartRate(page) is { } bpm)
        {
            HeartRateReceived?.Invoke(bpm);
        }
    }
}

/// <summary>A power meter on the Bicycle Power profile: power and, when it measures it, cadence, from page 16.</summary>
public sealed class AntPowerMeter(AntDeviceLink link, string name, DeviceId id) : AntConnection(link, name, id), ICyclingSensor
{
    public event Action<SensorReading>? ReadingReceived;

    protected override ushort Period => AntPlusPages.BikePowerPeriod;

    protected override void OnPage(byte[] page)
    {
        if (AntPlusPages.BikePower(page) is { } reading)
        {
            ReadingReceived?.Invoke(new SensorReading(reading.Power, reading.Cadence));
        }
    }
}

/// <summary>A cadence sensor, or a combined speed and cadence sensor, of which only the cadence is used.</summary>
public sealed class AntCadenceSensor(AntDeviceLink link, string name, DeviceId id) : AntConnection(link, name, id), ICyclingSensor
{
    private readonly CrankCadence _cadence = new();

    public event Action<SensorReading>? ReadingReceived;

    private bool Combined => Link.DeviceType == AntPlusPages.BikeSpeedCadenceType;

    protected override ushort Period => Combined ? AntPlusPages.BikeSpeedCadencePeriod : AntPlusPages.BikeCadencePeriod;

    protected override void OnPage(byte[] page)
    {
        if (AntPlusPages.Crank(page, Combined) is not { } crank)
        {
            return;
        }

        double? cadence;
        lock (_cadence)
        {
            cadence = _cadence.Update(crank, Environment.TickCount64);
        }

        ReadingReceived?.Invoke(new SensorReading(null, cadence));
    }
}
