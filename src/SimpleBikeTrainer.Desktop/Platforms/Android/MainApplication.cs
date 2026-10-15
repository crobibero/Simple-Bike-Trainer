using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using SimpleBikeTrainer.AntPlus;
using SimpleBikeTrainer.Ble;

namespace SimpleBikeTrainer.Desktop.Platforms;

/// <summary>Starts the app once per process, before any activity; the activity only shows it.</summary>
[Application]
public sealed class MainApplication(nint javaReference, JniHandleOwnership transfer)
    : AvaloniaAndroidApplication<App>(javaReference, transfer)
{
    public override void OnCreate()
    {
        // Rides go to shared storage, which a computer sees over USB; the settings and Garmin tokens stay in private files.
        if (GetExternalFilesDir(null)?.AbsolutePath is { } shared)
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", shared);
        }

        BleAdapter.PlatformOpen = _ => AndroidBleAdapter.CreateAsync();
        AntStick.OpenTransport = AndroidUsbTransport.TryOpen;
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).WithInterFont();
}
