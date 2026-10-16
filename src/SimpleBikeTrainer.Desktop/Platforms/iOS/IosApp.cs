using Avalonia.Threading;
using CoreGraphics;
using Foundation;
using UIKit;

namespace SimpleBikeTrainer.Desktop.Platforms;

internal static class IosApp
{
    /// <summary>
    /// The settings and Garmin tokens go in Application Support, which only the app sees. The rides go in Documents,
    /// which the Files app shows under On My iPad.
    /// </summary>
    public static void UseAppFolders()
    {
        if (Folder(NSSearchPathDirectory.ApplicationSupportDirectory) is { } support)
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", support);
        }

        if (Folder(NSSearchPathDirectory.DocumentDirectory) is { } documents)
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", documents);
        }
    }

    public static void OpenUrl(string url)
    {
        if (NSUrl.FromString(url) is not { } target)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => UIApplication.SharedApplication.OpenUrl(target, new UIApplicationOpenUrlOptions(), null));
    }

    /// <summary>The share sheet, pointing at the middle of the screen, as there is no button to point at from here.</summary>
    public static void ShareFile(string path) => Dispatcher.UIThread.Post(() =>
    {
        if (TopController() is not { View: { } view } presenter)
        {
            return;
        }

        var share = new UIActivityViewController([NSUrl.FromFilename(path)], null);
        if (share.PopoverPresentationController is { } popover)
        {
            // iPadOS shows the sheet as a popover, and ends the app if it has nothing to point at.
            popover.SourceView = view;
            popover.SourceRect = new CGRect(new CGPoint(view.Bounds.GetMidX(), view.Bounds.GetMidY()), CGSize.Empty);
            popover.PermittedArrowDirections = 0;
        }

        presenter.PresentViewController(share, true, null);
    });

    private static string? Folder(NSSearchPathDirectory directory) =>
        NSFileManager.DefaultManager.GetUrls(directory, NSSearchPathDomain.User).FirstOrDefault()?.Path;

    private static UIViewController? TopController()
    {
        var window = UIApplication.SharedApplication.ConnectedScenes.ToArray()
            .OfType<UIWindowScene>()
            .SelectMany(scene => scene.Windows)
            .FirstOrDefault(w => w.IsKeyWindow);
        var controller = window?.RootViewController;
        while (controller?.PresentedViewController is { } presented)
        {
            controller = presented;
        }

        return controller;
    }
}
