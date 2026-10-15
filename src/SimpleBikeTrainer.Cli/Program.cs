using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using SimpleBikeTrainer.Commands;
using SimpleBikeTrainer;

using var services = new ServiceCollection().AddTrainerHttpClients().BuildServiceProvider();

var app = new CommandApp<RideCliCommand>();
app.Configure(config =>
{
    // A registrar over the whole provider breaks trimming: Spectre's ITypeRegistrar does not say which constructors to keep.
    config.Settings.Registrar.RegisterInstance(services.GetRequiredService<IHttpClientFactory>());

    config.SetApplicationName("simple-bike-trainer-cli");
    config.SetHelpProvider(new LocalizedHelp());

    // Failures are reported below, the same way for every command.
    config.PropagateExceptions();

    config.AddCommand<RideCliCommand>("ride");
    config.AddCommand<ScanCommand>("scan");
    config.AddCommand<ProbeCommand>("probe");
    config.AddCommand<WorkoutsCommand>("workouts");
    config.AddCommand<UploadCommand>("upload");
    config.AddCommand<ConfigCommand>("config");
    config.AddBranch<TrainerSettings>("connect", connect => connect.AddCommand<ConnectGarminCommand>("garmin"));
});

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return await app.RunAsync(args, cts.Token);
}
catch (CommandAppException e)
{
    // An unknown command or option, a value of the wrong type, or one that failed its command's checks.
    if (e.Pretty is { } pretty)
    {
        AnsiConsole.Write(pretty);
    }
    else
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.Message)}[/]");
    }

    return 2;
}
catch (Exception e) when (e is ServiceException or InvalidOperationException or FormatException or HttpRequestException or FileNotFoundException)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.Message)}[/]");
    return 1;
}
catch (OperationCanceledException)
{
    return 130;
}
