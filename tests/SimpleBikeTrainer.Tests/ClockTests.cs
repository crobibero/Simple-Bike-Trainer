using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using SimpleBikeTrainer.Devices;
using SimpleBikeTrainer.Session;

namespace SimpleBikeTrainer.Tests;

public class ClockTests
{
    [Fact]
    public async Task AScaledClockRunsFaster()
    {
        var clock = new ScaledTimeProvider(100);
        var start = clock.GetTimestamp();
        var real = Stopwatch.StartNew();

        // A PeriodicTimer sets its period through ITimer.Change, which has to be scaled as well.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        await timer.WaitForNextTickAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), clock, TestContext.Current.CancellationToken);

        // Timers are scheduled on the millisecond tick count, so each 10 ms wait can end slightly early.
        Assert.True(clock.GetElapsedTime(start) >= TimeSpan.FromSeconds(1.8), $"elapsed {clock.GetElapsedTime(start)}");
        Assert.True(real.Elapsed < TimeSpan.FromSeconds(1), $"took {real.Elapsed}");
    }

    [Fact]
    public async Task ASimulatedSpinDownStepsWithTheClock()
    {
        var time = new FakeTimeProvider();
        await using var trainer = new SimulatedTrainer(time);
        var states = new List<SpinDownState>();
        trainer.SpinDownUpdated += u =>
        {
            lock (states)
            {
                states.Add(u.State);
            }
        };

        await trainer.StartSpinDownAsync(TestContext.Current.CancellationToken);
        Assert.Equal([SpinDownState.SpeedUp], Read());

        // Nothing moves until the clock does.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(Read());

        time.Advance(trainer.SpinDownStepTime);
        await Until(() => Read().Count == 2);
        time.Advance(trainer.SpinDownStepTime);
        await Until(() => Read().Count == 3);

        Assert.Equal([SpinDownState.SpeedUp, SpinDownState.StopPedaling, SpinDownState.Success], Read());

        List<SpinDownState> Read()
        {
            lock (states)
            {
                return [.. states];
            }
        }
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(done());
    }
}
