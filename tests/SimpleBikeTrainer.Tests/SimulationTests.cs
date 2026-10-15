using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Session;
using SimpleBikeTrainer.Workouts;

namespace SimpleBikeTrainer.Tests;

public sealed class SimulationTests : IDisposable
{
    private static readonly RiderModel Rider = new(TotalMassKg: 89, BikeMassKg: 9);

    private readonly string _dir = Directory.CreateTempSubdirectory("trainer-sim").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void InTheRealGearTheRoadIsSentAsItIs()
    {
        var road = TrainerSimulation.Road(3, Rider, gearFactor: 1, flywheelSpeedMs: 8);

        Assert.Equal(new RoadSimulation(3, 0.004, 0.2, 80, 9), road);
    }

    /// <summary>The trainer's force at its flywheel speed is the virtual gear's share of the road's force, even with wind capped.</summary>
    [Theory]
    [InlineData(0.5, -3)]
    [InlineData(1.5, 0)]
    [InlineData(2.5, 6)]
    [InlineData(2.7, 4)]
    public void AVirtualGearCostsWhatItWouldOnTheRoad(double gearFactor, double grade)
    {
        const double flywheel = 7;
        var weight = Rider.TotalMassKg * Physics.G;
        var road = TrainerSimulation.Road(grade, Rider, gearFactor, flywheel);

        var trainerForce = (weight * (road.Crr + (road.GradePercent / 100))) + (road.WindResistance * flywheel * flywheel);
        var virtualSpeed = flywheel * gearFactor;
        var roadForce = gearFactor * ((weight * (Rider.Crr + (grade / 100))) + (Rider.WindResistance * virtualSpeed * virtualSpeed));

        // Within the rounding in TrainerSimulation.Road: 0.1 % of grade and 0.01 kg/m.
        Assert.InRange(trainerForce, roadForce - 2, roadForce + 2);
        Assert.InRange(road.WindResistance, 0, TrainerSimulation.MaxWindResistance);
        Assert.InRange(road.Crr, 0, TrainerSimulation.MaxCrr);
    }

    [Fact]
    public void FeCSimulationPagesCarryGradeRollingResistanceWindAndWeights()
    {
        // 5 % is 205 % from the −200 % offset: 20500 in 0.01 %. 0.004 is 80 × 0.00005.
        Assert.Equal(new byte[] { 0x33, 0xFF, 0xFF, 0xFF, 0xFF, 0x14, 0x50, 80 }, AntPlusPages.TrackResistance(5, 0.004));
        Assert.Equal(new byte[] { 0x32, 0xFF, 0xFF, 0xFF, 0xFF, 51, 0, 100 }, AntPlusPages.WindResistance(0.51));

        // 75 kg is 7500 × 0.01 kg; 9 kg is 180 × 0.05 kg, its low nibble in the top of byte 4.
        Assert.Equal(new byte[] { 0x37, 0x4C, 0x1D, 0xFF, 0x40, 0x0B, 70, 0 }, AntPlusPages.UserConfiguration(75, 9));
    }

    [Fact]
    public void FtmsSaysWhetherItSimulates()
    {
        byte[] features = [0x02, 0x40, 0, 0, 0x08, 0x20, 0, 0];

        Assert.True(Ftms.SupportsSimulation(features));
        features[5] = 0;
        Assert.False(Ftms.SupportsSimulation(features));
    }

    [Fact]
    public async Task WithVirtualGearsAFreeRideSendsARoadThatShiftingMakesHarder()
    {
        await using var trainer = new SimTrainer();
        var options = Options("virtual.fit") with { Resistance = ResistanceMode.SimulationVirtualGears, ChainRatio = 2 };
        var session = new RideSession(Workout.FreeRide(), options, trainer, null, [], TestClock.Fast);
        RideSnapshot? seen = null;
        double? before = null;
        session.Updated += s =>
        {
            if (s.Moving.TotalSeconds >= 3 && before is null)
            {
                before = trainer.LastRoad?.GradePercent ?? -99;
                session.Send(RideCommand.GradeUp);
                session.Send(RideCommand.GradeUp);
                session.Send(RideCommand.ShiftUp);
                session.Send(RideCommand.ShiftUp);
            }
            else if (s.Moving.TotalSeconds >= 6)
            {
                seen = s;
                session.Send(RideCommand.Finish);
            }
        };

        await session.RunAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(seen);
        Assert.True(seen.Simulating);
        Assert.True(seen.VirtualGears);
        Assert.Null(seen.TargetWatts);
        Assert.Equal(14, seen.Gear);
        Assert.True(trainer.LastRoad?.GradePercent > before, $"a steeper road in a harder gear: {before} -> {trainer.LastRoad?.GradePercent}");
    }

    [Fact]
    public async Task WithRealGearsTheShifterLeavesTheGearsAlone()
    {
        await using var trainer = new SimTrainer();
        var session = new RideSession(Workout.FreeRide(), Options("real.fit") with { Resistance = ResistanceMode.SimulationRealGears }, trainer, null, [], TestClock.Fast);
        RideSnapshot? seen = null;
        session.Updated += s =>
        {
            if (s.Moving.TotalSeconds >= 2)
            {
                session.Send(RideCommand.ShiftUp);
            }

            if (s.Moving.TotalSeconds >= 4)
            {
                seen = s;
                session.Send(RideCommand.Finish);
            }
        };

        await session.RunAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(seen);
        Assert.False(seen.VirtualGears);
        Assert.Equal(12, seen.Gear);
        Assert.Equal(new RoadSimulation(0, 0.004, 0.2, 80, 9), trainer.LastRoad);
    }

    [Fact]
    public async Task ErgStepsHoldTheirPowerAndATrainerThatCannotSimulateGetsPower()
    {
        // ERG steps hold their power whatever the setting.
        await using (var trainer = new SimTrainer())
        {
            var workout = new Workout("erg", [new WorkoutStep("Work", 5, new PowerTarget(150, 150, PowerUnit.Watts))]);
            var session = new RideSession(workout, Options("erg.fit") with { Resistance = ResistanceMode.SimulationVirtualGears }, trainer, null, [], TestClock.Fast);
            session.Updated += s =>
            {
                if (s.Moving.TotalSeconds >= 3)
                {
                    session.Send(RideCommand.Finish);
                }
            };

            await session.RunAsync(TestContext.Current.CancellationToken);
            Assert.Null(trainer.LastRoad);
            Assert.Contains(150, trainer.Targets);
        }

        // A trainer without simulation gets the power of the virtual gears, and the rider is told.
        await using var plain = new FakeTrainer();
        var free = new RideSession(Workout.FreeRide(), Options("plain.fit") with { Resistance = ResistanceMode.SimulationVirtualGears }, plain, null, [], TestClock.Fast);
        RideSnapshot? last = null;
        free.Updated += s =>
        {
            if (s.Moving.TotalSeconds >= 3)
            {
                last = s;
                free.Send(RideCommand.Finish);
            }
        };

        await free.RunAsync(TestContext.Current.CancellationToken);
        Assert.False(last?.Simulating);
        Assert.Contains("cannot simulate", last?.Message);
        Assert.True(last?.TargetWatts > 0);
    }

    private RideOptions Options(string name) => new(200, 20000, Rider, Path.Combine(_dir, name));

    /// <summary>A trainer that simulates: it records the roads and targets it is sent, and the rider pedals 90 rpm at 150 W.</summary>
    private sealed class SimTrainer : ITrainer
    {
        private readonly Timer _timer;

        public SimTrainer()
        {
            _timer = new Timer(_ => DataReceived?.Invoke(new IndoorBikeData(30, 90, 150, null, null, null)), null, 0, 2);
        }

        public event Action<IndoorBikeData>? DataReceived;

        public string Name => "sim";

        public bool IsConnected => true;

        public string? Status => null;

        public string? LastError => null;

        public bool SupportsSimulation => true;

        /// <summary>The last road sent. Ending a ride sends 0 W, which leaves it as it was.</summary>
        public RoadSimulation? LastRoad { get; private set; }

        public List<int> Targets { get; } = [];

        public Task SetTargetPowerAsync(int watts, CancellationToken cancellationToken = default)
        {
            lock (Targets)
            {
                Targets.Add(watts);
            }

            return Task.CompletedTask;
        }

        public Task SetSimulationAsync(RoadSimulation road, CancellationToken cancellationToken = default)
        {
            LastRoad = road;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _timer.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
