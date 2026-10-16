using Avalonia.Threading;
using UIKit;

namespace SimpleBikeTrainer.Desktop.Platforms;

/// <summary>
/// iPadOS's KeepAwake: the screen stays on while a ride runs. Behind another app, iPadOS keeps the Bluetooth devices
/// connected (the bluetooth-central background mode in Info.plist) but runs the app only as their data arrives.
/// </summary>
internal sealed class RideLock : IAsyncDisposable
{
    private static int _held;

    private bool _released;

    public bool IsActive => true;

    /// <summary>What held the app awake, for the ride page's status line.</summary>
    public string How => "idle timer";

    public static Task<RideLock> AcquireAsync(string reason)
    {
        Interlocked.Increment(ref _held);
        Apply();
        return Task.FromResult(new RideLock());
    }

    public ValueTask DisposeAsync()
    {
        if (!_released)
        {
            _released = true;
            Interlocked.Decrement(ref _held);
            Apply();
        }

        return ValueTask.CompletedTask;
    }

    private static void Apply() =>
        Dispatcher.UIThread.Post(() => UIApplication.SharedApplication.IdleTimerDisabled = Volatile.Read(ref _held) > 0);
}
