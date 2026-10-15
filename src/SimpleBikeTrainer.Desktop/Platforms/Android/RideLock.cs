using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using SimpleBikeTrainer.Desktop.Resources;

namespace SimpleBikeTrainer.Desktop.Platforms;

/// <summary>Android's KeepAwake: the screen stays on, and a foreground service keeps the devices connected behind another app.</summary>
internal sealed class RideLock : IAsyncDisposable
{
    private static int _held;

    private bool _released;

    private RideLock(string how) => How = how;

    public static bool IsHeld => Volatile.Read(ref _held) > 0;

    public bool IsActive => true;

    /// <summary>What held the app awake, for the ride page's status line.</summary>
    public string How { get; }

    /// <summary>Never throws: without the service, the screen still stays on while the app is in front.</summary>
    public static Task<RideLock> AcquireAsync(string reason)
    {
        Interlocked.Increment(ref _held);
        ApplyToActivity();
        if (!RideService.MayStart())
        {
            // Nothing to keep connected, such as on a simulated ride.
            return Task.FromResult(new RideLock("screen only"));
        }

        string how;
        try
        {
            var intent = new Intent(AndroidApp.Context, typeof(RideService)).PutExtra(RideService.ReasonExtra, reason);
            AndroidApp.Context.StartForegroundService(intent);
            how = "foreground service";
        }
        catch (Exception e) when (e is Java.Lang.IllegalStateException or Java.Lang.SecurityException)
        {
            // Android refuses a foreground service started from the background; the screen lock alone is left.
            AndroidApp.Log(e, "Could not start the ride service");
            how = "screen only";
        }

        return Task.FromResult(new RideLock(how));
    }

    public ValueTask DisposeAsync()
    {
        if (!_released)
        {
            _released = true;
            if (Interlocked.Decrement(ref _held) == 0)
            {
                AndroidApp.Context.StopService(new Intent(AndroidApp.Context, typeof(RideService)));
            }

            ApplyToActivity();
        }

        return ValueTask.CompletedTask;
    }

    private static void ApplyToActivity()
    {
        if (AndroidApp.Activity is { } activity)
        {
            activity.RunOnUiThread(activity.ApplyKeepScreenOn);
        }
    }
}

/// <summary>The ride's foreground service: a notification that the ride is on, which keeps the process alive.</summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
internal sealed class RideService : Service
{
    public const string ReasonExtra = "reason";

    private const string ChannelId = "ride";
    private const int NotificationId = 1;

    public override IBinder? OnBind(Intent? intent) => null;

    /// <summary>Android stops a connected-device service with an exception unless the app may use Bluetooth or a USB device.</summary>
    public static bool MayStart() =>
        AndroidApp.IsGranted(global::Android.Manifest.Permission.BluetoothConnect)
        || AndroidApp.IsGranted(global::Android.Manifest.Permission.BluetoothScan)
        || AndroidUsbTransport.HasUsableStick();

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(new NotificationChannel(ChannelId, Strings.Android_RideChannel, NotificationImportance.Low));
        var notification = new Notification.Builder(this, ChannelId)
            .SetContentTitle(intent?.GetStringExtra(ReasonExtra) ?? AppInfo.Name)
            .SetContentText(Strings.Android_RideNotification)
            .SetSmallIcon(Resource.Drawable.ic_launcher_foreground)
            .SetContentIntent(MainActivity.ReturnIntent(this))
            .SetOngoing(true)
            .Build();
        try
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeConnectedDevice);
        }
        catch (Exception e) when (e is Java.Lang.SecurityException or Java.Lang.IllegalStateException)
        {
            // Thrown back into Java, this would end the app; without the service, the screen still stays on.
            AndroidApp.Log(e, "Android refused the ride service");
            StopSelf();
        }

        return StartCommandResult.NotSticky;
    }
}
