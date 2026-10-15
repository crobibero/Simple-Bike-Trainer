using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Intervals;
using SimpleBikeTrainer.Platform;
using SimpleBikeTrainer.Resources;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Ui;
using SimpleBikeTrainer.Uploads;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Commands;

/// <summary><c>ride</c>, the default: picks a workout, connects, and rides it. Not RideCommand, which names the ride loop's commands.</summary>
internal sealed class RideCliCommand(IHttpClientFactory http) : TrainerCommand<RideCliCommand.Settings>
{
    internal sealed class Settings : TrainerSettings
    {
        [CommandOption("--event <ID>")]
        public long? Event { get; set; }

        [CommandOption("--date <YYYY-MM-DD>")]
        public DateOnly? Date { get; set; }

        [CommandOption("--free")]
        public bool Free { get; set; }

        [CommandOption("--ramp")]
        public bool Ramp { get; set; }

        [CommandOption("--ramp-start <WATTS>")]
        public int? RampStart { get; set; }

        [CommandOption("--route <DISTANCE>")]
        public double? Route { get; set; }

        [CommandOption("--climb <HEIGHT>")]
        public double? Climb { get; set; }

        [CommandOption("--seed <N>")]
        public int? Seed { get; set; }

        [CommandOption("--file <PATH>")]
        public string? File { get; set; }

        [CommandOption("--ftp <WATTS>")]
        public int? Ftp { get; set; }

        [CommandOption("--offline")]
        public bool Offline { get; set; }

        [CommandOption("--upload")]
        public bool Upload { get; set; }

        [CommandOption("--no-upload")]
        public bool NoUpload { get; set; }

        [CommandOption("--upload-to <TARGETS>")]
        public string? UploadTo { get; set; }

        [CommandOption("--no-hr")]
        public bool NoHeartRate { get; set; }

        [CommandOption("--no-click")]
        public bool NoClick { get; set; }

        [CommandOption("--simulate")]
        public bool Simulate { get; set; }

        [CommandOption("--speed <N>")]
        public double? Speed { get; set; }

        [CommandOption("--no-ui")]
        public bool NoUi { get; set; }

        /// <summary><see cref="UploadTo"/>, read; null when it was not given.</summary>
        public IReadOnlyList<UploadTarget>? UploadTargets { get; private set; }

        public override ValidationResult Validate()
        {
            if (UploadTo is null)
            {
                return ValidationResult.Success();
            }

            var (targets, error) = UploadTargetList.Parse(UploadTo);
            UploadTargets = targets;
            return error;
        }
    }

    protected override async Task<int> ExecuteAsync(AppConfig config, Settings settings, CancellationToken ct)
    {
        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        var offline = settings.Offline || !config.HasApiKey;
        using var intervals = offline ? null : new IntervalsClient(http, config.Intervals.ApiKey, config.Intervals.AthleteId);

        // Athlete settings: Intervals.icu first, then the config file.
        var athlete = Athlete.FromConfig(config);
        if (intervals is not null)
        {
            await AnsiConsole.Status().StartAsync(CliStrings.Cli_LoadingSettings, async _ =>
            {
                athlete = await Athlete.LoadAsync(config, intervals, ct);
            });
        }

        var workout = await ChooseWorkoutAsync(settings, config, intervals, interactive, ct);
        if (workout is null)
        {
            return 0;
        }

        // A simulated ride can run fast: --speed 10 rides an hour in six minutes.
        TimeProvider clock = settings.Simulate ? new ScaledTimeProvider(settings.Speed ?? 1) : TimeProvider.System;
        var options = athlete.Options(config, workout, settings.Ftp);
        var ftp = options.Ftp;

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(workout.Name)}[/]: " + Markup.Escape(Loc.Format(CliStrings.Cli_WorkoutLine, workout.Steps.Count, ftp) +
            (workout.Ftp is { } wf && wf != ftp ? Loc.Format(CliStrings.Cli_WorkoutFtp, wf) : string.Empty)));

        await using var devices = settings.Simulate
            ? Simulate(clock, !settings.NoHeartRate)
            : await ConnectAsync(config, !settings.NoHeartRate, !settings.NoClick, ct);

        var session = new RideSession(workout, options, devices.Trainer, devices.HeartRate, devices.Shifters, clock);

        // Keep the screen on for the ride; the desktop releases it when this process ends.
        await using var awake = await KeepAwake.AcquireAsync(Loc.Format(CliStrings.Cli_AwakeReason, workout.Name));
        RideResult result;
        if (interactive && !settings.NoUi)
        {
            result = await RunDashboardAsync(session, devices, config.Units, ct);
        }
        else
        {
            result = await RunPlainAsync(session, config.Units, ct);
        }

        AnsiConsole.WriteLine();
        Summary.Write(AnsiConsole.Console, result, config.Units);
        await AfterRideAsync(settings, config, intervals, athlete.Settings, result, interactive, ct);

        return 0;
    }

    private static ConnectedDevices Simulate(TimeProvider clock, bool heartRate)
    {
        var trainer = new SimulatedTrainer(clock);
        return new ConnectedDevices(trainer, heartRate ? new SimulatedHeartRate(trainer, clock) : null, []);
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

    private async Task AfterRideAsync(Settings settings, AppConfig config, IntervalsClient? intervals, RideSettings? athleteSettings, RideResult result, bool interactive, CancellationToken ct)
    {
        var uploads = new UploadService(config, http);
        if (result.FitPath is not null && !settings.NoUpload)
        {
            var targets = settings.UploadTargets ?? uploads.DefaultTargets();
            var go = targets.Count > 0
                && (settings.Upload || config.Uploads.Automatic
                    || (interactive && await AnsiConsole.ConfirmAsync(Markup.Escape(Loc.Format(CliStrings.Cli_UploadPrompt, string.Join(", ", targets))), cancellationToken: ct)));
            foreach (var target in go ? targets : [])
            {
                var outcome = await uploads.UploadAsync(target, result.FitPath, result.Workout.Name, result.Workout.EventId, result.Workout.Description, ct);
                AnsiConsole.MarkupLine($"{(outcome.Succeeded ? "[green]" : "[red]")}{target}[/] {Markup.Escape(outcome.ToString())}");
            }
        }

        if (result.FtpEstimate is { } ftp && interactive && intervals is not null)
        {
            var current = athleteSettings?.TrainerFtp;
            if (await AnsiConsole.ConfirmAsync(Markup.Escape(Loc.Format(CliStrings.Cli_SetFtpPrompt, ftp, current is { } c ? Loc.Format(CliStrings.Cli_SetFtpNow, c) : string.Empty)), defaultValue: false, cancellationToken: ct))
            {
                await intervals.SetFtpAsync(ftp, alsoIndoor: athleteSettings?.IndoorFtp is > 0, ct);
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(Loc.Format(CliStrings.Cli_FtpSet, ftp))}[/] {Markup.Escape(CliStrings.Cli_FtpZwift)}");
            }
        }
    }

    private static async Task<Workout?> ChooseWorkoutAsync(Settings settings, AppConfig config, IntervalsClient? intervals, bool interactive, CancellationToken ct)
    {
        var rampStart = settings.RampStart ?? 100;
        if (settings.Free)
        {
            return Workout.FreeRide();
        }

        if (settings.Ramp)
        {
            return RampTest.Create(rampStart);
        }

        if (settings.Route is { } distance)
        {
            return RideSetup.Route(config.Units, distance, settings.Climb ?? 0, settings.Seed ?? Random.Shared.Next());
        }

        if (settings.File is { } file)
        {
            return RideSetup.LoadFile(file);
        }

        if (settings.Event is { } eventId)
        {
            if (intervals is null)
            {
                throw new InvalidOperationException(CliStrings.Cli_EventNeedsKey);
            }

            var ride = await intervals.GetPlannedRideAsync(eventId, ct);
            return ForRide(ride.Workout, rampStart);
        }

        var rides = new List<PlannedRide>();
        if (intervals is not null)
        {
            var from = settings.Date ?? DateOnly.FromDateTime(DateTime.Today);
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
            return rides.Count > 0 ? ForRide(rides[0].Workout, rampStart) : Workout.FreeRide();
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
            1 => RampTest.Create(rampStart),
            2 => RideSetup.LoadFile(await AnsiConsole.PromptAsync(new TextPrompt<string>(Markup.Escape(CliStrings.Cli_Path)), ct)),
            3 => null,
            _ => ForRide(rides[choice].Workout, rampStart),
        };
    }

    private static Workout ForRide(Workout planned, int rampStart)
    {
        var workout = RideSetup.ForPlanned(planned, rampStart);
        if (workout.IsRampTest)
        {
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(Loc.Format(CliStrings.Cli_NoStepsRamp, planned.Name))}[/]");
        }

        return workout;
    }

    private static string Describe(PlannedRide ride)
    {
        var w = ride.Workout;
        var length = w.Steps.Count > 0 ? Loc.Format(CliStrings.Cli_Length, w.TotalSeconds / 60, w.Steps.Count) : CliStrings.Cli_NoSteps;
        var day = ride.Date == DateOnly.FromDateTime(DateTime.Today) ? CliStrings.Cli_Today : ride.Date.ToString(CliStrings.Cli_ShortDate, CultureInfo.CurrentCulture);
        return Markup.Escape(Loc.Format(CliStrings.Cli_PlannedChoice, day, w.Name, length));
    }
}
