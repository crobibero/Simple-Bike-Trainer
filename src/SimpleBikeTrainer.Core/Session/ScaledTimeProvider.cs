namespace SimpleBikeTrainer.Session;

public sealed class ScaledTimeProvider : TimeProvider
{
    private readonly long _start = System.GetTimestamp();
    private readonly DateTimeOffset _startUtc = System.GetUtcNow();

    public ScaledTimeProvider(double factor)
    {
        Factor = Math.Max(factor, 0.01);
    }

    public double Factor { get; }

    public override long GetTimestamp() => _start + (long)((System.GetTimestamp() - _start) * Factor);

    public override DateTimeOffset GetUtcNow() => _startUtc + (System.GetElapsedTime(_start) * Factor);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        new ScaledTimer(System.CreateTimer(callback, state, Scale(dueTime), Scale(period)), this);

    private TimeSpan Scale(TimeSpan time) => time == Timeout.InfiniteTimeSpan ? time : time / Factor;

    /// <summary>Scales <see cref="Change"/> too, which <see cref="PeriodicTimer"/> uses to set its period.</summary>
    private sealed class ScaledTimer(ITimer timer, ScaledTimeProvider clock) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(clock.Scale(dueTime), clock.Scale(period));

        public void Dispose() => timer.Dispose();

        public ValueTask DisposeAsync() => timer.DisposeAsync();
    }
}
