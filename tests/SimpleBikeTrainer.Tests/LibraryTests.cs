using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Platform;
using SimpleBikeTrainer.Recording;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Tests;

public sealed class RideLibraryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("trainer-library").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ListsLoadsAndRecoversRides()
    {
        // A short route ride, saved the normal way.
        var course = CourseGenerator.Generate(1500, 20, seed: 3);
        var workout = Workout.Route("Hill loop", course) with { EventId = 77 };
        await using var trainer = new FakeTrainer();
        var path = Path.Combine(_dir, "2026-10-14-1200-hill-loop.fit");
        var session = new RideSession(workout, new RideOptions(200, 20000, new RiderModel(), path), trainer, null, [], TestClock.Fast);
        session.Updated += s =>
        {
            if (s.WorkoutComplete)
            {
                session.Send(RideCommand.Finish);
            }
        };
        var result = await session.RunAsync(TestContext.Current.CancellationToken);

        // The sidecar keeps the name, event, FTP and the workout with its route.
        var info = RideInfo.Load(path)!;
        Assert.Equal("Hill loop", info.Name);
        Assert.Equal(77, info.EventId);
        Assert.Equal(200, info.Ftp);
        Assert.Equal(course.Points, info.Workout!.Course!.Points);

        RideInfo.RecordUpload(path, "Intervals", "https://intervals.icu/activities/i1");
        var library = new RideLibrary(_dir);
        var listed = Assert.Single(library.List());
        Assert.Equal("Hill loop", listed.Name);
        Assert.Equal(result.Metrics.Seconds, listed.MovingSeconds);
        Assert.Equal(Math.Round(result.Metrics.AveragePower), listed.AveragePower); // the FIT session rounds to whole watts
        Assert.Equal(Math.Round(result.Metrics.ElevationGain), listed.ElevationGain);
        Assert.Equal("https://intervals.icu/activities/i1", listed.Uploads["Intervals"]);

        // Read back, the ride has the same totals and its route.
        var loaded = RideLibrary.Load(path, 20000);
        Assert.Equal(result.Metrics.Seconds, loaded.Result.Metrics.Seconds);
        Assert.Equal(result.Metrics.NormalizedPower, loaded.Result.Metrics.NormalizedPower, 0);
        Assert.Equal(result.Metrics.ElevationGain, loaded.Result.Metrics.ElevationGain, 0);
        Assert.Equal(result.Metrics.Seconds, loaded.Samples.Count);
        Assert.Equal(77, loaded.Result.Workout.EventId);
        Assert.NotNull(loaded.Result.Workout.Course);

        // An app that died mid-ride leaves only the checkpoint; recovering it makes a finished ride.
        var cut = Path.Combine(_dir, "2026-10-15-0700-cut.fit");
        File.Copy(path, cut + ".partial");
        (info with { Name = "Cut short" }).Save(cut);
        var interrupted = Assert.Single(library.FindInterrupted());
        Assert.Equal("Cut short", interrupted.Name);
        Assert.Equal(result.Metrics.Seconds, interrupted.MovingSeconds);
        var recovered = library.Recover(interrupted, 20000);
        Assert.True(File.Exists(cut));
        Assert.False(File.Exists(cut + ".partial"));
        Assert.Equal("Cut short", recovered.Result.Workout.Name);
        Assert.Empty(library.FindInterrupted());
        Assert.Equal(2, library.List().Count);

        // Discarding deletes the checkpoint and its sidecar.
        var other = Path.Combine(_dir, "2026-10-16-0700-other.fit");
        File.Copy(path, other + ".partial");
        info.Save(other);
        RideLibrary.Discard(Assert.Single(library.FindInterrupted()));
        Assert.False(File.Exists(other + ".partial"));
        Assert.False(File.Exists(RideInfo.PathFor(other)));
    }

    [Fact]
    public void AnUnreadableCheckpointIsLeftAlone()
    {
        File.WriteAllBytes(Path.Combine(_dir, "broken.fit.partial"), [1, 2, 3]);
        Assert.Empty(new RideLibrary(_dir).FindInterrupted());
        Assert.True(File.Exists(Path.Combine(_dir, "broken.fit.partial")));
    }
}

public class SpinDownTests
{
    [Fact]
    public void ReadsTheSpinDownFeature()
    {
        // The flags `simple-bike-trainer-cli probe` decoded from the KICKR CORE 6107: cadence and power; ERG, resistance and simulation
        // targets; no spin-down.
        byte[] kickr = [0x02, 0x40, 0x00, 0x00, 0x0C, 0x20, 0x00, 0x00];
        Assert.False(Ftms.SupportsSpinDown(kickr));
        byte[] withSpinDown = [0x02, 0x40, 0x00, 0x00, 0x0C, 0xA0, 0x00, 0x00];
        Assert.True(Ftms.SupportsSpinDown(withSpinDown));
    }

    [Fact]
    public void EncodesAndDecodesTheSpinDown()
    {
        Assert.Equal([0x13, 0x01], Ftms.StartSpinDown());

        // Success with a target of 30.00 to 33.00 km/h.
        var started = Ftms.ParseSpinDownResponse([0x80, 0x13, 0x01, 0xB8, 0x0B, 0xE4, 0x0C]);
        Assert.Equal(new SpinDownUpdate(SpinDownState.SpeedUp, 30, 33), started);
        Assert.Equal(SpinDownState.Error, Ftms.ParseSpinDownResponse([0x80, 0x13, 0x02])!.Value.State);
        Assert.Null(Ftms.ParseSpinDownResponse([0x80, 0x05, 0x01]));

        Assert.Equal(SpinDownState.StopPedaling, Ftms.ParseSpinDownStatus([0x14, 0x04])!.Value.State);
        Assert.Equal(SpinDownState.Success, Ftms.ParseSpinDownStatus([0x14, 0x02])!.Value.State);
        Assert.Null(Ftms.ParseSpinDownStatus([0x08, 0x01]));
    }

    [Fact]
    public async Task TrainersWithoutSpinDownSaySo()
    {
        ITrainer trainer = new FakeTrainer();
        Assert.False(trainer.SupportsSpinDown);
        await Assert.ThrowsAsync<NotSupportedException>(() => trainer.StartSpinDownAsync(TestContext.Current.CancellationToken));
    }
}

public class KeepAwakeTests
{
    [Fact]
    public async Task WithoutASessionBusItDoesNothing()
    {
        await using var awake = await KeepAwake.AcquireAsync("test", "unix:path=/nonexistent/trainer-test-bus");
        Assert.False(awake.IsActive);
        Assert.Contains("unavailable", awake.How, StringComparison.Ordinal);
    }
}
