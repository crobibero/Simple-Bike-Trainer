using Dynastream.Fit;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Recording;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Workouts;
using DateTime = System.DateTime;

namespace SimpleBikeTrainer.Tests;

/// <summary>A trainer whose rider holds any target up to a limit, reporting every couple of milliseconds.</summary>
internal sealed class FakeTrainer : ITrainer
{
    private readonly System.Threading.Timer _timer;
    private volatile int _target;

    public FakeTrainer(int maxWatts = 2000)
    {
        MaxWatts = maxWatts;
        _timer = new System.Threading.Timer(_ => Emit(), null, 0, 2);
    }

    public event Action<IndoorBikeData>? DataReceived;

    public int MaxWatts { get; }

    public List<int> Targets { get; } = [];

    public string Name => "fake";

    public bool IsConnected => true;

    public string? Status => null;

    public string? LastError => null;

    public Task SetTargetPowerAsync(int watts, CancellationToken cancellationToken = default)
    {
        lock (Targets)
        {
            Targets.Add(watts);
        }

        _target = watts;
        Emit();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _timer.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Emit()
    {
        var power = Math.Min(_target, MaxWatts);
        DataReceived?.Invoke(new IndoorBikeData(30, power >= _target ? 90 : 40, power, null, null, null));
    }
}

public sealed class SessionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trainer-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task RidesAWorkoutAndWritesAFitFile()
    {
        var workout = new Workout("test",
        [
            new WorkoutStep("Warmup", 30, new PowerTarget(100, 100, PowerUnit.Watts)),
            new WorkoutStep("Work", 20, new PowerTarget(80, 80, PowerUnit.PercentFtp)),
            new WorkoutStep("Cooldown", 10, new PowerTarget(100, 100, PowerUnit.Watts)),
        ]);
        await using var trainer = new FakeTrainer();
        var path = Path.Combine(_dir, "ride.fit");
        var session = new RideSession(workout, Options(path), trainer, null, [], TestClock.Fast);
        session.Updated += s =>
        {
            if (s.WorkoutComplete)
            {
                session.Send(RideCommand.Finish);
            }
        };

        var result = await session.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(path, result.FitPath);
        Assert.Equal(3, result.LapCount);
        Assert.InRange(result.Metrics.Seconds, 60, 62);
        lock (trainer.Targets)
        {
            Assert.Contains(160, trainer.Targets); // 80% of FTP 200
        }

        var fit = FitContents.Read(path);
        Assert.Equal(result.Metrics.Seconds, fit.Records.Count);
        Assert.Equal(3, fit.Laps.Count);
        Assert.InRange((int)fit.Laps[1].GetAvgPower()!.Value, 155, 165);
        Assert.Equal(Sport.Cycling, fit.Session!.GetSport());
        Assert.Equal(SubSport.VirtualActivity, fit.Session.GetSubSport());
        Assert.Equal((ushort)Math.Round(result.Metrics.NormalizedPower), fit.Session.GetNormalizedPower());
        Assert.Equal(result.Metrics.Seconds, (double)fit.Session.GetTotalTimerTime()!.Value, 0.5);
    }

    [Fact]
    public async Task RampTestEndsWhenTheRiderCannotHoldIt()
    {
        // The rider tops out at 300 W, so the test fails once a step asks for more than 300 / 0.7 = 428 W.
        await using var trainer = new FakeTrainer(maxWatts: 300);
        var session = new RideSession(RampTest.Create(startWatts: 200, stepWatts: 50), Options(Path.Combine(_dir, "ramp.fit")), trainer, null, [], TestClock.Fast);
        session.Updated += s =>
        {
            if (s.Step?.Label == "Cooldown" && s.StepElapsed >= 5)
            {
                session.Send(RideCommand.Finish);
            }
        };

        var result = await session.RunAsync(TestContext.Current.CancellationToken);

        // Best minute is 300 W, so FTP = 225 W.
        Assert.Equal(225, result.FtpEstimate);
    }

    [Fact]
    public async Task FreeRideHoldsThePowerTheGearCosts()
    {
        await using var trainer = new FakeTrainer();
        var session = new RideSession(Workout.FreeRide(), Options(Path.Combine(_dir, "free.fit")), trainer, null, [], TestClock.Fast);
        var shifted = false;
        var low = 0;
        var high = 0;
        session.Updated += s =>
        {
            if (s.Moving.TotalSeconds >= 5 && !shifted)
            {
                low = s.TargetWatts ?? 0;
                shifted = true;
                session.Send(RideCommand.ShiftUp);
                session.Send(RideCommand.ShiftUp);
            }
            else if (s.Moving.TotalSeconds >= 10)
            {
                high = s.TargetWatts ?? 0;
                session.Send(RideCommand.Finish);
            }
        };

        await session.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(low > 50, $"gear 12 at 90 rpm should cost real power, got {low} W");
        Assert.True(high > low, $"two gears up should cost more: {low} -> {high} W");
    }

    [Fact]
    public async Task RidesARouteAndRecordsTheClimbing()
    {
        var course = CourseGenerator.Generate(2000, 40, seed: 7);
        await using var trainer = new FakeTrainer();
        var path = Path.Combine(_dir, "route.fit");
        var session = new RideSession(Workout.Route("Route", course), Options(path), trainer, null, [], TestClock.Fast);
        var steepest = 0.0;
        session.Updated += s =>
        {
            steepest = Math.Max(steepest, s.Grade);

            // The road sets the grade; the keys do not.
            session.Send(RideCommand.GradeUp);
            if (s.WorkoutComplete)
            {
                session.Send(RideCommand.Finish);
            }
        };

        var result = await session.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Metrics.DistanceMeters >= 2000);
        Assert.InRange(steepest, 1, course.MaxGrade + 0.01);
        Assert.InRange(result.Metrics.ElevationGain, course.ElevationGain - 3, course.ElevationGain + 0.5);
        var fit = FitContents.Read(path);
        Assert.All(fit.Records, r => Assert.NotNull(r.GetEnhancedAltitude()));
        Assert.Equal((ushort)Math.Round(result.Metrics.ElevationGain), fit.Session!.GetTotalAscent());
        Assert.InRange((double)fit.Records[^1].GetEnhancedAltitude()!.Value, course.ElevationAt(0) - 1, course.ElevationAt(0) + 1); // a loop
    }

    [Fact]
    public async Task AFailedCheckpointDoesNotStopTheRide()
    {
        await using var trainer = new FakeTrainer();
        var path = Path.Combine(_dir, "checkpoint.fit");

        // A folder where the checkpoint's temporary file goes makes every checkpoint fail with access denied.
        Directory.CreateDirectory(path + ".partial.tmp");
        var session = new RideSession(Workout.FreeRide(), Options(path), trainer, null, [], TestClock.Fast);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        var result = await session.RunAsync(cts.Token);

        Assert.True(result.Metrics.Seconds > 300, $"rode {result.Metrics.Seconds} s, past the 5-minute checkpoint");
        Assert.True(System.IO.File.Exists(path));
    }

    [Fact]
    public async Task FinishingDropsTheTrainerTarget()
    {
        var course = CourseGenerator.Generate(2000, 40, seed: 7);
        await using var trainer = new FakeTrainer();
        var session = new RideSession(Workout.Route("Route", course), Options(Path.Combine(_dir, "summit.fit")), trainer, null, [], TestClock.Fast);
        session.Updated += s =>
        {
            if (s.Moving.TotalSeconds >= 20)
            {
                session.Send(RideCommand.Finish);
            }
        };

        await session.RunAsync(TestContext.Current.CancellationToken);

        lock (trainer.Targets)
        {
            Assert.Contains(trainer.Targets, t => t > 0);
            Assert.Equal(0, trainer.Targets[^1]);
        }
    }

    [Fact]
    public void ASecondRideInTheSameMinuteGetsItsOwnFile()
    {
        var config = new AppConfig { RidesDirectory = _dir };
        var athlete = Athlete.FromConfig(config);
        var first = athlete.Options(config, Workout.FreeRide()).OutputPath;

        // A ride writes its sidecar when it starts, and its FIT file when it ends.
        System.IO.File.WriteAllText(RideInfo.PathFor(first), "{}");
        var second = athlete.Options(config, Workout.FreeRide()).OutputPath;
        System.IO.File.WriteAllText(second, "fit");
        var third = athlete.Options(config, Workout.FreeRide()).OutputPath;

        Assert.Equal(3, new[] { first, second, third }.Distinct().Count());
    }

    private static RideOptions Options(string path) => new(200, 20000, new RiderModel(), path);
}

internal sealed class FitContents
{
    public List<RecordMesg> Records { get; } = [];

    public List<LapMesg> Laps { get; } = [];

    public SessionMesg? Session { get; private set; }

    public static FitContents Read(string path)
    {
        var contents = new FitContents();
        var decode = new Decode();
        var broadcaster = new MesgBroadcaster();
        decode.MesgEvent += broadcaster.OnMesg;
        broadcaster.RecordMesgEvent += (_, e) => contents.Records.Add((RecordMesg)e.mesg);
        broadcaster.LapMesgEvent += (_, e) => contents.Laps.Add((LapMesg)e.mesg);
        broadcaster.SessionMesgEvent += (_, e) => contents.Session = (SessionMesg)e.mesg;
        using var stream = System.IO.File.OpenRead(path);
        Assert.True(decode.IsFIT(stream));
        Assert.True(decode.CheckIntegrity(stream));
        Assert.True(decode.Read(stream));
        return contents;
    }
}

public class FitRecorderTests
{
    [Fact]
    public void RecordsPausesAsTimerEvents()
    {
        var start = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        var recorder = new FitRecorder(start);
        var metrics = new RideMetrics(200, 20000);
        var lap = new LapMetrics(start, 0);
        for (var i = 1; i <= 10; i++)
        {
            var s = new Sample(start.AddSeconds(i < 6 ? i : i + 20), 150, 85, 120, 8, i * 8, null);
            metrics.Add(s);
            lap.Add(s);
            recorder.AddRecord(s);
            if (i == 5)
            {
                recorder.AddTimerEvent(start.AddSeconds(5), start: false);
                recorder.AddTimerEvent(start.AddSeconds(25), start: true);
            }
        }

        recorder.AddLap(lap, manual: false);
        using var stream = new MemoryStream();
        recorder.Write(stream, metrics, start.AddSeconds(30));
        stream.Position = 0;

        var timerEvents = new List<EventType?>();
        var decode = new Decode();
        decode.MesgEvent += (_, e) =>
        {
            if (e.mesg.Num == MesgNum.Event)
            {
                timerEvents.Add(new EventMesg(e.mesg).GetEventType());
            }
        };
        Assert.True(decode.Read(stream));

        Assert.Equal([EventType.Start, EventType.StopAll, EventType.Start, EventType.StopAll], timerEvents);
    }
}
