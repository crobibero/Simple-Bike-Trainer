using Android.App;
using Android.Content;
using Android.Content.PM;
using AndroidX.Core.Content;
using SimpleBikeTrainer.Desktop.Resources;

namespace SimpleBikeTrainer.Desktop.Platforms;

internal static class AndroidApp
{
    private const string LogTag = "SimpleBikeTrainer";

    private static readonly SemaphoreSlim AskOneAtATime = new(1, 1);
    private static int _nextRequest = 100;

    /// <summary>Null while there is none, such as before the first one starts.</summary>
    public static MainActivity? Activity { get; set; }

    public static Context Context => Application.Context;

    public static void Log(string message) => global::Android.Util.Log.Info(LogTag, message);

    public static void Log(Exception e, string message) => global::Android.Util.Log.Warn(LogTag, $"{message}: {e}");

    public static bool IsGranted(string permission) =>
        ContextCompat.CheckSelfPermission(Context, permission) == Permission.Granted;

    /// <summary>One request at a time, as Android shows one dialog at a time. True when all of <paramref name="required"/> are given.</summary>
    public static async Task<bool> RequestPermissionsAsync(string[] required, string[]? optional = null)
    {
        await AskOneAtATime.WaitAsync();
        try
        {
            string[] missing = [.. required.Concat(optional ?? []).Where(p => !IsGranted(p))];
            if (missing.Length == 0)
            {
                return true;
            }

            if (Activity is not { } activity)
            {
                return required.All(IsGranted);
            }

            var code = Interlocked.Increment(ref _nextRequest) & 0xFFFF;
            var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnResult(int requestCode, string[] permissions, Permission[] results)
            {
                if (requestCode == code)
                {
                    answered.TrySetResult();
                }
            }

            activity.RequestPermissionsResult += OnResult;
            try
            {
                activity.RunOnUiThread(() => activity.RequestPermissions(missing, code));
                await answered.Task;
            }
            finally
            {
                activity.RequestPermissionsResult -= OnResult;
            }

            return required.All(IsGranted);
        }
        finally
        {
            AskOneAtATime.Release();
        }
    }

    /// <summary>Asks Android to turn Bluetooth on. True when it is on afterwards.</summary>
    public static async Task<bool> RequestEnableAsync(string action, Func<bool> isOn)
    {
        if (isOn())
        {
            return true;
        }

        if (Activity is not { } activity)
        {
            return false;
        }

        var code = Interlocked.Increment(ref _nextRequest) & 0xFFFF;
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnResult(int requestCode, Result result, Intent? data)
        {
            if (requestCode == code)
            {
                answered.TrySetResult();
            }
        }

        activity.ActivityResult += OnResult;
        try
        {
            activity.RunOnUiThread(() => activity.StartActivityForResult(new Intent(action), code));
            await answered.Task;
        }
        finally
        {
            activity.ActivityResult -= OnResult;
        }

        return isOn();
    }

    public static void OpenUrl(string url)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url));
            intent.AddFlags(ActivityFlags.NewTask);
            Context.StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            // No browser; nothing else to do.
        }
    }

    public static void ShareFile(string path)
    {
        try
        {
            var uri = FileProvider.GetUriForFile(Context, $"{Context.PackageName}.files", new Java.IO.File(path));
            var send = new Intent(Intent.ActionSend);
            send.SetType("application/octet-stream");
            send.PutExtra(Intent.ExtraStream, uri);
            send.AddFlags(ActivityFlags.GrantReadUriPermission);
            var chooser = Intent.CreateChooser(send, Strings.Summary_ShareFit)!;
            if (Activity is { } activity)
            {
                activity.StartActivity(chooser);
            }
            else
            {
                chooser.AddFlags(ActivityFlags.NewTask);
                Context.StartActivity(chooser);
            }
        }
        catch (Exception e) when (e is Java.Lang.IllegalArgumentException or ActivityNotFoundException)
        {
            Log(e, "Could not share " + path);
        }
    }
}
