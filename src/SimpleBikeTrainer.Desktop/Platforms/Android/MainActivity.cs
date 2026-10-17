using System.Diagnostics;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Usb;
using Android.OS;
using Android.Views;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;

namespace SimpleBikeTrainer.Desktop.Platforms;

/// <summary>Handles its own rotation and size changes, so turning the tablet keeps the ride on screen.</summary>
[Activity(
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
    /// <summary>Past this, the splash screen goes even without a frame, so a renderer that never starts can't hide the app behind it.</summary>
    private static readonly TimeSpan SplashLimit = TimeSpan.FromSeconds(2);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        AndroidApp.Activity = this;
        base.OnCreate(savedInstanceState);
        HoldSplashUntilFirstFrame();
    }

    /// <summary>
    /// Android takes the splash screen down at the window's first draw, but Avalonia draws into a surface behind the window,
    /// through a hole the window leaves for it. Until that surface has a frame, the hole shows the home screen.
    /// </summary>
    private void HoldSplashUntilFirstFrame()
    {
        if (FindViewById(global::Android.Resource.Id.Content) is not { ViewTreeObserver: { } observer } content
            || Content is not Control view)
        {
            return;
        }

        Task? firstFrame = null;
        var start = Stopwatch.GetTimestamp();
        view.SizeChanged += OnSizeChanged;
        observer.AddOnPreDrawListener(new DrawGate(content, () => firstFrame?.IsCompleted == true || Stopwatch.GetElapsedTime(start) > SplashLimit));

        // Until the surface exists the view is laid out at its 1×1 pixel placeholder, so a real size means the next frame lands on screen.
        void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 1 || e.NewSize.Height <= 1)
            {
                return;
            }

            view.SizeChanged -= OnSizeChanged;
            firstFrame = ElementComposition.GetElementVisual(view)?.Compositor.RequestCompositionBatchCommitAsync().Rendered;
        }
    }

    /// <summary>Cancels the window's draws, which keeps the splash screen up, until <paramref name="ready"/> says otherwise.</summary>
    private sealed class DrawGate(View content, Func<bool> ready) : Java.Lang.Object, ViewTreeObserver.IOnPreDrawListener
    {
        public bool OnPreDraw()
        {
            if (!ready())
            {
                return false;
            }

            content.ViewTreeObserver?.RemoveOnPreDrawListener(this);
            return true;
        }
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
