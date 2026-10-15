using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia;
using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Desktop.ViewModels;
using SimpleBikeTrainer.Desktop.Views;
using SimpleBikeTrainer.Desktop;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Intervals;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Desktop.Tests;

/// <summary>Builds the Avalonia app on the headless platform with real Skia rendering, so frames can be captured.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}

/// <summary>The desktop test rides in real time, so it has its own project and process, apart from the other ride tests.</summary>
[CollectionDefinition(nameof(DesktopCollection), DisableParallelization = true)]
public sealed class DesktopCollection : ICollectionFixture<HeadlessSession>;

/// <summary>One headless app for every desktop test, since Avalonia sets up its platform once per process.</summary>
public sealed class HeadlessSession : IAsyncDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));

    // Disposing the session stops its UI thread, which would otherwise keep the test process alive.
    public ValueTask DisposeAsync() => Session.DisposeAsync();
}

/// <summary>
/// Drives every page of the desktop app with simulated devices and saves a screenshot of each to
/// SIMPLE_BIKE_TRAINER_SCREENSHOTS (or the test output's screenshots folder).
/// </summary>
[Collection(nameof(DesktopCollection))]
public sealed class DesktopTests(HeadlessSession headless) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("simple-bike-trainer").FullName;
    private readonly string _shots = Environment.GetEnvironmentVariable("SIMPLE_BIKE_TRAINER_SCREENSHOTS") is { Length: > 0 } d
        ? d
        : Path.Combine(AppContext.BaseDirectory, "screenshots");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task EveryPageRendersThroughARide()
    {
        // The Func<Task<T>> overload, so the whole ride is awaited on the UI thread.
        Assert.True(await headless.Session.Dispatch(RideThroughEveryPageAsync, TestContext.Current.CancellationToken));
    }

    private async Task<bool> RideThroughEveryPageAsync()
    {
        Directory.CreateDirectory(_shots);
        var config = AppConfig.Load(Path.Combine(_dir, "config.json"));
        config.RidesDirectory = Path.Combine(_dir, "rides");
        config.Rider.Ftp = 240;
        config.Rider.MaxHeartRate = 190;
        var main = new MainWindowViewModel(config) { Athlete = Athlete.FromConfig(config) };
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 900 };
        window.Show();
        var rides = new List<RideViewModel>();
        try
        {

            // Home, with the kind of rides trainsync plans.
            var today = DateOnly.FromDateTime(DateTime.Today);
            var plan = new[]
            {
                Planned(1, today, "Sweet spot 3x12", "- Warmup 10m 55-65% 80rpm\n\nSweet spot 3x\n- Sweet spot 12m 88-93%\n- Recover 4m 55-65% 80rpm\n\n- Cooldown 5m 55-65% 80rpm"),
                Planned(2, today.AddDays(2), "Long ride 180 min, 2x15 tempo", "- Warmup 10m 55-65% 80rpm\n- Endurance 125m 65-75% 80rpm\n\n2x\n- Tempo 15m 76-87%\n- Endurance 5m 65-75% 80rpm\n\n- Cooldown 5m 55-65% 80rpm"),
                Planned(3, today.AddDays(3), "FTP test 45 min", string.Empty),
            };
            main.Home.AddPlanned(plan);
            main.Home.AddBuiltIns();
            main.Home.Selected = main.Home.Cards[0];

            // First connect: a picker, with no automatic connect. Selecting a second trainer clears the first.
            Assert.False(main.Devices.HasSaved);
            Assert.Equal("Find devices…", main.Devices.ConnectLabel);
            main.Devices.IsScanning = true;
            main.Devices.AddCandidate(Found("CE:D1:96:5E:7A:BB", "KICKR CORE 6107", DeviceKind.Trainer, -58), config.Devices);
            main.Devices.AddCandidate(Found("C8:A3:BD:AE:3B:B6", "Zwift Click", DeviceKind.Shifter, -64), config.Devices);
            main.Devices.AddCandidate(Found("E1:22:33:44:55:66", "HRM-Pro:123456", DeviceKind.HeartRate, -71), config.Devices);
            main.Devices.AddCandidate(Found("D4:00:00:00:00:01", "KICKR BIKE 0001", DeviceKind.Trainer, null), config.Devices);
            Assert.Equal(["Trainer", "Trainer", "Heart rate", "Controller"], main.Devices.Candidates.Select(c => c.KindLabel));
            Assert.False(main.Devices.HasTrainerPicked);
            main.Devices.Candidates[0].Selected = true;
            main.Devices.Candidates[1].Selected = true;
            Assert.False(main.Devices.Candidates[0].Selected);
            main.Devices.Candidates[0].Selected = true;
            main.Devices.Candidates[2].Selected = true;
            main.Devices.Candidates[3].Selected = true;
            Assert.True(main.Devices.HasTrainerPicked);
            Assert.Equal("not in range", main.Devices.Candidates[1].Signal);
            await ShootBoth(window, "0-picker");
            await main.Devices.CancelAsync();
            Assert.False(main.Devices.IsScanning);
            Assert.Empty(main.Devices.Candidates);

            Assert.Equal("Simple Bike Trainer", window.Title);
            Assert.Equal(3, main.Home.PlannedCards.Count);
            Assert.Equal(["Free ride", "Generate a route", "Ramp FTP test"], main.Home.OtherCards.Select(c => c.Title));

            // The two lists share one selection.
            var routeCard = main.Home.OtherCards[1];
            main.Home.SelectedOther = routeCard;
            Assert.Same(routeCard, main.Home.Selected);
            Assert.Null(main.Home.SelectedPlanned);
            Assert.Equal("Route 30 km, 400 m climbing", routeCard.Workout.Name);
            Assert.Contains("Steepest climb", main.Home.RouteStats, StringComparison.Ordinal);
            main.Home.RouteClimb = 600;
            Assert.Equal("Route 30 km, 600 m climbing", routeCard.Workout.Name);
            await ShootBoth(window, "1b-route");

            // Loading, then a failed load: a spinner, then an error card that says what to do.
            main.Home.IsLoading = true;
            await ShootBoth(window, "1c-loading");
            main.Home.IsLoading = false;
            (main.Home.LoadError, main.Home.LoadErrorNeedsSettings) = HomeViewModel.DescribeLoadError(
                new IntervalsException("GET athlete/0/events failed: HTTP 401", System.Net.HttpStatusCode.Unauthorized), "0");
            main.Home.LoadErrorDetail = "GET athlete/0/events failed: HTTP 401";
            Assert.True(main.Home.HasLoadError);
            await ShootBoth(window, "1d-load-error");
            main.Home.LoadError = null;
            main.Home.Selected = main.Home.Cards[0];
            Assert.Same(main.Home.Cards[0], main.Home.SelectedPlanned);
            Assert.Null(main.Home.SelectedOther);

            main.Devices.Simulate = true;
            await main.Devices.ConnectAsync(config);
            Assert.True(main.Devices.IsConnected);
            Assert.True(main.Home.StartCommand.CanExecute(null));
            await ShootBoth(window, "1-home");

            // The ramp test card stands in for the stepless FTP test entry.
            Assert.True(main.Home.Cards[2].Workout.IsRampTest);

            // A short version of the selected workout, ridden by the simulator at 40x.
            var workout = WorkoutTextParser.Parse("- Warmup 2m 55-65% 80rpm\n\nSweet spot 3x\n- Sweet spot 2m 88-93%\n- Recover 1m 55-65% 80rpm\n\n- Cooldown 2m ramp 65-45%", "Sweet spot 3x12") with { EventId = 1 };
            var options = main.Athlete.Options(config, workout, speedFactor: 40);
            var session = new RideSession(workout, options, main.Devices.Devices!.Trainer, main.Devices.Devices.HeartRate, []);
            var ride = new RideViewModel(main, session, options.Ftp, main.Devices) { CountdownStep = TimeSpan.FromMilliseconds(600) };
            main.Current = ride;
            rides.Add(ride);
            var cues = new List<string>();
            SimpleBikeTrainer.Desktop.Services.Sounds.Played = name =>
            {
                lock (cues)
                {
                    cues.Add(name);
                }
            };

            // 3, 2, 1, GO with a beep each before the session starts; nothing moves until it ends.
            ride.Start();
            Assert.Equal("3", ride.Countdown);
            Assert.Equal(Strings.Ride_GetReady, ride.State);
            Assert.StartsWith("First: Warmup", ride.CountdownDetail, StringComparison.Ordinal);
            Assert.Equal("0:00", ride.Moving);
            Shoot(window, "2-countdown.png");
            await Until(() => !ride.IsCountingDown, TimeSpan.FromSeconds(10));
            lock (cues)
            {
                Assert.Equal(new[] { "tick", "tick", "tick", "go" }, cues);
                cues.Clear();
            }

            // The countdown banner and beeps before each step.
            string? cueTitle = null;
            await Until(() => (cueTitle = ride.CueVisible && ride.CueCount is "3" or "2" ? ride.CueTitle : null) is not null, TimeSpan.FromSeconds(30));
            Shoot(window, "2a-cue.png");
            Assert.StartsWith("Next: Sweet spot", cueTitle, StringComparison.Ordinal);
            await Until(() => ride.CueCount == Strings.Ride_Go, TimeSpan.FromSeconds(10));
            lock (cues)
            {
                Assert.Contains("tick", cues);
                Assert.Contains("go", cues);
            }

            await Until(() => Seconds(ride.Moving) >= 390, TimeSpan.FromSeconds(30));
            ride.ShiftUpCommand.Execute(null);
            await Until(() => ride.TargetMode.Contains("+5", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            await ShootBoth(window, "2-ride");
            Assert.Equal(Strings.Ride_Riding, ride.State);
            Assert.False(string.IsNullOrEmpty(ride.StepLabel));
            Assert.NotEqual("-", ride.HeartRate);
            Assert.Equal(7, ride.Zones.Count);
            Assert.StartsWith("Z", ride.HeartRateZone, StringComparison.Ordinal); // zones from the max heart rate
            Assert.Equal(Strings.Ride_GradeErg, ride.GradeDetail);

            // Finish takes two presses, then the summary replaces the ride page.
            await Until(() => ride.State == Strings.Ride_WorkoutDone, TimeSpan.FromSeconds(30));
            ride.FinishCommand.Execute(null);
            Assert.Equal(Strings.Ride_PressAgain, ride.FinishButton);
            ride.FinishCommand.Execute(null);
            await Until(() => main.Current is SummaryViewModel, TimeSpan.FromSeconds(10));
            var summary = (SummaryViewModel)main.Current;
            Assert.True(File.Exists(summary.FitPath));
            Assert.InRange(summary.Laps.Count, 8, 9); // one per step, plus any riding after the workout ended
            Assert.All(summary.Uploads, u => Assert.False(u.Available && u.Selected)); // nothing set up in this config
            await ShootBoth(window, "3-summary");

            main.ShowSettings();
            await ShootBoth(window, "4-settings");

            // The theme choice shows as soon as it is picked; Cancel puts the saved one back.
            var preview = (SettingsViewModel)main.Current;
            preview.ThemeDark = true;
            Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
            preview.CancelCommand.Execute(null);
            Assert.Equal(Avalonia.Styling.ThemeVariant.Default, Application.Current.RequestedThemeVariant);
            main.ShowSettings();

            // Imperial: the weights convert in place, and the next ride shows miles and mph.
            var settings = (SettingsViewModel)main.Current;
            Assert.Equal("Rider (kg)", settings.WeightLabel);
            settings.Imperial = true;
            Assert.True(settings.StartCountdown);
            settings.StartCountdown = false;
            Assert.Equal("Rider (lb)", settings.WeightLabel);
            Assert.Equal("176.4", settings.Weight);
            Assert.Equal("19.8", settings.BikeWeight);
            await settings.SaveCommand.ExecuteAsync(null);
            Assert.Equal(UnitSystem.Imperial, config.Units);
            Assert.Equal(80, config.Rider.WeightKg, 1);
            Assert.Equal(UnitSystem.Imperial, AppConfig.Load(config.Path).Units);
            Assert.False(AppConfig.Load(config.Path).StartCountdown);

            // Escape during the countdown goes back home without recording anything.
            var ridesBefore = Directory.GetFiles(config.RidesDirectory, "*.fit*").Length;
            var abandoned = new RideViewModel(main, new RideSession(Workout.FreeRide(), main.Athlete.Options(config, Workout.FreeRide()), main.Devices.Devices.Trainer, main.Devices.Devices.HeartRate, []), options.Ftp, main.Devices)
            {
                UseCountdown = true,
                CountdownStep = TimeSpan.FromSeconds(5),
            };
            main.Current = abandoned;
            rides.Add(abandoned);
            abandoned.Start();
            Assert.True(main.HandleKey(Avalonia.Input.Key.Escape));
            await Until(() => main.Current is HomeViewModel, TimeSpan.FromSeconds(5));
            Assert.False(abandoned.IsCountingDown);
            Assert.Equal(ridesBefore, Directory.GetFiles(config.RidesDirectory, "*.fit*").Length);

            // Free ride: shifting changes the target the gears ask for.
            var free = new RideSession(Workout.FreeRide(), main.Athlete.Options(config, Workout.FreeRide(), speedFactor: 40), main.Devices.Devices.Trainer, main.Devices.Devices.HeartRate, []);
            var freeRide = new RideViewModel(main, free, options.Ftp, main.Devices);
            main.Current = freeRide;
            rides.Add(freeRide);
            freeRide.Start();
            Assert.False(freeRide.IsCountingDown); // turned off in Settings above
            await Until(() => Seconds(freeRide.Moving) >= 120, TimeSpan.FromSeconds(30));
            var before = int.Parse(freeRide.Target);
            freeRide.ShiftUpCommand.Execute(null);
            freeRide.ShiftUpCommand.Execute(null);
            freeRide.GradeUpCommand.Execute(null);
            await Until(() => freeRide.Gear == "14", TimeSpan.FromSeconds(5));
            await Until(() => Seconds(freeRide.Moving) >= 240, TimeSpan.FromSeconds(30));
            Assert.True(int.Parse(freeRide.Target) > before, $"gear 14 at +0.5% should cost more than {before} W, got {freeRide.Target}");
            Assert.EndsWith(" mi", freeRide.Distance, StringComparison.Ordinal);
            Assert.Equal("mph", freeRide.SpeedUnit);
            Assert.Equal("+0.5", freeRide.Grade);
            Assert.Contains(freeRide.Points, p => p.Grade == 0.5);
            Shoot(window, "5-free-ride.png");

            // The help panel opens over the page, and Escape closes it.
            main.ShowHelpCommand.Execute(null);
            await ShootBoth(window, "6-help");
            Assert.True(main.HandleKey(Avalonia.Input.Key.Escape));
            Assert.False(main.IsHelpOpen);
            await freeRide.StopAsync();

            // A short generated route: the grade follows the road and the climbing is recorded.
            var route = RideSetup.Route(config.Units, 2, 150, seed: 11);
            var routeSession = new RideSession(route, main.Athlete.Options(config, route, speedFactor: 40), main.Devices.Devices.Trainer, main.Devices.Devices.HeartRate, []);
            var routeRide = new RideViewModel(main, routeSession, options.Ftp, main.Devices) { CountdownStep = TimeSpan.Zero };
            main.Current = routeRide;
            rides.Add(routeRide);
            routeRide.Start();
            await Until(() => routeRide.StepLabel.StartsWith("Climbing", StringComparison.Ordinal) && Seconds(routeRide.Moving) >= 150, TimeSpan.FromSeconds(30));
            Assert.Equal(Strings.Ride_GradeRoute, routeRide.GradeDetail);
            Assert.Contains("climbed", routeRide.WorkoutProgress, StringComparison.Ordinal);
            await ShootBoth(window, "7-route-ride");
            await Until(() => routeRide.StepLabel == Strings.Ride_RouteComplete, TimeSpan.FromSeconds(60));
            routeRide.FinishCommand.Execute(null);
            routeRide.FinishCommand.Execute(null);
            await Until(() => main.Current is SummaryViewModel, TimeSpan.FromSeconds(10));
            var routeSummary = (SummaryViewModel)main.Current;
            Assert.Equal(Strings.Common_ClimbingCaps, routeSummary.Headline[^1].Label);
            Shoot(window, "8-route-summary.png");

            // History lists the three finished rides and opens one; Done comes back to the list.
            main.ShowHistory();
            var history = (HistoryViewModel)main.Current;
            await Until(() => !history.IsLoading, TimeSpan.FromSeconds(10));
            Assert.Equal(3, history.Rides.Count);
            Assert.Equal("Route 2 mi, 150 ft climbing", history.Rides[0].Name);
            Assert.Contains("rides", history.WeekText, StringComparison.Ordinal);
            await ShootBoth(window, "9-history");
            await history.OpenCommand.ExecuteAsync(history.Rides[1]);
            var past = Assert.IsType<SummaryViewModel>(main.Current);
            Assert.True(past.Points.Count > 100);
            await past.DoneCommand.ExecuteAsync(null);
            Assert.Same(history, main.Current);

            // Calibration, on the simulated trainer, which plays a spin-down.
            ((SimulatedTrainer)main.Devices.Devices!.Trainer).SpinDownStepTime = TimeSpan.FromMilliseconds(300);
            main.ShowCalibration();
            var calibration = (CalibrationViewModel)main.Current;
            Assert.True(calibration.IsSupported);
            await calibration.StartCommand.ExecuteAsync(null);
            await Until(() => calibration.Step.StartsWith("Pedal", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            Assert.Contains("mph", calibration.Target, StringComparison.Ordinal);
            await ShootBoth(window, "10-calibrate");
            await Until(() => calibration.Succeeded == true, TimeSpan.FromSeconds(5));
            calibration.BackCommand.Execute(null);

            // A ride cut off mid-way: the home page offers its checkpoint, and saving it opens its summary.
            var cut = Path.Combine(config.RidesDirectory, "2026-10-13-0600-cut-short.fit");
            File.Copy(summary.FitPath!, cut + ".partial");
            new SimpleBikeTrainer.Recording.RideInfo { Name = "Cut short", Ftp = 240 }.Save(cut);
            await main.Home.FindInterruptedAsync();
            var row = Assert.Single(main.Home.Interrupted);
            Assert.Contains("Cut short", row.Text, StringComparison.Ordinal);
            await ShootBoth(window, "11-unfinished");
            await main.Home.RecoverCommand.ExecuteAsync(row);
            Assert.Equal("Cut short", Assert.IsType<SummaryViewModel>(main.Current).Title);
            Assert.True(File.Exists(cut));
        }
        finally
        {
            foreach (var r in rides)
            {
                await r.StopAsync();
            }

            SimpleBikeTrainer.Desktop.Services.Sounds.Played = null;
            await main.Devices.DisposeAsync();
            window.Close();
        }

        return true;
    }

    [Fact]
    public async Task ClosingTheAppDuringARideFinishesTheCleanup()
    {
        var session = headless.Session;
        var finished = await session.Dispatch(async () =>
        {
            var (config, main) = await ConnectedAsync();
            var ride = StartRide(main, config, Workout.FreeRide());
            await Until(() => Seconds(ride.Moving) >= 3, TimeSpan.FromSeconds(30));

            // The cleanup's awaits resume on this thread, which App runs it on as the app closes.
            return App.DisposeBeforeExit(main, TimeSpan.FromSeconds(5));
        }, TestContext.Current.CancellationToken);

        Assert.True(finished);
    }

    [Fact]
    public async Task ClosingTheWindowShowsShuttingDownUntilTheDevicesAreDisconnected()
    {
        var (stayedOpen, overlay, closed) = await headless.Session.Dispatch(async () =>
        {
            var (config, main) = await ConnectedAsync();
            var window = new MainWindow { DataContext = main, Width = 1440, Height = 900 };
            App.KeepOpenUntilCleanedUp(window, main, null, TimeSpan.FromSeconds(5));
            window.Show();
            var ride = StartRide(main, config, Workout.FreeRide());
            await Until(() => Seconds(ride.Moving) >= 3, TimeSpan.FromSeconds(30));

            window.Close();
            var stayedOpen = window.IsVisible;
            var overlay = main.IsShuttingDown;
            await Until(() => !window.IsVisible, TimeSpan.FromSeconds(10));
            return (stayedOpen, overlay, !window.IsVisible);
        }, TestContext.Current.CancellationToken);

        Assert.True(stayedOpen);
        Assert.True(overlay);
        Assert.True(closed);
    }

    [Fact]
    public async Task SavedDevicesShowUntilConnectedAndMoreCanBeAddedWhileConnected()
    {
        var result = await headless.Session.Dispatch(async () =>
        {
            var config = AppConfig.Load(Path.Combine(_dir, "config.json"));
            config.RidesDirectory = Path.Combine(_dir, "rides");
            config.Devices = new DevicesConfig
            {
                Trainer = "CE:D1:96:5E:7A:BB",
                HeartRate = "E1:22:33:44:55:66",
                Shifters = ["C8:A3:BD:AE:3B:B6"],
                Picked = true,
                Names = new() { ["CE:D1:96:5E:7A:BB"] = "KICKR CORE 6107", ["E1:22:33:44:55:66"] = "COOSPO H6" },
            };
            var main = new MainWindowViewModel(config) { Athlete = Athlete.FromConfig(config) };
            var devices = main.Devices;
            var before = (devices.Saved.Select(r => $"{r.Kind}: {r.Name}").ToList(), devices.ScanLabel, devices.CanScan);

            // Simulated devices have no addresses, so every saved one still shows as not connected.
            devices.Simulate = true;
            await devices.ConnectAsync(config);
            devices.Simulate = false;
            var connected = (devices.ScanLabel, devices.CanScan, devices.Saved.Count);

            // Adding while connected: a saved monitor that was not found starts ticked, and no trainer is needed.
            devices.IsScanning = true;
            devices.AddCandidate(Found("E1:22:33:44:55:66", "COOSPO H6", DeviceKind.HeartRate, -60), config.Devices);
            var picked = (devices.Candidates.Single().Selected, devices.CanConnectPicked);
            await devices.CancelAsync();

            devices.Forget("E1:22:33:44:55:66");
            var after = (devices.Saved.Select(r => r.Name).ToList(), AppConfig.Load(config.Path).Devices.HeartRate);
            await main.DisposeAsync();
            return (before, connected, picked, after);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["Trainer: KICKR CORE 6107", "Heart rate: COOSPO H6", "Controller: C8:A3:BD:AE:3B:B6"], result.before.Item1);
        Assert.Equal(Strings.Home_ChooseDevices, result.before.ScanLabel);
        Assert.True(result.before.CanScan);
        Assert.Equal((Strings.Devices_Add, true, 3), result.connected);
        Assert.Equal((true, true), result.picked);
        Assert.Equal(["KICKR CORE 6107", "C8:A3:BD:AE:3B:B6"], result.after.Item1);
        Assert.Equal(string.Empty, result.after.HeartRate);
    }

    [Fact]
    public async Task SettingsRenamesDevicesAndForgetsThemOnlyWhenAskedTwice()
    {
        var result = await headless.Session.Dispatch(async () =>
        {
            var config = AppConfig.Load(Path.Combine(_dir, "config.json"));
            config.RidesDirectory = Path.Combine(_dir, "rides");
            config.Devices = new DevicesConfig
            {
                Trainer = "CE:D1:96:5E:7A:BB",
                HeartRate = "E1:22:33:44:55:66",
                Shifters = ["C8:A3:BD:AE:3B:B6"],
                Picked = true,
                Names = new() { ["CE:D1:96:5E:7A:BB"] = "KICKR CORE 6107", ["E1:22:33:44:55:66"] = "COOSPO H6" },
            };
            var main = new MainWindowViewModel(config) { Athlete = Athlete.FromConfig(config) };
            main.ShowSettings();
            var settings = (SettingsViewModel)main.Current;
            var listed = settings.SavedDevices.Select(d => $"{d.Kind}: {d.Name}").ToList();

            // Rename the trainer; Save shows the new name on the home page too.
            settings.SavedDevices[0].Name = "Garage KICKR";
            await settings.SaveCommand.ExecuteAsync(null);
            var home = main.Devices.Saved[0].Name;
            var alias = AppConfig.Load(config.Path).Devices.NameOf("CE:D1:96:5E:7A:BB");

            // Forget asks first, and Cancel keeps the device.
            main.ShowSettings();
            settings = (SettingsViewModel)main.Current;
            var monitor = settings.SavedDevices[1];
            settings.ForgetDeviceCommand.Execute(monitor);
            var asked = (settings.SavedDevices.Count, monitor.ForgetLabel, AppConfig.Load(config.Path).Devices.HeartRate);
            settings.KeepDeviceCommand.Execute(monitor);
            var kept = monitor.ConfirmForget;
            settings.ForgetDeviceCommand.Execute(monitor);
            settings.ForgetDeviceCommand.Execute(monitor);
            var forgot = (settings.SavedDevices.Count, AppConfig.Load(config.Path).Devices.HeartRate, main.Devices.Saved.Count);

            settings.ForgetDevicesCommand.Execute(null);
            var allAsked = settings.SavedDevices.Count;
            settings.ForgetDevicesCommand.Execute(null);
            var allGone = (settings.HasDevices, main.Devices.Saved.Count, AppConfig.Load(config.Path).Devices.Trainer);
            settings.CancelCommand.Execute(null);
            await main.DisposeAsync();
            return (listed, home, alias, asked, kept, forgot, allAsked, allGone);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["Trainer: KICKR CORE 6107", "Heart rate: COOSPO H6", "Controller: C8:A3:BD:AE:3B:B6"], result.listed);
        Assert.Equal("Garage KICKR", result.home);
        Assert.Equal("Garage KICKR", result.alias);
        Assert.Equal((3, Strings.Settings_ConfirmForget, "E1:22:33:44:55:66"), result.asked);
        Assert.False(result.kept);
        Assert.Equal((2, string.Empty, 2), result.forgot);
        Assert.Equal(2, result.allAsked);
        Assert.Equal((false, 0, string.Empty), result.allGone);
    }

    [Fact]
    public async Task OverlappingRefreshesListEachRideOnce()
    {
        var session = headless.Session;
        var titles = await session.Dispatch(async () =>
        {
            var config = AppConfig.Load(Path.Combine(_dir, "config.json"));
            config.RidesDirectory = Path.Combine(_dir, "rides");
            var main = new MainWindowViewModel(config) { Athlete = Athlete.FromConfig(config) };

            // The startup refresh, still running when Settings or the summary page refreshes again.
            await Task.WhenAll(main.Home.InitializeAsync(), main.Home.RefreshCommand.ExecuteAsync(null));
            return main.Home.OtherCards.Select(c => c.Title).ToList();
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["Free ride", "Generate a route", "Ramp FTP test"], titles);
    }

    [Fact]
    public async Task AFailedSaveLetsTheRiderGoBack()
    {
        var session = headless.Session;
        var (message, button, page) = await session.Dispatch(async () =>
        {
            var (config, main) = await ConnectedAsync();
            var path = Path.Combine(_dir, "unsaveable.fit");

            // A folder where the final save's temporary file goes makes it fail with access denied.
            Directory.CreateDirectory(path + ".tmp");
            var ride = StartRide(main, config, Workout.FreeRide(), path);
            await Until(() => Seconds(ride.Moving) >= 3, TimeSpan.FromSeconds(30));
            ride.FinishCommand.Execute(null);
            ride.FinishCommand.Execute(null);
            await Until(() => ride.Message?.Length > 0, TimeSpan.FromSeconds(10));
            var (message, button) = (ride.Message, ride.FinishButton);
            ride.FinishCommand.Execute(null);
            await Until(() => main.Current is not RideViewModel, TimeSpan.FromSeconds(5));
            var page = main.Current;
            await main.DisposeAsync();
            return (message, button, page);
        }, TestContext.Current.CancellationToken);

        Assert.StartsWith("Could not save the ride", message, StringComparison.Ordinal);
        Assert.Equal(Strings.Common_Back, button);
        Assert.IsType<HomeViewModel>(page);
    }

    [Fact]
    public async Task EndingARampTestKeepsTheChartOnTheTimeRidden()
    {
        var session = headless.Session;
        var (x, moving) = await session.Dispatch(async () =>
        {
            var (config, main) = await ConnectedAsync();
            var ride = StartRide(main, config, RampTest.Create(100));
            await Until(() => Seconds(ride.Moving) >= RampTest.WarmupSeconds + 70, TimeSpan.FromSeconds(60));
            ride.EndRampCommand.Execute(null);
            var ended = Seconds(ride.Moving);
            await Until(() => Seconds(ride.Moving) >= ended + 10, TimeSpan.FromSeconds(30));
            var result = (ride.Points[^1].X, Seconds(ride.Moving));
            await main.DisposeAsync();
            return result;
        }, TestContext.Current.CancellationToken);

        // Not the planned cooldown's start, 3060 s in.
        Assert.InRange(x, moving - 5, moving + 5);
    }

    private async Task<(AppConfig Config, MainWindowViewModel Main)> ConnectedAsync()
    {
        var config = AppConfig.Load(Path.Combine(_dir, "config.json"));
        config.RidesDirectory = Path.Combine(_dir, "rides");
        var main = new MainWindowViewModel(config) { Athlete = Athlete.FromConfig(config) };
        main.Devices.Simulate = true;
        await main.Devices.ConnectAsync(config);
        return (config, main);
    }

    private static RideViewModel StartRide(MainWindowViewModel main, AppConfig config, Workout workout, string? path = null)
    {
        var options = main.Athlete.Options(config, workout, speedFactor: 40);
        if (path is not null)
        {
            options = options with { OutputPath = path };
        }

        var session = new RideSession(workout, options, main.Devices.Devices!.Trainer, main.Devices.Devices.HeartRate, []);
        var ride = new RideViewModel(main, session, options.Ftp, main.Devices) { CountdownStep = TimeSpan.Zero };
        main.Current = ride;
        ride.Start();
        return ride;
    }

    private static DiscoveredDevice Found(string address, string name, DeviceKind kind, short? rssi) =>
        new(null!, address, name, kind, rssi);

    private static PlannedRide Planned(long id, DateOnly date, string name, string text) =>
        new(id, date, "VirtualRide", (text.Length == 0 ? new Workout(name, []) : WorkoutTextParser.Parse(text, name)) with { EventId = id, Ftp = 240 });

    private static int Seconds(string clock) => clock.Split(':').Aggregate(0, (total, part) => (total * 60) + int.Parse(part));

    /// <summary>Pumps the UI dispatcher until the condition holds.</summary>
    private static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not met in time");
            }

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Screenshots in the light theme and then the dark one.</summary>
    private async Task ShootBoth(Avalonia.Controls.Window window, string name)
    {
        var app = Application.Current!;
        foreach (var (variant, suffix) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
        {
            app.RequestedThemeVariant = variant;

            // Theme resources swap after the theme change, off the dispatcher.
            for (var i = 0; i < 30; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }

            Assert.Equal(variant, window.ActualThemeVariant);
            Shoot(window, $"{name}-{suffix}.png");
        }

        app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }

    [Fact]
    public void DeviceRowsShowTheBatteryOfAnyDeviceThatReportsIt()
    {
        var strap = new Services.DeviceRow { Kind = Strings.Device_HeartRate, Device = new Strap { Battery = 64 } };
        var unknown = new Services.DeviceRow { Kind = Strings.Device_HeartRate, Device = new Strap() };
        var gone = new Services.DeviceRow { Kind = Strings.Device_HeartRate, Device = new Strap { Battery = 64, IsConnected = false } };

        strap.Refresh();
        unknown.Refresh();
        gone.Refresh();

        Assert.Equal("battery 64%", strap.Detail);
        Assert.Equal(Strings.Device_Connected, unknown.Detail);
        Assert.Equal(Strings.Device_Disconnected, gone.Detail);
    }

    private sealed class Strap : IHeartRateMonitor
    {
        public event Action<int>? HeartRateReceived { add { } remove { } }

        public string Name => "COOSPO H6";

        public bool IsConnected { get; init; } = true;

        public string? Status => null;

        public int? Battery { get; init; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(401, "API key", true)]
    [InlineData(404, "athlete", true)]
    [InlineData(429, "limiting", false)]
    [InlineData(503, "HTTP 503", false)]
    public void LoadErrorsSayWhatToDo(int status, string mentions, bool settings)
    {
        var (message, needsSettings) = HomeViewModel.DescribeLoadError(new IntervalsException("raw", (System.Net.HttpStatusCode)status), "12345");
        Assert.Contains(mentions, message, StringComparison.Ordinal);
        Assert.Equal(settings, needsSettings);
        Assert.Contains("online", HomeViewModel.DescribeLoadError(new HttpRequestException("No such host"), "0").Message, StringComparison.Ordinal);
        Assert.Contains("30 seconds", HomeViewModel.DescribeLoadError(new TaskCanceledException(), "0").Message, StringComparison.Ordinal);
    }

    private void Shoot(Avalonia.Controls.Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame rendered");
        frame.Save(Path.Combine(_shots, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
