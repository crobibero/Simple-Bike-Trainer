using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Ble;

public static class DeviceConnector
{
    // Long enough for a strap that advertises slowly, or only once worn; the scan ends as soon as everything is in view.
    private static readonly TimeSpan ScanTime = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TrainerWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private const int Attempts = 2;

    /// <summary>Connects the saved devices for a ride, or with none saved any trainer in range. Throws when no trainer connects.</summary>
    public static async Task<ConnectedDevices> ConnectAsync(
        AppConfig config,
        bool wantHeartRate,
        bool wantShifters,
        Action<string, bool> progress,
        CancellationToken ct)
    {
        var set = new DeviceSet();
        try
        {
            await ConnectSavedAsync(config, set, wantHeartRate, wantShifters, progress, ct);
            // ConnectSavedAsync throws without a trainer.
            var devices = set.ForRide()!;
            set.Clear();
            return devices;
        }
        catch
        {
            await set.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Connects the saved devices not in <paramref name="set"/> yet, or with none saved any trainer in range. A device
    /// that will not connect yet keeps trying in the set. Throws when the set still has no trainer.
    /// </summary>
    public static async Task ConnectSavedAsync(
        AppConfig config,
        DeviceSet set,
        bool wantHeartRate,
        bool wantShifters,
        Action<string, bool> progress,
        CancellationToken ct)
    {
        var saved = config.Devices;
        await using var scanner = await DeviceScanner.CreateAsync(saved, ct);
        foreach (var warning in scanner.Warnings)
        {
            progress(warning, true);
        }

        progress(CoreStrings.Connect_Scanning, false);
        await scanner.StartAsync(ct);

        bool Wanted(DiscoveredDevice d) => !set.Contains(d.Id) && IsWanted(saved, d, wantHeartRate, wantShifters);
        bool HasTrainer(IReadOnlyList<DiscoveredDevice> found) => set.Trainer is not null || found.Any(d => d.Kind == DeviceKind.Trainer && Wanted(d));

        // Scan until everything wanted is in view, or the scan time is up.
        await scanner.WaitForAsync(found => AllInView(saved, found, wantHeartRate, wantShifters, set), ScanTime, ct);
        if (!HasTrainer(scanner.Found))
        {
            progress(CoreStrings.Connect_Waiting, false);
            await scanner.WaitForAsync(HasTrainer, TrainerWait, ct);
        }

        var found = scanner.Found.Where(Wanted).ToList();
        await scanner.StopAsync();

        // The ride goes ahead without a device that was not found, so the rider hears about it.
        foreach (var missing in saved.Saved.Where(s => s.Kind != DeviceKind.Trainer && IsWantedKind(s.Kind, wantHeartRate, wantShifters)
            && !set.Contains(s.Id) && !found.Any(d => d.Id == s.Id)))
        {
            progress(Loc.Format(CoreStrings.Connect_NotFound, saved.Describe(missing.Id)), true);
        }

        List<DiscoveredDevice> picks = [];
        foreach (var kind in (DeviceKind[])[DeviceKind.Trainer, DeviceKind.PowerMeter, DeviceKind.CadenceSensor, DeviceKind.HeartRate])
        {
            if (set.Get(kind) is null && Best(found, kind) is { } best)
            {
                picks.Add(best);
            }
        }

        picks.AddRange(found.Where(d => d.Kind == DeviceKind.Shifter));
        var connected = await ConnectIntoAsync(set, picks, progress, ct);

        var changed = false;
        foreach (var d in connected)
        {
            changed |= saved.RememberName(d.Id, d.Name);

            // With no trainer or monitor saved, the one that connected becomes the saved one.
            if (d.Kind is DeviceKind.Trainer or DeviceKind.HeartRate && saved.Of(d.Kind) is null)
            {
                saved.Pick(d.Kind, d.Id, d.Name);
                changed = true;
            }
        }

        if (changed)
        {
            config.Save();
        }

        if (set.Trainer is null)
        {
            throw new InvalidOperationException(
                saved.Trainer is null
                    ? CoreStrings.Connect_NoTrainer
                    : Loc.Format(CoreStrings.Connect_SavedMissing, saved.Describe(saved.Trainer)));
        }
    }

    /// <summary>
    /// Connects devices picked from a scan into <paramref name="set"/> and saves them; each replaces the one of its kind,
    /// but controllers add up. Returns the ones that connected.
    /// </summary>
    public static Task<IReadOnlyList<ISensorDevice>> AddAsync(
        AppConfig config,
        DeviceSet set,
        IReadOnlyList<DiscoveredDevice> picked,
        Action<string, bool> progress,
        CancellationToken ct) =>
        AddAsync(config, set, picked, TryConnectAsync, progress, ct);

    /// <summary>The same, with the connect step passed in, for tests.</summary>
    internal static async Task<IReadOnlyList<ISensorDevice>> AddAsync(
        AppConfig config,
        DeviceSet set,
        IReadOnlyList<DiscoveredDevice> picked,
        Func<DiscoveredDevice, Action<string, bool>, CancellationToken, Task<ISensorDevice?>> connect,
        Action<string, bool> progress,
        CancellationToken ct)
    {
        var added = new List<(DiscoveredDevice Found, ISensorDevice Device)>();
        try
        {
            foreach (var found in picked)
            {
                if (await connect(found, progress, ct) is { } device)
                {
                    added.Add((found, device));
                }
            }
        }
        catch
        {
            // Cancelled part way: the new ones go, the ones that were connected before stay.
            foreach (var (_, device) in added.AsEnumerable().Reverse())
            {
                await device.DisposeAsync();
            }

            throw;
        }

        if (added.Count == 0)
        {
            return [];
        }

        foreach (var (found, device) in added)
        {
            config.Devices.Pick(found.Kind, found.Id, found.Name);
            if (set.Put(found.Kind, device) is { } replaced)
            {
                await replaced.DisposeAsync();
            }
        }

        config.Devices.Picked = true;
        config.Save();
        return [.. added.Select(a => a.Device)];
    }

    /// <summary>Connects one device of any kind. Null, with the reason reported, when it would not connect.</summary>
    private static async Task<ISensorDevice?> TryConnectAsync(DiscoveredDevice found, Action<string, bool> progress, CancellationToken ct)
    {
        progress(Loc.Format(CoreStrings.Connect_ConnectingAt, found.Name, found.Id), false);
        var connection = found.Link.Open(found);
        try
        {
            await ConnectWithRetryAsync(connection, found, progress, ct);
            return connection;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            progress(Loc.Format(CoreStrings.Connect_DeviceFailed, found.Name, found.Id, e.Message), true);
            await connection.DisposeAsync();
            return null;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Connects <paramref name="picks"/> into <paramref name="set"/>; on a failure the set is left as it was.</summary>
    internal static async Task<IReadOnlyList<DiscoveredDevice>> ConnectIntoAsync(
        DeviceSet set,
        IReadOnlyList<DiscoveredDevice> picks,
        Action<string, bool> progress,
        CancellationToken ct)
    {
        var added = new List<(DiscoveredDevice Found, IConnectableDevice Device)>();
        try
        {
            foreach (var found in picks)
            {
                progress(Loc.Format(CoreStrings.Connect_ConnectingAt, found.Name, found.Id), false);
                var device = found.Link.Open(found);
                try
                {
                    if (!IsSensor(found.Kind))
                    {
                        await ConnectWithRetryAsync(device, found, progress, ct);
                    }
                    else if (InView(found))
                    {
                        // One try: the background connect below retries on its own, without holding up the ride.
                        await device.ConnectAsync(ct);
                    }
                    else
                    {
                        // BlueZ only remembers it, so a connect now would only time out.
                        progress(Loc.Format(CoreStrings.Connect_NotInRange, found.Name, found.Id), true);
                        device.ConnectInBackground();
                    }
                }
                catch (Exception e) when (!ct.IsCancellationRequested && IsSensor(found.Kind))
                {
                    // Such as a strap still asleep: it joins the ride once it answers.
                    progress(Loc.Format(CoreStrings.Connect_StillTrying, found.Name, found.Id, e.Message), true);
                    device.ConnectInBackground();
                }
                catch (Exception e) when (!ct.IsCancellationRequested && found.Kind == DeviceKind.Shifter)
                {
                    // A locked Click V2 left puck hides its Zwift service until the Zwift app unlocks it.
                    progress(Loc.Format(CoreStrings.Connect_DeviceFailed, found.Name, found.Id, e.Message), true);
                    await device.DisposeAsync();
                    continue;
                }
                catch (Exception e)
                {
                    await device.DisposeAsync();
                    if (ct.IsCancellationRequested)
                    {
                        throw;
                    }

                    throw new InvalidOperationException(Loc.Format(CoreStrings.Connect_DeviceFailed, found.Name, found.Id, e.Message), e);
                }

                added.Add((found, device));
            }
        }
        catch
        {
            foreach (var (_, device) in added.AsEnumerable().Reverse())
            {
                await device.DisposeAsync();
            }

            throw;
        }

        foreach (var (found, device) in added)
        {
            if (set.Put(found.Kind, device) is { } replaced)
            {
                await replaced.DisposeAsync();
            }
        }

        return [.. added.Where(a => a.Device.IsConnected).Select(a => a.Found)];
    }

    /// <summary>A second try often works: a strap that was still waking up, or a link BlueZ had left half open.</summary>
    private static async Task ConnectWithRetryAsync(IConnectableDevice device, DiscoveredDevice found, Action<string, bool> progress, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await device.ConnectAsync(ct);
                return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested && attempt < Attempts)
            {
                progress(Loc.Format(CoreStrings.Connect_Retrying, found.Name, e.Message), false);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    internal static bool IsWanted(DevicesConfig saved, DiscoveredDevice d, bool wantHeartRate, bool wantShifters) => d.Kind switch
    {
        DeviceKind.Trainer => Matches(saved.Trainer, d),
        // Once devices were picked, an empty slot means no device of that kind.
        DeviceKind.HeartRate => wantHeartRate && (saved.Picked ? saved.HeartRate == d.Id : Matches(saved.HeartRate, d)),
        DeviceKind.Shifter => wantShifters && (saved.Shifters.Count == 0 ? !saved.Picked : saved.Shifters.Contains(d.Id)),
        // Only the paired one: in a gym, a power meter on someone else's bike is easy to pick up.
        DeviceKind.PowerMeter or DeviceKind.CadenceSensor => saved.Of(d.Kind) == d.Id,
        _ => false,
    };

    /// <summary>Whether every wanted device is in view. One BlueZ only remembers, with no signal, may still be asleep.</summary>
    internal static bool AllInView(DevicesConfig saved, IReadOnlyList<DiscoveredDevice> found, bool wantHeartRate, bool wantShifters, DeviceSet? connected = null)
    {
        bool Here(DeviceId id) => connected?.Contains(id) == true || found.Any(d => d.Id == id && InView(d));
        bool AnyHere(DeviceKind kind) => found.Any(d => d.Kind == kind && InView(d) && IsWanted(saved, d, wantHeartRate, wantShifters));

        // Once devices were picked, an empty heart rate or controller slot means none is coming.
        return (saved.Trainer is { } trainer ? Here(trainer) : connected?.Trainer is not null || AnyHere(DeviceKind.Trainer))
            && (!wantHeartRate || (saved.HeartRate is { } hr ? Here(hr) : saved.Picked || AnyHere(DeviceKind.HeartRate)))
            && (!wantShifters || (saved.Shifters.Count > 0 ? saved.Shifters.All(Here) : saved.Picked || AnyHere(DeviceKind.Shifter)))
            && (saved.PowerMeter is not { } meter || Here(meter))
            && (saved.CadenceSensor is not { } sensor || Here(sensor));
    }

    private static bool InView(DiscoveredDevice d) => d.Rssi is not null || d.Id.Transport != DeviceTransport.Bluetooth;

    /// <summary>The kinds a ride can start without, which join once they answer.</summary>
    private static bool IsSensor(DeviceKind kind) => kind is DeviceKind.HeartRate or DeviceKind.PowerMeter or DeviceKind.CadenceSensor;

    private static bool IsWantedKind(DeviceKind kind, bool wantHeartRate, bool wantShifters) => kind switch
    {
        DeviceKind.HeartRate => wantHeartRate,
        DeviceKind.Shifter => wantShifters,
        _ => true,
    };

    private static bool Matches(DeviceId? saved, DiscoveredDevice d) => saved is null || saved == d.Id;

    /// <summary>A null Rssi, from a device BlueZ only remembers or an ANT+ stick without a reading, comes last.</summary>
    private static DiscoveredDevice? Best(IEnumerable<DiscoveredDevice> found, DeviceKind kind) =>
        found.Where(d => d.Kind == kind).OrderByDescending(d => d.Rssi ?? short.MinValue).FirstOrDefault();
}
