using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using SimpleBikeTrainer.Ble;
using SimpleBikeTrainer.Desktop.Resources;
using SimpleBikeTrainer.Desktop.ViewModels;
using SimpleBikeTrainer.Desktop.Views;
using SimpleBikeTrainer.Desktop;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Intervals;
using SimpleBikeTrainer.Resources;
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

    public static readonly TimeProvider Fast = new ScaledTimeProvider(40);

    public static IHttpClientFactory Http { get; } =
        new ServiceCollection().AddTrainerHttpClients().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

    /// <summary>Takes the place of the speakers, so the simulated rides' cues never play out loud.</summary>
    public static readonly Action<string> Silent = _ => { };

    /// <summary>Runs as the test assembly loads, before any test can start a ride.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Mute() => Services.Sounds.Played = Silent;
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
        var config = AppConfig.Load(Path.Combine(_dir, "config.json"));
        config.RidesDirectory = Path.Combine(_dir, "rides");
        config.Rider.Ftp = 240;
        config.Rider.MaxHeartRate = 190;
        var main = new MainWindowViewModel(config, HeadlessApp.Http) { Athlete = Athlete.FromConfig(config) };
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

            // Nothing paired or connected: home says so, and its one device button opens the Devices page, which scans.
            Assert.False(main.Devices.HasSaved);
            Assert.False(main.Devices.HasConnected);
            var radio = new FakeScanner();
            main.Devices.OpenScanner = _ => Task.FromResult<IDeviceScanner>(radio);
            main.Home.ShowDevicesCommand.Execute(null);
            var pairing = Assert.IsType<DevicesViewModel>(main.Current);
            await Until(() => main.Devices.IsScanning, TimeSpan.FromSeconds(5));
            var link = new FakeLink();
            radio.Find(Found(DeviceId.Bluetooth("CE:D1:96:5E:7A:BB"), "KICKR CORE 6107", DeviceKind.Trainer, -58, link));
            radio.Find(Found(DeviceId.Bluetooth("C8:A3:BD:AE:3B:B6"), "Zwift Click", DeviceKind.Shifter, -64, link));
            radio.Find(Found(DeviceId.Bluetooth("E1:22:33:44:55:66"), "HRM-Pro:123456", DeviceKind.HeartRate, -71, link));
            radio.Find(Found(DeviceId.Bluetooth("D4:00:00:00:00:01"), "KICKR BIKE 0001", DeviceKind.Trainer, null, link));
            radio.Find(Found(DeviceId.Bluetooth("F0:11:22:33:44:55"), "4iiii PRECISION", DeviceKind.PowerMeter, -66, link));
            radio.Find(Found(DeviceId.AntPlus(5151), "ANT+ cadence sensor 5151", DeviceKind.CadenceSensor, null, link));

            // The same trainer over Wi-Fi, and a strap over ANT+: the transport column tells them apart.
            radio.Find(Found(DeviceId.Wifi("DC:06:75:B6:E8:20"), "KICKR CORE 6107", DeviceKind.Trainer, null, link));
            radio.Find(Found(DeviceId.AntPlus(12345), "HRM-Dual", DeviceKind.HeartRate, null, link));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(
                ["Trainer", "Trainer", "Trainer", "Power meter", "Cadence sensor", "Heart rate", "Heart rate", "Controller"],
                main.Devices.Candidates.Select(c => c.KindLabel));
            Assert.Equal(["Bluetooth", "Bluetooth", "Wi-Fi", "Bluetooth", "ANT+", "Bluetooth", "ANT+", "Bluetooth"], main.Devices.Candidates.Select(c => c.Id.TransportLabel));
            Assert.Equal("not in range", main.Devices.Candidates[1].Signal);
            Assert.Equal("on the network", main.Devices.Candidates[2].Signal);
            Assert.Equal("DC:06:75:B6:E8:20", main.Devices.Candidates[2].Id.Address);
            Assert.Equal("in range", main.Devices.Candidates[6].Signal);
            Assert.All(main.Devices.Candidates, c => Assert.Equal("Pair", c.PairLabel));

            // Pairing the strap connects and saves it, with no trainer yet, and its row shows what it reads.
            await pairing.PairCommand.ExecuteAsync(main.Devices.Candidates.Single(c => c.Name == "HRM-Pro:123456"));
            Assert.Equal("Paired HRM-Pro:123456.", main.Devices.Status);
            Assert.Equal(DeviceId.Bluetooth("E1:22:33:44:55:66"), AppConfig.Load(config.Path).Devices.HeartRate);
            Assert.False(main.Devices.IsConnected);
            Assert.True(main.Devices.IsScanning);
            Assert.DoesNotContain(main.Devices.Candidates, c => c.Name == "HRM-Pro:123456");
            var paired = Assert.Single(pairing.Paired);
            Assert.True(paired.IsConnected);

            // The scan goes on and Disconnect shows; Connect all does not, with no trainer paired.
            Assert.True(main.Devices.CanDisconnect);
            Assert.False(pairing.CanConnectAll);
            link.Opened.Single().Beat(72);
            paired.Live!.Refresh();
            Assert.Equal("72 bpm", paired.Live.Detail);
            await ShootBoth(window, "0-devices");

            // A strap that will not answer is not saved, and the page says what to try.
            link.Fails.Add(DeviceId.AntPlus(12345));
            await pairing.PairCommand.ExecuteAsync(main.Devices.Candidates.Single(c => c.Name == "HRM-Dual"));
            Assert.Contains(Strings.Devices_PairHintHeartRate, main.Devices.Status, StringComparison.Ordinal);
            Assert.Contains("HRM-Dual (ANT+ 12345) did not connect", main.Devices.Status, StringComparison.Ordinal);
            Assert.Equal(DeviceId.Bluetooth("E1:22:33:44:55:66"), AppConfig.Load(config.Path).Devices.HeartRate);
            Assert.True(link.Opened[^1].Disposed);

            // Save goes back home and stops the scan; the strap stays connected until it is forgotten.
            pairing.SaveCommand.Execute(null);
            Assert.Same(main.Home, main.Current);
            await Until(() => !main.Devices.IsScanning, TimeSpan.FromSeconds(5));
            Assert.Empty(main.Devices.Candidates);
            Assert.Equal(["HRM-Pro:123456"], main.Devices.Rows.Select(r => r.Name));
            await main.Devices.ForgetAllAsync();
            Assert.Empty(main.Devices.Rows);
            Assert.True(link.Opened[0].Disposed);

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
            var options = main.Athlete.Options(config, workout);
            var session = new RideSession(workout, options, main.Devices.Devices!.Trainer, main.Devices.Devices.HeartRate, [], HeadlessApp.Fast);
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

            // Caught as it changes: at 40x each count shows for 25ms, which a slow runner's polling can miss.
            string? firstCue = null;
            ride.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RideViewModel.CueTitle) && ride.CueCount != Strings.Ride_Go)
                {
                    firstCue ??= ride.CueTitle;
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
            await Until(() => ride.CueVisible && ride.CueCount != Strings.Ride_Go, TimeSpan.FromSeconds(30));
            Shoot(window, "2a-cue.png");
            Assert.StartsWith("Next: Sweet spot", firstCue, StringComparison.Ordinal);
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
            var free = new RideSession(Workout.FreeRide(), main.Athlete.Options(config, Workout.FreeRide()), main.Devices.Devices.Trainer, main.Devices.Devices.HeartRate, [], HeadlessApp.Fast);
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
            var routeSession = new RideSession(route, main.Athlete.Options(config, route), main.Devices.Devices.Trainer, main.Devices.Devices.HeartRate, [], HeadlessApp.Fast);
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

            // Calibration, from the Devices page, on the simulated trainer, which plays a spin-down.
            ((SimulatedTrainer)main.Devices.Devices!.Trainer).SpinDownStepTime = TimeSpan.FromMilliseconds(300);
            main.Home.ShowDevicesCommand.Execute(null);
            var devicesPage = (DevicesViewModel)main.Current;
            Assert.True(devicesPage.Devices.IsConnected);
            devicesPage.CalibrateCommand.Execute(null);
            var calibration = (CalibrationViewModel)main.Current;
            Assert.True(calibration.IsSupported);
            await calibration.StartCommand.ExecuteAsync(null);
            await Until(() => calibration.Step.StartsWith("Pedal", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
            Assert.Contains("mph", calibration.Target, StringComparison.Ordinal);
            await ShootBoth(window, "10-calibrate");
            await Until(() => calibration.Succeeded == true, TimeSpan.FromSeconds(5));
            calibration.BackCommand.Execute(null);
            Assert.Same(devicesPage, main.Current);
            Assert.Equal(["Simulated trainer", "Simulated heart rate"], devicesPage.Devices.Rows.Select(r => r.Name));
            devicesPage.CancelCommand.Execute(null);
            Assert.Same(main.Home, main.Current);

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

            SimpleBikeTrainer.Desktop.Services.Sounds.Played = HeadlessApp.Silent;
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
                Saved =
                [
                    new() { Kind = DeviceKind.Trainer, Address = "CE:D1:96:5E:7A:BB", Name = "KICKR CORE 6107" },
                    new() { Kind = DeviceKind.HeartRate, Address = "E1:22:33:44:55:66", Name = "COOSPO H6" },
                    new() { Kind = DeviceKind.Shifter, Address = "C8:A3:BD:AE:3B:B6" },
                ],
                Picked = true,
            };
            var main = new MainWindowViewModel(config, HeadlessApp.Http) { Athlete = Athlete.FromConfig(config) };
            var devices = main.Devices;
            var before = (devices.Saved.Select(r => $"{r.Kind}: {r.Name}").ToList(), devices.HasSaved, devices.HasConnected);

            // Simulated devices have no addresses, so every saved one still shows as not connected.
            devices.Simulate = true;
            await devices.ConnectAsync(config);
            devices.Simulate = false;
            var connected = (devices.IsConnected, devices.HasConnected, devices.Saved.Count);

            // A paired monitor the scan finds only needs connecting.
            devices.AddCandidate(Found(DeviceId.Bluetooth("E1:22:33:44:55:66"), "COOSPO H6", DeviceKind.HeartRate, -60), config.Devices);
            var listed = (devices.Candidates.Single().IsPaired, devices.Candidates.Single().PairLabel);
            await devices.StopScanAsync();

            await devices.ForgetAsync(DeviceId.Bluetooth("E1:22:33:44:55:66"));
            var after = (devices.Saved.Select(r => r.Name).ToList(), AppConfig.Load(config.Path).Devices.HeartRate?.ToString() ?? string.Empty);
            await main.DisposeAsync();
            return (before, connected, listed, after);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["Trainer: KICKR CORE 6107", "Heart rate: COOSPO H6", "Controller: C8:A3:BD:AE:3B:B6"], result.before.Item1);
        Assert.True(result.before.HasSaved);
        Assert.False(result.before.HasConnected);
        Assert.Equal((true, true, 3), result.connected);
        Assert.Equal((true, Strings.Devices_ConnectOne), result.listed);
        Assert.Equal(["KICKR CORE 6107", "C8:A3:BD:AE:3B:B6"], result.after.Item1);
        Assert.Equal(string.Empty, result.after.Item2);
    }

    [Fact]
    public async Task TheDevicesPageRenamesDevicesAndForgetsThemOnlyWhenAskedTwice()
    {
        var result = await headless.Session.Dispatch(async () =>
        {
            var config = AppConfig.Load(Path.Combine(_dir, "config.json"));
            config.RidesDirectory = Path.Combine(_dir, "rides");
            config.Devices = new DevicesConfig
            {
                Saved =
                [
                    new() { Kind = DeviceKind.Trainer, Transport = DeviceTransport.Wifi, Address = "DC:06:75:B6:E8:20", Name = "KICKR CORE 6107" },
                    new() { Kind = DeviceKind.HeartRate, Transport = DeviceTransport.AntPlus, Address = "12345", Name = "COOSPO H6" },
                    new() { Kind = DeviceKind.Shifter, Address = "C8:A3:BD:AE:3B:B6" },
                    new() { Kind = DeviceKind.PowerMeter, Address = "F0:11:22:33:44:55", Name = "4iiii PRECISION" },
                ],
                Picked = true,
                WifiTrainers = ["192.168.1.40"],
            };
            var main = new MainWindowViewModel(config, HeadlessApp.Http) { Athlete = Athlete.FromConfig(config) };
            var radio = new FakeScanner();
            main.Devices.OpenScanner = _ => Task.FromResult<IDeviceScanner>(radio);
            var window = new MainWindow { DataContext = main, Width = 1440, Height = 900 };
            window.Show();
            main.ShowSettings();
            var settings = (SettingsViewModel)main.Current;

            settings.ManageDevicesCommand.Execute(null);
            var page = (DevicesViewModel)main.Current;
            var listed = page.Paired.Select(d => $"{d.Kind}: {d.Name} ({d.Id.TransportLabel} {d.Id.Address})").ToList();
            Shoot(window, "4a-devices.png");

            // A rename that is canceled is dropped.
            page.Paired[0].Name = "Dropped";
            page.CancelCommand.Execute(null);
            var canceled = (main.Current == settings, main.Devices.Saved[0].Name, config.Devices.NameOf(DeviceId.Wifi("DC:06:75:B6:E8:20")));
            settings.ManageDevicesCommand.Execute(null);
            page = (DevicesViewModel)main.Current;
            var reopened = page.Paired[0].Name;

            // Rename the trainer: Save keeps it and goes back to the same Settings page, and home shows the new name.
            page.Paired[0].Name = "Garage KICKR";
            page.SaveCommand.Execute(null);
            var back = main.Current == settings;
            var home = main.Devices.Saved[0].Name;
            var alias = AppConfig.Load(config.Path).Devices.NameOf(DeviceId.Wifi("DC:06:75:B6:E8:20"));
            settings.ManageDevicesCommand.Execute(null);
            page = (DevicesViewModel)main.Current;
            var renamed = page.Paired[0].Name;
            var connectAll = (main.Devices.IsScanning, page.CanConnectAll);

            // Connect one paired device, the strap, which the scan has not found: with no answer, the page says what to try.
            var monitor = page.Paired.Single(d => d.DeviceKind == DeviceKind.HeartRate);
            main.Devices.LookTime = TimeSpan.FromMilliseconds(300);
            await page.ConnectDeviceCommand.ExecuteAsync(monitor);
            var missing = (monitor.IsConnected, main.Devices.Status);

            // Found this time: it connects on its own, with no trainer, and disconnects on its own, still paired.
            main.Devices.LookTime = TimeSpan.FromSeconds(10);
            var link = new FakeLink();
            var connecting = page.ConnectDeviceCommand.ExecuteAsync(monitor);
            Assert.True(monitor.IsConnecting);
            radio.Find(Found(DeviceId.AntPlus(12345), "COOSPO H6", DeviceKind.HeartRate, null, link));
            await connecting;
            var joined = (monitor.IsConnected, monitor.IsConnecting, main.Devices.Status, main.Devices.IsConnected);
            await page.DisconnectDeviceCommand.ExecuteAsync(monitor);
            var left = (monitor.IsConnected, main.Devices.HasConnected, link.Opened.Single().Disposed, AppConfig.Load(config.Path).Devices.HeartRate?.ToString());

            // Forget asks first, and Cancel keeps the device.
            await page.ForgetDeviceCommand.ExecuteAsync(monitor);
            var asked = (page.Paired.Count, monitor.ForgetLabel, AppConfig.Load(config.Path).Devices.HeartRate?.ToString() ?? string.Empty);
            page.KeepDeviceCommand.Execute(monitor);
            var kept = monitor.ConfirmForget;
            await page.ForgetDeviceCommand.ExecuteAsync(monitor);
            await page.ForgetDeviceCommand.ExecuteAsync(monitor);
            var forgot = (page.Paired.Count, AppConfig.Load(config.Path).Devices.HeartRate?.ToString() ?? string.Empty, main.Devices.Saved.Count);

            // Heart rate from the trainer only: shown at once, saved by Save.
            var heartRate = page.Metrics.Single(m => m.Metric == Metric.HeartRate);
            Assert.Equal(MetricSource.Auto, heartRate.Selected.Source);
            heartRate.Selected = heartRate.Options.Single(o => o.Source == MetricSource.Trainer);
            var source = (AppConfig.Load(config.Path).Devices.Metrics.HeartRate, heartRate.Now);
            page.SaveCommand.Execute(null);
            settings.ManageDevicesCommand.Execute(null);
            page = (DevicesViewModel)main.Current;
            var sourceSaved = (AppConfig.Load(config.Path).Devices.Metrics.HeartRate, page.Metrics.Single(m => m.Metric == Metric.HeartRate).Selected.Source);

            await page.ForgetAllCommand.ExecuteAsync(null);
            var allAsked = page.Paired.Count;
            await page.ForgetAllCommand.ExecuteAsync(null);
            var allGone = (page.HasPaired, main.Devices.Saved.Count, AppConfig.Load(config.Path).Devices.Trainer?.ToString() ?? string.Empty);
            var kept2 = AppConfig.Load(config.Path).Devices;
            page.CancelCommand.Execute(null);
            settings.CancelCommand.Execute(null);
            window.Close();
            await main.DisposeAsync();
            return (listed, canceled, reopened, back, home, alias, renamed, connectAll, missing, joined, left, asked, kept, forgot, source, sourceSaved, allAsked, allGone, wifiKept: kept2.WifiTrainers, metricKept: kept2.Metrics.HeartRate);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "Trainer: KICKR CORE 6107 (Wi-Fi DC:06:75:B6:E8:20)",
                "Power meter: 4iiii PRECISION (Bluetooth F0:11:22:33:44:55)",
                "Heart rate: COOSPO H6 (ANT+ 12345)",
                "Controller: C8:A3:BD:AE:3B:B6 (Bluetooth C8:A3:BD:AE:3B:B6)",
            ],
            result.listed);
        Assert.Equal((true, "KICKR CORE 6107", "KICKR CORE 6107"), result.canceled);
        Assert.Equal("KICKR CORE 6107", result.reopened);
        Assert.True(result.back);
        Assert.Equal("Garage KICKR", result.home);
        Assert.Equal("Garage KICKR", result.alias);
        Assert.Equal("Garage KICKR", result.renamed);
        Assert.Equal((true, true), result.connectAll);
        Assert.Equal((false, Loc.Format(Strings.Devices_NotFound, "COOSPO H6", Strings.Devices_PairHintHeartRate)), result.missing);
        Assert.Equal((true, false, "Connected COOSPO H6.", false), result.joined);
        Assert.Equal((false, false, true, "ANT+ 12345"), result.left);
        Assert.Equal((4, Strings.Settings_ConfirmForget, "ANT+ 12345"), result.asked);
        Assert.False(result.kept);
        Assert.Equal((3, string.Empty, 3), result.forgot);
        Assert.Equal((MetricSource.Auto, Strings.Devices_NoReading), result.source);
        Assert.Equal((MetricSource.Trainer, MetricSource.Trainer), result.sourceSaved);
        Assert.Equal(3, result.allAsked);
        Assert.Equal((false, 0, string.Empty), result.allGone);

        // Forgetting the devices keeps the Wi-Fi addresses typed in, as it keeps the Bluetooth adapter, and the readings' sources.
        Assert.Equal(["192.168.1.40"], result.wifiKept);
        Assert.Equal(MetricSource.Trainer, result.metricKept);
    }

    [Fact]
    public async Task OverlappingRefreshesListEachRideOnce()
    {
        var session = headless.Session;
        var titles = await session.Dispatch(async () =>
        {
            var config = AppConfig.Load(Path.Combine(_dir, "config.json"));
            config.RidesDirectory = Path.Combine(_dir, "rides");
            var main = new MainWindowViewModel(config, HeadlessApp.Http) { Athlete = Athlete.FromConfig(config) };

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

    [Fact]
    public async Task SkippingARampTestsWarmupMovesTheChartToTheRamp()
    {
        var session = headless.Session;
        var (moving, position) = await session.Dispatch(async () =>
        {
            var (config, main) = await ConnectedAsync();
            var ride = StartRide(main, config, RampTest.Create(100));
            await Until(() => Seconds(ride.Moving) >= 10, TimeSpan.FromSeconds(30));
            var warmup = ride.StepLabel;
            ride.SkipCommand.Execute(null);
            await Until(() => ride.StepLabel != warmup, TimeSpan.FromSeconds(10));
            var result = (Seconds(ride.Moving), ride.Position);
            await main.DisposeAsync();
            return result;
        }, TestContext.Current.CancellationToken);

        // Ahead of the time ridden, by the warmup's unridden part: the line is on the first ramp step.
        Assert.InRange(position - moving, RampTest.WarmupSeconds - 60, RampTest.WarmupSeconds);
    }

    [Fact]
    public async Task TheGradeStaysOneSizeWhicheverWayItGoes()
    {
        var session = headless.Session;
        var heights = await session.Dispatch(async () =>
        {
            var (config, main) = await ConnectedAsync();
            var window = new Avalonia.Controls.Window { Content = new MainView { DataContext = main }, Width = 1316, Height = 760 };
            window.Show();
            var ride = StartRide(main, config, Workout.FreeRide());
            var view = window.GetVisualDescendants().OfType<RideView>().Single();
            var heights = new Dictionary<string, double>();
            foreach (var (command, grade) in new[] { (ride.GradeUpCommand, "0.0"), (ride.GradeUpCommand, "+1.0"), (ride.GradeDownCommand, "-1.0") })
            {
                // One step of 0.5% at a time, each shown before the next, so the grade cannot overshoot.
                while (ride.Grade != grade)
                {
                    var shown = ride.Grade;
                    command.Execute(null);
                    await Until(() => ride.Grade != shown, TimeSpan.FromSeconds(10));
                }

                window.UpdateLayout();
                var (top, bottom) = (view.GradeValue.TranslatePoint(default, window)!.Value.Y,
                    view.GradeValue.TranslatePoint(new Point(0, view.GradeValue.Bounds.Height), window)!.Value.Y);
                heights[grade] = bottom - top;
            }

            await main.DisposeAsync();
            window.Close();
            return heights;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(heights["0.0"], heights["+1.0"], 0.5);
        Assert.Equal(heights["0.0"], heights["-1.0"], 0.5);
    }

    [Fact]
    public async Task TheStepCardKeepsItsTextInsideOnATablet()
    {
        var session = headless.Session;
        var (card, bar, next, workout, controls, height) = await session.Dispatch(async () =>
        {
            var (config, main) = await ConnectedAsync();

            // A Galaxy Tab S9 FE in landscape, less Android's status and navigation bars.
            var window = new Avalonia.Controls.Window { Content = new MainView { DataContext = main }, Width = 1316, Height = 760 };
            window.Show();
            var ride = StartRide(main, config, RampTest.Create(100));
            await Until(() => ride.NextStep.Length > 0, TimeSpan.FromSeconds(10));
            for (var i = 0; i < 5; i++)
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }

            var view = window.GetVisualDescendants().OfType<RideView>().Single();
            Shoot(window, "ride-tablet.png");
            var result = (Span(view.StepCard, window), Span(view.StepBar, window), Span(view.StepNext, window), Span(view.StepWorkout, window),
                Span(view.Controls, window), window.Bounds.Height);
            await main.DisposeAsync();
            window.Close();
            return result;
        }, TestContext.Current.CancellationToken);

        // Top to bottom, inside the card's padding and without overlapping: the progress bar, the next step, the workout.
        Assert.True(bar.Bottom <= next.Top, $"bar {bar} overlaps next {next}");
        Assert.True(next.Bottom <= workout.Top, $"next {next} overlaps workout {workout}");
        Assert.True(workout.Bottom <= card.Bottom - 20, $"workout {workout} runs out of card {card}");

        // The stats make the room, so the buttons stay on screen.
        Assert.True(controls.Bottom <= height, $"controls {controls} below the screen ({height})");
    }

    private static (double Top, double Bottom) Span(Avalonia.Controls.Control control, Avalonia.Visual window)
    {
        var top = control.TranslatePoint(default, window)!.Value.Y;
        return (top, top + control.Bounds.Height);
    }

    private async Task<(AppConfig Config, MainWindowViewModel Main)> ConnectedAsync()
    {
        var config = AppConfig.Load(Path.Combine(_dir, "config.json"));
        config.RidesDirectory = Path.Combine(_dir, "rides");
        var main = new MainWindowViewModel(config, HeadlessApp.Http) { Athlete = Athlete.FromConfig(config) };
        main.Devices.Simulate = true;
        await main.Devices.ConnectAsync(config);
        return (config, main);
    }

    private static RideViewModel StartRide(MainWindowViewModel main, AppConfig config, Workout workout, string? path = null)
    {
        var options = main.Athlete.Options(config, workout);
        if (path is not null)
        {
            options = options with { OutputPath = path };
        }

        var session = new RideSession(workout, options, main.Devices.Devices!.Trainer, main.Devices.Devices.HeartRate, [], HeadlessApp.Fast);
        var ride = new RideViewModel(main, session, options.Ftp, main.Devices) { CountdownStep = TimeSpan.Zero };
        main.Current = ride;
        ride.Start();
        return ride;
    }

    private static DiscoveredDevice Found(DeviceId id, string name, DeviceKind kind, short? rssi, IDeviceLink? link = null) =>
        new(link!, id, name, kind, rssi);

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
    public void DeviceRowsShowWhatADeviceReportsAndItsBattery()
    {
        var device = new Strap { Battery = 64 };
        var strap = new Services.DeviceRow(DeviceKind.HeartRate, device);
        var unknown = new Services.DeviceRow(DeviceKind.HeartRate, new Strap());
        var gone = new Services.DeviceRow(DeviceKind.HeartRate, new Strap { Battery = 64, IsConnected = false });

        strap.Refresh();
        unknown.Refresh();
        gone.Refresh();
        Assert.Equal("battery 64%", strap.Detail);
        Assert.Equal(Strings.Device_Connected, unknown.Detail);
        Assert.Equal(Strings.Device_Disconnected, gone.Detail);
        Assert.Equal(Strings.Device_HeartRate, strap.Kind);

        device.Beat(142);
        strap.Refresh();
        Assert.Equal("142 bpm  ·  battery 64%", strap.Detail);
        Assert.Equal(142, strap.Value(Metric.HeartRate));
        Assert.Null(strap.Value(Metric.Power));

        strap.Detach();
        device.Beat(150);
        Assert.Equal(142, strap.Value(Metric.HeartRate));
    }

    [Fact]
    public async Task AScanStartedWhileTheLastOneStopsKeepsScanningWithItsFinds()
    {
        var result = await headless.Session.Dispatch(async () =>
        {
            var config = AppConfig.Load(Path.Combine(_dir, "config.json"));
            config.RidesDirectory = Path.Combine(_dir, "rides");
            var main = new MainWindowViewModel(config, HeadlessApp.Http) { Athlete = Athlete.FromConfig(config) };
            var devices = main.Devices;
            var disposing = new TaskCompletionSource();
            var radio = new FakeScanner();
            devices.OpenScanner = _ => Task.FromResult<IDeviceScanner>(new GatedScanner(disposing.Task));
            await devices.ScanAsync(config);

            // Leaving the Devices page and coming straight back.
            devices.OpenScanner = _ => Task.FromResult<IDeviceScanner>(radio);
            var stop = devices.StopScanAsync();
            var scan = devices.ScanAsync(config);
            disposing.SetResult();
            await stop;
            await scan;
            radio.Find(Found(DeviceId.Bluetooth("CE:D1:96:5E:7A:BB"), "KICKR CORE 6107", DeviceKind.Trainer, -58));
            Dispatcher.UIThread.RunJobs();
            var after = (devices.IsScanning, devices.Candidates.Count);
            await main.DisposeAsync();
            return after;
        }, TestContext.Current.CancellationToken);

        Assert.Equal((true, 1), result);
    }

    /// <summary>A scan whose dispose waits on the test.</summary>
    private sealed class GatedScanner(Task disposed) : IDeviceScanner
    {
        public event Action<DiscoveredDevice>? DeviceFound { add { } remove { } }

        public IReadOnlyList<DiscoveredDevice> Found => [];

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public async ValueTask DisposeAsync() => await disposed;
    }

    /// <summary>A scan whose finds the test makes up.</summary>
    private sealed class FakeScanner : IDeviceScanner
    {
        private readonly List<DiscoveredDevice> _found = [];

        public event Action<DiscoveredDevice>? DeviceFound;

        public IReadOnlyList<DiscoveredDevice> Found => [.. _found];

        public void Find(DiscoveredDevice device)
        {
            _found.Add(device);
            DeviceFound?.Invoke(device);
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Opens straps that connect at once, but for those in <see cref="Fails"/>, which time out.</summary>
    private sealed class FakeLink : IDeviceLink
    {
        public HashSet<DeviceId> Fails { get; } = [];

        public List<Strap> Opened { get; } = [];

        public IConnectableDevice Open(DiscoveredDevice found)
        {
            var strap = new Strap(found.Name, found.Id, Fails.Contains(found.Id)) { IsConnected = false };
            Opened.Add(strap);
            return strap;
        }
    }

    private sealed class Strap(string name = "COOSPO H6", DeviceId? id = null, bool fails = false) : IHeartRateMonitor, IConnectableDevice
    {
        public event Action<int>? HeartRateReceived;

        public string Name => name;

        public DeviceId? Id => id;

        public bool Disposed { get; private set; }

        public void Beat(int bpm) => HeartRateReceived?.Invoke(bpm);

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            if (fails)
            {
                return Task.FromException(new TimeoutException("The operation has timed out."));
            }

            IsConnected = true;
            return Task.CompletedTask;
        }

        public void ConnectInBackground()
        {
        }

        public bool IsConnected { get; set; } = true;

        public string? Status => null;

        public int? Battery { get; init; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
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
        Directory.CreateDirectory(_shots);
        frame.Save(Path.Combine(_shots, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }}
