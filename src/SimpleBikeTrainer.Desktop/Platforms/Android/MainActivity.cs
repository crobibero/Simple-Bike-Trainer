using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Usb;
using Android.OS;
using Android.Views;
using Avalonia.Android;

namespace SimpleBikeTrainer.Desktop.Platforms;

/// <summary>Handles its own rotation and size changes, so turning the tablet keeps the ride on screen.</summary>
[Activity(
    Label = "Simple Bike Trainer",
    Theme = "@style/AppTheme",
    Icon = "@mipmap/ic_launcher",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ResizeableActivity = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden
        | ConfigChanges.Navigation)]
[IntentFilter([UsbManager.ActionUsbDeviceAttached])]
[MetaData(UsbManager.ActionUsbDeviceAttached, Resource = "@xml/ant_usb")]
public sealed class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        AndroidApp.Activity = this;
        base.OnCreate(savedInstanceState);
    }

    protected override void OnResume()
    {
        AndroidApp.Activity = this;
        base.OnResume();
        ApplyKeepScreenOn();
    }

    protected override void OnDestroy()
    {
        if (AndroidApp.Activity == this)
        {
            AndroidApp.Activity = null;
        }

        base.OnDestroy();
    }

    /// <summary>Holds the screen on while a ride runs, as <see cref="RideLock"/> asks.</summary>
    public void ApplyKeepScreenOn()
    {
        if (RideLock.IsHeld)
        {
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        }
        else
        {
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
        }
    }

    /// <summary>Back to this activity, such as from the ride's notification.</summary>
    public static PendingIntent? ReturnIntent(Context context) =>
        PendingIntent.GetActivity(
            context,
            0,
            new Intent(context, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
}
