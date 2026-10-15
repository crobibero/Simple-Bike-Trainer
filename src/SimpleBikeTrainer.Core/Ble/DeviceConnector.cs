using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Ble;

public sealed class ConnectedDevices(ITrainer trainer, IHeartRateMonitor? heartRate, IReadOnlyList<IShifter> shifters) : IAsyncDisposable
{
    public ITrainer Trainer { get; } = trainer;

    public IHeartRateMonitor? HeartRate { get; } = heartRate;

    public IReadOnlyList<IShifter> Shifters { get; } = shifters;

    public IReadOnlyList<ISensorDevice> All => [Trainer, .. HeartRate is null ? Array.Empty<ISensorDevice>() : [HeartRate], .. Shifters];

    public async ValueTask DisposeAsync()
    {
        foreach (var device in All.Reverse())
        {
            await device.DisposeAsync();
        }
    }
}

public static class DeviceConnector
{
    private static readonly TimeSpan ScanTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TrainerWait = TimeSpan.FromSeconds(30);

    public static async Task<ConnectedDevices> ConnectAsync(
        AppConfig config,
        bool wantHeartRate,
        bool wantShifters,
        Action<string, bool> progress,
        CancellationToken ct)
    {
        await using var scanner = await DeviceScanner.CreateAsync(config.Devices, ct);
        foreach (var warning in scanner.Warnings)
        {
            progress(warning, true);
        }

        progress(CoreStrings.Connect_Scanning, false);
        await scanner.StartAsync(ct);

        var saved = config.Devices;
        bool Wanted(DiscoveredDevice d) => IsWanted(saved, d, wantHeartRate, wantShifters);

        // Scan until everything wanted is in view, or the scan time is up.
        await scanner.WaitForAsync(found => AllInView(saved, found, wantHeartRate, wantShifters), ScanTime, ct);

        if (!scanner.Found.Any(d => d.Kind == DeviceKind.Trainer && Wanted(d)))
        {
            progress(CoreStrings.Connect_Waiting, false);
            await scanner.WaitForAsync(found => found.Any(d => d.Kind == DeviceKind.Trainer && Wanted(d)), TrainerWait, ct);
        }

        var found = scanner.Found.Where(Wanted).ToList();
        await scanner.StopAsync();

        var trainerDevice = Best(found, DeviceKind.Trainer)
            ?? throw new InvalidOperationException(
                saved.Trainer is null
                    ? CoreStrings.Connect_NoTrainer
                    : Loc.Format(CoreStrings.Connect_SavedMissing, saved.Describe(saved.Trainer)));

        var hrDevice = Best(found, DeviceKind.HeartRate);
        var devices = await ConnectDevicesAsync(trainerDevice, hrDevice, [.. found.Where(d => d.Kind == DeviceKind.Shifter)], progress, ct);
        var heartRate = devices.HeartRate;

        var changed = false;
        foreach (var device in devices.All)
        {
            if (device.Id is { } id && found.FirstOrDefault(d => d.Id == id) is { } d)
            {
                changed |= saved.RememberName(id, d.Name);
            }
        }

        if (saved.Trainer is null)
        {
            saved.Pick(DeviceKind.Trainer, trainerDevice.Id, trainerDevice.Name);
            changed = true;
        }

        if (saved.HeartRate is null && heartRate is not null)
        {
            saved.Pick(DeviceKind.HeartRate, hrDevice!.Id, hrDevice.Name);
            changed = true;
        }

        if (changed)
        {
            config.Save();
        }

        return devices;
    }

    /// <summary>Connects the devices the rider picked from a scan and saves them for next time.</summary>
    public static async Task<ConnectedDevices> ConnectChosenAsync(
        AppConfig config,
        DiscoveredDevice trainer,
        DiscoveredDevice? heartRate,
        IReadOnlyList<DiscoveredDevice> shifters,
        Action<string, bool> progress,
        CancellationToken ct)
    {
        var devices = await ConnectDevicesAsync(trainer, heartRate, shifters, progress, ct);
        // The picks replace the saved devices; one picked again keeps the name the rider gave it.
        var chosen = new[] { trainer, heartRate }.OfType<DiscoveredDevice>().Concat(shifters).ToList();
        config.Devices.Saved.RemoveAll(s => !chosen.Any(d => d.Id == s.Id));
        foreach (var d in chosen)
        {
            config.Devices.Pick(d.Kind, d.Id, d.Name);
        }

        config.Devices.Picked = true;

        config.Save();
        return devices;
    }

    /// <summary>
    /// Connects devices picked from a scan alongside the ones already connected, and saves them. A new trainer or heart
    /// rate monitor replaces the current one once it connects; one that fails is reported and left out.
    /// </summary>
    public static Task<ConnectedDevices> AddAsync(
        AppConfig config,
        ConnectedDevices current,
        IReadOnlyList<DiscoveredDevice> picked,
        Action<string, bool> progress,
        CancellationToken ct) =>
        AddAsync(config, current, picked, TryConnectAsync, progress, ct);

    /// <summary>The same, with the connect step passed in, for tests.</summary>
    internal static async Task<ConnectedDevices> AddAsync(
        AppConfig config,
        ConnectedDevices current,
        IReadOnlyList<DiscoveredDevice> picked,
        Func<DiscoveredDevice, Action<string, bool>, CancellationToken, Task<ISensorDevice?>> connect,
        Action<string, bool> progress,
        CancellationToken ct)
    {
        var trainer = current.Trainer;
        var heartRate = current.HeartRate;
        var shifters = current.Shifters.ToList();
        var added = new List<ISensorDevice>();
        var replaced = new List<ISensorDevice>();
        try
        {
            foreach (var found in picked)
            {
                if (await connect(found, progress, ct) is not { } device)
                {
                    continue;
                }

                added.Add(device);
                config.Devices.Pick(found.Kind, found.Id, found.Name);
                switch (device)
                {
                    case ITrainer t:
                        replaced.Add(trainer);
                        trainer = t;
                        break;
                    case IHeartRateMonitor hr:
                        if (heartRate is not null)
                        {
                            replaced.Add(heartRate);
                        }

                        heartRate = (IHeartRateMonitor)hr;
                        break;
                    case IShifter shifter:
                        shifters.Add(shifter);
                        break;
                }
            }
        }
        catch
        {
            // Cancelled part way: the new ones go, the ones that were connected before stay.
            foreach (var device in added.AsEnumerable().Reverse())
            {
                await device.DisposeAsync();
            }

            throw;
        }

        config.Devices.Picked = true;
        config.Save();
        foreach (var device in replaced)
        {
            await device.DisposeAsync();
        }

        return new ConnectedDevices(trainer, heartRate, shifters);
    }

    /// <summary>Connects one device of any kind. Null, with the reason reported, when it would not connect.</summary>
    private static async Task<ISensorDevice?> TryConnectAsync(DiscoveredDevice found, Action<string, bool> progress, CancellationToken ct)
    {
        progress(Loc.Format(CoreStrings.Connect_ConnectingAt, found.Name, found.Id), false);
        var connection = found.Link.Open(found);
        try
        {
            await connection.ConnectAsync(ct);
            return connection;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            progress(Loc.Format(CoreStrings.Connect_DeviceFailed, found.Name, found.Id, e.Message), true);
            await connection.DisposeAsync();
            return null;
        }
        catch (OperationCanceledException)
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal static bool IsWanted(DevicesConfig saved, DiscoveredDevice d, bool wantHeartRate, bool wantShifters) => d.Kind switch
    {
        DeviceKind.Trainer => Matches(saved.Trainer, d),
        // Once devices were picked, an empty slot means no device of that kind.
        DeviceKind.HeartRate => wantHeartRate && (saved.Picked ? saved.HeartRate == d.Id : Matches(saved.HeartRate, d)),
        DeviceKind.Shifter => wantShifters && (saved.Shifters.Count == 0 ? !saved.Picked : saved.Shifters.Contains(d.Id)),
        _ => false,
    };

    /// <summary>Whether every wanted device is in view, so the scan can stop early.</summary>
    internal static bool AllInView(DevicesConfig saved, IReadOnlyList<DiscoveredDevice> found, bool wantHeartRate, bool wantShifters)
    {
        // Once devices were picked, an empty heart rate or controller slot means none is coming.
        var hrExpected = wantHeartRate && !(saved.Picked && saved.HeartRate is null);
        var shiftersExpected = wantShifters && !(saved.Picked && saved.Shifters.Count == 0);
        return found.Any(d => d.Kind == DeviceKind.Trainer && IsWanted(saved, d, wantHeartRate, wantShifters))
            && (!hrExpected || found.Any(d => d.Kind == DeviceKind.HeartRate && IsWanted(saved, d, wantHeartRate, wantShifters)))
            && (!shiftersExpected || found.Count(d => d.Kind == DeviceKind.Shifter && IsWanted(saved, d, wantHeartRate, wantShifters)) >= Math.Max(1, saved.Shifters.Count));
    }

    /// <summary>Connects the trainer (which must work) and whichever of the others do. Cancelling disconnects them all.</summary>
    private static async Task<ConnectedDevices> ConnectDevicesAsync(
        DiscoveredDevice trainerDevice,
        DiscoveredDevice? hrDevice,
        IReadOnlyList<DiscoveredDevice> shifterDevices,
        Action<string, bool> progress,
        CancellationToken ct)
    {
        var connected = new List<ISensorDevice>();
        try
        {
            progress(Loc.Format(CoreStrings.Connect_Connecting, trainerDevice.Name), false);
            var trainer = trainerDevice.Link.Open(trainerDevice);
            connected.Add(trainer);
            await trainer.ConnectAsync(ct);

            IHeartRateMonitor? heartRate = null;
            if (hrDevice is not null)
            {
                progress(Loc.Format(CoreStrings.Connect_Connecting, hrDevice.Name), false);
                var hr = hrDevice.Link.Open(hrDevice);
                try
                {
                    connected.Add(hr);
                    await hr.ConnectAsync(ct);
                    heartRate = (IHeartRateMonitor)hr;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    progress(Loc.Format(CoreStrings.Connect_HrFailed, hrDevice.Name, e.Message), true);
                    connected.Remove(hr);
                    await hr.DisposeAsync();
                }
            }

            var shifters = new List<IShifter>();
            foreach (var device in shifterDevices)
            {
                progress(Loc.Format(CoreStrings.Connect_ConnectingAt, device.Name, device.Id), false);
                var click = device.Link.Open(device);
                try
                {
                    connected.Add(click);
                    await click.ConnectAsync(ct);
                    shifters.Add((IShifter)click);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // A locked Click V2 left puck hides its Zwift service until the Zwift app unlocks it.
                    progress(Loc.Format(CoreStrings.Connect_DeviceFailed, device.Name, device.Id, e.Message), true);
                    connected.Remove(click);
                    await click.DisposeAsync();
                }
            }

            return new ConnectedDevices((ITrainer)trainer, heartRate, shifters);
        }
        catch
        {
            foreach (var device in connected.AsEnumerable().Reverse())
            {
                await device.DisposeAsync();
            }

            throw;
        }
    }

    private static bool Matches(DeviceId? saved, DiscoveredDevice d) => saved is null || saved == d.Id;

    /// <summary>A null Rssi, from a device BlueZ only remembers or an ANT+ stick without a reading, comes last.</summary>
    private static DiscoveredDevice? Best(IEnumerable<DiscoveredDevice> found, DeviceKind kind) =>
        found.Where(d => d.Kind == kind).OrderByDescending(d => d.Rssi ?? short.MinValue).FirstOrDefault();
}
