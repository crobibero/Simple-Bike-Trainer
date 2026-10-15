using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Dircon;
using SimpleBikeTrainer.Resources;

namespace SimpleBikeTrainer.Commands;

/// <summary><c>probe</c>: connects read-only and prints trainer data and Click presses; with <c>--control</c>, sets ERG power.</summary>
internal sealed class ProbeCommand : TrainerCommand<ProbeCommand.Settings>
{
    internal sealed class Settings : TrainerSettings
    {
        [CommandOption("--seconds <N>")]
        public int Seconds { get; set; } = 30;

        [CommandOption("--control")]
        public bool Control { get; set; }

        [CommandOption("--watts <WATTS>")]
        public int Watts { get; set; } = 100;

        [CommandOption("--wifi <HOST>")]
        public string? Wifi { get; set; }
    }

    protected override async Task<int> ExecuteAsync(AppConfig config, Settings settings, CancellationToken ct)
    {
        var seconds = settings.Seconds;
        List<DiscoveredDevice> found;
        var wifi = settings.Wifi;
        if (wifi is not null)
        {
            // Straight to a Wi-Fi trainer at a known address, with no scan.
            found = [DirconScanner.Manual(wifi)];
        }
        else
        {
            await using var scanner = await DeviceScanner.CreateAsync(config.Devices, ct);
            ScanCommand.Warn(scanner);
            await scanner.StartAsync(ct);
            AnsiConsole.MarkupLine(Markup.Escape(CliStrings.Cli_Scanning));
            await scanner.WaitForAsync(f => f.Any(d => d.Kind == DeviceKind.Trainer) && f.Any(d => d.Kind == DeviceKind.Shifter), TimeSpan.FromSeconds(10), ct);
            found = [.. scanner.Found];
            await scanner.StopAsync();
        }

        var log = new Action<string>(line => AnsiConsole.MarkupLine($"[grey]{DateTime.Now:HH:mm:ss.f}[/] {line}"));
        var connections = new List<IAsyncDisposable>();
        try
        {
            if (found.FirstOrDefault(d => d.Kind == DeviceKind.Trainer && (wifi is not null || config.Devices.Trainer is null || d.Id == config.Devices.Trainer)) is { } t)
            {
                var last = DateTime.MinValue;
                Action<IndoorBikeData> onData = d =>
                {
                    if (DateTime.UtcNow - last < TimeSpan.FromSeconds(1))
                    {
                        return;
                    }

                    last = DateTime.UtcNow;
                    log(Markup.Escape(Loc.Format(CliStrings.Cli_ProbeTrainer, d.PowerWatts, d.CadenceRpm, d.SpeedKmh, d.HeartRate is { } hr ? Loc.Format(CliStrings.Cli_ProbeHr, hr) : string.Empty)));
                };

                if (t.Link is not BlePeripheral peripheral)
                {
                    // ANT+ FE-C: listening takes no control, so only --control sets a target.
                    var trainer = (ITrainer)t.Link.Open(t);
                    connections.Add(trainer);
                    trainer.DataReceived += onData;
                    await ((IConnectableDevice)trainer).ConnectAsync(ct);
                    log($"[green]{Markup.Escape(t.Name)}[/] {Markup.Escape(Loc.Format(CliStrings.Cli_ProbeConnected, "FE-C", CoreStrings.Probe_NotReported))}");
                    if (settings.Control)
                    {
                        log($"[green]{Markup.Escape(t.Name)}[/] {Markup.Escape(Loc.Format(CliStrings.Cli_ProbeControlled, settings.Watts))}");
                        await trainer.SetTargetPowerAsync(settings.Watts, ct);
                        log(Markup.Escape(trainer.LastError ?? CoreStrings.Ftms_Success));
                    }
                }
                else if (settings.Control)
                {
                    // Takes control: request control, start, then hold the requested watts. Reset on exit.
                    var trainer = new FtmsTrainer(peripheral, t.Name, t.Id);
                    connections.Add(trainer);
                    trainer.ControlPointResponded += r => log(Markup.Escape(Loc.Format(CliStrings.Cli_ProbeControl, r.RequestOpCode, r.ResultName)));
                    trainer.DataReceived += onData;
                    await trainer.ConnectAsync(ct);
                    log($"[green]{Markup.Escape(t.Name)}[/] {Markup.Escape(Loc.Format(CliStrings.Cli_ProbeControlled, settings.Watts))}");
                    await trainer.SetTargetPowerAsync(settings.Watts, ct);
                }
                else
                {
                    var probe = new TrainerProbe(peripheral, t.Name, t.Id);
                    connections.Add(probe);
                    probe.DataReceived += onData;
                    await probe.ConnectAsync(ct);
                    log($"[green]{Markup.Escape(t.Name)}[/] {Markup.Escape(Loc.Format(CliStrings.Cli_ProbeConnected, probe.Features, probe.PowerRange))}");
                }
            }
            else
            {
                log($"[yellow]{Markup.Escape(CliStrings.Cli_NoTrainer)}[/]");
            }

            foreach (var s in found.Where(d => d.Kind == DeviceKind.Shifter))
            {
                var click = (ZwiftClick)s.Link.Open(s);
                connections.Add(click);
                try
                {
                    await click.ConnectAsync(ct);
                    log($"[green]{Markup.Escape(s.Name)}[/] {Markup.Escape(Loc.Format(CliStrings.Cli_ProbeClick, s.Id))}");
                    click.ButtonsPressed += b => log(Loc.Format(CliStrings.Cli_ProbeButtons, Markup.Escape(s.Name), b, click.Battery?.ToString(CultureInfo.CurrentCulture) ?? "?"));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log($"[yellow]{Markup.Escape(Loc.Format(CliStrings.Cli_ProbeFailed, s.Name, s.Id, e.Message))}[/]");
                }
            }

            log(Markup.Escape(Loc.Format(CliStrings.Cli_Listening, seconds)));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            foreach (var c in connections)
            {
                await c.DisposeAsync();
            }
        }

        return 0;
    }
}
