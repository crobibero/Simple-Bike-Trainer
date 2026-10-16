using AsyncAwaitBestPractices;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using SimpleBikeTrainer.Desktop.ViewModels;
using SimpleBikeTrainer.Desktop.Views;

namespace SimpleBikeTrainer.Desktop;

public partial class App : Application
{
    /// <summary>Set by tests and screenshots to skip loading the real config.</summary>
    internal static AppConfig? ConfigOverride { get; set; }

    /// <summary>Launching with this offers simulated devices, for trying the app without hardware.</summary>
    private const string TestFlag = "--test";

    public override void Initialize()
    {
        // Background work started with SafeFireAndForget reports its failures here instead of ending the app.
        SafeFireAndForgetExtensions.SetDefaultExceptionHandling(e => Console.Error.WriteLine($"{AppInfo.Name}: {e}"));
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Follows the system's light or dark setting, or forces one.</summary>
    public static void ApplyTheme(ThemeChoice theme)
    {
        if (Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                ThemeChoice.Light => Avalonia.Styling.ThemeVariant.Light,
                ThemeChoice.Dark => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default,
            };
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = CreateMain(desktop.Args?.Contains(TestFlag, StringComparer.OrdinalIgnoreCase) == true);
            var window = new MainWindow { DataContext = main };
            desktop.MainWindow = window;
            KeepOpenUntilCleanedUp(window, main, desktop, ShutdownTimeout);
            main.Home.InitializeAsync().SafeFireAndForget();
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            // Android: the app outlives its activity, which Android can recreate; each new one gets a view onto the same app.
            var main = CreateMain(testMode: false);
            activity.MainViewFactory = () => new MainView { DataContext = main };
            main.Home.InitializeAsync().SafeFireAndForget();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static MainWindowViewModel CreateMain(bool testMode)
    {
        var config = ConfigOverride ?? AppConfig.Load();
        ApplyTheme(config.Theme);

        // Lives as long as the app, as the factory's pooled handlers need it to.
        var services = new ServiceCollection().AddTrainerHttpClients().BuildServiceProvider();
        return new MainWindowViewModel(config, services.GetRequiredService<IHttpClientFactory>(), testMode);
    }

    /// <summary>How long closing waits for the ride to stop and the devices to disconnect.</summary>
    internal static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Keeps the window up, showing that it is shutting down, until the ride stops and the devices disconnect.
    /// Closing the window comes first, before the lifetime's ShutdownRequested, so the window has to wait for it.
    /// </summary>
    internal static void KeepOpenUntilCleanedUp(Window window, MainWindowViewModel main, IClassicDesktopStyleApplicationLifetime? desktop, TimeSpan timeout)
    {
        var canClose = false;
        window.Closing += (_, e) =>
        {
            if (canClose)
            {
                return;
            }

            e.Cancel = true;
            if (!main.IsShuttingDown)
            {
                CloseAfterCleanupAsync().SafeFireAndForget();
            }
        };

        // The system can also ask to shut down, such as at log out, before any window closes.
        if (desktop is not null)
        {
            desktop.ShutdownRequested += (_, _) =>
            {
                DisposeBeforeExit(main, timeout);
                canClose = true;
            };
        }

        async Task CloseAfterCleanupAsync()
        {
            await Task.WhenAny(main.DisposeAsync().AsTask(), Task.Delay(timeout));
            canClose = true;
            window.Close();
        }
    }

    /// <summary>
    /// Stops the ride and disconnects the devices as the app closes. Their awaits resume on the UI thread, so the
    /// dispatcher keeps running until they end rather than blocking on them. Returns false if they ran out of time.
    /// </summary>
    internal static bool DisposeBeforeExit(IAsyncDisposable main, TimeSpan timeout)
    {
        var cleanup = main.DisposeAsync().AsTask();
        var done = new CancellationTokenSource(timeout);
        cleanup.ContinueWith(_ => done.Cancel(), TaskScheduler.Default).SafeFireAndForget();
        Dispatcher.UIThread.MainLoop(done.Token);
        if (cleanup.Exception is { } e)
        {
            Console.Error.WriteLine($"{AppInfo.Name}: {e}");
        }

        return cleanup.IsCompleted;
    }
}
