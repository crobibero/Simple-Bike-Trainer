using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Intervals;
using SimpleBikeTrainer.Platform;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Ui;
using SimpleBikeTrainer.Uploads;
using SimpleBikeTrainer.Workouts;
using SimpleBikeTrainer;

var cli = Cli.Parse(args);
if (cli.Has("help") || cli.Has("h"))
{
    Cli.PrintHelp();
    return 0;
}

AppConfig config;
try
{
    config = AppConfig.Load(cli.Value("config"));
}
catch (Exception e) when (e is JsonException or IOException)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(CliStrings.Cli_ConfigUnreadable)}[/] {Markup.Escape(e.Message)}");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return cli.Command switch
    {
        "ride" => await Commands.RideAsync(cli, config, cts.Token),
        "scan" => await Commands.ScanAsync(cli, config, cts.Token),
        "probe" => await Commands.ProbeAsync(cli, config, cts.Token),
        "workouts" => await Commands.WorkoutsAsync(cli, config, cts.Token),
        "upload" => await Commands.UploadAsync(cli, config, cts.Token),
        "config" => Commands.Configure(cli, config),
        "connect" => await Commands.ConnectServiceAsync(cli, config, cts.Token),
        _ => Cli.Unknown(cli.Command),
    };
}
catch (Exception e) when (e is IntervalsException or GarminException or InvalidOperationException or FormatException or HttpRequestException or FileNotFoundException)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.Message)}[/]");
    return 1;
}
catch (OperationCanceledException)
{
    return 130;
}

namespace SimpleBikeTrainer
{
    /// <summary>A small command line: the first bare word is the command, then --flags with optional values.</summary>
    internal sealed class Cli
    {
        private static readonly HashSet<string> Switches =
        [
            "free", "ramp", "control", "simulate", "no-ui", "no-hr", "no-click", "offline", "upload", "no-upload", "help", "h", "yes",
        ];

        private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

        public string Command { get; private set; } = "ride";

        public List<string> Arguments { get; } = [];

        public static Cli Parse(string[] args)
        {
            var cli = new Cli();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.StartsWith('-'))
                {
                    var name = arg.TrimStart('-');
                    string? value = null;
                    var eq = name.IndexOf('=');
                    if (eq >= 0)
                    {
                        value = name[(eq + 1)..];
                        name = name[..eq];
                    }
                    else if (!Switches.Contains(name) && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    {
                        value = args[++i];
                    }

                    cli._options[name] = value;
                }
                else if (i == 0)
                {
                    cli.Command = arg.ToLowerInvariant();
                }
                else
                {
                    cli.Arguments.Add(arg);
                }
            }

            return cli;
        }

        public bool Has(string name) => _options.ContainsKey(name);

        public string? Value(string name) => _options.TryGetValue(name, out var v) ? v : null;

        public int? Int(string name) => Value(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : null;

        public double? Double(string name) => Value(name) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : null;

        public static int Unknown(string command)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(Loc.Format(CliStrings.Cli_UnknownCommand, command))}[/]");
            PrintHelp();
            return 2;
        }

        public static void PrintHelp()
        {
            AnsiConsole.Write(new Markup(CliStrings.Cli_Help));
        }
    }

    internal static class Commands
    {
        public static async Task<int> RideAsync(Cli cli, AppConfig config, CancellationToken ct)
        {
            var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
            var offline = cli.Has("offline") || !config.HasApiKey;
            using var intervals = offline ? null : new IntervalsClient(config.Intervals.ApiKey, config.Intervals.AthleteId);

            // Athlete settings: Intervals.icu first, then the config file.
            var athlete = Athlete.FromConfig(config);
            if (intervals is not null)
            {
                await AnsiConsole.Status().StartAsync(CliStrings.Cli_LoadingSettings, async _ =>
                {
                    athlete = await Athlete.LoadAsync(config, intervals, ct);
                });
            }

            var workout = await ChooseWorkoutAsync(cli, config, intervals, interactive, ct);
            if (workout is null)
            {
                return 0;
            }

            var speed = cli.Has("simulate") ? cli.Double("speed") ?? 1 : 1;
            var options = athlete.Options(config, workout, speed, cli.Int("ftp"));
            var ftp = options.Ftp;

            AnsiConsole.MarkupLine($"[bold]{Markup.Escape(workout.Name)}[/]: " + Markup.Escape(Loc.Format(CliStrings.Cli_WorkoutLine, workout.Steps.Count, ftp) +
                (workout.Ftp is { } wf && wf != ftp ? Loc.Format(CliStrings.Cli_WorkoutFtp, wf) : string.Empty)));

            await using var devices = cli.Has("simulate")
                ? Simulate(speed, !cli.Has("no-hr"))
                : await ConnectAsync(config, !cli.Has("no-hr"), !cli.Has("no-click"), ct);

            var session = new RideSession(workout, options, devices.Trainer, devices.HeartRate, devices.Shifters);

            // Keep the screen on for the ride; the desktop releases it when this process ends.
            await using var awake = await KeepAwake.AcquireAsync(Loc.Format(CliStrings.Cli_AwakeReason, workout.Name));
            RideResult result;
            if (interactive && !cli.Has("no-ui"))
            {
                result = await RunDashboardAsync(session, devices, config.Units, ct);
            }
            else
            {
                result = await RunPlainAsync(session, config.Units, ct);
            }

            AnsiConsole.WriteLine();
            Summary.Write(AnsiConsole.Console, result, config.Units);
            await AfterRideAsync(cli, config, intervals, athlete.Settings, result, interactive, ct);

            return 0;
        }

        private static ConnectedDevices Simulate(double speed, bool heartRate)
        {
            var trainer = new SimulatedTrainer(speed);
            return new ConnectedDevices(trainer, heartRate ? new SimulatedHeartRate(trainer, speed) : null, []);
        }

        private static async Task<ConnectedDevices> ConnectAsync(AppConfig config, bool hr, bool click, CancellationToken ct)
        {
            ConnectedDevices? devices = null;
            await AnsiConsole.Status().StartAsync(CliStrings.Cli_StartingBluetooth, async ctx =>
            {
                devices = await DeviceConnector.ConnectAsync(config, hr, click, (message, warning) =>
                {
                    ctx.Status(Markup.Escape(message));
                    if (warning)
                    {
                        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");
                    }
                }, ct);
            });

            foreach (var device in devices!.All)
            {
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(CliStrings.Cli_Connected)}[/] {Markup.Escape(device.Name)}");
            }

            if (click && devices.Shifters.Count == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(CliStrings.Cli_NoClick)}[/]");
            }

            return devices;
        }

        private static async Task<RideResult> RunDashboardAsync(RideSession session, ConnectedDevices devices, UnitSystem units, CancellationToken ct)
        {
            using var quit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            string? notice = null;
            DateTime? quitAsked = null;

            // Keys go through the same command queue as the Click.
            var keys = Task.Run(async () =>
            {
                while (!quit.IsCancellationRequested)
                {
                    if (!Console.KeyAvailable)
                    {
                        await Task.Delay(30, CancellationToken.None);
                        continue;
                    }

                    var key = Console.ReadKey(intercept: true);
                    notice = null;
                    switch (key.Key)
                    {
                        case ConsoleKey.UpArrow or ConsoleKey.Add or ConsoleKey.OemPlus:
                            session.Send(RideCommand.ShiftUp);
                            break;
                        case ConsoleKey.DownArrow or ConsoleKey.Subtract or ConsoleKey.OemMinus:
                            session.Send(RideCommand.ShiftDown);
                            break;
                        case ConsoleKey.RightArrow:
                            session.Send(RideCommand.GradeUp);
                            break;
                        case ConsoleKey.LeftArrow:
                            session.Send(RideCommand.GradeDown);
                            break;
                        case ConsoleKey.S:
                            session.Send(RideCommand.SkipStep);
                            break;
                        case ConsoleKey.L:
                            session.Send(RideCommand.Lap);
                            break;
                        case ConsoleKey.P or ConsoleKey.Spacebar:
                            session.Send(RideCommand.TogglePause);
                            break;
                        case ConsoleKey.E:
                            session.Send(RideCommand.ToggleErg);
                            break;
                        case ConsoleKey.X:
                            session.Send(RideCommand.EndRampTest);
                            break;
                        case ConsoleKey.Q or ConsoleKey.Escape:
                            // Two presses, so a stray key cannot end a ride.
                            if (quitAsked is { } asked && DateTime.UtcNow - asked < TimeSpan.FromSeconds(3))
                            {
                                session.Send(RideCommand.Finish);
                                return;
                            }

                            quitAsked = DateTime.UtcNow;
                            notice = CliStrings.Cli_PressQAgain;
                            break;
                    }
                }
            }, CancellationToken.None);

            RideResult? result = null;
            await AnsiConsole.Live(Dashboard.Render(session.Snapshot, session.Workout, devices.All, notice, units))
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .StartAsync(async live =>
                {
                    // Render on the ride loop's thread, where the metrics are consistent.
                    session.Updated += snapshot =>
                    {
                        live.UpdateTarget(Dashboard.Render(snapshot, session.Workout, devices.All, notice, units));
                        live.Refresh();
                    };
                    result = await session.RunAsync(ct);
                });

            await quit.CancelAsync();
            await keys;
            return result!;
        }

        private static async Task<RideResult> RunPlainAsync(RideSession session, UnitSystem units, CancellationToken ct)
        {
            var last = -1;
            session.Updated += s =>
            {
                // Checked on every update: moving time stops when the rider does, so it may never reach the next status line.
                if (s.WorkoutComplete && s.Step is null && session.Workout.Steps.Count > 0)
                {
                    session.Send(RideCommand.Finish);
                }

                var seconds = (int)s.Moving.TotalSeconds;
                if (seconds % 10 != 0 || seconds == last)
                {
                    return;
                }

                last = seconds;
                Console.WriteLine(
                    Loc.Format(CliStrings.Cli_StatusLine, Dashboard.Clock(s.Moving), s.State, s.Step?.Label ?? CliStrings.Cli_FreeRide, s.Power3s, s.TargetWatts, s.Cadence, s.HeartRate,
                        units.FormatDistance(s.DistanceKm * 1000), s.Metrics.NormalizedPower, s.Metrics.TrainingStressScore));
            };
            return await session.RunAsync(ct);
        }

        private static async Task AfterRideAsync(Cli cli, AppConfig config, IntervalsClient? intervals, RideSettings? settings, RideResult result, bool interactive, CancellationToken ct)
        {
            var uploads = new UploadService(config);
            if (result.FitPath is not null && !cli.Has("no-upload"))
            {
                var targets = uploads.DefaultTargets();
                if (cli.Value("upload-to") is { } list)
                {
                    targets = [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => Enum.Parse<UploadTarget>(t, ignoreCase: true))];
                }

                var go = targets.Count > 0
                    && (cli.Has("upload") || config.Uploads.Automatic
                        || (interactive && await AnsiConsole.ConfirmAsync(Markup.Escape(Loc.Format(CliStrings.Cli_UploadPrompt, string.Join(", ", targets))), cancellationToken: ct)));
                foreach (var target in go ? targets : [])
                {
                    var outcome = await uploads.UploadAsync(target, result.FitPath, result.Workout.Name, result.Workout.EventId, result.Workout.Description, ct);
                    AnsiConsole.MarkupLine($"{(outcome.Succeeded ? "[green]" : "[red]")}{target}[/] {Markup.Escape(outcome.ToString())}");
                }
            }

            if (result.FtpEstimate is { } ftp && interactive && intervals is not null)
            {
                var current = settings?.TrainerFtp;
                if (await AnsiConsole.ConfirmAsync(Markup.Escape(Loc.Format(CliStrings.Cli_SetFtpPrompt, ftp, current is { } c ? Loc.Format(CliStrings.Cli_SetFtpNow, c) : string.Empty)), defaultValue: false, cancellationToken: ct))
                {
                    await intervals.SetFtpAsync(ftp, alsoIndoor: settings?.IndoorFtp is > 0, ct);
                    AnsiConsole.MarkupLine($"[green]{Markup.Escape(Loc.Format(CliStrings.Cli_FtpSet, ftp))}[/] {Markup.Escape(CliStrings.Cli_FtpZwift)}");
                }
            }
        }

        private static async Task<Workout?> ChooseWorkoutAsync(Cli cli, AppConfig config, IntervalsClient? intervals, bool interactive, CancellationToken ct)
        {
            if (cli.Has("free"))
            {
                return Workout.FreeRide();
            }

            if (cli.Has("ramp"))
            {
                return RampTest.Create(cli.Int("ramp-start") ?? 100);
            }

            if (cli.Double("route") is { } distance)
            {
                return RideSetup.Route(config.Units, distance, cli.Double("climb") ?? 0, cli.Int("seed") ?? Random.Shared.Next());
            }

            if (cli.Value("file") is { } file)
            {
                return LoadFile(file);
            }

            if (cli.Value("event") is { } eventId)
            {
                if (intervals is null)
                {
                    throw new InvalidOperationException(CliStrings.Cli_EventNeedsKey);
                }

                var ride = await intervals.GetPlannedRideAsync(long.Parse(eventId, CultureInfo.InvariantCulture), ct);
                return ForRide(ride.Workout, cli);
            }

            var rides = new List<PlannedRide>();
            if (intervals is not null)
            {
                var from = cli.Value("date") is { } d ? DateOnly.Parse(d, CultureInfo.InvariantCulture) : DateOnly.FromDateTime(DateTime.Today);
                await AnsiConsole.Status().StartAsync(CliStrings.Cli_LoadingPlanned, async _ =>
                {
                    rides.AddRange(await intervals.GetPlannedRidesAsync(from, from.AddDays(6), ct));
                });
            }
            else if (!config.HasApiKey)
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(CliStrings.Cli_NoKey)}[/]");
            }

            if (!interactive)
            {
                return rides.Count > 0 ? ForRide(rides[0].Workout, cli) : Workout.FreeRide();
            }

            string free = Markup.Escape(CliStrings.Cli_ChoiceFree), ramp = Markup.Escape(CliStrings.Cli_ChoiceRamp), fromFile = Markup.Escape(CliStrings.Cli_ChoiceFile), cancel = Markup.Escape(CliStrings.Cli_ChoiceCancel);
            var choices = rides.Select(Describe).Concat([free, ramp, fromFile, cancel]).ToList();
            var choice = choices.IndexOf(await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>().Title(Markup.Escape(CliStrings.Cli_WhatRiding)).PageSize(15).AddChoices(choices),
                ct));

            // The fixed choices follow the planned rides, in this order.
            return (choice - rides.Count) switch
            {
                0 => Workout.FreeRide(),
                1 => RampTest.Create(cli.Int("ramp-start") ?? 100),
                2 => LoadFile(await AnsiConsole.PromptAsync(new TextPrompt<string>(Markup.Escape(CliStrings.Cli_Path)), ct)),
                3 => null,
                _ => ForRide(rides[choice].Workout, cli),
            };
        }

        private static Workout ForRide(Workout planned, Cli cli)
        {
            var workout = RideSetup.ForPlanned(planned, cli.Int("ramp-start") ?? 100);
            if (workout.IsRampTest)
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(Loc.Format(CliStrings.Cli_NoStepsRamp, planned.Name))}[/]");
            }

            return workout;
        }

        internal static Workout LoadFile(string path) => RideSetup.LoadFile(path);

        private static string Describe(PlannedRide ride)
        {
            var w = ride.Workout;
            var length = w.Steps.Count > 0 ? Loc.Format(CliStrings.Cli_Length, w.TotalSeconds / 60, w.Steps.Count) : CliStrings.Cli_NoSteps;
            var day = ride.Date == DateOnly.FromDateTime(DateTime.Today) ? CliStrings.Cli_Today : ride.Date.ToString(CliStrings.Cli_ShortDate, CultureInfo.CurrentCulture);
            return Markup.Escape(Loc.Format(CliStrings.Cli_PlannedChoice, day, w.Name, length));
        }

        public static async Task<int> ScanAsync(Cli cli, AppConfig config, CancellationToken ct)
        {
            var seconds = cli.Int("seconds") ?? 15;
            await using var scanner = await BleScanner.CreateAsync(config.Devices.Adapter);
            await scanner.StartAsync();
            await AnsiConsole.Status().StartAsync(Loc.Format(CliStrings.Cli_ScanningFor, seconds), async _ =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                }
                catch (OperationCanceledException)
                {
                }
            });
            await scanner.StopAsync();

            var found = scanner.Found.OrderBy(d => d.Kind).ThenByDescending(d => d.Rssi ?? short.MinValue).ToList();
            var table = new Table().AddColumns(CliStrings.Cli_ColKind, CliStrings.Cli_ColName, CliStrings.Cli_ColAddress, CliStrings.Cli_ColRssi);
            foreach (var d in found)
            {
                table.AddRow(d.Kind.ToString(), Markup.Escape(d.Name), d.Address, d.Rssi?.ToString(CultureInfo.CurrentCulture) ?? $"[grey]{Markup.Escape(CliStrings.Cli_Cached)}[/]");
            }

            AnsiConsole.Write(table);
            if (found.Count == 0 || Console.IsInputRedirected)
            {
                return 0;
            }

            var trainers = found.Where(d => d.Kind == DeviceKind.Trainer).ToList();
            if (trainers.Count > 0)
            {
                var pick = await AnsiConsole.PromptAsync(new SelectionPrompt<string>().Title(Markup.Escape(CliStrings.Cli_PickTrainer))
                    .AddChoices(trainers.Select(t => $"{t.Address} {Markup.Escape(t.Name)}").Append(Markup.Escape(CliStrings.Cli_Any))), ct);
                config.Devices.Trainer = pick == Markup.Escape(CliStrings.Cli_Any) ? string.Empty : pick.Split(' ')[0];
            }

            var hrs = found.Where(d => d.Kind == DeviceKind.HeartRate).ToList();
            if (hrs.Count > 0)
            {
                var pick = await AnsiConsole.PromptAsync(new SelectionPrompt<string>().Title(Markup.Escape(CliStrings.Cli_PickHr))
                    .AddChoices(hrs.Select(t => $"{t.Address} {Markup.Escape(t.Name)}").Append(Markup.Escape(CliStrings.Cli_Any))), ct);
                config.Devices.HeartRate = pick == Markup.Escape(CliStrings.Cli_Any) ? string.Empty : pick.Split(' ')[0];
            }

            var shifters = found.Where(d => d.Kind == DeviceKind.Shifter).ToList();
            if (shifters.Count > 0)
            {
                var picks = await AnsiConsole.PromptAsync(new MultiSelectionPrompt<string>().Title(Markup.Escape(CliStrings.Cli_PickShifters))
                    .NotRequired().AddChoices(shifters.Select(t => $"{t.Address} {Markup.Escape(t.Name)}")), ct);
                config.Devices.Shifters = [.. picks.Select(p => p.Split(' ')[0])];
            }

            foreach (var d in found.Where(d => d.Address == config.Devices.Trainer || d.Address == config.Devices.HeartRate || config.Devices.Shifters.Contains(d.Address)))
            {
                config.Devices.Names[d.Address] = d.Name;
            }

            // Here an empty slot means "any".
            config.Devices.Picked = false;
            config.Save();
            AnsiConsole.MarkupLine(Markup.Escape(Loc.Format(CliStrings.Cli_SavedTo, config.Path)));
            return 0;
        }

        public static async Task<int> ProbeAsync(Cli cli, AppConfig config, CancellationToken ct)
        {
            var seconds = cli.Int("seconds") ?? 30;
            List<DiscoveredDevice> found;
            await using (var scanner = await BleScanner.CreateAsync(config.Devices.Adapter))
            {
                await scanner.StartAsync();
                AnsiConsole.MarkupLine(Markup.Escape(CliStrings.Cli_Scanning));
                await scanner.WaitForAsync(f => f.Any(d => d.Kind == DeviceKind.Trainer) && f.Any(d => d.Kind == DeviceKind.Shifter), TimeSpan.FromSeconds(10), ct);
                found = [.. scanner.Found];
                await scanner.StopAsync();
            }

            var log = new Action<string>(line => AnsiConsole.MarkupLine($"[grey]{DateTime.Now:HH:mm:ss.f}[/] {line}"));
            var connections = new List<IAsyncDisposable>();
            try
            {
                if (found.FirstOrDefault(d => d.Kind == DeviceKind.Trainer && (config.Devices.Trainer.Length == 0 || d.Address == config.Devices.Trainer)) is { } t)
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

                    if (cli.Has("control"))
                    {
                        // Takes control: request control, start, then hold the requested watts. Reset on exit.
                        var trainer = new FtmsTrainer(t.Device, t.Name, t.Address);
                        connections.Add(trainer);
                        trainer.ControlPointResponded += r => log(Markup.Escape(Loc.Format(CliStrings.Cli_ProbeControl, r.RequestOpCode, r.ResultName)));
                        trainer.DataReceived += onData;
                        await trainer.ConnectAsync(ct);
                        var watts = cli.Int("watts") ?? 100;
                        log($"[green]{Markup.Escape(t.Name)}[/] {Markup.Escape(Loc.Format(CliStrings.Cli_ProbeControlled, watts))}");
                        await trainer.SetTargetPowerAsync(watts, ct);
                    }
                    else
                    {
                        var probe = new TrainerProbe(t.Device, t.Name, t.Address);
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
                    var click = new ZwiftClick(s.Device, s.Name, s.Address);
                    connections.Add(click);
                    try
                    {
                        await click.ConnectAsync(ct);
                        log($"[green]{Markup.Escape(s.Name)}[/] {Markup.Escape(Loc.Format(CliStrings.Cli_ProbeClick, s.Address))}");
                        click.ButtonsPressed += b => log(Loc.Format(CliStrings.Cli_ProbeButtons, Markup.Escape(s.Name), b, click.Battery?.ToString(CultureInfo.CurrentCulture) ?? "?"));
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        log($"[yellow]{Markup.Escape(Loc.Format(CliStrings.Cli_ProbeFailed, s.Name, s.Address, e.Message))}[/]");
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

        public static async Task<int> WorkoutsAsync(Cli cli, AppConfig config, CancellationToken ct)
        {
            using var intervals = Client(config);
            var today = DateOnly.FromDateTime(DateTime.Today);
            var rides = await intervals.GetPlannedRidesAsync(today, today.AddDays((cli.Int("days") ?? 7) - 1), ct);
            if (rides.Count == 0)
            {
                AnsiConsole.MarkupLine(Markup.Escape(CliStrings.Cli_NoPlanned));
                return 0;
            }

            foreach (var ride in rides)
            {
                var w = ride.Workout;
                AnsiConsole.MarkupLine($"[bold]{ride.Date.ToString(CliStrings.Cli_ShortDate, CultureInfo.CurrentCulture)}[/]  {Markup.Escape(w.Name)}  [grey]{Markup.Escape(Loc.Format(CliStrings.Cli_PlannedDetail, ride.Id, ride.Type, w.TotalSeconds / 60, w.Ftp?.ToString(CultureInfo.CurrentCulture) ?? "-"))}[/]");
                foreach (var step in w.Steps)
                {
                    AnsiConsole.MarkupLine($"    {Markup.Escape(step.Label),-22} {Dashboard.Clock(TimeSpan.FromSeconds(step.DurationSeconds)),7}  {Markup.Escape(step.Power?.ToString() ?? CliStrings.Cli_FreeRide)} {Markup.Escape(step.Cadence?.ToString() ?? string.Empty)}");
                }
            }

            return 0;
        }

        public static async Task<int> UploadAsync(Cli cli, AppConfig config, CancellationToken ct)
        {
            if (cli.Arguments.Count != 1)
            {
                throw new InvalidOperationException(CliStrings.Cli_UploadUsage);
            }

            var path = cli.Arguments[0];
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(Loc.Format(CliStrings.Cli_FileMissing, path));
            }

            using var intervals = Client(config);
            var eventId = cli.Value("event") is { } e ? long.Parse(e, CultureInfo.InvariantCulture) : (long?)null;
            var result = await intervals.UploadActivityAsync(path, cli.Value("name") ?? Path.GetFileNameWithoutExtension(path), eventId, description: null, ct);
            AnsiConsole.MarkupLine(result.ActivityId is { } id ? $"[green]{Markup.Escape(CliStrings.Cli_UploadedAt)}[/] https://intervals.icu/activities/{Markup.Escape(id)}" : $"[green]{Markup.Escape(CliStrings.Cli_Uploaded)}[/]");
            return 0;
        }

        public static async Task<int> ConnectServiceAsync(Cli cli, AppConfig config, CancellationToken ct)
        {
            switch (cli.Arguments.FirstOrDefault()?.ToLowerInvariant())
            {
                case "garmin":
                    using (var garmin = new GarminClient(config))
                    {
                        var email = await AnsiConsole.PromptAsync(new TextPrompt<string>(Markup.Escape(CliStrings.Cli_GarminEmail)), ct);
                        var password = await AnsiConsole.PromptAsync(new TextPrompt<string>(Markup.Escape(CliStrings.Cli_GarminPassword)).Secret(), ct);
                        if (await garmin.SignInAsync(email, password, ct))
                        {
                            var code = await AnsiConsole.PromptAsync(new TextPrompt<string>(Markup.Escape(CliStrings.Cli_GarminCode)), ct);
                            await garmin.SubmitMfaAsync(code, ct);
                        }

                        var name = await garmin.GetNameAsync(ct);
                        config.Uploads.Garmin = true;
                        config.Save();
                        AnsiConsole.MarkupLine($"[green]{Markup.Escape(Loc.Format(CliStrings.Cli_GarminSignedIn, name))}[/]");
                    }

                    return 0;
                default:
                    throw new InvalidOperationException(CliStrings.Cli_ConnectUsage);
            }
        }

        public static int Configure(Cli cli, AppConfig config)
        {
            var changed = false;
            if (cli.Value("api-key") is { } key)
            {
                config.SetFileApiKey(key);
                changed = true;
            }

            if (cli.Value("athlete-id") is { } athlete)
            {
                config.SetFileAthleteId(athlete);
                changed = true;
            }

            if (cli.Int("ftp") is { } ftp)
            {
                config.Rider.Ftp = ftp;
                changed = true;
            }

            if (cli.Value("units") is { } unitName)
            {
                config.Units = Enum.Parse<UnitSystem>(unitName, ignoreCase: true);
                changed = true;
            }

            // Weights are typed in the configured units.
            if (cli.Double("weight") is { } weight)
            {
                config.Rider.WeightKg = config.Units.WeightToKg(weight);
                changed = true;
            }

            if (cli.Double("bike-weight") is { } bikeWeight)
            {
                config.Rider.BikeWeightKg = config.Units.WeightToKg(bikeWeight);
                changed = true;
            }

            if (cli.Value("upload-to") is { } targets)
            {
                var set = targets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => Enum.Parse<UploadTarget>(t, ignoreCase: true)).ToHashSet();
                config.Uploads.Intervals = set.Contains(UploadTarget.Intervals);
                config.Uploads.Garmin = set.Contains(UploadTarget.Garmin);
                changed = true;
            }

            if (cli.Value("auto-upload") is { } auto)
            {
                config.Uploads.Automatic = bool.Parse(auto);
                changed = true;
            }

            if (cli.Int("max-hr") is { } maxHr)
            {
                config.Rider.MaxHeartRate = maxHr;
                changed = true;
            }

            if (cli.Has("forget-devices"))
            {
                config.Devices = new DevicesConfig { Adapter = config.Devices.Adapter };
                changed = true;
            }

            if (changed || !File.Exists(config.Path))
            {
                config.Save();
                AnsiConsole.MarkupLine(Markup.Escape(Loc.Format(CliStrings.Cli_Saved, config.Path)));
            }

            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow(CliStrings.Cli_RowConfig, Markup.Escape(config.Path));
            grid.AddRow(CliStrings.Cli_RowKey, config.HasApiKey ? CliStrings.Cli_Set : $"[yellow]{CliStrings.Cli_NotSet}[/]");
            grid.AddRow(CliStrings.Cli_RowAthlete, Markup.Escape(config.Intervals.AthleteId));
            grid.AddRow(CliStrings.Cli_RowFtp, Loc.Format(CliStrings.Cli_FtpValue, config.Rider.Ftp, config.Rider.WPrime));
            grid.AddRow(CliStrings.Cli_RowUnits, config.Units == UnitSystem.Imperial ? CliStrings.Cli_Imperial : CliStrings.Cli_Metric);
            grid.AddRow(CliStrings.Cli_RowWeights, $"{config.Units.FormatWeight(config.Rider.WeightKg)} + {config.Units.FormatWeight(config.Rider.BikeWeightKg)}");
            grid.AddRow(CliStrings.Cli_RowTrainer, Markup.Escape(string.IsNullOrEmpty(config.Devices.Trainer) ? CliStrings.Cli_AnyDevice : config.Devices.Describe(config.Devices.Trainer)));
            grid.AddRow(CliStrings.Cli_RowHr, Markup.Escape(string.IsNullOrEmpty(config.Devices.HeartRate) ? CliStrings.Cli_AnyDevice : config.Devices.Describe(config.Devices.HeartRate)));
            grid.AddRow(CliStrings.Cli_RowShifters, Markup.Escape(config.Devices.Shifters.Count > 0 ? string.Join(", ", config.Devices.Shifters.Select(config.Devices.Describe)) : CliStrings.Cli_AnyDevice));
            grid.AddRow(CliStrings.Cli_RowRides, Markup.Escape(config.RidesDirectory));
            var chosen = new[] { (UploadTarget.Intervals, config.Uploads.Intervals), (UploadTarget.Garmin, config.Uploads.Garmin) };
            grid.AddRow(CliStrings.Cli_RowUpload, string.Join(", ", chosen.Where(t => t.Item2).Select(t => t.Item1)) + (config.Uploads.Automatic ? CliStrings.Cli_Automatic : CliStrings.Cli_Ask));
            grid.AddRow(CliStrings.Cli_RowMaxHr, config.Rider.MaxHeartRate > 0 ? Loc.Format(CliStrings.Cli_Bpm, config.Rider.MaxHeartRate) : CliStrings.Cli_NotSet);
            grid.AddRow(CliStrings.Cli_RowGarmin, GarminClient.IsSignedIn(config) ? CliStrings.Cli_SignedIn : CliStrings.Cli_NotSignedIn);
            AnsiConsole.Write(grid);
            return 0;
        }

        private static IntervalsClient Client(AppConfig config) =>
            config.HasApiKey
                ? new IntervalsClient(config.Intervals.ApiKey, config.Intervals.AthleteId)
                : throw new InvalidOperationException(CliStrings.Cli_NoKey);

        private static string Or(string value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;
    }
}
