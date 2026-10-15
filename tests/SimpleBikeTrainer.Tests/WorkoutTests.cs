using System.Text.Json;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Tests;

public class WorkoutDocParserTests
{
    [Fact]
    public void ParsesAnIntervalsEvent()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine("Fixtures", "event-sweet-spot.json")));

        var workout = WorkoutDocParser.ParseEvent(doc.RootElement);

        Assert.Equal("Sweet spot 3x12", workout.Name);
        Assert.Equal(91234567, workout.EventId);
        Assert.Equal(240, workout.Ftp);

        // Warmup, 3 x (work + recover), cooldown, free ride.
        Assert.Equal(9, workout.Steps.Count);
        Assert.Equal(600 + (3 * 960) + 300 + 60, workout.TotalSeconds);
        Assert.Equal(["Warmup", "Sweet spot 1/3", "Recover 1/3", "Sweet spot 2/3"], workout.Steps.Take(4).Select(s => s.Label));

        // Resolved watts win over %FTP; a range rides at its midpoint.
        var warmup = workout.Steps[0];
        Assert.Equal(PowerUnit.Watts, warmup.Power!.Unit);
        Assert.Equal(144, warmup.Power.WattsAt(0.5, 999));
        Assert.Equal(new CadenceTarget(80, 80), warmup.Cadence);
        Assert.Equal(217, workout.Steps[1].Power!.WattsAt(0, 999));
        Assert.Null(workout.Steps[1].Cadence);

        // The cooldown ramps from 156 W to 96 W.
        var cooldown = workout.Steps[7];
        Assert.True(cooldown.Power!.Ramp);
        Assert.Equal(156, cooldown.Power.WattsAt(0, 999));
        Assert.Equal(126, cooldown.Power.WattsAt(0.5, 999));
        Assert.Equal(96, cooldown.Power.WattsAt(1, 999));

        Assert.True(workout.Steps[8].IsFreeRide);
    }

    [Fact]
    public void FallsBackToPercentFtpWhenUnresolved()
    {
        using var doc = JsonDocument.Parse("""{"steps":[{"duration":300,"power":{"value":75,"units":"%ftp"}},{"duration":60,"power":{"value":2,"units":"power_zone"}},{"distance":5000,"power":{"value":200,"units":"w"}}]}""");

        var workout = WorkoutDocParser.Parse(doc.RootElement, "x");

        Assert.Equal(150, workout.Steps[0].Power!.WattsAt(0, 200));
        Assert.Equal(130, workout.Steps[1].Power!.WattsAt(0, 200)); // Z2 midpoint, 65%
        Assert.Equal(200, workout.Steps[2].Power!.WattsAt(0, 200));
        Assert.Equal(600, workout.Steps[2].DurationSeconds); // 5 km at 30 km/h
        Assert.Null(workout.Ftp);
    }
}

public class WorkoutTextParserTests
{
    public static TheoryData<string> PlanRides()
    {
        var data = new TheoryData<string>();
        foreach (var ride in Fixtures())
        {
            data.Add(ride.GetProperty("spec").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PlanRides))]
    public void ParsesEveryRideTrainsyncWrites(string spec)
    {
        var ride = Fixtures().Single(r => r.GetProperty("spec").GetString() == spec);
        var text = ride.GetProperty("text").GetString()!;

        var workout = WorkoutTextParser.Parse(text, spec);

        if (text.Length == 0)
        {
            // FTP tests are calendar entries without steps.
            Assert.Empty(workout.Steps);
            return;
        }

        Assert.Equal(ride.GetProperty("seconds").GetInt32(), workout.TotalSeconds);
        Assert.Equal(ride.GetProperty("steps").GetInt32(), workout.Steps.Count);
        Assert.All(workout.Steps, s => Assert.NotNull(s.Power));

        // Easy and endurance steps carry trainsync's 80 rpm cadence target; harder ones none.
        Assert.All(workout.Steps.Where(s => s.Power!.High <= 75), s => Assert.Equal(80, s.Cadence?.Mid));
        Assert.All(workout.Steps.Where(s => s.Power!.Low > 75), s => Assert.Null(s.Cadence));
    }

    [Fact]
    public void ParsesTheRestOfTheSyntax()
    {
        var workout = WorkoutTextParser.Parse(
            """
            Some description line
            - Warmup 1h ramp 40-70%
            - Hard bit 1m30s 250w 95-100rpm
            - Spin 30s Z2
            - Sprint 10s freeride
            """,
            "x");

        Assert.Equal([3600, 90, 30, 10], workout.Steps.Select(s => s.DurationSeconds));
        Assert.True(workout.Steps[0].Power!.Ramp);
        Assert.Equal("Hard bit", workout.Steps[1].Label);
        Assert.Equal(250, workout.Steps[1].Power!.WattsAt(0, 100));
        Assert.Equal(new CadenceTarget(95, 100), workout.Steps[1].Cadence);
        Assert.Equal(130, workout.Steps[2].Power!.WattsAt(0, 200));
        Assert.True(workout.Steps[3].IsFreeRide);
    }

    [Fact]
    public void RejectsAStepWithoutALength()
    {
        Assert.Throws<FormatException>(() => WorkoutTextParser.Parse("- Endurance 65-75%", "x"));
    }

    private static IEnumerable<JsonElement> Fixtures() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine("Fixtures", "plan-rides.json"))).RootElement.EnumerateArray();
}

public class WorkoutPlayerTests
{
    [Fact]
    public void StepsThroughAndSkipsEmptySteps()
    {
        var workout = new Workout("x",
        [
            new WorkoutStep("empty", 0, null),
            new WorkoutStep("a", 2, new PowerTarget(100, 100, PowerUnit.Watts)),
            new WorkoutStep("b", 3, new PowerTarget(200, 200, PowerUnit.Watts)),
        ]);
        var player = new WorkoutPlayer(workout);

        Assert.Equal("a", player.Current!.Label);
        Assert.False(player.Tick());
        Assert.True(player.Tick());
        Assert.Equal("b", player.Current!.Label);
        Assert.Equal(3, player.StepRemaining);
        player.Tick();
        Assert.Equal(1.0 / 3, player.StepFraction, 3);

        player.Skip();
        Assert.True(player.IsFinished);
        Assert.Null(player.Current);
        Assert.Equal(5, player.WorkoutElapsed);
    }
}

public class RampTestTests
{
    [Fact]
    public void BuildsTheRampAndEstimatesFtp()
    {
        var test = RampTest.Create(startWatts: 100, stepWatts: 20, maxWatts: 200);

        Assert.True(test.IsRampTest);
        Assert.Equal(["Warmup", "Ramp 100 W", "Ramp 120 W", "Ramp 140 W", "Ramp 160 W", "Ramp 180 W", "Ramp 200 W", "Cooldown"], test.Steps.Select(s => s.Label));
        Assert.True(RampTest.IsRampStep(test.Steps[1]));
        Assert.False(RampTest.IsRampStep(test.Steps[^1]));
        Assert.Equal(240, RampTest.EstimateFtp(320));
    }

    [Fact]
    public void RecognizesTrainsyncTestEntries()
    {
        Assert.True(RampTest.LooksLikeTestEntry(new Workout("FTP test 45 min", [])));
        Assert.False(RampTest.LooksLikeTestEntry(new Workout("Easy spin 45 min", [])));
        Assert.False(RampTest.LooksLikeTestEntry(RampTest.Create()));
    }
}

public class PhysicsTests
{
    private static readonly RiderModel Rider = new(TotalMassKg: 85);

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-3)]
    public void SpeedAndPowerAreInverses(double grade)
    {
        foreach (var speed in new[] { 3.0, 8.0, 12.0 })
        {
            var power = Physics.PowerForSpeed(speed, grade, Rider);
            if (power > 0)
            {
                Assert.Equal(speed, Physics.SpeedForPower(power, grade, Rider), 3);
            }
        }
    }

    [Fact]
    public void TwoHundredWattsOnTheFlatIsAboutThirtyTwoKmh()
    {
        var kmh = Physics.SpeedForPower(200, 0, Rider) * 3.6;
        Assert.InRange(kmh, 30, 36);
    }

    [Fact]
    public void ShiftingUpCostsMorePowerAtTheSameCadence()
    {
        var gears = new Gearbox(12);
        var low = Physics.PowerForSpeed(gears.SpeedMs(85), 0, Rider);
        gears.Shift(2);
        var high = Physics.PowerForSpeed(gears.SpeedMs(85), 0, Rider);

        Assert.True(high > low);
        Assert.False(new Gearbox(24).Shift(1));
        Assert.Equal(1, new Gearbox(-3).Gear);
    }
}

public class RideMetricsTests
{
    [Fact]
    public void AnHourAtFtpIsOneHundredTss()
    {
        var metrics = Ride(Enumerable.Repeat(200, 3600), ftp: 200);

        Assert.Equal(200, metrics.NormalizedPower, 6);
        Assert.Equal(1.0, metrics.IntensityFactor, 6);
        Assert.Equal(100, metrics.TrainingStressScore, 6);
        Assert.Equal(720, metrics.KiloJoules, 6);
        Assert.Equal(200, metrics.Peaks[1200], 6);
        Assert.Equal(3600, metrics.TimeInPowerZones[3]); // 100% FTP is Z4 (90-105%)
    }

    [Fact]
    public void NormalizedPowerRewardsVariability()
    {
        // 30 s on / 30 s off at 400/0 W: average 200 W, NP well above.
        var powers = Enumerable.Range(0, 1200).Select(i => i / 30 % 2 == 0 ? 400 : 0);
        var metrics = Ride(powers, ftp: 250);

        Assert.Equal(200, metrics.AveragePower, 6);
        Assert.True(metrics.NormalizedPower > 230);
        Assert.Equal(400, metrics.MaxPower);
    }

    [Fact]
    public void WPrimeBalanceDrainsAboveFtpAndRecoversBelow()
    {
        var metrics = Ride(Enumerable.Repeat(300, 60), ftp: 200, wPrime: 20000);
        Assert.Equal(14000, metrics.WBalance, 6);

        var recovering = Ride(Enumerable.Repeat(300, 60).Concat(Enumerable.Repeat(100, 600)), ftp: 200, wPrime: 20000);
        Assert.True(recovering.WBalance > 19000);
        Assert.Equal(14000, recovering.MinWBalance, 6);
    }

    [Fact]
    public void LapComplianceCountsSecondsNearTarget()
    {
        var lap = new LapMetrics(DateTime.UnixEpoch, 0);
        var t = DateTime.UnixEpoch;
        foreach (var p in new[] { 200, 205, 195, 150 })
        {
            t = t.AddSeconds(1);
            lap.Add(new Sample(t, p, 90, 140, 9, 0, 200));
        }

        Assert.Equal(0.75, lap.Compliance);
        Assert.Equal(90, lap.AverageCadence);
    }

    private static RideMetrics Ride(IEnumerable<int> powers, int ftp, int wPrime = 20000)
    {
        var metrics = new RideMetrics(ftp, wPrime);
        var t = DateTime.UnixEpoch;
        var distance = 0.0;
        foreach (var p in powers)
        {
            t = t.AddSeconds(1);
            distance += 9;
            metrics.Add(new Sample(t, p, 90, 140, 9, distance, null));
        }

        return metrics;
    }
}

public class CourseTests
{
    [Theory]
    [InlineData(30_000, 400)]
    [InlineData(10_000, 150)]
    [InlineData(80_000, 1500)]
    [InlineData(5_000, 0)]
    public void GeneratesALoopWithTheClimbingAskedFor(double distance, double gain)
    {
        var course = CourseGenerator.Generate(distance, gain, seed: 42);
        Assert.Equal(distance, course.DistanceMeters, 3);
        Assert.Equal(gain, course.ElevationGain, 0.5);
        Assert.Equal(course.ElevationAt(0), course.ElevationAt(distance), 3);
        Assert.InRange(course.MaxGrade, 0, CourseGenerator.MaxGrade + 0.001);
        Assert.True(course.MinElevation >= 99.99);
    }

    [Fact]
    public void TooMuchClimbingIsCappedAtTheGradeLimit()
    {
        var course = CourseGenerator.Generate(5_000, 3_000, seed: 1);
        Assert.True(course.ElevationGain < 3_000);
        Assert.Equal(CourseGenerator.MaxGrade, course.MaxGrade, 3);
    }

    [Fact]
    public void TheSameSeedGivesTheSameRoute()
    {
        var a = CourseGenerator.Generate(20_000, 300, seed: 5);
        var b = CourseGenerator.Generate(20_000, 300, seed: 5);
        var c = CourseGenerator.Generate(20_000, 300, seed: 6);
        Assert.Equal(a.Points, b.Points);
        Assert.NotEqual(a.Points, c.Points);
    }

    [Fact]
    public void GradeAndElevationFollowThePoints()
    {
        var course = new Course([new(0, 100), new(1000, 150), new(2000, 150), new(3000, 100)]);
        Assert.Equal(5, course.GradeAt(500), 6);
        Assert.Equal(125, course.ElevationAt(500), 6);
        Assert.Equal(0, course.GradeAt(1500), 6);
        Assert.Equal(-5, course.GradeAt(2500), 6);
        Assert.Equal(0, course.GradeAt(3500), 6);
        Assert.Equal(50, course.ElevationGain, 6);
        Assert.Equal(25, course.GainBetween(500, 2500), 6);
    }

    [Fact]
    public void RouteNamesUseTheDisplayUnits()
    {
        var metric = SimpleBikeTrainer.Session.RideSetup.Route(UnitSystem.Metric, 30, 400, seed: 3);
        Assert.Equal("Route 30 km, 400 m climbing", metric.Name);
        Assert.NotNull(metric.Course);
        var imperial = SimpleBikeTrainer.Session.RideSetup.Route(UnitSystem.Imperial, 20, 1200, seed: 3);
        Assert.Equal("Route 20 mi, 1200 ft climbing", imperial.Name);
        Assert.Equal(20 * Units.MetersPerMile, imperial.Course!.DistanceMeters, 3);
    }
}
