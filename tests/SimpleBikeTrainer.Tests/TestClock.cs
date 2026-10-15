using SimpleBikeTrainer.Session;

namespace SimpleBikeTrainer.Tests;

internal static class TestClock
{
    public static readonly TimeProvider Fast = new ScaledTimeProvider(500);
}
