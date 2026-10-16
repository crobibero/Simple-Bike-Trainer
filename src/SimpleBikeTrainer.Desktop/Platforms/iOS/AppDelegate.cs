using Avalonia;
using Avalonia.iOS;
using Foundation;
using SimpleBikeTrainer.AntPlus;
using UIKit;

namespace SimpleBikeTrainer.Desktop.Platforms;

internal static class IosProgram
{
    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

[Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Before AppConfig.Load reads XDG_CONFIG_HOME.
        IosApp.UseAppFolders();

        // iPadOS gives apps no USB access, so there is never an ANT+ stick to find.
        AntStick.OpenTransport = static () => null;
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}
